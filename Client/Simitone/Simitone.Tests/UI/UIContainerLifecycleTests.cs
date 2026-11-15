/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework;
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the two things <see cref="UIContainer"/> owes every child: that removing one
    /// leaves it usable, and that the list it hands to a walk is not rebuilt every frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both are properties of a container rather than of any one control, so they are tested on
    /// the container itself with the smallest element that will do. Nothing here needs a
    /// graphics device: <see cref="UIElement"/>'s only abstract member is <c>Draw</c>, and the
    /// parts under test - <c>Parent</c>, the matrix, <c>WillDraw</c>, the <c>Find</c> walks and
    /// the child snapshot - are all arithmetic over plain fields.
    /// </para>
    /// <para>
    /// Remove-then-re-add is not a corner case. <c>UIPieMenu.RenderMenu</c> drops its whole
    /// button set and rebuilds it on every selection change, <c>UIScript</c> re-homes a named
    /// element, and <c>UILayer.RemoveDialog</c> removes a dialog through the parent it found it
    /// under. So "removed" is a state an element really is in, for a measurable number of
    /// frames, and it has to answer the same questions there as it does anywhere else.
    /// </para>
    /// </remarks>
    public sealed class UIContainerLifecycleTests
    {
        // ---- the smallest thing that will do -------------------------------------------------

        private sealed class TestElement : UIElement
        {
            public override void Draw(UISpriteBatch batch) { }
        }

        /// <summary>Exposes the protected snapshot so the caching can be observed.</summary>
        private sealed class ExposedContainer : UIContainer
        {
            public UIElement[] Snapshot() { return GetChildrenSafe(); }

            public int Version { get { return ChildrenVersion; } }
        }

        private sealed class Screen { }

        /// <summary>
        /// A marker subclass, so a <c>FindParent</c> walk has to pass a container that is not the
        /// type it is looking for before it finds one that is.
        /// </summary>
        private sealed class MarkerPanel : UIContainer { }

        // ---- Remove leaves the element in the tree -------------------------------------------

        [Fact]
        public void ARemovedChildStillKnowsTheContainerItWasIn()
        {
            // The finding. Remove used to null Parent, so between the remove and the re-add the
            // element had no way back up the tree at all.
            var container = new UIContainer();
            var child = new TestElement();
            container.Add(child);
            Assert.Same(container, child.Parent);

            container.Remove(child);

            Assert.Same(container, child.Parent);
        }

        [Fact]
        public void ARemovedChildStillReportsVisibleOnlyIfItsContainerIs()
        {
            // The consequence that reaches the screen. WillDraw walks Parent, so a null Parent
            // turns "hidden by my container" into "I am the top of the tree and visible" - and
            // UIPieMenu.Update, which calls GlobalPoint every frame, keeps running against a
            // button that is not on the menu.
            var hidden = new UIContainer { Visible = false };
            var child = new TestElement();
            hidden.Add(child);
            Assert.False(child.WillDraw());

            hidden.Remove(child);

            Assert.False(child.WillDraw());
        }

        [Fact]
        public void ARemovedChildStillFindsAnAncestorController()
        {
            // FindController falls off the end of the walk at a null Parent and answers
            // default(T), so every mouse handler on a removed-but-not-yet-re-added element
            // silently stopped finding the screen it lives on.
            var screen = new UIContainer();
            var controller = new Screen();
            screen.Controller = controller;
            var child = new TestElement();
            screen.Add(child);

            screen.Remove(child);

            Assert.Same(controller, child.FindController<Screen>());
        }

        [Fact]
        public void ARemovedChildStillFindsAnAncestorContainer()
        {
            // The walk has to get past the container it was removed from to find the marker
            // above it. A nulled Parent stopped the walk at the child, so this answered nothing.
            var marker = new MarkerPanel();
            var middle = new UIContainer();
            var child = new TestElement();
            marker.Add(middle);
            middle.Add(child);

            middle.Remove(child);

            Assert.Same(marker, child.FindParent<MarkerPanel>());
        }

        [Fact]
        public void ARemovedChildKeepsTheTransformItWasDrawnWith()
        {
            // The one with a visible symptom. Parent's setter recalculates the matrix, so
            // clearing Parent on a remove rebuilt the element's transform against IDENTITY -
            // the element and its hit region jumped to the screen origin for as long as it stayed
            // out of the tree.
            //
            // The root's own CalculateMatrix is called explicitly: an element's matrix is only
            // rebuilt when its Parent is assigned, and a container that is not itself in
            // something yet has nobody to do that for it. In the running UI the screen is
            // attached to the layer first, and the same order applies.
            var outer = new UIContainer { X = 100f, Y = 50f };
            outer.CalculateMatrix();
            var inner = new UIContainer { X = 10f, Y = 5f };
            var child = new TestElement { X = 7f, Y = 3f };
            outer.Add(inner);
            inner.Add(child);

            var before = child.LocalPoint(Vector2.Zero);
            Assert.Equal(new Vector2(117f, 58f), before);

            inner.Remove(child);

            Assert.Equal(before, child.LocalPoint(Vector2.Zero));
            Assert.Equal(Vector2.Zero, child.GlobalPoint(before));
        }

        [Fact]
        public void ReAddingToADifferentContainerMovesTheTransformToTheNewOne()
        {
            // The other half: keeping Parent is only safe if a real re-parent still recalculates.
            var first = new UIContainer { X = 100f, Y = 50f };
            var second = new UIContainer { X = 0f, Y = 0f };
            var child = new TestElement { X = 7f, Y = 3f };
            first.Add(child);
            first.Remove(child);

            second.Add(child);

            Assert.Same(second, child.Parent);
            Assert.Equal(new Vector2(7f, 3f), child.LocalPoint(Vector2.Zero));
        }

        [Fact]
        public void ThePieMenuShapeOfRemoveThenRebuildEndsUpWhereItStarted()
        {
            // UIPieMenu.RenderMenu: remove every button, then add a fresh set. The buttons that
            // are still in the container when the walk happens have to be the ones the
            // container holds, and the removed ones must not have changed where they would draw.
            var pie = new UIContainer { X = 400f, Y = 300f };
            pie.CalculateMatrix();
            var oldButtons = new[] { new TestElement { X = 10f }, new TestElement { X = 20f } };
            foreach (var button in oldButtons) pie.Add(button);

            foreach (var button in oldButtons) pie.Remove(button);
            var newButtons = new[] { new TestElement { X = 30f }, new TestElement { X = 40f }, new TestElement { X = 50f } };
            foreach (var button in newButtons) pie.Add(button);

            Assert.Equal(3, pie.ChildCount);
            foreach (var button in newButtons) Assert.Same(pie, button.Parent);
            Assert.Equal(new Vector2(430f, 300f), newButtons[0].LocalPoint(Vector2.Zero));
        }

        [Fact]
        public void AChildThatWasNeverAddedHasNoParentAndStillDrawsItself()
        {
            // The guard on the other side: null Parent is a legitimate state for a top-level
            // element, and it is not a state to be talked out of. CalculateMatrix is called
            // explicitly because a never-added element is the one case where nothing else has -
            // the Parent setter is what normally does it.
            var loose = new TestElement { X = 5f, Y = 6f };
            loose.CalculateMatrix();

            Assert.Null(loose.Parent);
            Assert.True(loose.WillDraw());
            Assert.Equal(new Vector2(5f, 6f), loose.LocalPoint(Vector2.Zero));
        }

        // ---- the child snapshot is a snapshot, not a fresh allocation --------------------------

        [Fact]
        public void TheChildSnapshotIsTheSameArrayUntilTheListChanges()
        {
            // GetChildrenSafe is called from PreDraw, Draw, Update, GameResized and
            // DeviceReset - four per container per frame. A fresh array on each of those is four
            // allocations per container per frame, on the UI thread, in the middle of the pass
            // that delivers mouse events. Returning the same instance is what a cache looks
            // like; a per-call allocation can never do it.
            var container = new ExposedContainer();
            container.Add(new TestElement());
            container.Add(new TestElement());

            var first = container.Snapshot();
            for (int i = 0; i < 10; i++) Assert.Same(first, container.Snapshot());
        }

        [Fact]
        public void AddingAChildInvalidatesTheSnapshot()
        {
            var container = new ExposedContainer();
            container.Add(new TestElement());
            var before = container.Snapshot();
            var version = container.Version;

            container.Add(new TestElement());

            var after = container.Snapshot();
            Assert.NotSame(before, after);
            Assert.Equal(2, after.Length);
            Assert.True(container.Version > version);
            Assert.Same(after, container.Snapshot());
        }

        [Fact]
        public void AddingTheSameChildTwiceIsOneChildAndStillRefreshesTheSnapshot()
        {
            // Add moves an existing child to the end rather than duplicating it, which is a
            // reordering the snapshot has to see.
            var container = new ExposedContainer();
            var child = new TestElement();
            container.Add(child);
            var before = container.Snapshot();

            container.Add(child);

            Assert.Equal(1, container.ChildCount);
            Assert.NotSame(before, container.Snapshot());
        }

        [Fact]
        public void RemovingAChildInvalidatesTheSnapshot()
        {
            var container = new ExposedContainer();
            var child = new TestElement();
            container.Add(child);
            container.Add(new TestElement());
            var before = container.Snapshot();

            container.Remove(child);

            var after = container.Snapshot();
            Assert.Single(after);
            Assert.Same(after, container.Snapshot());
        }

        [Fact]
        public void InsertingAChildAtAnIndexInvalidatesTheSnapshot()
        {
            var container = new ExposedContainer();
            container.Add(new TestElement());
            container.Add(new TestElement());
            var before = container.Snapshot();

            container.AddAt(0, new TestElement());

            var after = container.Snapshot();
            Assert.Equal(3, after.Length);
            Assert.NotSame(before, after);
        }

        [Fact]
        public void ReorderingInvalidatesTheSnapshot()
        {
            // Neither of the two reorder helpers changes the count, so a cache keyed on the
            // count - the obvious cheap version - would hand back the old order here and the
            // draw order of the whole container would be a frame late.
            var container = new ExposedContainer();
            var first = new TestElement();
            var second = new TestElement();
            container.Add(first);
            container.Add(second);
            var before = container.Snapshot();

            container.SendToBack(second);

            var after = container.Snapshot();
            Assert.NotSame(before, after);
            Assert.Same(second, after[0]);
            Assert.Same(first, after[1]);
        }

        [Fact]
        public void SendingToFrontReordersTheSnapshotToo()
        {
            var container = new ExposedContainer();
            var first = new TestElement();
            var second = new TestElement();
            container.Add(first);
            container.Add(second);

            container.SendToFront(first);

            var after = container.Snapshot();
            Assert.Same(first, after[1]);
            Assert.Same(second, after[0]);
        }

        [Fact]
        public void ARepeatedSnapshotAfterAChangeAllocatesNothingFurther()
        {
            // The literal claim: the four calls per frame do not allocate. Warmed first, because
            // the rebuild is supposed to be the only allocation and it has to have happened.
            var container = new ExposedContainer();
            for (int i = 0; i < 4; i++) container.Add(new TestElement());
            Assert.NotNull(container.Snapshot());

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++) Assert.NotNull(container.Snapshot());
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
        }

        [Fact]
        public void AChildAddedMidWalkIsNotSeenByTheWalkThatIsAlreadyRunning()
        {
            // The bug the snapshot existed to prevent, and the reason the cache cannot be
            // written through in place: mutating a shared array while another call is iterating
            // it is what it was there to stop.
            var container = new ExposedContainer();
            var first = new TestElement();
            container.Add(first);

            var walking = container.Snapshot();
            container.Add(new TestElement());

            Assert.Single(walking);
            Assert.Same(first, walking[0]);
            Assert.Equal(2, container.Snapshot().Length);
        }

        // ---- source-shape guards -------------------------------------------------------------

        [Fact]
        public void TheChildListIsOnlyAllocatedInsideTheSnapshotAccessor()
        {
            // GetChildrenSafe is the one place allowed to make an array, because it is the one
            // place that gives it out. A `new UIElement[` anywhere else in this file is a walk
            // over a live list again, or a second cache with its own invalidation.
            var source = ReadContainer();
            var occurrences = Regex.Matches(StripComments(source), @"new\s+UIElement\s*\[").Count;

            Assert.True(occurrences <= 1,
                "UIContainer.cs allocates a child array " + occurrences
                + " times; only GetChildrenSafe may do it, because only it hands the array out."
                + Environment.NewLine + source);
        }

        [Fact]
        public void EveryMethodThatChangesTheChildListInvalidatesTheSnapshot()
        {
            // A version stamp is only as good as its coverage: a sixth mutator that forgets to
            // bump it serves a stale child list to every draw and update from then on, with no
            // error anywhere. Read the file and check each mutation site is paired with a bump.
            var code = StripComments(ReadContainer());
            foreach (var method in new[] { "Add", "AddAt", "Remove", "SendToBack", "SendToFront" })
            {
                var body = MethodBody(code, method);
                Assert.True(body != null, "could not find " + method + " in UIContainer.cs");
                Assert.True(MutatesChildren(body), method + " does not touch Children");
                Assert.True(body.Contains("InvalidateChildrenSnapshot()"),
                    method + " changes the child list but never calls InvalidateChildrenSnapshot(),"
                    + " so every walk after it would be served the previous list."
                    + Environment.NewLine + body);
            }
        }

        [Fact]
        public void RemoveDoesNotClearTheParentOrInvalidateTheMatrix()
        {
            // Both were added with the null and are the other half of the same regression:
            // clearing Parent truncates the tree the four walkers use, and InvalidateMatrix then
            // recomputes what is left of it - IDENTITY - into the element that is about to be
            // asked where the mouse is.
            var code = StripComments(ReadContainer());
            var remove = MethodBody(code, "Remove");

            Assert.False(remove.Contains("Parent = null"),
                "UIContainer.Remove clears Parent, which is how WillDraw, FindParent,"
                + " FindController and CalculateMatrix get back up the tree."
                + Environment.NewLine + remove);
            Assert.False(remove.Contains("InvalidateMatrix()"),
                "UIContainer.Remove invalidates the matrix of an element that has not changed"
                + " container, so its transform is still correct and rebuilding it is a frame of"
                + " the element drawn against the screen origin."
                + Environment.NewLine + remove);
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static bool MutatesChildren(string body)
        {
            return body.Contains("Children.Add(") || body.Contains("Children.Remove(")
                || body.Contains("Children.Insert(");
        }

        private static string ReadContainer()
        {
            var path = Path.Combine(UiDirectory(), "Framework", "UIContainer.cs");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Could not find " + path
                    + ". These tests read the UI source on purpose, so they cannot pass by"
                    + " finding nothing.", path);
            }
            return File.ReadAllText(path);
        }

        /// <summary>The UI framework's own source, found by walking up out of the test output.</summary>
        private static string UiDirectory()
        {
            var marker = Path.Combine("FreeSO", "TSOClient", "FSO.UI", "FSO.UI.csproj");
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, marker);
                if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not find " + marker + " above " + AppContext.BaseDirectory);
        }

        private static string StripComments(string source)
        {
            var code = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(code, @"//[^\n]*", "");
        }

        /// <summary>
        /// The body of one method: from the brace that opens its declaration to the one that
        /// closes it. Comments are stripped first so a brace inside one cannot end the search
        /// early.
        /// </summary>
        private static string MethodBody(string code, string method)
        {
            var declaration = Regex.Match(code,
                @"(public|protected|private|internal|override|virtual|static|\s)+[^;{}]*\b"
                + Regex.Escape(method) + @"\s*\([^;{}]*\)\s*\{");
            if (!declaration.Success) return null;

            var open = code.IndexOf('{', declaration.Index);
            var depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0) return code.Substring(open + 1, i - open - 1);
            }
            return null;
        }
    }
}
