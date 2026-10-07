using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests that the lighting pass both <em>releases</em> everything it owns and <em>restores</em>
    /// everything it borrows, read off the source.
    ///
    /// <para>
    /// These are the two findings that cannot be reached from a headless test any other way. A
    /// <c>RenderTarget2D</c> cannot be created without a device, so "did we free it" is not
    /// observable; and "did we put the blend state back" is only observable by drawing, which is
    /// the thing a headless build cannot do. What is observable is whether the ownership is
    /// <em>written down</em>, and that is worth pinning: both of these bugs are invisible in a
    /// screenshot and neither throws.
    /// </para>
    ///
    /// <para>
    /// Reading the source is a blunt instrument, and these tests are deliberately narrow - one
    /// construct in one named method body, not a shape - so a rename that matters fails them and
    /// one that does not does not. This is the trade <c>GrassPassSelectionTests</c> already makes
    /// when it reads <c>GrassShader.fx</c> and the content project off disk: the build does not
    /// compile the shader, so the C# and the .fx can disagree silently, and reading both is the
    /// only way to notice.
    /// </para>
    /// </summary>
    public sealed class LightingPassOwnershipTests
    {
        private const string LMap = "LMap/LMapBatch.cs";
        private const string Terrain = "Components/TerrainComponent.cs";
        private const string WorldSource = "World.cs";
        private const string BlueprintSource = "Model/Blueprint.cs";

        // ---- LMapBatch: the state it borrows has to go back ------------------------------------

        [Theory]
        [InlineData("ParseInvalidated")]
        [InlineData("RedrawAll")]
        public void TheLightingEntryPointsPutTheDeviceStateBack(string method)
        {
            // The finding: DrawRoom sets `GD.RasterizerState = Scissor` and never clears it,
            // ParseInvalidated sets `GD.BlendState = AlphaBlend` on entry and the rooms overwrite
            // that with Opaque, Mul, Min, Max, AddRed and MaxBlendGreen, and the only thing either
            // exit path did was `SetRenderTarget(null)`. The scissor rectangle was left at
            // whichever room drew last.
            //
            // Nothing downstream resets a rasterizer state - the 2D world batch that draws the lot
            // sets a blend state and never a rasterizer state - so a leftover scissor test is a
            // clip against a room-sized rectangle on the lot itself. ParseInvalidated also returned
            // early at `if (DirtyRooms.Count == 0) return false;` with the blend state already
            // clobbered, which is why a restore at the end of the method would not have been
            // enough.
            var body = MethodBody(LMap, "LMapBatch", method);

            AssertMentions(body, "DeviceStateScope",
                "LMapBatch." + method + " must open a DeviceStateScope, so the device state it finds is the state it leaves behind on the early return and on a throw as well as on the normal path.");
        }

        [Theory]
        [InlineData("ParseInvalidated")]
        [InlineData("RedrawAll")]
        public void TheLightingEntryPointsStillUnbindTheBackbuffer(string method)
        {
            // The restore is about state, not about the target: whatever the pass was doing it
            // still has to hand the device back pointing at the backbuffer, because the caller is
            // about to draw the lot into it.
            var body = MethodBody(LMap, "LMapBatch", method);

            AssertMentions(body, "SetRenderTarget(null)",
                "LMapBatch." + method + " must unbind its render targets before returning.");
        }

        // ---- LMapBatch: what it owns has to go away -------------------------------------------

        [Theory]
        [InlineData("ShadowTarg")]
        [InlineData("ObjShadowTarg")]
        [InlineData("OutsideShadowTarg")]
        [InlineData("OutsideShadowTargPost")]
        [InlineData("LightMap")]
        [InlineData("LightMapDirection")]
        [InlineData("LightBuf")]
        [InlineData("ShadowTargBlit")]
        public void DisposeFreesEveryResourceTheBatchAllocates(string field)
        {
            // The finding: Dispose freed the six render targets and nothing else. LightBuf is a
            // VertexBuffer and ShadowTargBlit is a SpriteBatch, Init calls Dispose and then
            // immediately rebuilds both, and Init runs on every lot load and on every graphics
            // settings change - so that was one abandoned vertex buffer and one abandoned sprite
            // batch per lot, for the life of the process.
            var body = MethodBody(LMap, "LMapBatch", "Dispose");

            AssertMentions(body, field + "?.Dispose()",
                "LMapBatch.Dispose does not free " + field + ", which Init rebuilds on every lot load.");
        }

        [Theory]
        [InlineData("ShadowTarg")]
        [InlineData("ObjShadowTarg")]
        [InlineData("OutsideShadowTarg")]
        [InlineData("OutsideShadowTargPost")]
        [InlineData("LightMap")]
        [InlineData("LightMapDirection")]
        [InlineData("LightBuf")]
        [InlineData("ShadowTargBlit")]
        public void DisposeClearsEveryResourceTheBatchAllocates(string field)
        {
            // Freed but not cleared is the pattern that produced the terrain grid bug: a
            // disposed-but-non-null buffer that the next draw binds. Init rebuilds all of these, so
            // nulling them is what makes a second Dispose and a null check both mean something.
            var body = MethodBody(LMap, "LMapBatch", "Dispose");

            AssertMentions(body, field + " = null;",
                "LMapBatch.Dispose leaves " + field + " pointing at a disposed resource.");
        }

        [Fact]
        public void TheBatchOwnsNothingThatInitAllocatesAndDisposeMisses()
        {
            // The other direction: a resource added to Init without a line in Dispose is a leak
            // this file would otherwise never see, because the disposal list was written down once
            // and then never compared against anything. Only the graphics resource types count -
            // `var vec = new Vector3(...)` in the same method is not something to free.
            var init = MethodBody(LMap, "LMapBatch", "Init");
            var dispose = MethodBody(LMap, "LMapBatch", "Dispose");

            var allocations = Regex.Matches(init,
                @"\b([A-Za-z_][A-Za-z0-9_]*)\s*=\s*new\s+(RenderTarget2D|SpriteBatch|VertexBuffer|IndexBuffer|Texture2D)\s*\(");

            Assert.True(allocations.Count >= 4,
                "expected Init to allocate at least the four render targets and the sprite batch, found " + allocations.Count);

            foreach (Match allocation in allocations)
            {
                var field = allocation.Groups[1].Value;
                AssertMentions(dispose, field + "?.Dispose()",
                    "LMapBatch.Init allocates " + field + " (" + allocation.Groups[2].Value + ") and LMapBatch.Dispose does not free it.");
                AssertMentions(dispose, field + " = null;",
                    "LMapBatch.Dispose frees " + field + " but leaves it assigned, which is the disposed-but-non-null pattern.");
            }

            // Named explicitly as well, because the two the audit is about are the two a rename or
            // a refactor is most likely to fold away. Both are allocated by InitBasicData, which
            // Init calls immediately after Dispose.
            AssertMentions(Read(LMap), "LightBuf = new VertexBuffer(");
            AssertMentions(Read(LMap), "ShadowTargBlit = new SpriteBatch(");
            AssertMentions(dispose, "LightBuf?.Dispose()");
            AssertMentions(dispose, "ShadowTargBlit?.Dispose()");
        }

        [Fact]
        public void TheLightingPassForgetsItsQueueWhenItIsTornDown()
        {
            // The dirty room queue is a list of ushorts, so it is not a leak, but carrying one
            // lot's rooms into the next lot's lighting is a correctness problem: the new batch
            // would try to redraw rooms of the previous blueprint.
            var body = MethodBody(LMap, "LMapBatch", "Dispose");

            AssertMentions(body, "DirtyRooms.Clear()",
                "LMapBatch.Dispose must not carry a lot's dirty rooms into the next lot.");
        }

        // ---- the same shape in the terrain component --------------------------------------------

        [Fact]
        public void TheTerrainReleasePathClearsWhatItFrees()
        {
            // The finding R-02 exists for: RegenTerrain disposed the two grid index buffers and
            // left the fields assigned, only reallocating them `if (indices.Length > 0)`. A regen
            // that produced an empty grid - a legitimate answer, since a fine area can cover
            // nothing - therefore left a disposed buffer bound to a stale primitive count. The two
            // TGrid checks had an IsDisposed guard and the Grid ones did not, which is what says it
            // was an oversight rather than a policy.
            var body = MethodBody(Terrain, "TerrainComponent", "ReleaseBuffers");

            foreach (var field in new[] { "GridIndexBuffer", "TGridIndexBuffer" })
            {
                AssertMentions(body, field + " = null;", "ReleaseBuffers leaves " + field + " assigned.");
            }
            foreach (var field in new[] { "GridPrimitives", "TGridPrimitives" })
            {
                AssertMentions(body, field + " = 0;", "ReleaseBuffers leaves a stale " + field + ".");
            }
        }

        [Fact]
        public void BothGridOverlaysAreCheckedForDrawability()
        {
            // Four bind sites, one rule. The target-area pair had an IsDisposed test; the fine-area
            // pair bound the buffer straight into device.Indices.
            var source = Read(Terrain);
            var binds = Regex.Matches(source, @"device\.Indices = (Grid|TGrid)IndexBuffer;");

            Assert.Equal(4, binds.Count);
            foreach (Match bind in binds)
            {
                var buffer = bind.Groups[1].Value + "IndexBuffer";
                var primitives = bind.Groups[1].Value + "Primitives";
                AssertMentions(source, "CanDrawGrid(" + primitives + ", " + buffer + ")",
                    "the " + buffer + " bind is not guarded by the drawability rule.");
            }
        }

        [Fact]
        public void TheTerrainReleasePathIsUsedByBothTheRegenAndTheDispose()
        {
            // R-61: the old pair of methods each had their own release logic, and neither was
            // right - RegenTerrain left the grid fields assigned, and Dispose gated the whole
            // thing on VertexBuffer != null and then dereferenced IndexBuffer unguarded. One
            // helper, called by both, is what stops them drifting apart again.
            var source = Read(Terrain);

            AssertMentions(MethodBody(Terrain, "TerrainComponent", "RegenTerrain"), "ReleaseBuffers();");
            AssertMentions(MethodBody(Terrain, "TerrainComponent", "Dispose"), "ReleaseBuffers();");
            Assert.Single(Regex.Matches(source, @"private void ReleaseBuffers\(\)"));
        }

        // ---- the blueprint's teardown ------------------------------------------------------------

        [Fact]
        public void TheWorldTeardownReachesTheBluestprintsOwnGeometry()
        {
            // R-15: World.Dispose freed the ambient light, the 2d batch, the objects, the
            // particles, the terrain and the roof - and stopped. What it did not reach was the 3d
            // wall shadow buffers, the 3d floor groups, the sims64 geometry, or every vertex and
            // index buffer in the cached 2d wall draw groups, which was only ever freed by a wall
            // recache. So a lot you never edited a wall in kept a full screen of wall vertices for
            // the life of the process.
            var dispose = MethodBody(WorldSource, "World", "Dispose");

            AssertMentions(dispose, "DisposeGpuResources()",
                "World.Dispose must reach Blueprint.DisposeGpuResources.");
        }

        [Fact]
        public void TheBluestprintsTeardownCoversItsGeometryOwners()
        {
            var body = MethodBody(BlueprintSource, "Blueprint", "DisposeGpuResources");

            AssertMentions(body, "WCRC?.Dispose()", "the 3d wall shadow buffers are never freed.");
            AssertMentions(body, "FloorGeom?.Dispose()", "the 3d floor groups are never freed.");
            AssertMentions(body, "SM64?.Dispose()", "the sims64 geometry is never freed.");
            AssertMentions(body, "WallCache2D.Clear()",
                "the cached 2d wall draw groups are freed but left in the list, so the next recache would draw disposed buffers.");
        }

        [Fact]
        public void TheDisposalHelpersAreSafeToRunTwice()
        {
            // Every one of these is now reachable from a teardown, and several of them are also
            // reachable from a regen, so "dispose an absent buffer" has to be a no-op rather than a
            // NullReferenceException. Grouped because it is one property of all of them: each frees
            // a field its own producer may not have set - a wall group is registered before the
            // geometry that gives it buffers, a pool floor group before its first PrepareGPU, a 2d
            // draw group before its index buffer is written.
            AssertMentions(MethodBody("RC/WallComponentRC.cs", "WallGroupRC", "Dispose"), "GVerts?.Dispose()");
            AssertMentions(MethodBody("RC/WallComponentRC.cs", "WallGroupRC", "Dispose"), "GIndices?.Dispose()");
            AssertMentions(MethodBody("Components/Geometry/Modelled3DFloor.cs", "Modelled3DFloor", "Dispose"), "VertGPUData?.Dispose()");
            AssertMentions(MethodBody("Components/3DFloorGeometry.cs", "FloorTileGroup", "Dispose"), "GPUData?.Dispose()");
            AssertMentions(MethodBody("Utils/_2DDrawGroup.cs", "_2DDrawGroup", "Dispose"), "VertBuf?.Dispose()");
            AssertMentions(MethodBody("Utils/_2DDrawGroup.cs", "_2DDrawGroup", "Dispose"), "IndexBuf?.Dispose()");
        }

        // ---- helpers ---------------------------------------------------------------------------

        /// <summary>
        /// Asserts that a snippet of source mentions something. xunit's string
        /// <c>Contains</c> has no message overload, and the message is the entire value of a test
        /// that reads source - it is what tells the next person which ownership they just broke.
        /// </summary>
        private static void AssertMentions(string source, string statement, string because)
        {
            Assert.True(source.Contains(statement),
                because + " (looked for '" + statement + "' in:" + Environment.NewLine + source + ")");
        }

        /// <summary>Asserts that a snippet of source mentions something, with no explanation to add.</summary>
        private static void AssertMentions(string source, string statement)
        {
            AssertMentions(source, statement, "expected the source to contain '" + statement + "'");
        }

        private static string Read(string file)
        {
            return File.ReadAllText(Path.Combine(LotViewDirectory(), file));
        }

        /// <summary>The lot view's own source, found by walking up rather than counting levels.</summary>
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

            throw new FileNotFoundException(
                "Could not find FreeSO/TSOClient/tso.world/FSO.LotView.csproj above " + AppContext.BaseDirectory);
        }

        /// <summary>
        /// The body of one method, from its opening brace to the one that closes it, with comments
        /// and literals blanked out first so a brace inside either cannot end the search early.
        /// </summary>
        private static string MethodBody(string file, string type, string method)
        {
            var text = StripCommentsAndStrings(Read(file));

            var typeIndex = text.IndexOf("class " + type, StringComparison.Ordinal);
            Assert.True(typeIndex > 0, "class " + type + " not found in " + file);

            var header = Regex.Match(text.Substring(typeIndex),
                @"\b" + Regex.Escape(method) + @"\s*\([^;{]*\)\s*\{");
            Assert.True(header.Success, method + " not found in " + type + " (" + file + ")");

            return Slice(text, typeIndex + header.Index + header.Length - 1);
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
        /// every offset, so that a brace or a word inside one cannot move the end of a method body.
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
    }
}
