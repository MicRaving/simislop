using FSO.LotView;
using FSO.LotView.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for <see cref="GrassPassSelection"/> - which pass of the grass effect each surface
    /// is drawn with.
    ///
    /// <para>
    /// These indices are load bearing and were previously bare literals at five call sites
    /// (<c>3DFloorGeometry.DrawFloor</c> twice, <c>RoofComponent.Draw</c> twice,
    /// <c>RoofComponent.DrawOne</c>, <c>TerrainComponent.Draw</c> twice). Picking the wrong one
    /// does not throw: it draws a floor with the roof's parallax pass, or a roof with the
    /// unlit pass, and the only symptom is a lot that looks subtly wrong. So the numbers get
    /// names, and the names get pinned here.
    /// </para>
    ///
    /// <para>
    /// The last few tests read <c>GrassShader.fx</c> back off disk and check its pass order
    /// against the constants. That is worth doing precisely because the build does not compile
    /// the shader: the game runs the pre-built <c>GrassShader.xnb</c>, so editing the .fx alone
    /// is inert, and the failure mode is a silent C#/shader disagreement rather than a build
    /// error. This cannot detect the .xnb itself drifting from the .fx - nothing headless can -
    /// but it does catch the mistake a future editor will actually make, which is reordering the
    /// source and assuming the game followed.
    /// </para>
    /// </summary>
    public class GrassPassSelectionTests
    {
        // ---- the pass list, as the shader declares it -------------------------------------------

        [Fact]
        public void PassIndicesAreTheOnesTheCallSitesUsed()
        {
            // Before this existed: floor/terrain and roof-without-parallax were `? 2`,
            // roof-with-parallax was `3`, water/floor parallax was `4`, grass blades were
            // `(_3d) ? ((parallax) ? 3 : 2) : PassOffset`.
            Assert.Equal(0, GrassPassSelection.DrawBaseSimple);
            Assert.Equal(1, GrassPassSelection.DrawBaseFlat);
            Assert.Equal(2, GrassPassSelection.DrawBase3D);
            Assert.Equal(3, GrassPassSelection.RoofParallax3D);
            Assert.Equal(4, GrassPassSelection.FloorParallax3D);
        }

        [Fact]
        public void BladesAndBaseAgreeOnTheFirstFourAndBaseHasOneMore()
        {
            // The reason a single expression used to serve both techniques. It holds for 0-3 and
            // stops there: DrawBlades has no floor-parallax pass.
            Assert.Equal(GrassPassSelection.DrawBaseSimple, GrassPassSelection.BladesSimple);
            Assert.Equal(GrassPassSelection.DrawBaseFlat, GrassPassSelection.BladesFlat);
            Assert.Equal(GrassPassSelection.DrawBase3D, GrassPassSelection.Blades3D);

            Assert.Equal(5, GrassPassSelection.DrawBasePassCount);
            Assert.Equal(4, GrassPassSelection.BladesPassCount);
            Assert.Equal(GrassPassSelection.DrawBasePassCount, GrassPassSelection.BladesPassCount + 1);
        }

        [Fact]
        public void EveryIndexIsInsideItsOwnTechnique()
        {
            // A pass index past the end of the technique is an ArgumentOutOfRangeException on the
            // first frame that draws the surface, which is the good case. The bad case is an
            // index that happens to exist but means the wrong shader, and that is what the named
            // selection rules below are for.
            for (int i = 0; i < GrassPassSelection.DrawBasePassCount; i++)
            {
                Assert.InRange(i, 0, GrassPassSelection.DrawBasePassCount - 1);
            }
            Assert.True(GrassPassSelection.FloorParallax3D < GrassPassSelection.DrawBasePassCount);
            Assert.True(GrassPassSelection.BladesParallax3D < GrassPassSelection.BladesPassCount);
        }

        // ---- floor selection ----------------------------------------------------------------------

        [Fact]
        public void FloorIn3dAlwaysUsesThePerPixelLitPass()
        {
            var lit = new WorldConfig { LightingMode = 1 };
            var unlit = new WorldConfig { LightingMode = 0 };

            Assert.Equal(GrassPassSelection.DrawBase3D, GrassPassSelection.Floor(lit, true));
            Assert.Equal(GrassPassSelection.DrawBase3D, GrassPassSelection.Floor(unlit, true));
        }

        [Fact]
        public void FloorIn2dFollowsTheLightingModeOffset()
        {
            // This is the whole reason PassOffset exists: with lighting off the 2d lot needs the
            // unlit simple pass, with it on the flat light-map pass.
            var unlit = new WorldConfig { LightingMode = 0 };
            var lit = new WorldConfig { LightingMode = 1 };
            var ultra = new WorldConfig { LightingMode = 3 };

            Assert.Equal(GrassPassSelection.DrawBaseSimple, unlit.PassOffset);
            Assert.Equal(GrassPassSelection.DrawBaseFlat, lit.PassOffset);
            Assert.Equal(GrassPassSelection.DrawBaseFlat, ultra.PassOffset);

            Assert.Equal(GrassPassSelection.DrawBaseSimple, GrassPassSelection.Floor(unlit, false));
            Assert.Equal(GrassPassSelection.DrawBaseFlat, GrassPassSelection.Floor(lit, false));
            Assert.Equal(GrassPassSelection.DrawBaseFlat, GrassPassSelection.Floor(ultra, false));
        }

        [Fact]
        public void A3DFloorIsNeverDrawnWithAFlat2DPass()
        {
            // The specular term, the grass blades and the ceiling colour all live in the 3d pass
            // only. If this regressed, 3d would still render - just unlit and without sheen.
            for (int mode = 0; mode <= 3; mode++)
            {
                var config = new WorldConfig { LightingMode = mode };
                var pass = GrassPassSelection.Floor(config, true);

                Assert.NotEqual(GrassPassSelection.DrawBaseSimple, pass);
                Assert.NotEqual(GrassPassSelection.DrawBaseFlat, pass);
            }
        }

        // ---- roof selection ------------------------------------------------------------------------

        [Fact]
        public void RoofIn3dIsParallaxOrPlainAndNothingElse()
        {
            var config = new WorldConfig { Complex = true, LightingMode = 3 };

            Assert.Equal(GrassPassSelection.RoofParallax3D, GrassPassSelection.Roof(config, true, true));
            Assert.Equal(GrassPassSelection.DrawBase3D, GrassPassSelection.Roof(config, true, false));
        }

        [Fact]
        public void RoofIn3dIgnoresTheLightingMode()
        {
            // A roof is always outside, so the light-map pass choice is a 2d concern. Even with
            // lighting completely off, the 3d roof still takes the per-pixel lit 3d pass.
            for (int mode = 0; mode <= 3; mode++)
            {
                var config = new WorldConfig { LightingMode = mode, Complex = true };

                Assert.Equal(GrassPassSelection.RoofParallax3D, GrassPassSelection.Roof(config, true, true));
                Assert.Equal(GrassPassSelection.DrawBase3D, GrassPassSelection.Roof(config, true, false));
            }
        }

        [Fact]
        public void RoofIn2dOnlyGoesParallaxWithAdvancedLighting()
        {
            var lit = new WorldConfig { LightingMode = 1, Complex = true };
            var unlit = new WorldConfig { LightingMode = 0, Complex = true };

            Assert.True(lit.AdvancedLighting);
            Assert.False(unlit.AdvancedLighting);

            Assert.Equal(GrassPassSelection.RoofParallax3D, GrassPassSelection.Roof(lit, false, true));
            Assert.Equal(GrassPassSelection.Flat(unlit), GrassPassSelection.Roof(unlit, false, true));
        }

        [Fact]
        public void RoofIn2dWithoutAParallaxTextureIsTheOrdinaryFlatPass()
        {
            var config = new WorldConfig { LightingMode = 3, Complex = true };

            Assert.Equal(GrassPassSelection.DrawBaseFlat, GrassPassSelection.Roof(config, false, false));
        }

        [Fact]
        public void TheOnly3DPassWithANormalMapIsTheRoofParallaxPass()
        {
            // The reason normal mapping is a shader change and not a settings row: of the two 3d
            // passes, exactly one - RoofParallax3D - samples NormalMapSampler, and only the roof
            // path is routed to it.
            Assert.Equal(3, GrassPassSelection.RoofParallax3D);
            Assert.NotEqual(GrassPassSelection.RoofParallax3D, GrassPassSelection.DrawBase3D);
            Assert.NotEqual(GrassPassSelection.RoofParallax3D, GrassPassSelection.FloorParallax());

            // And the floor parallax pass is the one whose normal-map read is commented out in
            // the shader, so no configuration can reach a normal-mapped floor.
            Assert.Equal(GrassPassSelection.FloorParallax3D, GrassPassSelection.FloorParallax());
        }

        // ---- grass blades ----------------------------------------------------------------------------

        [Fact]
        public void BladesIn3dPickTheParallaxPassOnlyWhenAsked()
        {
            var config = new WorldConfig { LightingMode = 3 };

            Assert.Equal(GrassPassSelection.Blades3D, GrassPassSelection.Blades(config, true, false));
            Assert.Equal(GrassPassSelection.BladesParallax3D, GrassPassSelection.Blades(config, true, true));
        }

        [Fact]
        public void BladesIn2dIgnoreTheParallaxFlagAndFollowTheLightingMode()
        {
            // Blade parallax is a 3d-only feature; `parallax` is a local that is currently always
            // false, so 2d must not be pushed at the blades-parallax pass by accident.
            var lit = new WorldConfig { LightingMode = 1 };
            var unlit = new WorldConfig { LightingMode = 0 };

            Assert.Equal(GrassPassSelection.DrawBaseFlat, GrassPassSelection.Blades(lit, false, true));
            Assert.Equal(GrassPassSelection.DrawBaseSimple, GrassPassSelection.Blades(unlit, false, true));
            Assert.Equal(GrassPassSelection.DrawBaseFlat, GrassPassSelection.Blades(lit, false, false));
        }

        [Fact]
        public void BladesNeverSelectTheFloorParallaxPass()
        {
            // DrawBlades has only four passes, so index 4 does not exist there. The shared indices
            // stop at 3 precisely so this cannot happen.
            var config = new WorldConfig { LightingMode = 3 };

            foreach (var is3D in new[] { false, true })
            {
                foreach (var parallax in new[] { false, true })
                {
                    Assert.NotEqual(GrassPassSelection.FloorParallax3D,
                        GrassPassSelection.Blades(config, is3D, parallax));
                }
            }
        }

        [Fact]
        public void ANullConfigDegradesToTheUnlitPassRatherThanThrowing()
        {
            Assert.Equal(0, GrassPassSelection.Flat(null));
            Assert.Equal(0, GrassPassSelection.Floor(null, false));
            Assert.Equal(GrassPassSelection.DrawBase3D, GrassPassSelection.Floor(null, true));
            Assert.Equal(GrassPassSelection.DrawBase3D, GrassPassSelection.Roof(null, true, false));
            Assert.Equal(GrassPassSelection.DrawBase3D, GrassPassSelection.Blades(null, true, false));
        }

        // ---- drift against the shader source -------------------------------------------------------

        [Fact]
        public void GrassShaderDeclaresTheDrawBasePassesInTheOrderTheConstantsAssume()
        {
            var passes = TechniquePasses(ShaderSource(), "DrawBase");

            Assert.Equal(new[] { "MainPassSimple", "MainPass", "MainPass3D", "RoofParallax3D", "FloorParallax3D" }, passes);
            Assert.Equal(GrassPassSelection.DrawBaseSimple, 0);
            Assert.Equal(GrassPassSelection.DrawBaseFlat, passes.IndexOf("MainPass"));
            Assert.Equal(GrassPassSelection.DrawBase3D, passes.IndexOf("MainPass3D"));
            Assert.Equal(GrassPassSelection.RoofParallax3D, passes.IndexOf("RoofParallax3D"));
            Assert.Equal(GrassPassSelection.FloorParallax3D, passes.IndexOf("FloorParallax3D"));
            Assert.Equal(GrassPassSelection.DrawBasePassCount, passes.Count);
        }

        [Fact]
        public void GrassShaderDeclaresTheDrawBladesPassesInTheOrderTheConstantsAssume()
        {
            var passes = TechniquePasses(ShaderSource(), "DrawBlades");

            Assert.Equal(
                new[] { "MainBladesSimple", "MainBlades", "MainBlades3D", "MainBladesParallax3D" },
                passes);
            Assert.Equal(GrassPassSelection.BladesSimple, 0);
            Assert.Equal(GrassPassSelection.BladesFlat, passes.IndexOf("MainBlades"));
            Assert.Equal(GrassPassSelection.Blades3D, passes.IndexOf("MainBlades3D"));
            Assert.Equal(GrassPassSelection.BladesParallax3D, passes.IndexOf("MainBladesParallax3D"));
            Assert.Equal(GrassPassSelection.BladesPassCount, passes.Count);
        }

        [Fact]
        public void TheParallaxPassesAreNotStrippedOutOfTheDesktopShader()
        {
            // Both parallax passes are behind `#if !SIMPLE`. The desktop grass effect is built
            // without SIMPLE, so indices 3 and 4 exist; the iOS/GLES2 effect (GrassShaderiOS) is
            // built with it and has neither. `WorldContent.EffectSuffix` picks between them, and
            // every call site assumes the desktop layout, so if SIMPLE were ever defined for the
            // desktop build these indices would read past the end of the technique.
            var source = ShaderSource();

            Assert.Contains("#if !SIMPLE", source, StringComparison.Ordinal);
            Assert.Contains("pass RoofParallax3D", source, StringComparison.Ordinal);
            Assert.Contains("pass FloorParallax3D", source, StringComparison.Ordinal);
            Assert.Contains("void RoofParallaxPS3D", source, StringComparison.Ordinal);
            Assert.Contains("void FloorParallaxPS3D", source, StringComparison.Ordinal);
        }

        [Fact]
        public void TheShaderSaysOutLoudThatItIsNotWhatRuns()
        {
            // The header note exists because the next editor needs to know that this source does
            // not reach the GPU. If somebody strips it as "just a comment", the trap reopens.
            var source = ShaderSource();
            var header = source.Substring(0, Math.Min(4000, source.Length));

            Assert.Contains("GrassShader.xnb", header, StringComparison.Ordinal);
            Assert.Contains("NOT what the game runs", header, StringComparison.Ordinal);
            Assert.Contains("2MGFX", header, StringComparison.Ordinal);
        }

        [Fact]
        public void TheFloorNormalMapIsStillCommentedOutInTheShaderSource()
        {
            // The concrete blocked item, pinned so that "we already support it" cannot be claimed
            // by accident. Lifting this needs an .xnb rebuild AND a tangent frame, which
            // TerrainVertex does not carry.
            var source = ShaderSource();
            var start = source.IndexOf("void FloorParallaxPS3D", StringComparison.Ordinal);
            var end = source.IndexOf("void GridPS", StringComparison.Ordinal);

            Assert.True(start > 0, "FloorParallaxPS3D not found in GrassShader.fx");
            Assert.True(end > start, "FloorParallaxPS3D bounds not found in GrassShader.fx");

            var body = source.Substring(start, end - start);

            Assert.Contains("float3 normal = float3(0, 0, 1);", body, StringComparison.Ordinal);
            Assert.Contains("//tex2D(NormalMapSampler, texCoords).xyz;", body, StringComparison.Ordinal);
            // The roof, by contrast, does read its normal map - which is why the roof's parallax
            // pass is the only normal-mapped surface in the lot view.
            Assert.Contains("float3 normal = tex2D(NormalMapSampler, texCoords).xyz;", source, StringComparison.Ordinal);
        }

        [Fact]
        public void TheContentProjectDoesNotCompileShaders()
        {
            // The root cause of all three blocked items. `MonoGame.Content.Builder.Task` is
            // commented out, ContentSrc is only <None>, and no project in the build graph imports
            // MonoGame.Content.Builder.targets - so the .xnb is a checked-in binary.
            var project = File.ReadAllText(ContentProject());

            Assert.Contains("<!-- <PackageReference Include=\"MonoGame.Content.Builder.Task\"", project, StringComparison.Ordinal);
            Assert.Contains("<None Include=\"ContentSrc\\**\\*.*\" />", project, StringComparison.Ordinal);
            Assert.DoesNotContain("MonoGameContentReference", project, StringComparison.Ordinal);
        }

        // ---- helpers ---------------------------------------------------------------------------------

        /// <summary>
        /// The pass names of one <c>technique X { ... }</c> block, in declaration order. Only
        /// top-level <c>pass Name</c> lines count, so the <c>pass</c> keyword cannot be matched
        /// inside a comment or a nested block.
        /// </summary>
        private static List<string> TechniquePasses(string source, string technique)
        {
            var open = source.IndexOf("technique " + technique, StringComparison.Ordinal);
            Assert.True(open > 0, "technique " + technique + " not found in GrassShader.fx");

            // A technique block ends at the first line that is exactly "}" at column zero.
            var end = source.IndexOf("\r\n}", open, StringComparison.Ordinal);
            if (end < 0) end = source.IndexOf("\n}", open, StringComparison.Ordinal);
            Assert.True(end > open, "end of technique " + technique + " not found");

            var body = source.Substring(open, end - open);
            var names = new List<string>();
            foreach (Match m in Regex.Matches(body, @"(?m)^\s*pass\s+([A-Za-z0-9_]+)\s*$"))
            {
                names.Add(m.Groups[1].Value);
            }

            return names;
        }

        private static string ShaderSource()
        {
            return File.ReadAllText(Path.Combine(ContentProjectDirectory(), "ContentSrc", "Effects", "GrassShader.fx"));
        }

        private static string ContentProject()
        {
            return Path.Combine(ContentProjectDirectory(), "FSO.Content.csproj");
        }

        /// <summary>
        /// The FreeSO checkout's <c>tso.content</c> folder, found by walking up out of the test
        /// output rather than by a fixed number of levels, so a change to the build layout does
        /// not turn these into "file not found".
        /// </summary>
        private static string ContentProjectDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "FreeSO", "TSOClient", "tso.content");
                if (File.Exists(Path.Combine(candidate, "FSO.Content.csproj"))) return candidate;

                var nested = Path.Combine(dir.FullName, "TSOClient", "tso.content");
                if (File.Exists(Path.Combine(nested, "FSO.Content.csproj"))) return nested;

                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                "Could not find FreeSO/TSOClient/tso.content/FSO.Content.csproj above " + AppContext.BaseDirectory);
        }
    }
}
