using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for how an object standing in another object's containment slot is placed, and for
    /// the rule that keeps the terrain altitude out of the slot offset.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reported failure was that a contained object is reported one terrain height away from
    /// the thing it is standing on, for the length of a snap interpolation - so its sprite
    /// vertices and the hit box derived from the same reported position stop agreeing and the
    /// click lands on the counter instead of on the thing on the counter.
    /// </para>
    /// <para>
    /// The arithmetic itself is now <see cref="SlotOffset"/>, which is where it can be pinned
    /// without a lot, a blueprint or a graphics device. What is worth being precise about is that
    /// the old form - <c>Container.Position + (GetSLOTPosition - Container.Position)</c> -
    /// produced the <i>same number</i> whenever the container answered with a plain offset, which
    /// it did. The altitude cancelled because both operands of the subtraction came from the same
    /// expression evaluated at the same instant. So the defect it fixes is not an arithmetic one;
    /// it is that nothing in the code said the altitude was supposed to cancel, the cancellation
    /// depended on <c>GetSLOTPosition</c> being defined as <c>Position + offset</c>, and the old
    /// form read the container's position <i>twice</i> to do it. The tests below therefore assert
    /// the property as a property - the container's altitude is added once, so moving the
    /// container moves a contained object by the same amount - and guard the source shape that
    /// makes it true.
    /// </para>
    /// </remarks>
    public sealed class SlotOffsetArithmeticTests
    {
        // A counter on a sloped lot, and a cooked dinner standing on it. The counter's reported
        // position is tile units plus the terrain altitude under it; the slot offset is what the
        // SLOT chunk authored, in tile units, with no altitude in it.
        private static readonly Vector3 FlatCounter = new Vector3(20f, 30f, 0f);
        private static readonly Vector3 SlopedCounter = new Vector3(20f, 30f, 7.5f);
        private static readonly Vector3 SurfaceSlot = new Vector3(0.5f, -0.25f, 1.2f);
        private static readonly Vector3 DeeperSlot = new Vector3(0.5f, -0.25f, 0.4f);

        // ---- the altitude is added once --------------------------------------------------------

        [Fact]
        public void AContainedObjectIsItsContainerPlusTheAuthoredSlotOffset()
        {
            var placed = SlotOffset.FromLocal(FlatCounter, SurfaceSlot);

            Assert.Equal(FlatCounter + SurfaceSlot, placed);
        }

        [Fact]
        public void TheTerrainAltitudeEndsUpInTheResultExactlyOnce()
        {
            foreach (var altitude in new[] { -3.25f, 0f, 0.5f, 7.5f, 22f })
            {
                var counter = new Vector3(20f, 30f, altitude);
                var placed = SlotOffset.FromLocal(counter, SurfaceSlot);

                Assert.Equal(altitude, placed.Z - SurfaceSlot.Z, 4);
            }
        }

        [Fact]
        public void MovingTheContainerMovesEverythingOnItByTheSameAmount()
        {
            // The property stated as a difference, so it does not care what the slot offset's own
            // Z happens to be. If the altitude were being counted twice - once by the container
            // and once because the offset had absorbed it - this would move by twice the amount.
            var shift = new Vector3(-14f, 9f, 7.5f);

            for (float pct = 1f; pct > 0f; pct -= 0.05f)
            {
                var here = SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, DeeperSlot, pct);
                var moved = SlotOffset.Interpolate(SlopedCounter + shift, SurfaceSlot, DeeperSlot, pct);

                AssertClose(here + shift, moved);
            }
        }

        [Fact]
        public void AnOffsetThatHasAbsorbedTheAltitudeWouldNotHaveThatProperty()
        {
            // The guard on the test above: it must not be passing because the shift is ignored.
            // This is the composition the old shape invites once the offset is derived from two
            // positions that each carry the altitude, and it is off by exactly the altitude.
            var counter = new Vector3(20f, 30f, 7.5f);
            var leaked = SurfaceSlot + new Vector3(0f, 0f, counter.Z);

            var here = SlotOffset.Interpolate(counter, SurfaceSlot, DeeperSlot, 0.5f);
            var leakedResult = SlotOffset.Interpolate(counter, leaked, leaked, 0.5f);

            Assert.NotEqual(here, leakedResult);
        }

        [Fact]
        public void BothEndsOfTheInterpolationCarryTheSameOneAltitude()
        {
            // Stated per frame, because a lerp of two wrong values is still wrong in the middle
            // and the middle is where the object is most of the time it is moving.
            foreach (var pct in new[] { 1f, 0.75f, 0.5f, 0.25f, 0.01f })
            {
                var at = SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, DeeperSlot, pct);
                var expectedZ = SlopedCounter.Z + (DeeperSlot.Z * pct) + (SurfaceSlot.Z * (1 - pct));

                Assert.Equal(expectedZ, at.Z, 4);
            }
        }

        [Fact]
        public void ThePositionIsTheSameBeforeAndAfterTheInterpolationWindow()
        {
            // "Exactly one altitude, before, during and after": the settled position is the
            // composition, and the window has to end on it rather than near it.
            var before = SlotOffset.FromLocal(SlopedCounter, DeeperSlot);
            var after = SlotOffset.FromLocal(SlopedCounter, SurfaceSlot);

            AssertClose(before, SlotOffset.Interpolate(SlopedCounter, DeeperSlot, SurfaceSlot, 0f));
            AssertClose(after, SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, DeeperSlot, 0f));
        }

        // ---- the shape of the interpolation ------------------------------------------------------

        [Fact]
        public void AFullyStartedInterpolationIsThePreviousPositionExactly()
        {
            // PrepareSlotInterpolation sets the percentage to 1 and the draw loop counts it down
            // to 0, so 1 is the frame the object is still where it was.
            AssertClose(SlotOffset.FromLocal(SlopedCounter, DeeperSlot),
                SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, DeeperSlot, 1f));
        }

        [Fact]
        public void AFinishedInterpolationIsTheSettledPositionAndNotAnExtrapolation()
        {
            // The loop runs past 0 - it stops at -3 - so a negative percentage has to clamp
            // rather than carry on along the same line.
            var settled = SlotOffset.FromLocal(SlopedCounter, SurfaceSlot);

            AssertClose(settled, SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, DeeperSlot, 0f));
            AssertClose(settled, SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, DeeperSlot, -3f));
        }

        [Fact]
        public void AMoveInsideTheThresholdIsInterpolated()
        {
            // Halfway between the two offsets, on top of the container. Interpolating a stored
            // offset gives the same answer as interpolating the two world positions it is added
            // to, and the two only agree because the offset is added once - which is the
            // property this whole class is about.
            var half = SlopedCounter + (SurfaceSlot + DeeperSlot) * 0.5f;

            AssertClose(half, SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, DeeperSlot, 0.5f));
        }

        [Fact]
        public void AMoveBeyondTheThresholdIsNotInterpolated()
        {
            // The old guard: a gap over a tile and a half is not a move the object made, it is a
            // re-slot or a re-seat, and sliding there reads as the object flying across the room.
            // Both endpoints collapse onto the settled position.
            var settled = SlotOffset.FromLocal(SlopedCounter, SurfaceSlot);
            var far = new Vector3(SurfaceSlot.X + 4f, SurfaceSlot.Y, SurfaceSlot.Z);

            AssertClose(settled, SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, far, 1f));
            AssertClose(settled, SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, far, 0.5f));
        }

        [Fact]
        public void AMoveExactlyAtTheThresholdIsStillInterpolated()
        {
            // The guard is `>`, not `>=`, so a gap of exactly the threshold still slides. Asserting
            // the boundary is what stops it being tightened to `>=` later and popping an object
            // the last frame of a slide that happened to be that long. Built by subtraction so
            // the gap is the threshold and not a tenth of it more.
            var threshold = SurfaceSlot - new Vector3(0f, 0f, SlotOffset.MaxInterpolationDistance);
            var half = SlopedCounter + (SurfaceSlot + threshold) * 0.5f;
            var gap = SlotOffset.FromLocal(SlopedCounter, SurfaceSlot)
                - SlotOffset.FromLocal(SlopedCounter, threshold);

            Assert.Equal(SlotOffset.MaxInterpolationDistance, gap.Length(), 4);
            AssertClose(half, SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, threshold, 0.5f));
        }

        [Fact]
        public void TheThresholdIsTheOneTheInterpolationHasAlwaysUsed()
        {
            // 1.5 tiles. Changing it changes how long a slide is, which is a design decision and
            // not something a refactor gets to do quietly.
            Assert.Equal(1.5f, SlotOffset.MaxInterpolationDistance);
        }

        [Fact]
        public void AnInterpolationBetweenTheSameOffsetIsThatOffset()
        {
            var at = SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, SurfaceSlot, 0.5f);

            AssertClose(SlotOffset.FromLocal(SlopedCounter, SurfaceSlot), at);
        }

        [Fact]
        public void TheInterpolationIsMonotonicBetweenTheTwoEnds()
        {
            // Whatever the percentage, the answer is on the segment between the old and the new
            // position. A composition that leaked the altitude would leave that segment, because
            // one end would be in a different frame from the other.
            var old = SlotOffset.FromLocal(SlopedCounter, DeeperSlot);
            var now = SlotOffset.FromLocal(SlopedCounter, SurfaceSlot);

            for (float pct = 0f; pct <= 1f; pct += 0.05f)
            {
                var at = SlotOffset.Interpolate(SlopedCounter, SurfaceSlot, DeeperSlot, pct);
                var axis = Vector3.Dot(at - old, now - old) / Vector3.Dot(now - old, now - old);

                Assert.InRange(axis, -0.001f, 1.001f);
            }
        }

        // ---- source-shape guards ----------------------------------------------------------------

        [Fact]
        public void ThePreviousSlotOffsetIsNeverDerivedBySubtractingTwoWorldPositions()
        {
            // The root cause. That subtraction takes two values which each carry the terrain
            // altitude, and the altitude only cancels because GetSLOTPosition happens to be
            // defined as Position + offset and both are read at the same instant. The offset has
            // to come from the container's own SLOT chunk, which is a local quantity by
            // construction.
            var body = MethodBody(Read("Components", "EntityComponent.cs"), "PrepareSlotInterpolation");

            Assert.True(body != null, "could not find PrepareSlotInterpolation in EntityComponent.cs");
            Assert.True(body.Contains("GetSLOTOffset("),
                "PrepareSlotInterpolation should capture the container's authored slot offset."
                + Environment.NewLine + body);
            Assert.False(body.Contains("GetSLOTPosition("),
                "PrepareSlotInterpolation still asks for a world position and would be taking a"
                + " difference of two altitude-carrying values."
                + Environment.NewLine + body);
        }

        [Fact]
        public void TheSnapPreviousPositionIsStoredRawAndNotWithTheAltitudeFoldedIn()
        {
            // The other half of the raw/alted split. If this ever stores an alted position then
            // the non-container branch of the Position getter would add the altitude to it a
            // second time, which is the same defect one path further along.
            var prepare = MethodBody(Read("Components", "EntityComponent.cs"), "PrepareSlotInterpolation");
            var snap = MethodBody(Read("Components", "EntityComponent.cs"), "PrepareSnapInterpolation");

            Assert.True(prepare.Contains("SnapSelfPrevious = _Position;"), prepare);
            Assert.True(snap.Contains("SnapSelfPrevious = _Position;"), snap);
            Assert.True(snap.Contains("PreviousSlotOffset = null;"),
                "a snap clears the slot offset so a contained object is drawn at its slot rather"
                + " than interpolated from an offset belonging to a container it has left." + snap);
        }

        [Fact]
        public void TheInterpolatedPositionIsBuiltFromOneSampleOfTheContainer()
        {
            // The old form read Container.Position for the old end and Container.GetSLOTPosition
            // for the new one - two reads of a getter that samples the terrain and, while the
            // container is itself interpolating, answers differently depending on when it is
            // asked. Both ends now come from one read and one expression.
            var code = StripComments(Read("Components", "EntityComponent.cs"));
            var getter = PropertyBody(code, "Position");

            Assert.True(getter.Contains("SlotOffset.Interpolate("),
                "the contained-object branch of the Position getter should go through"
                + " SlotOffset.Interpolate, which samples the container once." + Environment.NewLine + getter);
            Assert.True(getter.Contains("Container.GetSLOTOffset("), getter);
            Assert.False(getter.Contains("PreviousSlotOffset.Value;"),
                "the stored offset should be read as an argument, not added to a separately"
                + " sampled container position." + Environment.NewLine + getter);
        }

        [Fact]
        public void EveryContainerJoinsItsOffsetToItsPositionInTheSameOnePlace()
        {
            // ObjectComponent and AvatarComponent both answer GetSLOTPosition, and both used to
            // build it as their own expression. That is two places to get the altitude convention
            // wrong and to keep in agreement, so they now both go through GetSLOTPositionFrom.
            foreach (var file in new[] { "ObjectComponent.cs", "AvatarComponent.cs" })
            {
                var code = StripComments(Read("Components", file));
                var slot = MethodBody(code, "GetSLOTPosition");

                Assert.True(slot != null, "could not find GetSLOTPosition in " + file);
                Assert.True(slot.Contains("GetSLOTPositionFrom(GetSLOTOffset("),
                    file + " builds a slot position without going through the one place a local"
                    + " offset and a container position are joined." + Environment.NewLine + slot);
            }
        }

        [Fact]
        public void TheAuthoredSlotOffsetHasNoAltitudeInIt()
        {
            // The offset is what the SLOT chunk says, in the chunk's own units. Anything that
            // reaches for a reported position inside it has put the container's altitude into a
            // value that is about to be added to a position that already has it.
            var code = StripComments(Read("Components", "ObjectComponent.cs"));
            var offset = MethodBody(code, "GetSLOTOffset");

            Assert.True(offset != null, "could not find GetSLOTOffset in ObjectComponent.cs");
            Assert.True(offset.Contains("centerRelative"),
                "the slot offset should be the authored centre-relative vector." + Environment.NewLine + offset);
            Assert.False(Regex.IsMatch(offset, @"\bthis\.Position\b|\bPosition\s*\+"),
                "GetSLOTOffset must not read a reported position: the altitude belongs to"
                + " Position and is added once, by GetSLOTPositionFrom."
                + Environment.NewLine + offset);
        }

        [Fact]
        public void TheJoinItselfAddsTheContainerAltitudeAndNothingElse()
        {
            // One expression, and it is the only one. If the altitude were applied here as well
            // as in the container's own Position, every contained object on the lot would be a
            // terrain height high.
            var body = MethodBody(Read("Components", "EntityComponent.cs"), "GetSLOTPositionFrom");

            Assert.True(body != null, "could not find GetSLOTPositionFrom in EntityComponent.cs");
            Assert.True(body.Contains("SlotOffset.FromLocal(Position, localOffset)"),
                "the join should be the one the rest of the lot view uses."
                + Environment.NewLine + body);
            Assert.False(body.Contains("AltitudeAt(") || body.Contains("WithAltitude("),
                "the join must not add the altitude itself; it arrives with the container's"
                + " Position." + Environment.NewLine + body);
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>
        /// Compares two positions to within a rounding error rather than bit for bit. The
        /// arithmetic here is a lerp, so the exact float a frame produces is a function of the
        /// order the additions happen in; what is under test is the property, and a property that
        /// could only be stated exactly would be one about rounding rather than about altitude.
        /// </summary>
        private static void AssertClose(Vector3 expected, Vector3 actual, int places = 4)
        {
            Assert.True(Math.Abs(expected.X - actual.X) < 0.0005f
                        && Math.Abs(expected.Y - actual.Y) < 0.0005f
                        && Math.Abs(expected.Z - actual.Z) < 0.0005f,
                "expected " + expected + " but got " + actual);
        }

        private static string Read(params string[] relativePath)
        {
            var path = Path.Combine(new[] { LotViewDirectory() }.Concat(relativePath).ToArray());
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Could not find " + path
                    + ". These tests read the lot view source on purpose, so they cannot pass by"
                    + " finding nothing.", path);
            }
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The lot view's own source, found by walking up out of the test output rather than
        /// counting levels.
        /// </summary>
        private static string LotViewDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var nested = Path.Combine(dir.FullName, "FreeSO", "TSOClient", "tso.world");
                if (File.Exists(Path.Combine(nested, "FSO.LotView.csproj"))) return nested;

                var flat = Path.Combine(dir.FullName, "TSOClient", "tso.world");
                if (File.Exists(Path.Combine(flat, "FSO.LotView.csproj"))) return flat;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not find FreeSO/TSOClient/tso.world/FSO.LotView.csproj above "
                + AppContext.BaseDirectory);
        }

        private static string StripComments(string source)
        {
            var code = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(code, @"//[^\n]*", "");
        }

        private static string MethodBody(string code, string method)
        {
            var declaration = Regex.Match(code,
                @"(public|protected|private|internal|override|virtual|static|\s)+[^;{}]*\b"
                + Regex.Escape(method) + @"\s*\([^;{}]*\)\s*\{");
            if (!declaration.Success) return null;

            return Braced(code, code.IndexOf('{', declaration.Index));
        }

        /// <summary>The body of a property, from the brace of its <c>get</c> to the one that closes it.</summary>
        private static string PropertyBody(string code, string property)
        {
            var declaration = Regex.Match(code,
                @"(public|protected|private|internal|override|virtual|\s)+[^;{}]*\b"
                + Regex.Escape(property) + @"\s*\r?\n?\s*\{\s*\r?\n\s*get");
            if (!declaration.Success) return null;

            return Braced(code, code.IndexOf('{', declaration.Index));
        }

        private static string Braced(string code, int open)
        {
            if (open < 0) return null;
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
