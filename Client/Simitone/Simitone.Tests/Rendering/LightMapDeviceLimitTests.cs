/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.LotView;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using System;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// The live lot's light map is sized from the shadow-detail setting and the lot's tile count,
    /// and a device answers a description past its own limit with
    /// <c>SharpDXException: E_INVALIDARG</c> rather than with a smaller texture. A 58 tile lot at a
    /// shadow detail of 24 with ultra lighting on asks for <c>24 * 57 * 3</c> = 4104 pixels across,
    /// which is one row past a 4096 cap, and that exception arrives from inside
    /// <c>VMTS1Activator.LoadFromIff</c> - with the whole lot load already paid for - and takes the
    /// process with it.
    ///
    /// <para><b>Both halves of that are arithmetic, and so both are pinned here.</b> The clamp
    /// (<see cref="LightMapMetrics.MaxResolutionFor"/>) and the check the neighbourhood facade asks
    /// before it stands a throwaway lot up (<c>NeighbourhoodFacadeBuilder.LightMapFits</c>) are the
    /// same question asked in two places, so they are asserted against each other over the whole
    /// range of lot sizes, shadow details and device limits - which is what stops them drifting.</para>
    ///
    /// <para>No graphics device is needed: the limit arrives as a <see cref="Point"/> and the whole
    /// rule is multiplication and comparison. That is the point of having moved it out of the
    /// renderer.</para>
    /// </summary>
    public sealed class LightMapDeviceLimitTests
    {
        /// <summary>The cap the machine this crash came off holds, and the engine's own fallback.</summary>
        private static readonly Point CommonDevice = new Point(4096, 4096);

        /// <summary>Per-axis caps differ, so a limit that is not square has to be honoured too.</summary>
        private static readonly Point NarrowDevice = new Point(2048, 4096);

        /// <summary>
        /// Every lot size the lot view will be asked about, from the two tile minimum the
        /// activator clamps to through retail's 64 to the 512 it refuses above.
        /// </summary>
        private static int[] LotSizes()
        {
            var sizes = new List<int>();
            for (var size = 2; size <= NeighbourhoodFacadeBuilder.MaximumLotSize; size++) sizes.Add(size);
            return sizes.ToArray();
        }

        private static bool[] UltraModes() => new[] { false, true };

        /// <summary>The shadow-detail settings <c>WorldConfig</c> will store, defaults included.</summary>
        private static int[] ShadowDetails()
        {
            var details = new List<int>();
            for (var detail = WorldConfig.MinShadowDetail; detail <= WorldConfig.MaxShadowDetail; detail++)
                details.Add(detail);
            return details.ToArray();
        }

        // ---- the size arithmetic the lot view actually uses -------------------------------------

        [Fact]
        public void TheLightMapIsThreeTilesAcrossAndTwoDown()
        {
            // LMapBatch.Init: LightMap = new RenderTarget2D(gd, wl * 3, wh * 2). These are the
            // multipliers TargetsFit checks against, so they are pinned here rather than left to
            // agree with the constructor by inspection.
            Assert.Equal(3, LightMapMetrics.LightMapWidthTiles);
            Assert.Equal(2, LightMapMetrics.LightMapHeightTiles);
            Assert.Equal(2, LightMapMetrics.ObjectShadowMapUltraMultiplier);
            Assert.Equal(1, LightMapMetrics.BorderTiles);
        }

        [Fact]
        public void TheClampedResolutionStillFitsEveryTargetTheBatchBuilds()
        {
            // The property the clamp exists for, over the whole range of lot sizes, shadow details
            // and both lighting modes: whatever MaxResolutionFor hands back, the three targets
            // LMapBatch.Init goes on to allocate are inside the device's limit. Not one case, one
            // pair of loops - the crash was one combination out of several hundred, and a rule
            // that only happens to hold for retail sizes is the same bug with a smaller number.
            foreach (var maxTarget in new[] { CommonDevice, NarrowDevice, new Point(1024, 1024), new Point(16384, 16384) })
            {
                foreach (var size in LotSizes())
                {
                    foreach (var detail in ShadowDetails())
                    {
                        foreach (var ultra in UltraModes())
                        {
                            var resolution = LightMapMetrics.ResolutionFor(detail, ultra);
                            var capped = LightMapMetrics.MaxResolutionFor(size, size, resolution, ultra, maxTarget);

                            AssertFits(size, size, capped, ultra, maxTarget,
                                detail + "/" + ultra + " on " + maxTarget.X + "x" + maxTarget.Y + ": " + size + " tiles");
                        }
                    }
                }
            }
        }

        [Fact]
        public void NoShadowDetailCanAskThisDeviceForATargetItCannotAllocate()
        {
            // The crash itself, as arithmetic. A shadow detail the player can actually choose
            // must never produce a target past the limit, for any lot the activator will hand over.
            foreach (var size in LotSizes())
            {
                foreach (var detail in ShadowDetails())
                {
                    foreach (var ultra in UltraModes())
                    {
                        var resolution = LightMapMetrics.MaxResolutionFor(size, size,
                            LightMapMetrics.ResolutionFor(detail, ultra), ultra, CommonDevice);

                        Assert.True(resolution >= 1, size + " tiles at detail " + detail + " has no resolution at all");
                        var side = (size - LightMapMetrics.BorderTiles) * resolution;
                        Assert.True(side * LightMapMetrics.LightMapWidthTiles <= CommonDevice.X,
                            size + " tiles at detail " + detail + " ultra " + ultra
                            + " is " + (side * LightMapMetrics.LightMapWidthTiles) + " pixels of light map across");
                        Assert.True(side * LightMapMetrics.LightMapHeightTiles <= CommonDevice.Y,
                            size + " tiles at detail " + detail + " ultra " + ultra
                            + " is " + (side * LightMapMetrics.LightMapHeightTiles) + " pixels of light map down");
                    }
                }
            }
        }

        [Fact]
        public void TheLotFromTheCrashIsBuiltOnePixelPerTileLower()
        {
            // The live report: a ~58 tile lot, shadow detail 24, ultra lighting, 4096 device. It
            // asked for 4104 and died; the clamp brings the resolution to what does fit, and says
            // so rather than silently shipping a different quality.
            const int lotSize = 58;
            const int detail = 24;

            var asked = LightMapMetrics.ResolutionFor(detail, ultraLighting: true);
            Assert.Equal(4104, asked * (lotSize - LightMapMetrics.BorderTiles) * LightMapMetrics.LightMapWidthTiles);
            Assert.False(LightMapMetrics.TargetsFit(lotSize, lotSize, asked, ultraLighting: true, CommonDevice));

            var capped = LightMapMetrics.MaxResolutionFor(lotSize, lotSize, asked, ultraLighting: true, CommonDevice);

            Assert.True(capped < asked, "the crash lot should have been clamped down, got " + capped);
            Assert.True(LightMapMetrics.TargetsFit(lotSize, lotSize, capped, ultraLighting: true, CommonDevice));
        }

        [Fact]
        public void ALotThatFitsIsNotClamped()
        {
            // The behaviour this must not change: retail's 64 tile lot at the default shadow
            // detail is 16 pixels per tile on every setting, on every device below, and asking
            // for what the player asked for is the whole point. Nothing above the default is
            // promised - a 64 tile lot at 24 with ultra lighting is 4536 pixels of light map and
            // is exactly the case the facade has been answering by lowering the shadow detail.
            foreach (var maxTarget in new[] { CommonDevice, new Point(16384, 16384) })
            {
                Assert.Equal(16, LightMapMetrics.MaxResolutionFor(64, 64, 16, ultraLighting: false, maxTarget));
                Assert.Equal(16, LightMapMetrics.MaxResolutionFor(64, 64, 16, ultraLighting: true, maxTarget));
            }
            Assert.Equal(24, LightMapMetrics.MaxResolutionFor(64, 64, 24, ultraLighting: true,
                new Point(16384, 16384)));
            Assert.Equal(21, LightMapMetrics.MaxResolutionFor(64, 64, 24, ultraLighting: true, CommonDevice));
        }

        [Fact]
        public void AnOddShadowDetailStillHalvesDownwardsWithoutUltraLighting()
        {
            // The resolution is not "the setting, halved": integer division on an odd setting
            // halves downwards, and this is what SetFloor's scissor base and the shadow projection
            // are derived from, so it has to be the same number every time it is asked for.
            Assert.Equal(11, LightMapMetrics.ResolutionFor(23, ultraLighting: false));
            Assert.Equal(23, LightMapMetrics.ResolutionFor(23, ultraLighting: true));
            Assert.Equal(0, LightMapMetrics.ResolutionFor(1, ultraLighting: false));
        }

        [Fact]
        public void AnAbsurdlyLargeLotStillGetsSomethingToDraw()
        {
            // The floor. A hand-edited SIMI can claim more tiles than any resolution at any device
            // can light - 4000 tiles is a 11997 pixel light map even at one pixel per tile - and
            // the answer is still a resolution, because the caller clamps the finished sizes
            // against the device afterwards. Returning zero here would be the E_INVALIDARG again.
            var capped = LightMapMetrics.MaxResolutionFor(4000, 4000, 24, ultraLighting: true, CommonDevice);

            Assert.Equal(1, capped);
        }

        [Fact]
        public void ALotOfOneTileIsRefusedRatherThanAskedForNothing()
        {
            // Zero pixels per side is a zero-sized render target, which is the refusal itself.
            Assert.False(LightMapMetrics.TargetsFit(1, 1, 24, ultraLighting: true, CommonDevice));
            Assert.False(LightMapMetrics.TargetsFit(0, 0, 24, ultraLighting: true, CommonDevice));
        }

        [Fact]
        public void NoDeviceMeansNoLimitAndNoOpinion()
        {
            // A non-positive point is "nothing has been asked", not "a device with no room". It has
            // to leave the requested resolution exactly as it was asked for.
            foreach (var missing in new[] { Point.Zero, new Point(-1, -1), new Point(0, 4096) })
            {
                Assert.True(LightMapMetrics.TargetsFit(512, 512, 24, ultraLighting: true, missing));
                Assert.Equal(24, LightMapMetrics.MaxResolutionFor(512, 512, 24, ultraLighting: true, missing));
            }
        }

        [Fact]
        public void TheDirectionalAtlasIsSizedFromTheLotAloneAndNeverByTheResolution()
        {
            // (Width - 1) * 3 * 4 across, and * 2 * 4 down. It is the one target in the batch no
            // resolution can bring inside the device, so it is clamped as a size of its own.
            var size = LightMapMetrics.DirectionalLightMapSizeFor(64, 64);

            Assert.Equal(new Point(63 * 12, 63 * 8), size);
            Assert.Equal(12, LightMapMetrics.DirectionalLightMapWidthPixels);
            Assert.Equal(8, LightMapMetrics.DirectionalLightMapHeightPixels);
        }

        // ---- the same rule, asked in the other place ---------------------------------------------

        [Fact]
        public void TheFacadeFitCheckAndTheLiveClampAreTheSameQuestion()
        {
            // NeighbourhoodFacadeBuilder.LightMapFits asks "can this machine light this lot at this
            // shadow detail" before standing a throwaway lot up; LightMapMetrics.TargetsFit is the
            // live lot asking the same thing about the targets it is about to allocate. If the two
            // rules ever answer differently, the facade refuses a lot the live path would have
            // built, or - the failure that matters - the facade accepts one the live path cannot
            // allocate, and the crash is back.
            foreach (var maxTarget in new[] { CommonDevice, NarrowDevice, new Point(1024, 1024), new Point(16384, 16384) })
            {
                foreach (var size in LotSizes())
                {
                    foreach (var detail in ShadowDetails())
                    {
                        foreach (var ultra in UltraModes())
                        {
                            var resolution = LightMapMetrics.ResolutionFor(detail, ultra);
                            var live = LightMapMetrics.TargetsFit(size, size, resolution, ultra, maxTarget);
                            //Without the directional atlas the two are the same arithmetic, so
                            //they have to give the same answer - including for the lot sizes
                            //neither of them can size a light map for all.
                            var withoutAtlas = NeighbourhoodFacadeBuilder.LightMapFits(size, detail, ultra,
                                directionalLighting: false, maxTarget);
                            Assert.True(withoutAtlas == live,
                                "lot " + size + " at detail " + detail + " ultra " + ultra
                                + " on " + maxTarget.X + "x" + maxTarget.Y
                                + ": the facade says " + withoutAtlas + " and the live targets say " + live);

                            //With directional lighting the facade also bounds an atlas the live
                            //path clamps as a size of its own, so its answer is the stricter of
                            //the two - and it accepting a lot must still mean the live targets
                            //fit, which is the half of the crash that matters.
                            if (NeighbourhoodFacadeBuilder.LightMapFits(size, detail, ultra,
                                directionalLighting: true, maxTarget))
                            {
                                Assert.True(live, "the facade would light lot " + size + " at detail "
                                    + detail + " on " + maxTarget.X + "x" + maxTarget.Y
                                    + ", and the live path's three targets do not fit");
                            }
                        }
                    }
                }
            }
        }

        [Fact]
        public void TheFacadeSearchLandsOnTheResolutionTheLivePathChooses()
        {
            // The facade walks the shadow detail down until one fits and hands the result to the
            // engine as WorldConfig.ShadowDetail; the live lot then resolves that setting through
            // the same ultra-halving rule and must land on the same number. This is the whole
            // reason both sides are asked through LightMapMetrics.ResolutionFor.
            foreach (var maxTarget in new[] { CommonDevice, NarrowDevice, new Point(1024, 1024) })
            {
                foreach (var size in LotSizes())
                {
                    foreach (var detail in ShadowDetails())
                    {
                        foreach (var ultra in UltraModes())
                        {
                            var forFacade = NeighbourhoodFacadeBuilder.ShadowDetailFor(size, detail, ultra,
                                directionalLighting: false, maxTarget);
                            if (forFacade < 1) continue;   //a lot this device cannot light at all
                            var forLive = LightMapMetrics.ResolutionFor(forFacade, ultra);

                            Assert.True(LightMapMetrics.TargetsFit(size, size, forLive, ultra, maxTarget),
                                "lot " + size + ": the facade chose a shadow detail of " + forFacade
                                + " and the live path resolved it to " + forLive
                                + " pixels per tile, which does not fit "
                                + maxTarget.X + "x" + maxTarget.Y);
                            //and nothing above the facade's choice would have fitted either, which is
                            //what makes it the answer rather than one of several
                            Assert.True(forLive <= LightMapMetrics.ResolutionFor(detail, ultra),
                                "lot " + size + ": the facade chose a higher resolution than was asked for");
                        }
                    }
                }
            }
        }

        [Fact]
        public void TheCrashLotIsTheOneTheFacadeAlreadyLearnedToAnswer()
        {
            // The live lot and the facade are the same lot on the same machine: the facade has
            // been skipping 58 tile lots in neighbourhoods since the two lots of 60 tiles were
            // found, and the live path had no equivalent. Both now ask through the same rule, so
            // the live lot is built at the resolution the facade would have handed the engine.
            const int lotSize = 58;
            const int detail = 24;
            var maxTarget = CommonDevice;

            var chosen = NeighbourhoodFacadeBuilder.ShadowDetailFor(lotSize, detail, ultraLighting: true,
                directionalLighting: false, maxTarget);
            var live = LightMapMetrics.MaxResolutionFor(lotSize, lotSize,
            LightMapMetrics.ResolutionFor(detail, ultraLighting: true), ultraLighting: true, maxTarget);

            Assert.True(chosen < detail, "58 tiles at detail 24 is the case that cannot fit");
            Assert.Equal(LightMapMetrics.ResolutionFor(chosen, ultraLighting: true), live);
        }

        [Fact]
        public void AFacadeAsksForTheLowestShadowDetailTheEngineAllows()
        {
            // The eight seconds per facade in the live log. ShadowDetailFor walks the player's
            // setting down until one fits, which on a modern device returns it unchanged - correct
            // for a fit question and no help at all for a time question. The facade's ceiling is now
            // the engine's minimum instead, so the light map it builds is a fraction of the area and
            // the light map pass that follows stops being fill-bound.
            Assert.Equal(WorldConfig.MinShadowDetail, NeighbourhoodFacadeBuilder.FacadeShadowDetail);

            foreach (var maxTarget in new[] { CommonDevice, NarrowDevice, new Point(1024, 1024) })
            {
                foreach (var size in LotSizes())
                {
                    foreach (var ultra in UltraModes())
                    {
                        var chosen = NeighbourhoodFacadeBuilder.ShadowDetailFor(
                            size, NeighbourhoodFacadeBuilder.FacadeShadowDetail, ultra,
                            directionalLighting: false, maxTarget);

                        //still a fit question underneath: a lot whose targets do not fit even at the
                        //minimum is still refused, rather than silently handed a value the lot view
                        //would crash on
                        if (chosen > 0)
                        {
                            Assert.True(chosen <= NeighbourhoodFacadeBuilder.FacadeShadowDetail,
                                "lot " + size + ": the facade search returned " + chosen
                                + ", which is above the ceiling it was asked to respect");
                            AssertFits(size, size, LightMapMetrics.ResolutionFor(chosen, ultra),
                                ultra, maxTarget, "lot " + size + " at facade detail " + chosen);
                        }
                    }
                }
            }
        }

        [Fact]
        public void TheFacadeLightMapIsSixteenTimesSmallerThanTheOneAPlayersDefaultWouldBuild()
        {
            // The claim the ceiling makes, as arithmetic. A 60 tile lot is a real retail house file
            // (the two that could not be built in a 41 lot neighbourhood were exactly that size), so
            // the numbers below are the ones the live log was about.
            const int size = 60;
            foreach (var ultra in UltraModes())
            {
                var players = LightMapMetrics.ResolutionFor(WorldConfig.DefaultShadowDetail, ultra);
                var facades = LightMapMetrics.ResolutionFor(NeighbourhoodFacadeBuilder.FacadeShadowDetail, ultra);
                var side = size - LightMapMetrics.BorderTiles;

                Assert.True(facades < players,
                    "the facade ceiling has stopped being lower than the default setting");

                //sixteen times less area on both the light map and the shadow map, which is what a
                //fill-bound pass costs
                var playerArea = (long)side * players * side * players;
                var facadeArea = (long)side * facades * side * facades;
                Assert.Equal(16L, playerArea / facadeArea);

                //and the light map's own width in both cases, so the ratio cannot be satisfied by
                //the two numbers collapsing towards each other
                Assert.Equal(side * players * LightMapMetrics.LightMapWidthTiles,
                    side * facades * LightMapMetrics.LightMapWidthTiles * 4);
                Assert.True(side * facades >= 1, "a zero-pixel light map is the refusal, not a saving");
            }
        }

        // ---- helpers -------------------------------------------------------------------------------

        /// <summary>
        /// The targets <c>LMapBatch.Init</c> allocates, straight from its own multiplications, each
        /// brought inside the device by <c>PPXDepthEngine.ClampToDevice</c> exactly as
        /// <c>Init</c> brings it inside - so the property being asserted is the one that matters:
        /// every target the batch goes on to ask for is inside what the device accepts.
        /// </summary>
        private static void AssertFits(int width, int height, int resolution, bool ultraLighting,
            Point maxTarget, string because)
        {
            var sideW = (width - LightMapMetrics.BorderTiles) * resolution;
            var sideH = (height - LightMapMetrics.BorderTiles) * resolution;
            var targets = new (string Name, int Width, int Height)[]
            {
                ("ShadowTarg", sideW, sideH),
                ("ObjShadowTarg", ultraLighting ? sideW * 2 : sideW, ultraLighting ? sideH * 2 : sideH),
                ("LightMap", sideW * LightMapMetrics.LightMapWidthTiles, sideH * LightMapMetrics.LightMapHeightTiles)
            };
            foreach (var target in targets)
            {
                var heldWidth = Math.Min(Math.Max(1, target.Width), maxTarget.X);
                var heldHeight = Math.Min(Math.Max(1, target.Height), maxTarget.Y);
                Assert.True(heldWidth >= 1 && heldWidth <= maxTarget.X
                    && heldHeight >= 1 && heldHeight <= maxTarget.Y,
                    because + ": " + target.Name + " is " + target.Width + "x" + target.Height
                    + " and even clamped it cannot be held by a " + maxTarget.X + "x" + maxTarget.Y
                    + " device");
                //If a target still needed clamping, even the one pixel per tile floor was not
                //enough - which is the size-clamp backstop's case to catch, and not this
                //resolution's. Asserted rather than left implicit so a change to the floor that
                //quietly starts leaning on it shows up here instead of as a crash.
                if (heldWidth != target.Width || heldHeight != target.Height)
                {
                    Assert.False(LightMapMetrics.TargetsFit(width, height, resolution, ultraLighting, maxTarget),
                        because + ": " + target.Name + " was clamped from " + target.Width + "x"
                        + target.Height + " although the resolution was reported as fitting");
                }
            }
        }
    }
}
