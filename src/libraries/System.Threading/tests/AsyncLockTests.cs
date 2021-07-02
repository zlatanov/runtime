// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Threading.Tests
{
    /// <summary>
    /// SemaphoreSlim unit tests
    /// </summary>
    public class AsyncLockTests
    {
        private readonly ITestOutputHelper _output;

        public AsyncLockTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task WaitShouldAqcuireLock()
        {
            AsyncLock asyncLock = new();

            bool taken = await asyncLock.WaitAsync(0, CancellationToken.None);
            Assert.True(taken);

            taken = await asyncLock.WaitAsync(0, CancellationToken.None);
            Assert.False(taken);

            asyncLock.Release();
        }


        [Fact]
        public async Task Measure()
        {
            AsyncLock asyncLock = new();
            SemaphoreSlim semaphore = new(1, 1);

            Stopwatch stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < 1_000_000; ++i)
            {
                await asyncLock.WaitAsync(CancellationToken.None);
                asyncLock.Release();
            }

            stopwatch.Stop();
            _output.WriteLine($"AsyncLock: {stopwatch.Elapsed}");

            stopwatch = Stopwatch.StartNew();

            for (int i = 0; i < 1_000_000; ++i)
            {
                await semaphore.WaitAsync(CancellationToken.None);
                semaphore.Release();
            }

            stopwatch.Stop();
            _output.WriteLine($"AsyncLock: {stopwatch.Elapsed}");
        }
    }
}
