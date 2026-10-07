/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The generated neighbourhood's terrain draw, as far as a headless build can see it.
    ///
    /// <para>
    /// <b>The bug.</b> The neighbourhood built 315,424 triangles and every frame of it submitted
    /// zero. The device threw while preparing the first draw, and the message was
    /// <c>"the current vertex declaration does not include all the elements required by the current
    /// vertex shader"</c> - MonoGame's <c>InputLayoutCache</c> reporting D3D11's <c>E_INVALIDARG</c>,
    /// which names a mismatch without saying what it was compared against.
    /// </para>
    ///
    /// <para>
    /// <b>What it was compared against.</b> The input layout is built from the vertex declaration
    /// against the vertex shader that is <em>bound</em>, and a shader is only bound by
    /// <c>EffectPass.Apply</c> - assigning <c>CurrentTechnique</c> and the parameters reaches
    /// nothing at all. The terrain draw took the pass from <c>BeginCity</c> and never applied it, so
    /// the shader in force was whatever the previous draw left behind: the facades'
    /// <c>RenderCityObj</c> (<c>float4</c> position, <c>float2</c>, <c>float3</c>), or a sprite
    /// batch's <c>SpriteEffect</c>, which wants a <c>COLOR</c> this vertex does not carry. The
    /// declaration itself is <c>TLayerVertex</c>'s, unchanged, and <c>RenderNCity</c> is the
    /// technique FSO draws that identical buffer with - so the mesh and the shader did agree, and
    /// the two were simply never both in force at the same time. Nothing about the vertex was wrong,
    /// which is why nothing about the vertex needed changing.
    /// </para>
    ///
    /// <para>
    /// <b>Why these tests read source.</b> "Did the draw apply its pass" is not observable without
    /// a device: it is a question about a side effect on the pipeline, and a headless build has no
    /// pipeline. It is observable as a written-down construct, which is the same trade
    /// <c>LightingPassOwnershipTests</c> and <c>GrassPassSelectionTests</c> already make - and
    /// GrassPassSelectionTests' reason applies here exactly: the build does not compile
    /// <c>VerShader.fx</c>, the game runs the checked-in <c>VerShader.xnb</c>, so the C# and the
    /// .fx can disagree silently and reading both is the only way to notice.
    /// </para>
    /// </summary>
    public class NeighbourhoodCityDrawTests
    {
        private const string Renderer =
            "Client/Simitone/Simitone.Client/Neighbourhood/NeighbourhoodTerrainRenderer.cs";

        // ---- every 3D draw applies its pass, not just the terrain's ----------------------------------
        //
        // The terrain draw had this bug and the facade draw had the same error message for two
        // different reasons, which is the part that makes a one-draw check worthless: the message
        // names a declaration/shader mismatch without saying which side is wrong, so "did it apply
        // its pass" and "is its declaration right" are separate questions that produce the same
        // symptom. These are the first; NeighbourhoodFacadeDrawTests is the second.

        public static IEnumerable<object[]> ThreeDimensionalDraws()
        {
            // Every draw in the 3D neighbourhood that binds a city technique and submits geometry,
            // with the token in that body which means "this reached the device". The token differs
            // because the two terrain draws submit through a private DrawRanges helper - they chunk
            // their submissions and the helper owns the DrawIndexedPrimitives call - while the
            // facades and the trees submit in the loop themselves. Pinning the token per draw is
            // what lets one rule cover both shapes without either being special-cased away.
            //
            // SimitoneSurroundings' surround terrain is the fourth: it is the in-lot backdrop's copy
            // of the terrain draw, on the same vertex type and the same technique, so a regression
            // there is the same bug in a different scene.
            yield return new object[]
            {
                "Client/Simitone/Simitone.Client/Neighbourhood/NeighbourhoodTerrainRenderer.cs",
                "NeighbourhoodTerrainRenderer", "Draw", "Matrix view", @"DrawRanges\("
            };
            // "BoundingFrustum frustum" rather than "Matrix view", because the facade draw's
            // delegating overload names a parameter called viewProjection and so contains "Matrix
            // view" as a substring - which would have made the hint select two overloads and leave
            // the choice to whichever happened to be last.
            yield return new object[]
            {
                "Client/Simitone/Simitone.Client/Neighbourhood/NeighbourhoodFacades.cs",
                "NeighbourhoodFacades", "Draw", "BoundingFrustum frustum", @"DrawIndexedPrimitives\("
            };
            yield return new object[]
            {
                "Client/Simitone/Simitone.Client/Neighbourhood/NeighbourhoodFoliage.cs",
                "NeighbourhoodFoliage", "Draw", "BoundingFrustum frustum", @"DrawIndexedPrimitives\("
            };
            yield return new object[]
            {
                "Client/Simitone/Simitone.Client/Neighbourhood/SimitoneSurroundings.cs",
                "SurroundTerrain", "Draw", "GraphicsDevice device", @"DrawIndexedPrimitives\("
            };
        }

        [Theory]
        [MemberData(nameof(ThreeDimensionalDraws))]
        public void EveryThreeDimensionalDrawAppliesItsPassBeforeItSubmitsGeometry(
            string relativePath, string typeName, string methodName, string parameterHint,
            string submissionPattern)
        {
            // The rule, stated as an invariant rather than a count: within the body of a draw that
            // submits geometry, no submission may be preceded by a shader-parameter change that was
            // not followed by an Apply. `pendingChanges` is that parameter change; it is set by every
            // SetValue and cleared only by an Apply, and a DrawIndexedPrimitives while it is set is
            // a submission that reached the device with the previous draw's constant buffers.
            //
            // This is the shape the fix takes, not a search for the word Apply. The terrain re-applies
            // per layer because each layer swaps the texture on sampler 0; the facades re-apply per
            // surface because each surface swaps ObjTex; the trees and the surround terrain apply
            // once because nothing inside their loops changes a parameter. A test that demanded
            // "applies >= submissions" would fail the last two, which are correct as they are, and
            // the fix would then be to add a pointless Apply per chunk rather than to fix a bug.
            var body = MethodBody(relativePath, typeName, methodName, parameterHint);

            int pendingChanges = 0;
            int applies = 0;
            int submissions = 0;
            int firstApply = -1;
            int firstSubmission = -1;
            foreach (var hit in Statements(body, submissionPattern))
            {
                switch (hit.Kind)
                {
                    case StatementKind.ParameterChange:
                        pendingChanges++;
                        break;
                    case StatementKind.Apply:
                        applies++;
                        if (firstApply < 0) firstApply = hit.Index;
                        //An apply is the only thing that carries a parameter to the pipeline, so it
                        //also discharges everything that was waiting for one.
                        pendingChanges = 0;
                        break;
                    case StatementKind.Submission:
                        submissions++;
                        if (firstSubmission < 0) firstSubmission = hit.Index;
                        Assert.True(firstApply >= 0,
                            typeName + "." + methodName + " submits geometry without ever applying a"
                            + " pass. Effect.CurrentTechnique and the parameters reach nothing without"
                            + " EffectPass.Apply, so this draw would be submitted against whatever"
                            + " vertex shader the previous draw left bound, and the device would"
                            + " reject the input layout.");
                        Assert.True(pendingChanges == 0,
                            typeName + "." + methodName + " changes a shader parameter and then"
                            + " submits without re-applying its pass (statement offset " + hit.Index
                            + "). The changed value does not reach the pipeline, so the submission is"
                            + " drawn with the previous one's texture or constant buffer.");
                        break;
                }
            }

            Assert.True(submissions > 0,
                typeName + "." + methodName + " was expected to submit geometry (no "
                + submissionPattern + " found in the body that takes a " + parameterHint + "). If the"
                + " draw was renamed or restructured, this needs to follow it rather than pass"
                + " vacuously - a check that finds nothing to check is worse than none.");
            Assert.True(applies > 0,
                typeName + "." + methodName + " submits " + submissions + " time(s) with no Apply in"
                + " sight.");
            Assert.True(firstApply < firstSubmission,
                typeName + "." + methodName + " applies its first pass at offset " + firstApply
                + " but first submits at " + firstSubmission + ".");
        }

        [Fact]
        public void TheFacadeDrawIsTheOneThatAppliesBothPassesPerSurface()
        {
            // Pinned specifically, because it is the only draw in the neighbourhood that has to
            // re-apply for a reason the others do not. Every facade surface carries its own wall,
            // roof or floor texture, and they all go through one sampler as ObjTex - so a facade is
            // three submissions with three different textures between them, and a pass applied once
            // before the loop draws the first surface with its texture and the other two with
            // whatever the last bind left. The fix is Apply per surface, and this is what stops it
            // being "optimised" back to once.
            //
            // Two applies per surface, not one: the vertex pass binds the vertex shader and the
            // pixel pass the pixel one, and a facade is the one draw here that re-applies inside its
            // loop. Re-applying only the pixel one would leave the vertex shader up from the
            // pre-loop apply, which works - and which is one edit away from not working the moment
            // anything else binds a shader in between.
            const string facades =
                "Client/Simitone/Simitone.Client/Neighbourhood/NeighbourhoodFacades.cs";
            //Raw, because the parameter names are string literals and the stripped body blanks them -
            //which is the right behaviour for the pass check above and the wrong one here.
            var body = RawMethodBody(facades, "NeighbourhoodFacades", "Draw", "BoundingFrustum frustum");

            var objTexBinds = Regex.Matches(body, @"Parameters\s*\[\s*""ObjTex""\s*\]").Count;
            var vertexApplies = Regex.Matches(body, @"vs\.Passes\[[^\]]+\]\.Apply\s*\(\s*\)").Count;
            var pixelApplies = Regex.Matches(body, @"ps\.Passes\[[^\]]+\]\.Apply\s*\(\s*\)").Count;

            Assert.True(objTexBinds > 0,
                "NeighbourhoodFacades.Draw no longer binds ObjTex per surface; the test above can no"
                + " longer tell whether the pass is re-applied for each texture.");
            Assert.True(pixelApplies >= objTexBinds,
                "every ObjTex bind in NeighbourhoodFacades.Draw needs the pixel pass applied after it ("
                + objTexBinds + " ObjTex binds, " + pixelApplies + " pixel-pass applies). A surface"
                + " submitted with the previous surface's texture is a house with the wrong walls.");
            Assert.True(vertexApplies >= objTexBinds,
                "every ObjTex bind in NeighbourhoodFacades.Draw needs the vertex pass applied after it"
                + " too (" + objTexBinds + " ObjTex binds, " + vertexApplies + " vertex-pass"
                + " applies). The device builds the input layout from whichever vertex shader is"
                + " bound, so leaving it up from a pre-loop apply is a draw that works until"
                + " something else binds a shader.");
        }

        // ---- the draw call's arguments --------------------------------------------------------------

        [Fact]
        public void ARangeBecomesADrawCallWhoseStartIndexIsTheStartIndexAndNotTheBaseVertex()
        {
            // DrawIndexedPrimitives(PrimitiveType, baseVertex, startIndex, primitiveCount). Two of
            // those are adjacent integers meaning different things, and the renderer used to pass
            // (TriangleList, range.StartIndex, 0, count): the index offset went in as the vertex
            // offset and the index offset stayed at zero. Every chunk after the first then read from
            // the wrong place in the vertex buffer, and the last one read past its end. Nothing
            // threw - which is why a neighbourhood with whole chunks missing looked like a culling
            // bug - so the arithmetic is pinned here instead.
            var range = new NeighbourhoodMeshDrawRange(5, 8, 1234, 90);
            var call = range.ToDrawCall();

            Assert.Equal(0, call.BaseVertex);
            Assert.Equal(1234, call.StartIndex);
            Assert.Equal(30, call.PrimitiveCount);
            // and the two ways of asking for it agree, so a call site cannot pick the wrong one
            Assert.Equal(call.ToString(), NeighbourhoodDrawCall.ForRange(range).ToString());
        }

        [Fact]
        public void ARangesPrimitiveCountIsItsIndexCountOverThree()
        {
            Assert.Equal(30, new NeighbourhoodMeshDrawRange(0, 1, 0, 90).PrimitiveCount);
            Assert.Equal(0, new NeighbourhoodMeshDrawRange(0, 1, 0, 2).PrimitiveCount);
        }

        [Fact]
        public void TheWholeBufferCallStartsAtTheStartWithNoVertexOffset()
        {
            // The pre-culling path, the one that was always right, still has to be expressible: no
            // chunk bookkeeping means no range, and the buffer is drawn whole.
            var call = NeighbourhoodDrawCall.WholeBuffer(12345);

            Assert.Equal(0, call.BaseVertex);
            Assert.Equal(0, call.StartIndex);
            Assert.Equal(4115, call.PrimitiveCount);
        }

        [Fact]
        public void AnEmptyRangeIsACallThatSubmitsNothing()
        {
            Assert.True(new NeighbourhoodMeshDrawRange(2, 2, 40, 0).ToDrawCall().IsEmpty);
            Assert.False(new NeighbourhoodMeshDrawRange(2, 3, 40, 3).ToDrawCall().IsEmpty);
        }

        [Fact]
        public void TheDrawCallDescribesItselfInTheOrderTheOverloadTakes()
        {
            Assert.Equal("baseVertex 0, startIndex 42, primitiveCount 6",
                new NeighbourhoodDrawCall(0, 42, 6).ToString());
        }

        // ---- the ranges against the buffer they are read out of ------------------------------------

        [Fact]
        public void EveryChunkVisibleIsOneRangeOverTheWholeIndexBuffer()
        {
            // A 512x512 map at full screen: all 1024 chunks, so the cull's answer must cost exactly
            // the one draw call it always cost. This is the arithmetic the transposed arguments
            // broke: with baseVertex carrying the start index, even a single range over the whole
            // buffer reads from the middle of the vertex buffer instead of its start.
            var chunks = new List<int>(NeighbourhoodCityMeshBuilder.ChunkCount);
            for (int i = 0; i < NeighbourhoodCityMeshBuilder.ChunkCount; i++) chunks.Add(i);
            var ranges = new List<NeighbourhoodMeshDrawRange>();
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(ChunkStarts(6), chunks, ranges);

            Assert.Single(ranges);
            Assert.Equal(0, ranges[0].StartIndex);
            Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount * 6, ranges[0].IndexCount);

            var call = NeighbourhoodDrawCall.ForRange(ranges[0]);
            Assert.Equal(0, call.BaseVertex);
            Assert.Equal(0, call.StartIndex);
            Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount * 6 / 3, call.PrimitiveCount);
        }

        [Fact]
        public void RangesOfAPartlyVisibleMapCoverExactlyTheVisibleChunksAndNothingElse()
        {
            // The property the transposition broke: read as (baseVertex 0, startIndex, count) the
            // ranges have to tile the visible chunks' spans exactly, disjointly, and never past the
            // end of the buffer.
            var starts = ChunkStarts(6);
            var total = starts[NeighbourhoodCityMeshBuilder.ChunkCount];
            var visible = new List<int> { 0, 1, 2, 7, 8, 40, 41, 1023 };
            var ranges = new List<NeighbourhoodMeshDrawRange>();
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, visible, ranges);

            var covered = new List<int>();
            foreach (var range in ranges)
            {
                var call = NeighbourhoodDrawCall.ForRange(range);
                Assert.Equal(0, call.BaseVertex);
                Assert.Equal(range.StartIndex, call.StartIndex);
                Assert.InRange(call.StartIndex + call.PrimitiveCount * 3, 0, total);
                for (int i = 0; i < call.PrimitiveCount * 3; i++) covered.Add(call.StartIndex + i);
            }

            var expected = new List<int>();
            foreach (var chunk in visible)
            {
                for (int i = starts[chunk]; i < starts[chunk + 1]; i++) expected.Add(i);
            }
            Assert.Equal(expected, covered);
        }

        [Fact]
        public void ADisjointVisibleSetCostsMoreRangesButTheSameIndices()
        {
            // Every other chunk: 512 runs, and the union of the ranges is exactly the even chunks.
            var starts = ChunkStarts(6);
            var total = starts[NeighbourhoodCityMeshBuilder.ChunkCount];
            var visible = new List<int>();
            for (int i = 0; i < NeighbourhoodCityMeshBuilder.ChunkCount; i += 2) visible.Add(i);
            var ranges = new List<NeighbourhoodMeshDrawRange>();
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, visible, ranges);

            Assert.Equal(512, ranges.Count);
            int totalIndices = 0;
            foreach (var range in ranges)
            {
                var call = NeighbourhoodDrawCall.ForRange(range);
                Assert.Equal(0, call.BaseVertex);
                Assert.Equal(range.StartIndex, call.StartIndex);
                totalIndices += call.PrimitiveCount * 3;
                Assert.InRange(call.StartIndex + call.PrimitiveCount * 3, 0, total);
            }
            Assert.Equal(512 * 6, totalIndices);
        }

        /// <summary>Six indices per chunk, so a range's span is legible in the assertions.</summary>
        private static int[] ChunkStarts(int indicesPerChunk)
        {
            var starts = new int[NeighbourhoodCityMeshBuilder.ChunkCount + 1];
            for (int i = 0; i < starts.Length; i++) starts[i] = i * indicesPerChunk;
            return starts;
        }

        // ---- the declaration, against the shader it is drawn with ------------------------------------

        [Fact]
        public void TheDeclarationIsTheOneTsoDrawsTLayerVertexWith()
        {
            // Read off the declaration rather than trusted: the offsets are the whole point.
            // RenderNCity reads SV_Position (x, y, z), TEXCOORD0 (ground u, v, blend mask u, v) and
            // TEXCOORD1 (normal x, y, z, transparency), so the declaration has to be a
            // three-component position at byte 0 followed by two four-component texture
            // coordinates at 12 and 28, ending exactly on the vertex size and leaving no gap.
            var elements = NeighbourhoodTerrainVertex.VertexElements.GetVertexElements();

            Assert.Equal(3, elements.Length);
            Assert.Equal(VertexElementUsage.Position, elements[0].VertexElementUsage);
            Assert.Equal(0, elements[0].UsageIndex);
            Assert.Equal(VertexElementFormat.Vector3, elements[0].VertexElementFormat);
            Assert.Equal(0, elements[0].Offset);
            Assert.Equal(VertexElementUsage.TextureCoordinate, elements[1].VertexElementUsage);
            Assert.Equal(0, elements[1].UsageIndex);
            Assert.Equal(VertexElementFormat.Vector4, elements[1].VertexElementFormat);
            Assert.Equal(12, elements[1].Offset);
            Assert.Equal(VertexElementUsage.TextureCoordinate, elements[2].VertexElementUsage);
            Assert.Equal(1, elements[2].UsageIndex);
            Assert.Equal(VertexElementFormat.Vector4, elements[2].VertexElementFormat);
            Assert.Equal(28, elements[2].Offset);

            // the last element ends on the vertex, so nothing is read past the buffer
            Assert.Equal(28 + 4 * sizeof(float), NeighbourhoodTerrainVertex.SizeInBytes);
        }

        [Fact]
        public void TheStructIsStillElevenFloatsOfTLayerVertex()
        {
            // The mesh has to stay the one CityGeometry builds, byte for byte: FSO's own city draw
            // is the only thing that has ever drawn this buffer, and it is what the shader is
            // written for. A "fix" that widened the position to a float4 would have stopped
            // describing TSO's layout, and would not have fixed the blank screen.
            // position xyz, ground uv, blend mask uv, normal xyz, transparency: eleven floats
            Assert.Equal(11 * sizeof(float), NeighbourhoodTerrainVertex.SizeInBytes);
            Assert.Equal((3 + 2 + 2 + 3 + 1) * sizeof(float),
                NeighbourhoodTerrainVertex.SizeInBytes);        }

        [Fact]
        public void TheDeclarationIsDescribedForTheLogWithoutRenamingAnything()
        {
            // The diagnostic the draw throws into has to name the declaration, because MonoGame's
            // message names the failure without naming the thing it was compared against.
            var described = NeighbourhoodTerrainVertex.DescribeDeclaration();

            Assert.Contains("Position0 Vector3 at byte 0", described, StringComparison.Ordinal);
            Assert.Contains("TextureCoordinate0 Vector4 at byte 12", described, StringComparison.Ordinal);
            Assert.Contains("TextureCoordinate1 Vector4 at byte 28", described, StringComparison.Ordinal);
        }

        // ---- the pass the draw has to apply ----------------------------------------------------------

        [Fact]
        public void TheTerrainDrawAppliesThePassBeforeEveryRangeItSubmits()
        {
            // The finding. BeginCity hands back the pass to Apply after each texture change, the
            // sibling in-lot terrain drawer (SimitoneSurroundings) applies it on exactly this
            // pattern, and this one did not - so the draw reached the device with the previous
            // draw's vertex shader still bound, the input layout check rejected the declaration, and
            // the neighbourhood never promoted off the painted map.
            var body = FullDrawBody();

            var applies = Regex.Matches(body, @"NeighbourhoodShaderState\.ApplyCity\(").Count;
            var submits = Regex.Matches(body, @"DrawRanges\(").Count;

            Assert.True(applies >= 2,
                "NeighbourhoodTerrainRenderer.Draw must apply the city pass before it draws: the"
                + " ground layers and the roads are two different textures on the same sampler, so"
                + " the pass has to be applied for each, and applying it nowhere is what made the"
                + " device validate this vertex declaration against the previous draw's shader ("
                + applies + " ApplyCity calls, " + submits + " DrawRanges calls in the full Draw).");
            Assert.True(applies >= submits,
                "every DrawRanges in NeighbourhoodTerrainRenderer.Draw needs an ApplyCity before it ("
                + applies + " ApplyCity calls, " + submits + " DrawRanges calls in the full Draw).");
        }

        [Fact]
        public void TheTerrainDrawBindsTheCityTechniqueNotTheObjectOne()
        {
            // The two techniques this file knows about read different vertices, so which one is bound
            // is the whole question. RenderNCity takes a float4 position, a float4 texture coordinate
            // and a float4 normal-plus-transparency; RenderCityObj takes a float4 position, a
            // float2 and a float3, and is what the facades and the trees are drawn with - so leaving
            // it bound is what the input layout check was rejecting.
            Assert.Equal("RenderNCity", NeighbourhoodShaderState.CityTechnique);
            Assert.Equal("RenderCityObj", NeighbourhoodShaderState.CityObjectTechnique);
            Assert.NotEqual(NeighbourhoodShaderState.CityTechnique,
                NeighbourhoodShaderState.CityObjectTechnique);
        }

        [Fact]
        public void TheFogPassIsAPassTheCityTechniquesActuallyHave()
        {
            // The transparency the fade skirt is written into is only read by the fog pass, so this
            // index is not cosmetic - and an index past the end of the technique is an
            // ArgumentOutOfRangeException on the first frame that draws.
            Assert.InRange(NeighbourhoodShaderState.FogPass, 0, 4);
        }

        // ---- the shipped shader, read back off disk --------------------------------------------------

        [Fact]
        public void TheShippedVerShaderDeclaresTheInputTheDeclarationProvides()
        {
            // VerShader.fx is the source of the checked-in VerShader.xnb - the build does not compile
            // it, which is the same thing GrassPassSelectionTests pins for GrassShader - so this is
            // the one place the C# and the shader are read side by side. RenderNCity's input struct
            // is CityVertex, and the declaration above satisfies it: one position, one four-float
            // texture coordinate (ground uv in xy, blend mask in zw) and one four-float normal with
            // the transparency in w. All three are read; there is none to drop.
            var source = File.ReadAllText(
                Path.Combine(ContentDirectory(), "ContentSrc", "Effects", "VerShader.fx"));

            var start = source.IndexOf("struct CityVertex", StringComparison.Ordinal);
            Assert.True(start > 0, "struct CityVertex not found in VerShader.fx");
            var end = source.IndexOf("};", start, StringComparison.Ordinal);
            Assert.True(end > start, "the end of struct CityVertex not found in VerShader.fx");
            var body = source.Substring(start, end - start);

            Assert.Matches(@"float4\s+VertexPosition\s*:\s*SV_Position0\s*;", body);
            Assert.Matches(@"float4\s+TextureCoords\s*:\s*TEXCOORD0\s*;", body);
            Assert.Matches(@"float4\s+NormalTrans\s*:\s*TEXCOORD1\s*;", body);

            // and the technique that reads it is the one the renderer asks for
            Assert.Contains("technique " + NeighbourhoodShaderState.CityTechnique, source,
                StringComparison.Ordinal);
        }

        [Fact]
        public void TheShippedPixShaderSubtractsTheTransparencyFromOne()
        {
            // The other half of the same input: NormalTrans.w is a transparency, so zero is opaque
            // and one is invisible. Getting it backwards produces a mesh that submits triangles and
            // draws nothing at all, which is the failure this file exists to stop recurring.
            var source = File.ReadAllText(
                Path.Combine(ContentDirectory(), "ContentSrc", "Effects", "PixShader.fx"));

            Assert.Contains("BCol.w *= (1.0 - Input.NormalTrans.w);", source, StringComparison.Ordinal);
        }

        // ---- helpers ---------------------------------------------------------------------------------

        private enum StatementKind
        {
            /// <summary>A shader parameter was written, which needs a pass apply to take effect.</summary>
            ParameterChange,

            /// <summary>An <c>EffectPass.Apply</c>, which carries the pending parameter changes.</summary>
            Apply,

            /// <summary>Geometry handed to the device.</summary>
            Submission
        }

        private readonly struct Statement
        {
            public readonly StatementKind Kind;
            public readonly int Index;

            public Statement(StatementKind kind, int index)
            {
                Kind = kind;
                Index = index;
            }
        }

        /// <summary>
        /// Every parameter write, pass apply and geometry submission in a method body, in source
        /// order.
        /// </summary>
        /// <remarks>
        /// Ordered rather than counted, because the question is not "how many applies are there" but
        /// "is any submission preceded by an unapplied change" - a count cannot answer that, and an
        /// answer to the wrong question is how a correct draw gets made to look wrong. Comments and
        /// literals are blanked before this runs, so the word <c>Apply</c> in a comment about not
        /// applying is not a submission.
        /// </remarks>
        /// <param name="body">The method body, already stripped of comments and literals.</param>
        /// <param name="submissionPattern">
        /// The token that means "this reached the device" in this body. Parameterised because the
        /// two terrain draws submit through a private <c>DrawRanges</c> helper while the facades and
        /// the trees call <c>DrawIndexedPrimitives</c> in their own loop, and a rule that only
        /// recognised one of the two would pass the terrain by not looking at it.
        /// </param>
        private static IEnumerable<Statement> Statements(string body, string submissionPattern)
        {
            var found = new List<Statement>();
            // ApplyCity is the shared helper the two terrain draws go through, and it is the same
            // effect as Apply - it calls EffectPass.Apply on both effects - so it counts. Matching
            // only a literal ".Apply(" would find nothing in either of them and pass vacuously,
            // which is the failure mode this whole file exists to prevent.
            var pattern = @"Parameters\s*\[[^\]]*\]\s*\.\s*SetValue\s*\("
                + @"|NeighbourhoodShaderState\.ApplyCity\s*\("
                + @"|\.Apply\s*\(\s*\)"
                + @"|" + submissionPattern;
            foreach (Match hit in Regex.Matches(body, pattern))
            {
                var text = hit.Value;
                StatementKind kind;
                if (text.StartsWith("Parameters", StringComparison.Ordinal)) kind = StatementKind.ParameterChange;
                else if (text.StartsWith("NeighbourhoodShaderState", StringComparison.Ordinal)
                    || text.StartsWith(".Apply", StringComparison.Ordinal)) kind = StatementKind.Apply;
                else kind = StatementKind.Submission;
                found.Add(new Statement(kind, hit.Index));
            }
            return found;
        }

        /// <summary>
        /// The body of a named method, from its opening brace to the one that closes it.
        /// </summary>
        /// <param name="relativePath">The file, from the Simitone root.</param>
        /// <param name="typeName">The type the method is in.</param>
        /// <param name="methodName">The method.</param>
        /// <param name="parameterHint">
        /// A parameter the overload must take. Several of these draws are overloaded, and the one
        /// that delegates to another is not the one that submits anything, so the hint is what tells
        /// them apart without hard-coding a signature that a refactor would change for no reason.
        /// </param>
        private static string MethodBody(
            string relativePath, string typeName, string methodName, string parameterHint)
        {
            var text = StripCommentsAndStrings(File.ReadAllText(Path.Combine(Root(), relativePath)));
            return Slice(text, MethodBodyOffset(text, typeName, methodName, parameterHint, relativePath));
        }

        /// <summary>
        /// The same method body, with the comments and string literals left in.
        /// </summary>
        /// <remarks>
        /// The stripping preserves every offset by replacing content with spaces rather than
        /// deleting it, so a body located in the stripped text is the same span of the original. That
        /// is what lets a test need both views: <c>StripCommentsAndStrings</c> is what stops a word
        /// in a comment about not applying a pass from counting as an apply, and it also blanks
        /// string literals - so a check that is <em>about</em> a parameter name has to read the
        /// unstripped text or it will never find <c>Parameters["ObjTex"]</c> at all.
        /// </remarks>
        private static string RawMethodBody(
            string relativePath, string typeName, string methodName, string parameterHint)
        {
            var raw = File.ReadAllText(Path.Combine(Root(), relativePath));
            var offset = MethodBodyOffset(
                StripCommentsAndStrings(raw), typeName, methodName, parameterHint, relativePath);
            return raw.Substring(offset, BodyLength(StripCommentsAndStrings(raw), offset));
        }

        private static int MethodBodyOffset(
            string text, string typeName, string methodName, string parameterHint, string relativePath)
        {
            var typeIndex = text.IndexOf("class " + typeName, StringComparison.Ordinal);
            Assert.True(typeIndex > 0, "class " + typeName + " not found in " + relativePath);

            var found = -1;
            foreach (Match header in Regex.Matches(text.Substring(typeIndex),
                @"\b" + methodName + @"\s*\([^;{]*\)\s*\{"))
            {
                if (header.Value.Contains(parameterHint))
                {
                    found = typeIndex + header.Index + header.Length - 1;
                }
            }

            Assert.True(found > 0,
                "the " + typeName + "." + methodName + " overload taking a " + parameterHint
                + " was not found in " + relativePath);
            return found;
        }

        private static int BodyLength(string text, int openBrace)
        {
            return Slice(text, openBrace).Length;
        }

        /// <summary>
        /// The body of the real <c>Draw</c> - the last of the three overloads, the one that does the
        /// work rather than delegating - from its opening brace to the one that closes it, with
        /// comments and literals blanked out first so a brace inside either cannot end the search
        /// early. Two of the three take a <c>BoundingFrustum</c>, so the last one is what
        /// distinguishes them.
        /// </summary>
        private static string FullDrawBody()
        {
            return MethodBody(Renderer, "NeighbourhoodTerrainRenderer", "Draw", "BoundingFrustum");
        }

        private static string Slice(string text, int openBrace)
        {
            var depth = 0;
            for (int i = openBrace; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(openBrace, i - openBrace + 1);
            }

            throw new InvalidOperationException("unbalanced braces from offset " + openBrace);
        }

        /// <summary>
        /// Replaces the contents of comments, string and character literals with spaces, keeping
        /// every offset, so a brace or a word inside one cannot move the end of a method body.
        /// </summary>
        private static string StripCommentsAndStrings(string source)
        {
            var chars = source.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
                {
                    while (i < chars.Length && chars[i] != '\n') chars[i++] = ' ';
                    continue;
                }
                if (chars[i] == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
                {
                    var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? chars.Length : end + 2;
                    for (; i < end; i++) if (chars[i] != '\n') chars[i] = ' ';
                    i--;
                    continue;
                }
                if (chars[i] == '"' || chars[i] == '\'')
                {
                    var quote = chars[i];
                    for (i++; i < chars.Length && chars[i] != quote; i++)
                    {
                        if (chars[i] == '\\') i++;
                        if (chars[i] != '\n') chars[i] = ' ';
                    }
                    continue;
                }
            }
            return new string(chars);
        }

        /// <summary>
        /// The Simitone checkout, found by walking up out of the test output rather than by counting
        /// levels, so a change to the build layout does not turn these into "file not found".
        /// </summary>
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Client", "Simitone", "Simitone.Client",
                    "Simitone.Client.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                "Could not find Client/Simitone/Simitone.Client/Simitone.Client.csproj above "
                + AppContext.BaseDirectory);
        }

        /// <summary>
        /// The FreeSO <c>tso.content</c> folder, which holds <c>ContentSrc/Effects/VerShader.fx</c>.
        /// </summary>
        private static string ContentDirectory()
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
                "Could not find FreeSO/TSOClient/tso.content/FSO.Content.csproj above "
                + AppContext.BaseDirectory);
        }
    }
}
