// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Tasks;
using Xunit;

namespace System.Threading.Tests
{
    /// <summary>
    /// SemaphoreSlim unit tests
    /// </summary>
    public class AsyncLockTests
    {
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
    }
}
