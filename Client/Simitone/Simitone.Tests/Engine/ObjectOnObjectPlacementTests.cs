using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FSO.LotView.Components;
using FSO.SimAntics;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// "Placing a coffeemaker on a dishwasher does nothing, and only sometimes."
    /// </summary>
    /// <para>
    /// Three separate things have to hold for a placement onto another object's surface, and the
    /// report is the shape all three of them broke at once:
    /// </para>
    /// <list type="number">
    /// <item>the engine has to agree that the surface has a height, and to be able to name it, so
    /// that <c>VMContext.GetObjPlace</c>'s <c>1 &lt;&lt; (GetSlotHeight(0) - 1)</c> is a height
    /// rather than a shift by thirty;</item>
    /// <item>the container has to be able to say no, and to say <em>which</em> rule said no;</item>
    /// <item>everything drawn through the container's world component has to be at the height the
    /// engine thinks it is at, or the player aims at a tile that is not the one they are
    /// pointing at.</item>
    /// </list>
    /// <para>
    /// The third is the one that made it read as intermittent, and it is not a rendering nit. A
    /// coffeemaker already on a dishwasher is drawn at <c>dishwasher.Position + surface offset</c>,
    /// and the dishwasher's <c>Position</c> is an <c>EntityComponent</c> read - so a container
    /// reporting a position with the lot's terrain altitude missing puts its contents, and the
    /// click target the player uses to aim at them, a terrain height away from where the engine
    /// has them. The engine's own <c>VMEntity.Position</c> is unaffected, which is exactly why
    /// nothing in the engine complained and why it only showed up as "only sometimes works": on a
    /// flat lot the altitude is zero and there is nothing to see.
    /// </para>
    /// </summary>
    public sealed class ObjectOnObjectPlacementTests
    {
        // ------------------------------------------------------------------ the slot height rule

        [Fact]
        public void ASlotTheContainerDoesNotHaveHasNoHeight()
        {
            // The bounds refusal, and the only one that answers -1. It is what a negative slot
            // number from a register, or a stale ContainerSlot, must keep producing.
            Assert.Equal(VMGameObject.NoSlotHeight, VMGameObject.ResolveSlotHeight(false, 0, 0));
            Assert.Equal(VMGameObject.NoSlotHeight, VMGameObject.ResolveSlotHeight(false, 3, 3));
        }

        [Fact]
        public void ASlotTheContainerDescribesAnswersWithItsOwnHeight()
        {
            // A counter surface (height 3) and an end table (height 8) are different numbers, and
            // the object being placed is matched against its own allowed-heights mask on exactly
            // this answer.
            Assert.Equal(3, VMGameObject.ResolveSlotHeight(true, 3, 3));
            Assert.Equal(8, VMGameObject.ResolveSlotHeight(true, 8, 3));
        }

        [Fact]
        public void AContainerWithAnUndescribedSlotStillAnswersWithItsSurfaceHeight()
        {
            // The regression. The container *has* the slot - VMContext.GetObjPlace got there
            // through `TotalSlots() > 0`, which is the container having slot 0 - so answering
            // NoSlotHeight is not "no height": the caller shifts by `height - 1`, C# masks the
            // count, and `1 << (-1 - 1)` becomes `1 << 30`. Intersected with a 16-bit
            // allowed-heights mask that is always zero, so the placement is refused with
            // HeightNotAllowed by a rule that never ran.
            //
            // The height the engine has always meant by "the surface of this object" is slot 0's,
            // and giving that back is what restores stacking without giving up the bounds check.
            Assert.Equal(3, VMGameObject.ResolveSlotHeight(true, 0, 3));
        }

        [Fact]
        public void AContainerThatDescribesNothingAtAllStillRefusesToGuess()
        {
            // No SLOT chunk, or a chunk with nothing in it. There is no honest height here, and
            // the answer has to stay the refusal - this is the one case where -1 is the truth.
            Assert.Equal(VMGameObject.NoSlotHeight, VMGameObject.ResolveSlotHeight(true, 0, 0));
        }

        [Fact]
        public void TheRefusalValueIsTheOneTheCallerCannotReadAsAHeight()
        {
            // Pinned rather than assumed. The whole failure mode is that the caller shifts by
            // `height - 1` and intersects with a 16-bit mask, so the sentinel has to be a number
            // that can only ever fail that test. This is the arithmetic, written out, so a change
            // to the sentinel cannot quietly start matching a real height.
            const short allowedCounterHeight = 1 << 2;   // bit 2 == height 3
            const short allowedEndTable = 1 << 7;        // bit 7 == height 8
            const int everyHeightTheFlagCanName = 0x7FFF;

            foreach (var allowed in new[] { allowedCounterHeight, allowedEndTable, everyHeightTheFlagCanName })
            {
                Assert.Equal(0, (1 << (VMGameObject.NoSlotHeight - 1)) & allowed);
            }
        }

        // ------------------------------------------------------------------ the refusal rules

        [Fact]
        public void AnEmptySlotOfACounterAcceptsThePlacement()
        {
            Assert.Equal(VMGameObject.VMSlotPlacementRefusal.None,
                VMGameObject.ClassifySlotPlacement(true, false, false, false));
        }

        [Fact]
        public void AnOccupiedSurfaceIsRefusedAsOccupiedAndNotAsAnythingElse()
        {
            // "The surface is taken" is the game working. It has to be distinguishable from a
            // dangling reference and from a containment cycle, because only one of those three is
            // a defect and a player report that cannot tell them apart is a report nobody can act
            // on.
            Assert.Equal(VMGameObject.VMSlotPlacementRefusal.AlreadyOccupied,
                VMGameObject.ClassifySlotPlacement(true, true, false, false));
        }

        [Fact]
        public void ARemovedObjectAndACycleAreRefusedForTheirOwnReasons()
        {
            Assert.Equal(VMGameObject.VMSlotPlacementRefusal.ObjectDead,
                VMGameObject.ClassifySlotPlacement(true, false, true, false));
            Assert.Equal(VMGameObject.VMSlotPlacementRefusal.WouldLoopSlot,
                VMGameObject.ClassifySlotPlacement(true, false, false, true));
        }

        [Fact]
        public void ASlotTheContainerDoesNotHaveIsRefusedBeforeTheObjectIsEvenAskedAbout()
        {
            // There is nowhere to put it, so nothing about the object can change the answer -
            // including the fact that it is dead, which belongs to the rule that names it.
            Assert.Equal(VMGameObject.VMSlotPlacementRefusal.NoSuchSlot,
                VMGameObject.ClassifySlotPlacement(false, false, true, true));
        }

        [Fact]
        public void EveryRefusalHasItsOwnNameAndNoneOfThemIsTheDefault()
        {
            var reasons = new[]
            {
                VMGameObject.VMSlotPlacementRefusal.NoSuchSlot,
                VMGameObject.VMSlotPlacementRefusal.AlreadyOccupied,
                VMGameObject.VMSlotPlacementRefusal.ObjectDead,
                VMGameObject.VMSlotPlacementRefusal.WouldLoopSlot,
                VMGameObject.VMSlotPlacementRefusal.GhostImageNotSlotable,
            };

            Assert.Equal(5, reasons.Distinct().Count());

            // The enum is a wire the log line is written from, so every member has to print as
            // something a reader can act on rather than as a number.
            foreach (var reason in reasons)
            {
                Assert.NotEqual(VMGameObject.VMSlotPlacementRefusal.None, reason);
                Assert.False(string.IsNullOrWhiteSpace(reason.ToString()));
            }
        }

        [Fact]
        public void AGhostPreviewIsReportedAsNotSlotableRatherThanRefused()
        {
            // A ghost is drawn on a real object's surface, and PlaceInSlot has to keep answering
            // true so the placement check driving the preview keeps running. It is a reason worth
            // naming anyway: it is the one case where a slot is left unfilled on a path that
            // reported success.
            Assert.NotEqual(VMGameObject.VMSlotPlacementRefusal.None,
                VMGameObject.VMSlotPlacementRefusal.GhostImageNotSlotable);
            Assert.NotEqual(VMGameObject.VMSlotPlacementRefusal.AlreadyOccupied,
                VMGameObject.VMSlotPlacementRefusal.GhostImageNotSlotable);
        }

        // ------------------------------------------------------------------ what the log says

        [Fact]
        public void AnEmptyTileIsReportedAsNothingRatherThanAsAFailedRefusal()
        {
            Assert.Equal("nothing on this tile", UIObjectHolder.DescribePlacementSubject(-1, -1));
        }

        [Fact]
        public void ASurfaceIsReportedWithItsSlotCountAndHeight()
        {
            // The whole value of the line: "a container with 1 slot(s), surface height 3" is the
            // difference between the height rule refusing it and there being no dishwasher there.
            var line = UIObjectHolder.DescribePlacementSubject(1, 3);
            Assert.Contains("container", line);
            Assert.Contains("1 slot", line);
            Assert.Contains("3", line);
        }

        [Fact]
        public void ACounterWhoseHeightCannotBeReadSaysSoRatherThanSayingNothing()
        {
            // A container with a slot and an unreadable height is the state the GetSlotHeight
            // fallback exists to prevent, and the line has to name it if it ever happens anyway.
            var line = UIObjectHolder.DescribePlacementSubject(1, VMGameObject.NoSlotHeight);
            Assert.Contains("unknown", line);
        }

        [Fact]
        public void ASolidObjectIsNotReportedAsAContainer()
        {
            // A chair has no slots. Calling it a container would send the next report looking at
            // the slot rules instead of the placement flags.
            var line = UIObjectHolder.DescribePlacementSubject(0, VMGameObject.NoSlotHeight);
            Assert.Contains("no containment slots", line);
            Assert.DoesNotContain("container", line);
        }

        // ------------------------------------------------------------------ the undo's half
        //
        // A placement onto a surface has to be undoable too, and the part of that which is pure is
        // the record. It is addressed by the tile it was put down on rather than by the object id
        // it was given, because an object that has been moved in and out of a container's slot -
        // and an object that an undo/redo cycle has replaced outright - does not keep its id, and
        // a command sent to a stale id is a silent no-op that leaves the family a placement short.

        [Fact]
        public void APlacementOntoASurfaceIsUndoneByTheTileItLandedOn()
        {
            // Tile (3, 4, 1) in 1/16ths, which is the dishwasher's own tile: a coffeemaker put on
            // its surface shares the base tile, because the engine puts it in the slot rather than
            // on a tile of its own.
            var entry = new BuildUndoEntry
            {
                Kind = BuildUndoKind.Place,
                Guid = 0x2000u,
                X = 48,
                Y = 64,
                Level = 1,
                Dir = 0,
                Value = 300,
                ObjectId = 0,          // the engine has not made it yet
                Settled = true,        // ...and by the time the record is pushed it has
                Exact = true
            };

            Assert.Equal(3, entry.TilePos.TileX);
            Assert.Equal(4, entry.TilePos.TileY);
            Assert.True(entry.IsActionable);

            // The id is diagnostic, and the tile is what undo acts on - so a record whose object
            // id has gone stale is still actionable rather than silently doing nothing.
            var stack = new BuildUndoStack();
            Assert.True(stack.Push(entry));

            var undone = stack.Undo();
            Assert.Same(entry, undone);
            Assert.Equal(48, undone.TilePos.x);
            Assert.Equal(64, undone.TilePos.y);

            Assert.Same(entry, stack.Redo());
        }

        [Fact]
        public void AMoveOfAnObjectThatIsSittingOnSomethingIsRecordedWithWhereItCameFrom()
        {
            // The same shape for the other kind of interaction with a container: picking a
            // coffeemaker off a dishwasher is a Move, and the tile it came from is the tile it was
            // on - not a tile of its own, and not a null.
            var entry = new BuildUndoEntry
            {
                Kind = BuildUndoKind.Move,
                Guid = 0x2000u,
                ObjectId = 42,
                X = 96, Y = 32, Level = 1, Dir = 0,
                FromX = 48, FromY = 64, FromLevel = 1, FromDir = 0,
                Settled = true,
                Exact = true
            };

            Assert.Equal(3, entry.FromTilePos.TileX);
            Assert.Equal(4, entry.FromTilePos.TileY);
            Assert.True(entry.IsActionable);
        }

        [Fact]
        public void APlacementWhoseObjectNeverTurnedUpIsNotUndoable()
        {
            // The settle step drops a record the engine never acted on, and a stack that accepted
            // one would offer the player an undo that removes nothing and refunds nothing.
            var entry = new BuildUndoEntry
            {
                Kind = BuildUndoKind.Place,
                Guid = 0x2000u,
                X = 48, Y = 64, Level = 1,
                Value = 300,
                Settled = false,
                Exact = false
            };

            Assert.False(entry.IsActionable);
            Assert.False(new BuildUndoStack().Push(entry));
        }

        // ------------------------------------------------------------------ the wiring
        //
        // The rules above are pure, and the tree above is the bug. Neither of them is the whole
        // of it: a correct rule nobody consults is the same defect as no rule at all. These read
        // the sources, which is the only thing a test process can do here - it has no content, no
        // graphics device and no lot.

        [Fact]
        public void TheSlotHeightGoesThroughTheRuleAndOnlyAfterTheSlotIsKnownToExist()
        {
            var height = MethodBody(GameObject(), "public override int GetSlotHeight(int slot)");

            Assert.Contains("ResolveSlotHeight", height);
            Assert.True(height.IndexOf("IsValidIndex(Contained, slot)", StringComparison.Ordinal)
                        < height.IndexOf("ResolveSlotHeight", StringComparison.Ordinal),
                "GetSlotHeight answers a height before it has established that the slot exists, so"
                + " the fallback would answer for a slot number that is out of range.");
        }

        [Fact]
        public void TheHeightOfASlotIsNotReadOutOfTheDictionaryByAnUncheckedIndex()
        {
            // `Slots.Slots[slot]` throws for a slot the chunk does not describe, and
            // `Slots.Slots[0][0]` answers slot 0's height for every slot asked about. The
            // description has to come from a lookup that can say "not described".
            var described = MethodBody(GameObject(), "private int DescribedSlotHeight(int slot)");
            Assert.Contains("TryGetValue((ushort)slot", described);
            Assert.DoesNotContain("Slots.Slots[slot]", described);
            Assert.DoesNotContain("Slots.Slots[0][0]", described);
        }

        [Fact]
        public void ARefusedPlacementIsReportedWithItsReasonRatherThanABareFalse()
        {
            var place = MethodBody(GameObject(), "public override bool PlaceInSlot(VMEntity obj, int slot, bool cleanOld, VMContext context)");

            Assert.Contains("ClassifySlotPlacement", place);
            Assert.Contains("ContentLog.Info", place);
            Assert.DoesNotContain("|| WillLoopSlot(obj) || obj.Dead) return false",
                "the refusals are back to one opaque branch, so the next report cannot name a rule.");
        }

        [Fact]
        public void AGhostPreviewIsLoggedWithoutBeingRefused()
        {
            // The buy-mode ghost has to keep drawing on a real object's surface, so the report goes
            // in the else of the mismatch and PlaceInSlot still answers true.
            var place = MethodBody(GameObject(), "public override bool PlaceInSlot(VMEntity obj, int slot, bool cleanOld, VMContext context)");
            var reported = place.IndexOf("ReportGhostNotSlotable(obj, slot)", StringComparison.Ordinal);
            var answered = place.LastIndexOf("return true", StringComparison.Ordinal);

            Assert.True(reported > 0, "the ghost/lot mismatch is no longer reported at all.");
            Assert.True(reported < answered,
                "the ghost mismatch is reported after PlaceInSlot has already answered, or the"
                + " preview has been turned into a refusal - which would break the buy-mode ghost"
                + " entirely, since it is what the placement check runs on.");

            // Once per ghost per container, not once per frame: a buy-mode preview re-runs the
            // whole placement every frame the cursor is over the object.
            var throttle = MethodBody(GameObject(), "private void ReportGhostNotSlotable(VMEntity obj, int slot)");
            Assert.Contains("LastGhostNotSloted", throttle);
            Assert.Contains("ContentLog.Info", throttle);
        }

        [Fact]
        public void WritingTheRawPositionDoesNotRaiseTheUnaltedDrawFlag()
        {
            // THE regression. The flag used to be a side effect of this setter, so the borrow
            // restore - `UnmoddedDraw = old; UnmoddedPosition = oldPos;` - re-armed the flag the
            // restore had just cleared, and every object the player had ever looked at went on
            // reporting a position with the lot's terrain altitude missing. With a container that
            // is every object standing on it, and the click target the player aims with.
            var setter = Window(Entity(), "public Vector3 UnmoddedPosition", "public void SetUnmoddedPosition");

            Assert.DoesNotContain("UnmoddedDraw = true", setter);
        }

        [Fact]
        public void EnteringAThumbnailBorrowIsAnExplicitCall()
        {
            // And the other half: something has to raise the flag, or a catalogue thumbnail is
            // drawn at a catalogue position with the lot's terrain altitude sampled underneath it.
            var enter = MethodBody(Entity(), "public void SetUnmoddedPosition(Vector3 raw)");
            Assert.Contains("_UnmoddedDraw = true", enter);

            var caller = MethodBody(Platform(), "public Texture2D GetObjectThumb(ObjectComponent[] objects, Vector3[] positions, GraphicsDevice gd, WorldState state)");
            Assert.Contains("SetUnmoddedPosition(tilePosition)", caller);
        }

        [Fact]
        public void TheBorrowIsReturnedWithTheFlagFirstAndThePositionSecond()
        {
            // Only correct in this order, now that the position write no longer touches the flag -
            // and it is the order that was already there, so the fix is the setter and not this.
            var borrow = MethodBody(Platform(), "public Texture2D GetObjectThumb(ObjectComponent[] objects, Vector3[] positions, GraphicsDevice gd, WorldState state)");

            var flag = borrow.IndexOf("obj.UnmoddedDraw = oldUnmodded;", StringComparison.Ordinal);
            var pos = borrow.IndexOf("obj.UnmoddedPosition = oldObjPos;", StringComparison.Ordinal);
            Assert.True(flag > 0 && pos > flag, "the thumbnail borrow is not returned flag-then-position.");
        }

        [Fact]
        public void TheFlagPushesTheReportedPositionDownWhenItChanges()
        {
            // The draw group caches the position as a plain field, and every read of this
            // component's reported position goes through it - so a flag that moves the position
            // without pushing it is a frame late, which on a moving camera is a frame of the object
            // and everything on it drawn at the old height.
            var flag = MethodBody(Entity(), "public bool UnmoddedDraw");
            Assert.Contains("OnPositionChanged()", flag);
        }

        [Fact]
        public void TheAltitudeIsReadThroughTheFlagAndNotFoldedIntoTheStoredPosition()
        {
            // The altitude has to live in the getter. A setter that folds it in is the original
            // defect (R-31): assigning Position twice put the terrain height in twice, so a move
            // or a re-seat left the object floating - and a contained object's position is
            // re-assigned every time it is dropped somewhere.
            var setter = Window(Entity(), "public override Vector3 Position", "private float AltitudeAt(Vector3 raw)");
            Assert.DoesNotContain("_Position.Z +=", setter);

            var altitude = MethodBody(Entity(), "private float AltitudeAt(Vector3 raw)");
            Assert.Contains("_UnmoddedDraw", altitude);
        }

        [Fact]
        public void ASurfaceOffsetIsNeverReadOffASlotTheComponentDoesNotHave()
        {
            // `ContainerSlots[-1]` is reachable: ClearSlot sets a cleared contained object's slot
            // to -1 and a contained component then asks its container for that slot's position. The
            // bounds test has to be a lower bound, not just a Count.
            //
            // The check reads the slot, so it lives in the accessor that does - GetSLOTOffset, the
            // authored local offset - and GetSLOTPosition is that offset joined to the component's
            // own position. The guard follows the code, it is not weakened.
            var slotOffset = MethodBody(Object(), "public override Vector3 GetSLOTOffset(int slot, bool avatar)");
            Assert.Contains("slot > -1", slotOffset);
        }

        [Fact]
        public void ARefusedPlacementIsReportedFromTheClientSideToo()
        {
            // The engine names the container and the reason; the client names what the cursor was
            // over. Without the second, "it did nothing" is still all the report says.
            var holder = Source(ClientSource("Panels", "UIObjectHolder.cs"));

            Assert.Contains("LogPlacementRefusal", holder);
            Assert.Contains("DescribePlacementSubject", holder);
            Assert.Contains("FSO.Content.Utils.ContentLog", holder);
        }

        [Fact]
        public void ARefusalLineIsNotWrittenOncePerFrame()
        {
            // The cursor does not move while a player is deciding whether a placement is legal, and
            // a line per frame would bury every other line in the file.
            var holder = Source(ClientSource("Panels", "UIObjectHolder.cs"));
            var method = MethodBody(holder, "private void LogPlacementRefusal(VMPlacementError status, Vector2 bigTilePos)");

            Assert.Contains("LastPlacementRefusal", method);
        }

        // ------------------------------------------------------------------ helpers

        private static string Object() => Source(FreeSource("tso.world", "Components", "ObjectComponent.cs"));
        private static string Entity() => Source(FreeSource("tso.world", "Components", "EntityComponent.cs"));
        private static string GameObject() => Source(FreeSource("tso.simantics", "Entities", "VMGameObject.cs"));
        private static string Platform() => Source(FreeSource("tso.world", "Platform", "WorldPlatform3D.cs"));

        private static string ClientSource(params string[] parts)
        {
            return Path.Combine(new[] { ClientDir(), "Simitone.Client", "UI" }.Concat(parts).ToArray());
        }
        private static string FreeSource(params string[] parts)
        {
            return Path.Combine(new[] { FreeSoDir(), "FreeSO", "TSOClient" }.Concat(parts).ToArray());
        }

        private static string Source(string path)
        {
            Assert.True(File.Exists(path), "could not find " + path);
            var text = File.ReadAllText(path);
            var withoutBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
        }

        private static string FreeSoDir()
        {
            var dir = new DirectoryInfo(ClientDir());
            for (var i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO", "TSOClient"))) return dir.FullName;
            }
            throw new DirectoryNotFoundException("Could not find FreeSO/TSOClient above " + ClientDir());
        }

        private static string ClientDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows", "Simitone.Windows.csproj"))) return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("Could not find Simitone.Windows.csproj above " + AppContext.BaseDirectory);
        }

        /// <summary>A C# method body, located by its opening anchor.</summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "no '" + signature + "' in the source under test");

            var open = source.IndexOf('{', at);
            Assert.True(open >= 0, "'" + signature + "' has no body");

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException("unbalanced braces after '" + signature + "'");
        }

        /// <summary>The text between two markers, exclusive.</summary>
        private static string Window(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "no '" + from + "' in the source under test");
            start += from.Length;

            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end >= 0, "no '" + to + "' after '" + from + "'");

            return source.Substring(start, end - start);
        }
    }
}
