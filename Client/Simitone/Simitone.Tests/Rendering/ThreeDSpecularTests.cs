using FSO.LotView;
using FSO.LotView.Model;
using System;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for the lot view's 3d specular configuration: the pure rules in
    /// <see cref="ThreeDSpecular"/> and the <see cref="WorldConfig"/> properties that feed them.
    ///
    /// <para>
    /// The term under test is <c>LightSpecular</c> in <c>GrassShader.fx</c>:
    /// <c>DiffuseColor * (1 - pow(|dot(view, normal)|, n))</c>. It already exists and is already
    /// called by every per-pixel 3d surface in the grass effect, and the vertex formats already
    /// carry the normal it needs - what was missing was that four call sites hard-coded the
    /// exponent as a float literal, so the terms could not be reached at all and could not be
    /// reasoned about. These tests pin the mapping from tile type and from config to exponent, and
    /// above all pin that <b>every default is the literal it replaced</b>, which is the whole
    /// reason the change is safe: a player who changes nothing gets byte-identical rendering.
    /// </para>
    ///
    /// <para>
    /// The negative test to read hardest is
    /// <see cref="OrdinaryFloorsAreUnaffectedByAnyShininessSetting"/>: 0 is not "the default
    /// shininess", it is "no sheen", and the shader evaluates <c>pow(x, 0)</c> to 1 for every
    /// <c>x &gt; 0</c>, which makes the additive term exactly zero. If that ever changed, every
    /// tiled floor in the game would start shining.
    /// </para>
    /// </summary>
    public class ThreeDSpecularTests
    {
        /// <summary>
        /// The literals the renderer used before this existed. Named here as literals on purpose:
        /// if one of these ever has to change, the test is supposed to fail and make a human say
        /// so, rather than drifting quietly.
        /// </summary>
        private const float LegacyGrassShininess = 0.02f;
        private const float LegacyRoofShininess = 0.01f;
        private const float LegacyRoofEdgeShininess = 0.003f;

        // ---- config defaults: every one is the literal it replaced ------------------------------

        [Fact]
        public void SpecularDefaultsAreTheLiteralsTheyReplaced()
        {
            var config = new WorldConfig();

            Assert.Equal(LegacyGrassShininess, config.GrassShininess);
            Assert.Equal(LegacyRoofShininess, config.RoofShininess);
            Assert.Equal(LegacyRoofEdgeShininess, config.RoofEdgeShininess);
            // The water exponent's default was 0.02 too, and still is.
            Assert.Equal(LegacyGrassShininess, config.WaterShininess);
        }

        [Fact]
        public void SpecularConstantsAgreeWithTheConfigDefaults()
        {
            var config = new WorldConfig();

            Assert.Equal(ThreeDSpecular.DefaultGrassShininess, config.GrassShininess);
            Assert.Equal(ThreeDSpecular.DefaultRoofShininess, config.RoofShininess);
            Assert.Equal(ThreeDSpecular.DefaultRoofEdgeShininess, config.RoofEdgeShininess);
        }

        [Fact]
        public void EverySpecularDefaultIsInRange()
        {
            // A default outside [0, max] would be silently clamped the first time anyone read it
            // back, so the "default preserves today's rendering" claim would be a fiction.
            var config = new WorldConfig();

            foreach (var value in new[] { config.GrassShininess, config.RoofShininess, config.RoofEdgeShininess, config.WaterShininess })
            {
                Assert.InRange(value, 0f, WorldConfig.MaxShininess);
            }
        }

        [Fact]
        public void CeilingsAreOnByDefault()
        {
            // withCeilings: true is what TerrainComponent.Draw has always passed, and the branch
            // in DrawFloor is a pre-existing feature, not something this introduced.
            Assert.True(new WorldConfig().Ceilings3D);
        }

        [Fact]
        public void TheSharedBoundMatchesTheWaterBound()
        {
            Assert.Equal(WorldConfig.MaxWaterShininess, WorldConfig.MaxShininess);
            Assert.Equal(0.25f, WorldConfig.MaxShininess);
        }

        // ---- clamping ---------------------------------------------------------------------------

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0.01f, 0.01f)]
        [InlineData(0.25f, 0.25f)]
        [InlineData(1f, 0.25f)]
        [InlineData(1000f, 0.25f)]
        [InlineData(float.MaxValue, 0.25f)]
        [InlineData(-1f, 0f)]
        [InlineData(-0.0001f, 0f)]
        [InlineData(float.NegativeInfinity, 0f)]
        public void SanitizeClampsToTheShaderUsableRange(float input, float expected)
        {
            Assert.Equal(expected, ThreeDSpecular.Sanitize(input, WorldConfig.MaxShininess), 6);
        }

        [Fact]
        public void SanitizeTreatsNaNAsOff()
        {
            // A NaN that reaches the GPU turns the frame's colour into NaN. A config file is user
            // input, so this is the one value that must never survive a setter.
            Assert.Equal(0f, ThreeDSpecular.Sanitize(float.NaN, WorldConfig.MaxShininess));
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(0.5f)]
        [InlineData(float.NaN)]
        public void EveryShininessPropertyUsesTheSameClamp(float bad)
        {
            var config = new WorldConfig();

            config.GrassShininess = bad;
            config.RoofShininess = bad;
            config.RoofEdgeShininess = bad;
            config.WaterShininess = bad;

            Assert.InRange(config.GrassShininess, 0f, WorldConfig.MaxShininess);
            Assert.InRange(config.RoofShininess, 0f, WorldConfig.MaxShininess);
            Assert.InRange(config.RoofEdgeShininess, 0f, WorldConfig.MaxShininess);
            Assert.InRange(config.WaterShininess, 0f, WorldConfig.MaxShininess);
            Assert.False(float.IsNaN(config.GrassShininess));
            Assert.False(float.IsNaN(config.RoofShininess));
            Assert.False(float.IsNaN(config.RoofEdgeShininess));
            Assert.False(float.IsNaN(config.WaterShininess));
        }

        [Fact]
        public void ShininessPropertiesAreIndependentOfEachOther()
        {
            var config = new WorldConfig();

            config.RoofShininess = 0.2f;

            Assert.Equal(0.2f, config.RoofShininess);
            Assert.Equal(ThreeDSpecular.DefaultGrassShininess, config.GrassShininess);
            Assert.Equal(ThreeDSpecular.DefaultGrassShininess, config.WaterShininess);
            Assert.Equal(ThreeDSpecular.DefaultRoofEdgeShininess, config.RoofEdgeShininess);
        }

        // ---- tile type to exponent ----------------------------------------------------------------

        [Theory]
        [InlineData((ushort)1)]
        [InlineData((ushort)2)]
        [InlineData((ushort)99)]
        [InlineData((ushort)1000)]
        [InlineData((ushort)65502)]
        public void OrdinaryFloorsGetNoSheenAtAll(ushort tileType)
        {
            // Anything from 1 up to the air tile type is a normal floor. These are drawn with 0,
            // which is not a weak setting: pow(x, 0) == 1 for x > 0, so the additive specular
            // term is identically zero and the floor is flat no matter what the other settings
            // say. Note tile type 0 is NOT in this set - it is bare grass, and is in the next test.
            var config = new WorldConfig { GrassShininess = 0.2f, WaterShininess = 0.2f };

            Assert.Equal(0f, ThreeDSpecular.ShininessFor(config, tileType));
        }

        [Theory]
        [InlineData((ushort)65503)] // air overlay
        [InlineData((ushort)0)]     // bare grass - special-cased above the range table
        public void GrassAndAirTakeTheGrassExponent(ushort tileType)
        {
            var config = new WorldConfig { GrassShininess = 0.11f, WaterShininess = 0.2f };

            Assert.Equal(0.11f, ThreeDSpecular.ShininessFor(config, tileType));
        }

        [Theory]
        [InlineData((ushort)65504)] // first water tile
        [InlineData((ushort)65519)] // last water tile
        [InlineData((ushort)65520)] // first pool tile
        [InlineData((ushort)65535)] // last pool tile
        public void WaterAndPoolsTakeTheWaterExponent(ushort tileType)
        {
            var config = new WorldConfig { GrassShininess = 0.11f, WaterShininess = 0.2f };

            Assert.Equal(0.2f, ThreeDSpecular.ShininessFor(config, tileType));
        }

        [Fact]
        public void TheModelledPoolIsAWaterSurfaceNotAGrassSurface()
        {
            // The one place the mapping is load bearing: FloorLevel.AddGroup keys the modelled
            // pool geometry on tile type 65535, and the draw used to give it its own 0.02 literal.
            // It now falls out of the same table as every other water tile, so the water setting
            // finally reaches it. At the default both are 0.02, so nothing moves today.
            var atDefaults = new WorldConfig();
            Assert.Equal(LegacyGrassShininess, atDefaults.WaterShininess);
            Assert.Equal(atDefaults.WaterShininess, ThreeDSpecular.ShininessFor(atDefaults, 65535));

            var turnedUp = new WorldConfig { WaterShininess = 0.06f };
            Assert.Equal(0.06f, ThreeDSpecular.ShininessFor(turnedUp, 65535));
        }

        [Fact]
        public void TileTypeMappingIsTheWholeRangeWithNoGap()
        {
            // The two thresholds are the only thing the table has, so a typo in either one would
            // show up here as a tile type that lands in the wrong bucket.
            Assert.Equal(65503, ThreeDSpecular.AirTileType);
            Assert.Equal(65504, ThreeDSpecular.WaterTileType);
            Assert.Equal(ThreeDSpecular.WaterTileType, ThreeDSpecular.AirTileType + 1);
        }

        [Fact]
        public void ADefaultConfigReproducesTheLegacyMappingExactly()
        {
            // The claim the whole change rests on: with an untouched WorldConfig, every tile type
            // lands on the exponent it landed on before, for every surface the old code drew.
            //
            // The old code had two literals, not one: `id == 0` (bare grass) and `id >= 65503`
            // (air, water and pools) both got 0.02, and 1..65502 got 0. Tile type 0 in
            // particular was reached by a separate branch, which is exactly the case it is easy
            // to break when folding the table together - so it is swept like everything else.
            var config = new WorldConfig();

            for (int tileType = 0; tileType <= ushort.MaxValue; tileType++)
            {
                var got = ThreeDSpecular.ShininessFor(config, (ushort)tileType);
                var legacy = (tileType == 0 || tileType >= 65503) ? LegacyGrassShininess : 0f;

                Assert.Equal(legacy, got, 6);
            }
        }

        [Fact]
        public void BareGrassKeepsItsSheenWhenTheOtherSettingsAreTurnedUp()
        {
            // The regression this table was written to prevent, stated as a test: tile type 0 is
            // the ground floor and must follow GrassShininess, not fall into the "ordinary floor,
            // no sheen" bucket that every value from 1 upwards lands in.
            var config = new WorldConfig { GrassShininess = 0.15f, WaterShininess = 0.06f };

            Assert.Equal(0.15f, ThreeDSpecular.ShininessFor(config, 0));
            Assert.Equal(0f, ThreeDSpecular.ShininessFor(config, 1));
            Assert.Equal(0.15f, ThreeDSpecular.ShininessFor(config, 65503));
            Assert.Equal(0.06f, ThreeDSpecular.ShininessFor(config, 65510));
        }

        [Fact]
        public void ANullConfigIsNoSheenRatherThanANullReference()
        {
            // The renderer always passes WorldConfig.Current, but a null here should be a flat
            // surface (safe) rather than a crash on a graphics thread.
            Assert.Equal(0f, ThreeDSpecular.ShininessFor(null, 65510));
            Assert.False(ThreeDSpecular.ShouldDrawCeiling(null, true, true, 3));
        }

        // ---- the ceiling branch -------------------------------------------------------------------

        [Fact]
        public void CeilingsAreDrawnWhereTheyAlwaysWereByDefault()
        {
            // The historical rule, storey index 2 and up (0-based), in 3d, when the caller asked
            // for them. Index 0 and 1 never got one and still do not.
            var config = new WorldConfig();

            Assert.False(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 0));
            Assert.False(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 1));
            Assert.True(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 2));
            Assert.True(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 3));
            Assert.True(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 99));
        }

        [Fact]
        public void TurningCeilingsOffSuppressesEveryStorey()
        {
            var config = new WorldConfig { Ceilings3D = false };

            for (int floor = 0; floor < 8; floor++)
            {
                Assert.False(ThreeDSpecular.ShouldDrawCeiling(config, true, true, floor));
            }
        }

        [Fact]
        public void CeilingsNeed3dAndNeedTheCallerToHaveAsked()
        {
            var config = new WorldConfig();

            // 2d never drew one, whatever the caller asked for.
            Assert.False(ThreeDSpecular.ShouldDrawCeiling(config, false, true, 4));
            // The surround/facade draw path passes withCeilings: false and never did either.
            Assert.False(ThreeDSpecular.ShouldDrawCeiling(config, true, false, 4));
            Assert.False(ThreeDSpecular.ShouldDrawCeiling(config, false, false, 4));
        }

        [Fact]
        public void TheCeilingToggleAndThe3DFlagAreIndependent()
        {
            var config = new WorldConfig();

            Assert.True(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 5));
            config.Ceilings3D = false;
            Assert.False(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 5));
            config.Ceilings3D = true;
            Assert.True(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 5));
        }

        [Fact]
        public void ACeilingIsNeverDrawnForAStoreyBelowTheThird()
        {
            // Sanity on the off-by-one. The branch used to read `f > 1` with f a zero-based loop
            // counter over Floors, so storeys 0 and 1 - the ground and the one above it - are the
            // two that never get an underside. If this ever moves, it is because the rule itself
            // changed, not because a counter was renumbered.
            var config = new WorldConfig();

            for (int floor = 0; floor < 2; floor++)
            {
                Assert.False(ThreeDSpecular.ShouldDrawCeiling(config, true, true, floor));
            }
            Assert.True(ThreeDSpecular.ShouldDrawCeiling(config, true, true, 2));
        }
    }
}
