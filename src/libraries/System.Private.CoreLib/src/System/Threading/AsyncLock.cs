// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace System.Threading
{
    /// <summary>
    /// Provides a mutual exclusion asynchronous lock primitive.
    /// </summary>
    public sealed class AsyncLock
    {
        // The lock object and a bool value representing whether the lock is free or not.
        private readonly StrongBox<bool> _lockObjAndFree;

        // Head of list representing asynchronous waits.
        private TaskNode? m_asyncHead;

        /// <summary>
        /// Creates a new instance of <see cref="AsyncLock"/>.
        /// </summary>
        public AsyncLock()
        {
            _lockObjAndFree = new StrongBox<bool>(true);
        }

        /// <summary>
        /// Creates a new instance of <see cref="AsyncLock"/> and optionally acquires the lock.
        /// </summary>
        /// <param name="acquire">when true the lock is acquired until <see cref="Release"/> is explicitly called</param>
        public AsyncLock(bool acquire)
        {
            _lockObjAndFree = new StrongBox<bool>(!acquire);
        }

        /// <summary>
        /// Attempts to enter the <see cref="AsyncLock" /> synchronously, without blocking if the lock is already taken.
        /// </summary>
        /// <returns>true if the lock has been taken, false otherwise</returns>
        public bool TryWait()
        {
            lock (_lockObjAndFree)
            {
                if (_lockObjAndFree.Value)
                {
                    _lockObjAndFree.Value = false;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Asynchronously waits to enter the <see cref="AsyncLock"/>, while observing a <see cref="CancellationToken"/>.
        /// </summary>
        public ValueTask WaitAsync(CancellationToken cancellationToken)
        {
            ValueTask<bool> result = WaitAsync(Timeout.Infinite, cancellationToken);

            if (result.IsCompletedSuccessfully)
            {
                result.GetAwaiter().GetResult();
                return default;
            }

            return new ValueTask(result.AsTask());
        }

        /// <summary>
        /// Asynchronously waits to enter the <see cref="AsyncLock"/>, using a <see cref="TimeSpan"/> to measure the time interval.
        /// </summary>
        /// <param name="timeout">
        /// A <see cref="TimeSpan"/> that represents the number of milliseconds
        /// to wait, or a <see cref="TimeSpan"/> that represents -1 milliseconds to wait indefinitely.
        /// </param>
        /// <returns>
        /// A task that will complete with a result of true if the current thread successfully entered
        /// the <see cref="AsyncLock"/>, otherwise with a result of false.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="timeout"/> is a negative number other than -1 milliseconds, which represents
        /// an infinite time-out -or- timeout is greater than <see cref="int.MaxValue"/>.
        /// </exception>
        public ValueTask<bool> WaitAsync(TimeSpan timeout) => WaitAsync(timeout, CancellationToken.None);

        /// <summary>
        /// Asynchronously waits to enter the <see cref="AsyncLock"/>, using a <see cref="TimeSpan"/> to measure the time interval.
        /// </summary>
        /// <param name="timeout">
        /// A <see cref="TimeSpan"/> that represents the number of milliseconds
        /// to wait, or a <see cref="TimeSpan"/> that represents -1 milliseconds to wait indefinitely.
        /// </param>
        /// <param name="cancellationToken">
        /// The <see cref="CancellationToken"/> token to observe.
        /// </param>
        /// <returns>
        /// A task that will complete with a result of true if the current thread successfully entered
        /// the <see cref="AsyncLock"/>, otherwise with a result of false.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="timeout"/> is a negative number other than -1 milliseconds, which represents
        /// an infinite time-out -or- timeout is greater than <see cref="int.MaxValue"/>.
        /// </exception>
        public ValueTask<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            // Validate the timeout
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            // Call wait with the timeout milliseconds
            return WaitAsync((int)timeout.TotalMilliseconds, cancellationToken);
        }

        /// <summary>
        /// Asynchronously waits to enter the <see cref="AsyncLock"/>,
        /// using a 32-bit signed integer to measure the time interval,
        /// while observing a <see cref="System.Threading.CancellationToken"/>.
        /// </summary>
        /// <param name="millisecondsTimeout">
        /// The number of milliseconds to wait, or <see cref="Timeout.Infinite"/>(-1) to wait indefinitely.
        /// </param>
        /// <param name="cancellationToken">The <see cref="System.Threading.CancellationToken"/> to observe.</param>
        /// <returns>
        /// A task that will complete with a result of true if the current thread successfully entered
        /// the <see cref="AsyncLock"/>, otherwise with a result of false.
        /// </returns>
        /// <exception cref="System.ObjectDisposedException">The current instance has already been
        /// disposed.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="millisecondsTimeout"/> is a negative number other than -1,
        /// which represents an infinite time-out.
        /// </exception>
        public ValueTask<bool> WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (millisecondsTimeout < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            // Bail early for cancellation
            if (cancellationToken.IsCancellationRequested)
            {
                return ValueTask.FromCanceled<bool>(cancellationToken);
            }
            TaskNode asyncWaiter;

            lock (_lockObjAndFree)
            {
                // If the lock is free, take it without waiting
                if (_lockObjAndFree.Value)
                {
                    _lockObjAndFree.Value = false;
                    return new ValueTask<bool>(true);
                }

                if (millisecondsTimeout == 0)
                {
                    // No counts, if timeout is zero fail fast
                    return new ValueTask<bool>(false);
                }

                asyncWaiter = CreateAndAddAsyncWaiter();
            }

            return new ValueTask<bool>(millisecondsTimeout == Timeout.Infinite && !cancellationToken.CanBeCanceled ?
                asyncWaiter : WaitUntilCountOrTimeoutAsync(asyncWaiter, millisecondsTimeout, cancellationToken));
        }

        /// <summary>Creates a new task and stores it into the async waiters list.</summary>
        private TaskNode CreateAndAddAsyncWaiter()
        {
            Debug.Assert(Monitor.IsEntered(_lockObjAndFree), "Requires the lock be held");

            // Create the task
            var task = new TaskNode();

            // Add it to the linked list
            if (m_asyncHead is null)
            {
                m_asyncHead = task;
            }
            else
            {
                if (m_asyncHead.Last is null)
                {
                    Debug.Assert(m_asyncHead.Next is null);
                    m_asyncHead.Next = task;
                }
                else
                {
                    m_asyncHead.Last.Next = task;
                }

                m_asyncHead.Last = task;
            }

            return task;
        }

        /// <summary>Performs the asynchronous wait.</summary>
        private static async Task<bool> WaitUntilCountOrTimeoutAsync(TaskNode asyncWaiter, int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (millisecondsTimeout != Timeout.Infinite)
            {
                // Wait until either the task is completed, cancellation is requested, or the timeout occurs.
                // We need to ensure that the Task.Delay task is appropriately cleaned up if the await
                // completes due to the asyncWaiter completing, so we use our own token that we can explicitly
                // cancel, and we chain the caller's supplied token into it.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (asyncWaiter == await Task.WhenAny(asyncWaiter, Task.Delay(millisecondsTimeout, cts.Token)).ConfigureAwait(false))
                {
                    cts.Cancel(); // ensure that the Task.Delay task is cleaned up
                    return true; // successfully acquired
                }
            }
            else // millisecondsTimeout == Timeout.Infinite
            {
                // Wait until either the task is completed or cancellation is requested.
                var cancellationTask = new Task(null, TaskCreationOptions.RunContinuationsAsynchronously, promiseStyle: true);
                using (cancellationToken.UnsafeRegister(static s => ((Task)s!).TrySetResult(), cancellationTask))
                {
                    if (asyncWaiter == await Task.WhenAny(asyncWaiter, cancellationTask).ConfigureAwait(false))
                    {
                        return true; // successfully acquired
                    }
                }
            }

            // If we get here, the wait has timed out or been canceled.
            // If we fail to complete the async waiter, than it means that it succeeded acquiring the lock
            // and in that case we should not return false.
            if (asyncWaiter.TrySetResult(false))
            {
                cancellationToken.ThrowIfCancellationRequested(); // Cancellation occurred
                return false; // Timeout occurred
            }

            // The waiter had already been resulted, which means it's already completed or is about to
            // complete, so let it, and don't return until it does.
            return await asyncWaiter.ConfigureAwait(false);
        }

        /// <summary>
        /// Exits the <see cref="AsyncLock"/>.
        /// </summary>
        public void Release()
        {
            lock (_lockObjAndFree)
            {
                if (_lockObjAndFree.Value)
                {
                    throw new InvalidOperationException("The lock is not taken.");
                }

                // Now signal to any asynchronous waiters, if there are any.
                while (m_asyncHead is not null)
                {
                    TaskNode? waiterTask = m_asyncHead;
                    m_asyncHead = waiterTask.Next;

                    if (m_asyncHead is not null)
                    {
                        // Because the last element is only guaranteed to be up to date for
                        // the first element, and we're removing it, we need to make sure
                        // the next element has reference to the actual last element.
                        m_asyncHead.Last = waiterTask.Last;
                    }

                    // This would return false in the case when a waiter has timed out or has been cancelled.
                    if (waiterTask.TrySetResult(true))
                    {
                        // The waiter has been succesfully notified, the lock remains taken.
                        return;
                    }
                }

                // Mark the lock as free
                _lockObjAndFree.Value = true;
            }
        }

        // TaskNode in a linked list of asynchronous waiters
        private sealed class TaskNode : Task<bool>
        {
            internal TaskNode() : base((object?)null, TaskCreationOptions.RunContinuationsAsynchronously) { }

            /// <summary>
            /// The next node in the linked list.
            /// </summary>
            internal TaskNode? Next;

            /// <summary>
            /// The last node in the linked list. It is required to be up to date only
            /// for the first element in the list.
            /// </summary>
            /// <remarks>Gives O(1) insertion capability to the linked list.</remarks>
            internal TaskNode? Last;
        }
    }
}
