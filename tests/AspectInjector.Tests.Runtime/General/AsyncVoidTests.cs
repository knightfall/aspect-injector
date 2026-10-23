using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace AspectInjector.Tests.Runtime.General
{
    public class AsyncVoidTests
    {
        [Fact]
        public void WaitsBeyondTheOldFixedDelayAndRestoresTheContext()
        {
            var previous = SynchronizationContext.Current;
            var completed = false;

            AsyncVoid.Run(async () =>
            {
                await Task.Delay(500);
                completed = true;
            });

            Assert.True(completed);
            Assert.Same(previous, SynchronizationContext.Current);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PropagatesAsyncVoidFailuresAndRestoresTheContext(bool yield)
        {
            var previous = SynchronizationContext.Current;
            var expected = new InvalidOperationException("Async void failed");

            var actual = Record.Exception(() => AsyncVoid.Run(async () =>
            {
                if (yield)
                    await Task.Yield();
                throw expected;
            }));

            Assert.Same(expected, actual);
            Assert.Same(previous, SynchronizationContext.Current);
        }

        [Fact]
        public void PropagatesSynchronousFailuresAndRestoresTheContext()
        {
            var previous = SynchronizationContext.Current;
            var expected = new InvalidOperationException("Invocation failed");

            var actual = Record.Exception(() => AsyncVoid.Run(() => throw expected));

            Assert.Same(expected, actual);
            Assert.Same(previous, SynchronizationContext.Current);
        }
    }
}
