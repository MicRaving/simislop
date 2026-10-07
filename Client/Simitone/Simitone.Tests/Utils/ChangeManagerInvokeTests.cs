using FSO.Content;
using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="ChangeManager.Invoke"/> and the queue <see cref="ChangeManager.RunResModifications"/>
    /// drains, which together are the game's cross-thread path for resource modifications: a
    /// worker thread hands a delegate to the game thread and blocks until it has run.
    ///
    /// <para>
    /// The signal that releases the blocked caller sat immediately after the delegate ran, with
    /// nothing between the two. Any exception the delegate threw - the ordinary case when a
    /// queued modification touches a file that has gone - therefore left the caller blocked on
    /// <c>WaitOne()</c> with no timeout, and the element had already been dequeued so nothing
    /// could pick it up again. The calling thread hung permanently, holding whatever it was
    /// doing, and the failure was invisible: no log, no exception, just a game that has stopped.
    /// </para>
    ///
    /// <para>
    /// The pump below loops, which is what the game does - <c>RunResModifications</c> is called
    /// every frame - so the test does not depend on winning a race between the enqueue and the
    /// first drain.
    /// </para>
    /// </summary>
    public sealed class ChangeManagerInvokeTests
    {
        /// <summary>
        /// How long to wait for a caller to come back. Generous, because on the unfixed code it
        /// never comes back at all and the failure is the point; on the fixed code the wait is
        /// milliseconds.
        /// </summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Runs <see cref="ChangeManager.RunResModifications"/> on its own thread until
        /// <paramref name="until"/> is set, exactly as the game loop does.
        /// </summary>
        private static Thread StartPump(ChangeManager changes, Func<bool> until, List<Exception> pumpErrors)
        {
            var pump = new Thread(() =>
            {
                var deadline = DateTime.UtcNow + Timeout;
                while (!until() && DateTime.UtcNow < deadline)
                {
                    try
                    {
                        changes.RunResModifications();
                    }
                    catch (Exception e)
                    {
                        lock (pumpErrors) pumpErrors.Add(e);
                    }
                    Thread.Sleep(1);
                }
            });
            pump.IsBackground = true;
            pump.Start();
            return pump;
        }

        /// <summary>
        /// The regression. A delegate that throws must not be able to wedge the thread that
        /// queued it.
        /// </summary>
        [Fact]
        public void AQueuedDelegateThatThrowsDoesNotHangItsCaller()
        {
            var changes = new ChangeManager();
            var returned = new ManualResetEventSlim(false);
            var callerErrors = new List<Exception>();
            var pumpErrors = new List<Exception>();

            var caller = new Thread(() =>
            {
                try
                {
                    changes.Invoke((Action)(() => throw new InvalidOperationException("the resource went missing")));
                }
                catch (Exception e)
                {
                    //Invoke is not documented to rethrow, and the point of the fix is that the
                    //caller comes back at all - but if it ever does rethrow, that is not a hang.
                    lock (callerErrors) callerErrors.Add(e);
                }
                finally
                {
                    returned.Set();
                }
            });
            caller.IsBackground = true;
            caller.Start();

            var pump = StartPump(changes, () => returned.IsSet, pumpErrors);

            Assert.True(returned.Wait(Timeout),
                "the thread that queued a throwing delegate never came back - it is blocked forever");
            Assert.True(pump.Join(Timeout));
            Assert.Empty(pumpErrors);
        }

        /// <summary>
        /// One bad element must not take the rest of the queue with it. Before the fix the throw
        /// escaped <c>RunResModifications</c> entirely, abandoning every element queued behind
        /// it as well as the one that threw.
        /// </summary>
        [Fact]
        public void AThrowingElementDoesNotStopTheQueueFromDraining()
        {
            var changes = new ChangeManager();
            var returned = new ManualResetEventSlim(false);
            var ranAfterwards = 0;
            var pumpErrors = new List<Exception>();

            var caller = new Thread(() =>
            {
                try
                {
                    changes.Invoke((Action)(() => throw new InvalidOperationException("boom")));
                }
                finally
                {
                    changes.Invoke((Action)(() => Interlocked.Increment(ref ranAfterwards)));
                    returned.Set();
                }
            });
            caller.IsBackground = true;
            caller.Start();

            var pump = StartPump(changes, () => returned.IsSet, pumpErrors);

            Assert.True(returned.Wait(Timeout), "the second queued delegate never ran");
            Assert.True(pump.Join(Timeout));
            Assert.Equal(1, ranAfterwards);
            Assert.Empty(pumpErrors);
        }

        /// <summary>
        /// A delegate that throws several times in a row must not poison the queue either: the
        /// flag that used to be left set was per-archive there, and the "stop signalling on
        /// error" behaviour is per-queue here.
        /// </summary>
        [Fact]
        public void RepeatedlyThrowingDelegatesDoNotPoisonTheQueue()
        {
            var changes = new ChangeManager();
            var returned = new ManualResetEventSlim(false);
            var succeeded = 0;
            var pumpErrors = new List<Exception>();

            var caller = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 5; i++)
                    {
                        changes.Invoke((Action)(() => throw new InvalidOperationException("boom ")));
                    }
                    changes.Invoke((Action)(() => Interlocked.Increment(ref succeeded)));
                }
                finally
                {
                    returned.Set();
                }
            });
            caller.IsBackground = true;
            caller.Start();

            var pump = StartPump(changes, () => returned.IsSet, pumpErrors);

            Assert.True(returned.Wait(Timeout));
            Assert.True(pump.Join(Timeout));
            Assert.Equal(1, succeeded);
            Assert.Empty(pumpErrors);
        }

        /// <summary>
        /// The ordinary case, which the fix must not have broken: a delegate runs on the pump
        /// thread and the caller sees it complete.
        /// </summary>
        [Fact]
        public void AQueuedDelegateRunsAndReleasesItsCaller()
        {
            var changes = new ChangeManager();
            var returned = new ManualResetEventSlim(false);
            var ranOn = 0;
            var pumpErrors = new List<Exception>();

            var caller = new Thread(() =>
            {
                changes.Invoke((Action)(() => ranOn = Thread.CurrentThread.ManagedThreadId));
                returned.Set();
            });
            var callerId = caller.ManagedThreadId;
            caller.IsBackground = true;
            caller.Start();

            var pump = StartPump(changes, () => returned.IsSet, pumpErrors);

            Assert.True(returned.Wait(Timeout));
            Assert.True(pump.Join(Timeout));
            Assert.NotEqual(0, ranOn);
            Assert.NotEqual(callerId, ranOn);
            Assert.Empty(pumpErrors);
        }

        /// <summary>
        /// Arguments travel with the delegate, and a mismatch between them and the signature is
        /// the other thing that makes <c>DynamicInvoke</c> throw - so it must be survivable too.
        /// </summary>
        [Fact]
        public void ADelegateCalledWithTheWrongArgumentsDoesNotHangItsCaller()
        {
            var changes = new ChangeManager();
            var returned = new ManualResetEventSlim(false);
            var pumpErrors = new List<Exception>();

            var caller = new Thread(() =>
            {
                try
                {
                    changes.Invoke((Action<string>)(s => { }), 42);
                }
                finally
                {
                    returned.Set();
                }
            });
            caller.IsBackground = true;
            caller.Start();

            var pump = StartPump(changes, () => returned.IsSet, pumpErrors);

            Assert.True(returned.Wait(Timeout));
            Assert.True(pump.Join(Timeout));
            Assert.Empty(pumpErrors);
        }

        /// <summary>
        /// <c>ResAction</c> already signals in a way that survives a throwing action, so
        /// <c>BlockingResMod</c> does not hang. Pinned because the two queue-draining loops sit
        /// side by side in one method and only one of them used to be safe.
        /// </summary>
        [Fact]
        public void ABlockingResourceModificationThatThrowsStillReleasesItsCaller()
        {
            var changes = new ChangeManager();
            var returned = new ManualResetEventSlim(false);
            var pumpErrors = new List<Exception>();

            var caller = new Thread(() =>
            {
                try
                {
                    changes.BlockingResMod(new ResAction(() => throw new InvalidOperationException("boom")));
                }
                finally
                {
                    returned.Set();
                }
            });
            caller.IsBackground = true;
            caller.Start();

            var pump = StartPump(changes, () => returned.IsSet, pumpErrors);

            Assert.True(returned.Wait(Timeout));
            Assert.True(pump.Join(Timeout));
            Assert.Empty(pumpErrors);
        }
    }
}
