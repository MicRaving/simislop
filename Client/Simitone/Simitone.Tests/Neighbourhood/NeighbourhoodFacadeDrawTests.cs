/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The lot facades' draw, as far as a headless build can see it.
    ///
    /// <para>
    /// <b>The bug, and why it looked exactly like the terrain's.</b> The terrain draw threw
    /// <c>"the current vertex declaration does not include all the elements required by the current
    /// vertex shader"</c> and was fixed by applying its pass. The facades then threw the same
    /// message from the same method signature, and applying the pass was not the answer - they were
    /// already applying theirs. MonoGame's message names a mismatch without saying which side of it
    /// is wrong, so two different causes produce one identical line, and the first one fixed is the
    /// one that hides the second.
    /// </para>
    ///
    /// <para>
    /// <b>The second cause.</b> A facade is drawn on <c>RenderCityObj</c>, whose input struct is
    /// <c>ObjVertexIn</c>: a <c>float4</c> position, a <c>float2</c> texture coordinate and a
    /// <c>float3</c> in <c>TEXCOORD1</c> - three elements. The surfaces were uploaded as
    /// <see cref="VertexPositionTexture"/>, which is a <c>float3</c> and a <c>float2</c>: two
    /// elements and no <c>TEXCOORD1</c> at all. D3D11 rejects an input layout that does not carry
    /// every element the shader reads, so the draw failed with a correct pass bound. Applying the
    /// pass is what made the declaration get checked at all.
    /// </para>
    ///
    /// <para>
    /// <b>Why reading source and reading the shipped <c>.fx</c> are the only ways to see it.</b> A
    /// headless build has no graphics device, so there is no pipeline to observe: the declaration
    /// is a property of a <c>VertexBuffer</c> created behind one, and whether it satisfies a shader
    /// is a question about two things that are only ever compared on a real machine. The declaration
    /// is a static field, though, and the shader is a checked-in file, so both can be read directly -
    /// which is the same trade <c>NeighbourhoodCityDrawTests</c> already makes, and for the same
    /// reason: the build does not compile <c>VerShader.fx</c>, the game runs the checked-in
    /// <c>VerShader.xnb</c>, so the C# and the .fx can disagree silently and reading both is the
    /// only way to notice.
    /// </para>
    /// </summary>
    public class NeighbourhoodFacadeDrawTests
    {
        // ---- the declaration, against the shader it is validated against ------------------------

        [Fact]
        public void TheFacadeDeclarationCarriesEveryElementTheObjectVertexReads()
        {
            // The whole fix, as a single assertion: the declaration the facades are uploaded and
            // drawn with has a position, a texture coordinate and a second texture coordinate, which
            // is what ObjVertexIn declares. A VertexPositionTexture would fail this - it has the
            // first two and no TEXCOORD1 - which is the bug.
            Assert.True(NeighbourhoodFacadeVertex.SatisfiesObjectInput(
                    NeighbourhoodFacadeVertex.ObjectDeclaration),
                "the declaration the facades are drawn with is "
                + NeighbourhoodFacadeVertex.DescribeDeclaration()
                + ", which does not carry every element "
                + NeighbourhoodFacadeVertex.ObjectInputStruct + " reads.");
        }

        [Fact]
        public void TheDeclarationTheFacadesUsedToBeUploadedWithIsTheOneThatFails()
        {
            // The negative, so the positive above cannot be satisfied by a rule that accepts
            // anything. VertexPositionTexture is what LotFacadeGenerator produces and what the
            // surfaces used to be uploaded as; it is the exact regression this file exists for, so
            // it is pinned as the counter-example rather than left as a comment.
            var wrong = new VertexDeclaration(
                new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
                new VertexElement(12, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));

            Assert.False(NeighbourhoodFacadeVertex.SatisfiesObjectInput(wrong));
            Assert.False(NeighbourhoodFacadeVertex.SatisfiesObjectInput(null));
        }

        [Fact]
        public void TheFacadeDeclarationIsDgrpVertsOwnThreeElementsWithNoGaps()
        {
            // Read off the declaration rather than trusted, the offsets being the whole point:
            // DGRP3DVert is CityFoliage's own object vertex, laid out position / texture coordinate /
            // normal with nothing between them and nothing past the end, and it is the layout the
            // effect was written for - so re-using it is the fix rather than a coincidence.
            var elements = NeighbourhoodFacadeVertex.ObjectDeclaration.GetVertexElements();

            Assert.Equal(NeighbourhoodFacadeVertex.ObjectInputElementCount, elements.Length);
            Assert.Equal(VertexElementUsage.Position, elements[0].VertexElementUsage);
            Assert.Equal(0, elements[0].UsageIndex);
            Assert.Equal(VertexElementFormat.Vector3, elements[0].VertexElementFormat);
            Assert.Equal(0, elements[0].Offset);
            Assert.Equal(VertexElementUsage.TextureCoordinate, elements[1].VertexElementUsage);
            Assert.Equal(0, elements[1].UsageIndex);
            Assert.Equal(VertexElementFormat.Vector2, elements[1].VertexElementFormat);
            Assert.Equal(12, elements[1].Offset);
            Assert.Equal(VertexElementUsage.TextureCoordinate, elements[2].VertexElementUsage);
            Assert.Equal(1, elements[2].UsageIndex);
            Assert.Equal(VertexElementFormat.Vector3, elements[2].VertexElementFormat);
            Assert.Equal(20, elements[2].Offset);

            // the last element ends on the vertex, so nothing is read past the buffer
            Assert.Equal(20 + 3 * sizeof(float), NeighbourhoodFacadeVertex.SizeInBytes);
        }

        [Fact]
        public void TheShippedVerShaderDeclaresTheInputTheFacadeDeclarationProvides()
        {
            // VerShader.fx is the source of the checked-in VerShader.xnb, and the build does not
            // compile it, so this is the one place the C# and the shader are read side by side.
            // ObjVertexIn is RenderCityObj's input struct.
            var source = File.ReadAllText(
                Path.Combine(ContentDirectory(), "ContentSrc", "Effects", "VerShader.fx"));

            // Balanced braces rather than a search for "};", because the struct's own fields have no
            // braces and the next thing that does is the following struct - so a search for the
            // first "};" would find this one's and the assertions would be reading half of it.
            var body = ShaderBlock(source,
                "struct " + NeighbourhoodFacadeVertex.ObjectInputStruct);

            Assert.Matches(@"float4\s+position\s*:\s*SV_Position0\s*;", body);
            Assert.Matches(@"float2\s+texCoord\s*:\s*TEXCOORD0\s*;", body);
            Assert.Matches(@"float3\s+normal\s*:\s*TEXCOORD1\s*;", body);

            // and the technique the facades ask for is the one that declares that input
            Assert.Contains("technique " + NeighbourhoodShaderState.CityObjectTechnique, source,
                StringComparison.Ordinal);
        }

        [Fact]
        public void ThePassTheFacadesAreDrawnWithReadsEveryElementItDeclares()
        {
            // Because "the declaration has three elements" only helps if the shader needs three. A
            // shader that ignored TEXCOORD1 would have compiled the broken draw fine and the field
            // log would have said the same thing either way - so the point is not the declaration,
            // it is that the declaration and the shader agree. CityObjFogVS is the vertex pass
            // these facades are on; the other two are checked rather than assumed because the
            // technique's passes share one input struct and a fix that only holds for one of them
            // is a fix that works until someone switches pass.
            var source = File.ReadAllText(
                Path.Combine(ContentDirectory(), "ContentSrc", "Effects", "VerShader.fx"));

            // CityObjNoShadVS is the one that reads all three directly; the other two are built on
            // it, so the check is that they delegate rather than re-deriving the transform. Reading
            // a delegate's body for its callee's names would pass for any of the three, which is
            // why the delegation is asserted instead.
            var direct = ShaderBody(source, "CityObjNoShadVS");
            Assert.Contains("Input.position", direct);
            Assert.Contains("Input.texCoord", direct);
            Assert.Contains("Input.normal", direct);

            foreach (var built in new[] { "CityObjVS", "CityObjFogVS" })
            {
                Assert.Contains("CityObjNoShadVS(Input)",
                    ShaderBody(source, built, includeSignature: false));
            }

            // and the trees read the normal slot as a translation instead of a direction, which is
            // why they are on their own pass and why the same declaration suits both
            var tree = ShaderBody(source, "TreeVS");
            Assert.Contains("Input.normal", tree);
            Assert.Contains("Input.texCoord", tree);
        }

        [Fact]
        public void TheObjectPixelPassTheFacadesUseDoesNotSampleTheShadowMap()
        {
            // Why FinalFog and not Final. CityObjPS reads ShadSampler, and the neighbourhood scene
            // has no shadow map bound - a lot facade is not a shadow caster in this scene, so
            // nothing sets one. FinalFog's CityObjPSFog reads the object's colour, the light colour
            // and the fog and nothing else, so taking the distance fade costs no state the scene
            // does not have. This is the reason the pass index is not cosmetic, and why it is 3.
            var source = File.ReadAllText(
                Path.Combine(ContentDirectory(), "ContentSrc", "Effects", "PixShader.fx"));

            var fogged = ShaderBody(source, "CityObjPSFog");
            Assert.Contains("GetObjColor", fogged);
            Assert.Contains("FogColor", fogged);
            Assert.DoesNotContain("ShadSampler", fogged);
            Assert.DoesNotContain("shadowLerp", fogged);

            // and the pass the facades would have used before, which does sample it
            Assert.Contains("ShadSampler", ShaderBody(source, "CityObjPS"));
        }

        // ---- the conversion the upload goes through -------------------------------------------------

        [Fact]
        public void AConvertedSurfaceKeepsItsGeometryAndGainsAnObjectNormal()
        {
            // The conversion has to be a widening, not a change: a facade's vertices are already
            // placed in world tiles by the lot's own generator, and moving one by a millimetre puts
            // a house in its neighbour's wall.
            var placed = new[]
            {
                new VertexPositionTexture(new Vector3(0f, 0f, 0f), new Vector2(0f, 0f)),
                new VertexPositionTexture(new Vector3(4f, 0f, 0f), new Vector2(1f, 0f)),
                new VertexPositionTexture(new Vector3(4f, 3f, 0f), new Vector2(1f, 1f)),
                new VertexPositionTexture(new Vector3(0f, 3f, 0f), new Vector2(0f, 1f))
            };
            var indices = new[] { 0, 1, 2, 0, 2, 3 };

            var converted = NeighbourhoodFacadeVertex.ToObjectVertices(placed, indices);

            Assert.NotNull(converted);
            Assert.Equal(placed.Length, converted.Length);
            for (int i = 0; i < placed.Length; i++)
            {
                Assert.Equal(placed[i].Position, converted[i].Position);
                Assert.Equal(placed[i].TextureCoordinate, converted[i].TextureCoordinate);
            }
            // every normal is a unit vector, so the shader can normalize it without producing a NaN
            for (int i = 0; i < converted.Length; i++)
            {
                Assert.Equal(1f, converted[i].Normal.Length(), 3);
            }
        }

        [Fact]
        public void AVertexNoTriangleReachesStillGetsAFiniteNormal()
        {
            // The degenerate case, and the reason the normals are seeded rather than accumulated
            // from nothing: a vertex in no triangle has no face to take a normal from, and a zero
            // normal is one normalize() away from a NaN in the vertex shader.
            var placed = new[]
            {
                new VertexPositionTexture(Vector3.Zero, Vector2.Zero),
                new VertexPositionTexture(Vector3.UnitX, Vector2.One),
                new VertexPositionTexture(Vector3.UnitY, new Vector2(0.5f, 0.5f))
            };

            // index 2 is deliberately absent from the list
            var converted = NeighbourhoodFacadeVertex.ToObjectVertices(placed, new[] { 0, 1, 0 });

            Assert.NotNull(converted);
            Assert.Equal(3, converted.Length);
            for (int i = 0; i < converted.Length; i++)
            {
                Assert.False(float.IsNaN(converted[i].Normal.X),
                    "vertex " + i + " has a NaN normal");
                Assert.False(float.IsInfinity(converted[i].Normal.X),
                    "vertex " + i + " has an infinite normal");
            }
        }

        [Fact]
        public void ASurfaceWithNothingInItConvertsToNullRatherThanToAnEmptyBuffer()
        {
            // A zero-length vertex buffer is not a legal thing to upload, so the conversion has to
            // answer null and let the caller treat it as "this surface has nothing in it".
            Assert.Null(NeighbourhoodFacadeVertex.ToObjectVertices(null, new[] { 0, 1, 2 }));
            Assert.Null(NeighbourhoodFacadeVertex.ToObjectVertices(
                new VertexPositionTexture[0], null));
        }

        [Fact]
        public void ASurfaceWithNoIndicesStillConvertsSoTheDeclarationIsNeverTheReasonItIsEmpty()
        {
            // The path the draw then refuses on IsDrawable, rather than a crash on a missing index.
            var placed = new[] { new VertexPositionTexture(Vector3.One, Vector2.Zero) };

            var converted = NeighbourhoodFacadeVertex.ToObjectVertices(placed, null);

            Assert.NotNull(converted);
            Assert.Single(converted);
        }

        // ---- the report, which is how any of this would be noticed ---------------------------------

        [Fact]
        public void TheFrameReportNamesTheFacadesSubmittedTrianglesRatherThanSayingThereWereSome()
        {
            // The gap this closes. The report said "facades drawn" off a cache count, so a
            // neighbourhood whose facade draw threw every frame reported a healthy line forever. A
            // world with 315,000 ground triangles and no houses on it has to be sayable in one
            // sentence, and "facades drawn" is not that sentence.
            var report = new NeighbourhoodFrameReport();
            report.Reset(1, Placement());
            report.Submit(315424, 946272, 6, 1024);
            report.SubmitProps(0, 0, 38, 4096);

            Assert.True(report.SubmittedGeometry);
            Assert.Equal(315424, report.Submitted);
            Assert.Equal(0, report.FacadeTriangles);
            Assert.Equal(38, report.FacadesVisible);
            // 38 lots were in the frustum and not one triangle came out of them: this is the frame
            // that promotes on terrain alone and still has no houses.
            Assert.False(report.FacadeDrawn);

            var text = report.ToString();
            Assert.Contains("facades 0 triangle(s)", text, StringComparison.Ordinal);
            Assert.Contains("38 visible lot(s)", text, StringComparison.Ordinal);
        }

        [Fact]
        public void AFrameThatSubmitsFacadesSaysHowManyAndTheTerrainCountIsStillThere()
        {
            // The same frame with the houses on it. The promotion gate is on the terrain's
            // submission and does not change; what changes is that the report can now show where the
            // picture came from, and the terrain's own number is not lost by adding the others.
            var report = new NeighbourhoodFrameReport();
            report.Reset(1, Placement());
            report.Submit(315424, 946272, 6, 1024);
            report.SubmitProps(4128, 114, 38, 28672);

            Assert.True(report.FacadeDrawn);
            Assert.Equal(4128, report.FacadeTriangles);
            Assert.Equal(114, report.FacadeDrawCalls);
            Assert.Equal(28672, report.FoliageTriangles);

            var text = report.ToString();
            Assert.Contains("terrain 315424 triangle(s)", text, StringComparison.Ordinal);
            Assert.Contains("facades 4128 triangle(s)", text, StringComparison.Ordinal);
            Assert.Contains("trees 28672 triangle(s)", text, StringComparison.Ordinal);
        }

        [Fact]
        public void AFrameReportThatNeverReachesTheDeviceReportsNothingForAnyOfTheThreePasses()
        {
            // Reset-first in every one of the three, which is what stops a frame that returned early
            // describing itself with the previous frame's numbers.
            var report = new NeighbourhoodFrameReport();
            report.Reset(1, Placement());
            report.Submit(100, 300, 1, 1);
            report.SubmitProps(50, 2, 1, 70);
            report.Reset(2, Placement());
            report.Skip("no generated world");

            Assert.Equal(0, report.Submitted);
            Assert.Equal(0, report.FacadeTriangles);
            Assert.Equal(0, report.FacadeDrawCalls);
            Assert.Equal(0, report.FacadesVisible);
            Assert.Equal(0, report.FoliageTriangles);
            Assert.False(report.SubmittedGeometry);
            Assert.Contains("no generated world", report.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void ANegativeCountIsNotCarriedIntoTheReport()
        {
            // A count is a count of a submission, and no submission is negative. Clamped here rather
            // than at each call site because the three passes are counted by three independent
            // owners and the report is the one place they meet.
            var report = new NeighbourhoodFrameReport();
            report.Reset(1, Placement());
            report.SubmitProps(-1, -1, -1, -1);

            Assert.Equal(0, report.FacadeTriangles);
            Assert.Equal(0, report.FacadeDrawCalls);
            Assert.Equal(0, report.FacadesVisible);
            Assert.Equal(0, report.FoliageTriangles);
            Assert.False(report.FacadeDrawn);
        }

        [Fact]
        public void TheSurfaceUploadGoesThroughTheConversionRatherThanUploadingTheGeneratorsVertex()
        {
        // The declaration tests above are all about the type; this one is about the line that decides
        // it. The conversion being correct is worth nothing if the surface is still uploaded as the
        // type LotFacadeGenerator produced, which is the exact regression: the type would be right
        // in a test and wrong on the GPU, and the device would fail the draw with a correct pass
        // bound - the state this file was written for.
        //
        // Read as source because the upload is behind a graphics device. The three things pinned are
        // the ones that can each drift on their own: the vertex type the buffer is created for, that
        // it is the converted array rather than the placed one, and that nothing uploads the
        // generator's VertexPositionTexture any more.
        var path = Path.Combine(Root(),
            "Client/Simitone/Simitone.Client/Neighbourhood/NeighbourhoodFacadeBuilder.cs");
        var text = StripCommentsAndStrings(File.ReadAllText(path));
        var body = BodyOf(text, "class NeighbourhoodFacadeBuilder", "Surface",
            "bool ownsTexture", path);

        Assert.Contains("typeof(DGRP3DVert)", body);
        Assert.Contains("NeighbourhoodFacadeVertex.ToObjectVertices(", body);
        Assert.Contains("surface.VertexBuffer.SetData(objectVertices)", body);
        Assert.DoesNotContain("typeof(VertexPositionTexture)", body);
        }

        // ---- the pass, which is the half that is not the declaration -------------------------------

        [Fact]
        public void TheFacadeDrawIsOnTheObjectTechniqueAndNotTheCityOne()
        {
            // The two techniques read different vertices, so which one is bound is the whole
            // question: RenderCityObj's input is a float4, a float2 and a float3, which is what the
            // facade declaration provides, and RenderNCity is the terrain's.
            Assert.Equal("RenderCityObj", NeighbourhoodShaderState.CityObjectTechnique);
            Assert.NotEqual(NeighbourhoodShaderState.CityTechnique,
                NeighbourhoodShaderState.CityObjectTechnique);
        }

        [Fact]
        public void TheFacadesAndTheTreesShareOneObjectTechniqueAndOneFogPass()
        {
            // Because the facades and the trees are the same declaration and the same input struct,
            // so they have to be on the same technique - and both are on its fog pass, so distance
            // fades them at the same rate as the ground rather than one of the three being the odd
            // one out. The trees use the TreeVS pass, which is the same input read differently: it
            // treats the normal slot as a per-vertex world offset instead of a direction.
            Assert.Equal(NeighbourhoodShaderState.CityObjectTechnique,
                NeighbourhoodShaderState.CityObjectTechnique);
            Assert.InRange(NeighbourhoodShaderState.CityObjectOffsetVertexPass, 0, 5);
            Assert.InRange(NeighbourhoodShaderState.FogPass, 0, 4);
            Assert.NotEqual(NeighbourhoodShaderState.CityObjectOffsetVertexPass,
                NeighbourhoodShaderState.FogPass);
        }

        // ---- helpers ---------------------------------------------------------------------------------

        /// <summary>
        /// A method body inside an already-stripped source file, from its opening brace to the one
        /// that closes it.
        /// </summary>
        private static string BodyOf(
            string text, string typeName, string methodName, string parameterHint, string path)
        {
            var typeIndex = text.IndexOf(typeName, StringComparison.Ordinal);
            Assert.True(typeIndex > 0, typeName + " not found in " + path);

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
                "the " + methodName + " overload taking a " + parameterHint + " was not found in " + path);

            var depth = 0;
            for (int i = found; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) return text.Substring(found, i - found + 1);
            }

            throw new InvalidOperationException("unbalanced braces from offset " + found);
        }

        /// <summary>
        /// Replaces the contents of comments and string literals with spaces, keeping every offset,
        /// so a brace or a word inside one cannot move the end of a method body.
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
        /// A shader function's body, from its opening brace to the one that closes it.
        /// </summary>
        /// <remarks>
        /// A shader's parameter list and its body are in the same declaration, so matching up to the
        /// opening brace and then balancing braces is what separates the two - otherwise the
        /// parameter list's own <c>)</c> or a <c>}</c> inside a comment ends the search early and
        /// the assertions run against half a function.
        /// </remarks>
        /// <param name="source">The whole <c>.fx</c> file.</param>
        /// <param name="function">The function name, without the return type.</param>
        /// <param name="includeSignature">
        /// Whether the text from the name to the opening brace is part of the result. False for a
        /// caller that only wants the body.
        /// </param>
        private static string ShaderBody(string source, string function, bool includeSignature = true)
        {
            var start = source.IndexOf(function + "(", StringComparison.Ordinal);
            Assert.True(start > 0, function + " not found in the shader source");
            return ShaderBlock(source, function, start, includeSignature);
        }

        /// <summary>
        /// A brace-delimited block named by a declaration that is not a function - a struct, whose
        /// body is not preceded by a parameter list.
        /// </summary>
        private static string ShaderBlock(string source, string declaration, bool includeSignature = true)
        {
            var start = source.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(start > 0, declaration + " not found in the shader source");
            return ShaderBlock(source, declaration, start, includeSignature);
        }

        private static string ShaderBlock(
            string source, string declaration, int start, bool includeSignature)
        {
            var open = source.IndexOf('{', start);
            Assert.True(open > start, "the opening brace of " + declaration + " was not found");

            var depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                {
                    return includeSignature
                        ? source.Substring(start, i - start + 1)
                        : source.Substring(open, i - open + 1);
                }
            }

            throw new InvalidOperationException("unbalanced braces in " + declaration);
        }

        /// <summary>
        /// A camera placement, because the frame report carries the camera it drew against and its
        /// Reset will not take null.
        /// </summary>
        private static NeighbourhoodCameraPlacement Placement()
        {
            return NeighbourhoodCameraPlacement.Framing(
                new Vector3(256f, 0f, 256f), 512f, 16f / 9f);
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
