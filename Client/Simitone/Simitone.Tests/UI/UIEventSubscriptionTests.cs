/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="UIEventSubscription"/> - the rule that makes a repeat subscription of
    /// the same handler a no-op.
    ///
    /// <para>
    /// The finding (R-43) was that <c>UIButton</c>'s four events are field-like events with no
    /// teardown, so a panel method that re-runs over a persistent control accumulates a handler
    /// per run. The dangerous fix is to null them in <c>UIElement.Removed()</c>: a removed dialog
    /// is not a dead one - <c>GameController</c> re-adds the same debug menu instance on the next
    /// keypress - so that leaves half a menu inert. The safe fix is that the accumulation cannot
    /// happen in the first place.
    /// </para>
    ///
    /// <para>
    /// These are pure delegate tests. That is the whole point: the mechanism is <c>Delegate</c>
    /// arithmetic, so a graphics device would prove nothing extra. The handlers are held in
    /// explicitly typed locals rather than passed as method groups inline, because generic
    /// inference on a method group is a lower-bound inference and gets the type parameter wrong.
    /// </para>
    /// </summary>
    public sealed class UIEventSubscriptionTests
    {
        private sealed class Panel
        {
            public int Clicks;
            public int Other;
            public void OnClick(UIElement button) { Clicks++; }
            public void AlsoOnClick(UIElement button) { Other++; }
        }

        private static ButtonClickDelegate Bind(Panel panel)
        {
            return panel.OnClick;
        }

        private static ButtonClickDelegate BindOther(Panel panel)
        {
            return panel.AlsoOnClick;
        }

        [Fact]
        public void DelegateCombineDoesNotDeduplicateSoTheGuardIsNotOptional()
        {
            // If this ever stops being true the guard is dead weight; if it ever becomes true the
            // guard is still correct, just redundant. Asserting it keeps the reason for the code
            // visible instead of a comment nobody re-reads.
            Action first = Noop;
            Action second = Noop;
            Action combined = first + second;

            Assert.True(first.Equals(second), "the two delegates are equal");
            Assert.Equal(2, combined.GetInvocationList().Length);
        }

        private static void Noop() { }

        [Fact]
        public void SubscribingTheSameMethodGroupTwiceIsOneSubscription()
        {
            var panel = new Panel();
            var handlers = UIEventSubscription.AddUnique(Bind(panel), Bind(panel));
            Assert.Single(handlers.GetInvocationList());

            handlers(null);
            Assert.Equal(1, panel.Clicks);
        }

        [Fact]
        public void SubscribingTwentyTimesIsStillOneSubscription()
        {
            // The shape a Refresh() takes. With the field-like event the handler ran twenty times
            // on the twentieth refresh; with the accessor it runs once, always.
            var panel = new Panel();
            ButtonClickDelegate handlers = null;
            for (var i = 0; i < 20; i++)
            {
                handlers = UIEventSubscription.AddUnique(handlers, Bind(panel));
            }

            handlers(null);
            Assert.Equal(1, panel.Clicks);
        }

        [Fact]
        public void DifferentHandlersOnTheSameObjectAreStillBothSubscribed()
        {
            // Two handlers on one target that do different things are different delegates.
            // De-duplicating those would silently break a real panel.
            var panel = new Panel();
            var handlers = UIEventSubscription.AddUnique(Bind(panel), BindOther(panel));
            handlers(null);

            Assert.Equal(1, panel.Clicks);
            Assert.Equal(1, panel.Other);
        }

        [Fact]
        public void TheSameMethodOnADifferentObjectIsADifferentSubscription()
        {
            // Panels are re-created; a handler bound to an old panel instance must not be mistaken
            // for the same handler bound to a new one.
            var first = new Panel();
            var second = new Panel();
            var handlers = UIEventSubscription.AddUnique(Bind(first), Bind(second));

            Assert.Equal(1, UIEventSubscription.CountOf(handlers, Bind(first)));
            Assert.Equal(1, UIEventSubscription.CountOf(handlers, Bind(second)));
            Assert.Equal(2, handlers.GetInvocationList().Length);

            handlers(null);
            Assert.Equal(1, first.Clicks);
            Assert.Equal(1, second.Clicks);
        }

        [Fact]
        public void AddingToNothingAndAddingNothingBehaveLikePlusEquals()
        {
            var panel = new Panel();

            var fromNothing = UIEventSubscription.AddUnique<ButtonClickDelegate>(null, Bind(panel));
            Assert.NotNull(fromNothing);
            fromNothing(null);
            Assert.Equal(1, panel.Clicks);

            // A null handler is what `+= null` compiles to, and it must not wipe the list.
            var unchanged = UIEventSubscription.AddUnique(fromNothing, null);
            Assert.Same(fromNothing, unchanged);
            unchanged(null);
            Assert.Equal(2, panel.Clicks);
        }

        [Fact]
        public void CountOfIsZeroForAnAbsentHandler()
        {
            var panel = new Panel();
            Assert.Equal(0, UIEventSubscription.CountOf<ButtonClickDelegate>(null, Bind(panel)));
            Assert.Equal(0, UIEventSubscription.CountOf(Bind(panel), null));
            Assert.Equal(0, UIEventSubscription.CountOf(Bind(panel), BindOther(panel)));
        }

        [Fact]
        public void AnUnsubscribeStillRemovesTheHandler()
        {
            // The accessors are not read-only: -= has to work, or a caller that deliberately
            // detaches could not.
            var panel = new Panel();
            var handlers = UIEventSubscription.AddUnique(Bind(panel), BindOther(panel));
            Assert.Equal(2, handlers.GetInvocationList().Length);

            handlers = UIEventSubscription.Remove(handlers, Bind(panel));
            Assert.Single(handlers.GetInvocationList());

            handlers(null);
            Assert.Equal(0, panel.Clicks);
            Assert.Equal(1, panel.Other);
        }

        [Fact]
        public void RemovingSomethingThatWasNeverSubscribedIsANoOp()
        {
            // A teardown path has to be able to run unconditionally, and -= from an empty list has
            // to behave the way it reads rather than throw.
            var panel = new Panel();
            var stranger = new Panel();

            Assert.Null(UIEventSubscription.Remove<ButtonClickDelegate>(null, Bind(panel)));

            // `-= null` is what the language turns a null handler into, and it must leave the
            // list exactly as it was rather than emptying it.
            var only = UIEventSubscription.AddUnique<ButtonClickDelegate>(null, Bind(panel));
            Assert.Same(only, UIEventSubscription.Remove(only, null));

            var unchanged = UIEventSubscription.Remove(only, Bind(stranger));
            Assert.Same(only, unchanged);
            Assert.Single(unchanged.GetInvocationList());
            unchanged(null);
            Assert.Equal(1, panel.Clicks);
            Assert.Equal(0, stranger.Clicks);
        }

        [Fact]
        public void TheAccessorsAreTheOnlyDifferenceFromAFieldLikeEvent()
        {
            // A UIButton cannot be built without a graphics device (its static constructor loads a
            // texture), so what can be checked here is that the event surface is unchanged: four
            // events of the same delegate type, still usable with += and -= from outside.
            var type = typeof(UIButton);
            foreach (var name in new[] { "OnButtonClick", "OnButtonHover", "OnButtonDown", "OnButtonExit" })
            {
                var declared = type.GetEvent(name);
                Assert.NotNull(declared);
                Assert.Equal(typeof(ButtonClickDelegate), declared.EventHandlerType);
                Assert.NotNull(declared.GetAddMethod());
                Assert.NotNull(declared.GetRemoveMethod());
                Assert.False(declared.GetAddMethod().IsStatic);
            }
        }
    }
}
