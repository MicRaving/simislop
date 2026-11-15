/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// B8's live-lot lighting, asked as questions rather than watched.
    ///
    /// <para>
    /// <b>What is pinned here.</b> Two halves. The first is the rule itself - how far the
    /// neighbourhood clock may move before the lot's outside colour is written again - which is a
    /// comparison over four numbers and is now a type that can be asked with no graphics device, no
    /// content manager and no running <c>VM</c>. The second is the screen's use of it, pinned the way
    /// this repository pins everything on a screen: by reading its source with the comments
    /// stripped, so that a sentence <em>explaining</em> the bug cannot satisfy an assertion about
    /// the code that had it.
    /// </para>
    ///
    /// <para>
    /// <b>Why the second half is the one that matters.</b> The rule was already there - a quarter
    /// hour of neighbourhood time, plus an immediate apply when the sun crosses the horizon - and it
    /// was untestable, because it lived in two private fields on a class that cannot be constructed
    /// without a device. What it did was call <c>VMArchitecture.SetTimeOfDay</c> on the live lot,
    /// which makes the screen a second writer of state the engine writes itself on every tick from
    /// the lot's own clock, from outside the VM's tick, on the object the player is editing. The
    /// assertions below are about that: the lot's own sun is the engine's business, and the only
    /// field this feature writes is the one the engine has no term for.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldLightApplyTests
    {
        /// <summary>A quarter of an hour of a day, which is the whole of the constant under test.</summary>
        private const double QuarterHour = 15.0 / 1440.0;

        // ---- the rule, run for real ----------------------------------------------------------------

        [Fact]
        public void TheStepIsAQuarterOfAnHourOfNeighbourhoodTime()
        {
            Assert.Equal(QuarterHour, NeighbourhoodWorldLightApplyGate.WorldLightApplyStep, 12);
            //and it is a bound rather than a suggestion: a move of half the step does not apply, one
            //of ten percent either side of it does. Written either side rather than exactly on it
            //because the two divisions are not bit-identical and the test is about the rule, not
            //about rounding.
            var gate = new NeighbourhoodWorldLightApplyGate();
            gate.Apply(0.500000, true);

            Assert.False(gate.ShouldApply(0.500000 + QuarterHour * 0.5, true));
            Assert.True(gate.ShouldApply(0.500000 + QuarterHour * 1.001, true));
            Assert.True(gate.ShouldApply(0.500000 - QuarterHour * 1.001, true));
            Assert.False(gate.ShouldApply(0.500000, true));
        }

        [Fact]
        public void ASteadyClockIsWrittenOnceAndThenNotAgain()
        {
            //The regression in its purest form: 1440 calls a second on a clock moving one game minute
            //a call must produce about a hundred writes, not 1440.
            var gate = new NeighbourhoodWorldLightApplyGate();
            var writes = 0;

            for (int frame = 0; frame < 1440; frame++)
            {
                var fraction = (0.25 + frame / 1440.0) % 1.0;
                var sunUp = fraction > 0.25 && fraction < 0.75;
                if (!gate.ShouldApply(fraction, sunUp)) continue;
                gate.Apply(fraction, sunUp);
                writes++;
            }

            //A whole day of world time at a quarter hour a write is 96. The assertion is a band
            //rather than an equality because the sun-up test adds its own crossings, and a band is
            //what fails loudly if the step is dropped and this becomes 1440.
            Assert.InRange(writes, 90, 110);
        }

        [Fact]
        public void ASunriseIsNeverHeldBackByTheQuarterHourStep()
        {
            //The one change a player sees as a step rather than a drift. Two minutes of world time
            //after the sun rose is far inside the step, and it must still apply.
            var gate = new NeighbourhoodWorldLightApplyGate();
            gate.Apply(0.2490, false);

            Assert.True(gate.ShouldApply(0.2510, true));
            gate.Apply(0.2510, true);

            //and the same on the way down
            Assert.True(gate.ShouldApply(0.2520, false));
        }

        [Fact]
        public void ALotThatHasNeverBeenWrittenAlwaysIs()
        {
            var gate = new NeighbourhoodWorldLightApplyGate();
            //A gate nobody has touched is "never", not "at midnight" - which is why the hour it
            //starts out holding is a separate flag and not the number itself.
            Assert.False(gate.HasApplied);
            Assert.True(gate.ShouldApply(0.4, true));

            //which is what makes Reset() enough to move the lot on: the hour it was last written at
            //is not consulted at all on the first frame of a new lot.
            gate.Apply(0.9, true);
            gate.Reset();
            Assert.Equal(NeighbourhoodWorldLightApplyGate.NeverAppliedDayFraction,
                gate.AppliedDayFraction);
            Assert.False(gate.HasApplied);
            Assert.True(gate.ShouldApply(0.4, true));
        }

        [Fact]
        public void ALatchThatSurvivesItsLotWouldSkipTheNextLotAndResetIsWhatStopsIt()
        {
            //The failure this exists for, in four lines. Lot one is written at 10:00; lot two loads
            //at 10:07, which is inside the step, so an un-reset latch skips it - and the lot is left
            //with the previous lot's sky with nothing in the log to say so.
            var latch = new NeighbourhoodWorldLightApplyGate();
            latch.Apply(10.0 / 24.0, true);

            Assert.False(latch.ShouldApply(10.0 / 24.0 + 7.0 / 1440.0, true));

            latch.Reset();
            Assert.True(latch.ShouldApply(10.0 / 24.0 + 7.0 / 1440.0, true));
        }

        [Fact]
        public void ATimeOfDayThatIsNotATimeOfDayIsNeverApplied()
        {
            //WorldClock.SeedFromGlobalState does not normalise, on purpose, so a corrupt or
            //hand-edited global can leave the hour at 47 - and VMArchitecture.SetTimeOfDay indexes
            //its own 14-entry sky ramp with the fraction it is handed. Refusing is the only safe
            //answer, and it is also the one that cannot be reached by a thrown IndexOutOfRange.
            var gate = new NeighbourhoodWorldLightApplyGate();
            gate.Apply(0.5, true);

            Assert.False(gate.ShouldApply(47.0 / 24.0, true));
            Assert.False(gate.ShouldApply(1.0, true));
            Assert.False(gate.ShouldApply(-0.25, true));
            Assert.False(gate.ShouldApply(double.NaN, true));
            Assert.False(gate.ShouldApply(double.PositiveInfinity, true));

            //and the latch is untouched by any of them, so a good hour still applies afterwards
            Assert.True(gate.HasApplied);
            Assert.True(gate.ShouldApply(0.5 + QuarterHour * 1.001, true));
        }

        [Fact]
        public void AStepOfZeroMeansEveryChangeApplies()
        {
            var gate = new NeighbourhoodWorldLightApplyGate();
            gate.Apply(0.5, true);
            Assert.True(gate.ShouldApply(0.5 + 1e-12, true, 0.0));

            //and it is honoured rather than clamped, for a negative step too, so a caller that asks
            //for "any change" gets it rather than a number it did not ask for.
            gate.Apply(0.5, true);
            Assert.True(gate.ShouldApply(0.5, true, -1.0));
        }

        // ---- the screen's use of it, read from source ------------------------------------------------

        [Fact]
        public void TheScreenAsksTheGateAndWritesOnlyTheFieldTheEngineHasNoTermFor()
        {
            var source = CommentsRemoved(ClientSource("UI", "Screens", "TS1GameScreen.cs"));

            var body = MethodBody(source, "private void UpdateNeighbourhoodWorldLighting()");
            Assert.True(body.Length > 0);

            //the tested decision is the only thing between a per-frame write and a quarter-hourly one
            Assert.Contains("LightingApplied.ShouldApply(dayFraction, lighting.IsSunUp, WorldLightApplyStep)",
                body);
            Assert.Contains("LightingApplied.Apply(dayFraction, lighting.IsSunUp);", body);

            //The one field the engine has no term for: the background fill under fog and cloud.
            Assert.Contains("state.OutsideColor = outside;", body);
            Assert.Contains("blueprint.OutsideColor = outside;", body);

            //and NOT the engine's own seam. VMArchitecture.Tick calls SetTimeOfDay() from the lot's
            //own clock on every tick, so a second writer - from outside the VM's tick, several times
            //a second, from a different clock - is a lot lit by whichever of the two ran last.
            Assert.DoesNotContain("SetTimeOfDay", body);
            Assert.DoesNotContain("BuildOutdoorsLight", body);
            Assert.DoesNotContain("OutsideTime", body);
            Assert.DoesNotContain("LightVec", body);
            //and it must not have reached for the architecture at all to do any of it
            Assert.DoesNotContain("Context.Architecture", body);
        }

        [Fact]
        public void TheLatchIsResetOnTheOnePathEveryLotLoadTakes()
        {
            var source = CommentsRemoved(ClientSource("UI", "Screens", "TS1GameScreen.cs"));

            //One owner for the reset, and it is the load path - the one that constructs the new lot
            //view. A Reset nothing calls is the bug; a Reset in two places is two answers.
            Assert.Equal(1, CountOf(source, "LightingApplied.Reset();"));

            var init = MethodBody(source, "public void InitializeLot(");
            Assert.True(init.Length > 0);
            Assert.Contains("LightingApplied.Reset();", init);

            //and nothing anywhere writes the latch's fields by hand any more, which is how the
            //un-resettable pair of private fields came to exist in the first place.
            Assert.DoesNotContain("LightingAppliedDayFraction", source);
            Assert.DoesNotContain("LightingAppliedSunUp", source);
        }

        [Fact]
        public void EveryPartBSiteStillRunsBeforeTheLotIsGivenItsFrame()
        {
            //Item three of the same audit: with any capability on, the lot control (via base.Update,
            //which is what processes the build/buy tool and the pie menu) and vm.Update() - which is
            //what drains the global link's architecture queue - must both still run, and vm.Update()
            //must be last of the two so the tick a command is queued into is the same tick that
            //drains it.
            var source = CommentsRemoved(ClientSource("UI", "Screens", "TS1GameScreen.cs"));
            var update = MethodBody(source, "public override void Update(");

            var baseUpdate = update.IndexOf("base.Update(state)", StringComparison.Ordinal);
            var vmUpdate = update.IndexOf("if (vm != null) vm.Update();", StringComparison.Ordinal);
            Assert.True(baseUpdate > 0, "the lot control no longer runs from base.Update");
            Assert.True(vmUpdate > baseUpdate, "vm.Update() no longer runs after the lot control");

            //Every Part B per-frame site sits between them, so none of them can be reached by
            //starving the VM of its frame.
            foreach (var site in new[]
            {
                "UpdateSurroundings();",
                "AttachHollowNeighbours();",
                "UpdateNeighbourhoodWorld(FrameSeconds(state));",
                "UpdateNeighbourhoodWorldLighting();"
            })
            {
                var at = update.IndexOf(site, StringComparison.Ordinal);
                Assert.True(at > 0, site + " is no longer in Update");
                Assert.True(at < vmUpdate, site + " runs after the lot has been given its frame");
            }

            //and the diagnostics are the one thing that is meant to be after it: they report on the
            //tick the player just watched, so asking before the frame would report the previous one.
            Assert.True(update.IndexOf("UpdateLotDiagnostics();", StringComparison.Ordinal) > vmUpdate,
                "the per-lot diagnostics no longer run after the frame they describe");
        }

        [Fact]
        public void TheWorldTickCannotSkipTheRestOfTheFrame()
        {
            //Item two: a world that will not tick must not cost the lot its frame. Every statement
            //from the world call to the end of Update is guarded, and the guard drops the world
            //rather than retrying it, so there is nothing left to throw.
            var source = CommentsRemoved(ClientSource("UI", "Screens", "TS1GameScreen.cs"));

            var tick = MethodBody(source, "private void UpdateNeighbourhoodWorld(float deltaSeconds)");
            Assert.Contains("try", tick);
            Assert.Contains("world.Tick(deltaSeconds);", tick);
            Assert.Contains("catch (Exception ex)", tick);
            //and it is the world that is dropped, never an exception that escapes
            Assert.Contains("WorldSession.End();", tick);
            //while a lot is up its clock is what the world follows, and the wall-clock tick is
            //only for when there is no lot clock
            Assert.Contains("world.FollowLotClock(lotClock);", tick);
            Assert.DoesNotContain("throw", tick);

            //The call site is a comparison and a call, so a capability that is off costs no frame
            //length and no world at all.
            var update = MethodBody(source, "public override void Update(");
            Assert.Contains("UpdateNeighbourhoodWorld(FrameSeconds(state));", update);
        }

        // ---- helpers ---------------------------------------------------------------------------------

        private static string ClientSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(new[] { dir.FullName, "Simitone.Client" }
                    .Concat(parts).ToArray());
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                "Could not find Simitone.Client above " + AppContext.BaseDirectory);
        }

        /// <summary>Source with every comment removed, so prose cannot satisfy an assertion about code.</summary>
        private static string CommentsRemoved(string source)
        {
            var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
        }

        /// <summary>Only the body of one member, found by counting braces.</summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "no member starting with: " + signature);
            var open = source.IndexOf('{', at);
            var depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(open, i - open + 1);
                }
            }
            throw new InvalidOperationException("unterminated body for: " + signature);
        }

        private static int CountOf(string source, string text)
        {
            var count = 0;
            var at = 0;
            while ((at = source.IndexOf(text, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += text.Length;
            }
            return count;
        }
    }
}