using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Covers the flattening of the community remesh pack into the flat MeshReplace folder the
    /// runtime mesh loader reads from (<c>FSO.Content.RCMeshProvider</c>).
    ///
    /// <para>Three things are load-bearing and easy to break silently:</para>
    /// <list type="bullet">
    /// <item><b>Names.</b> the loader builds <c>&lt;iff filename&gt;_&lt;drawgroup&gt;.fsom</c> and
    /// asks for <c>&lt;iff filename&gt;_TEX_&lt;id&gt;.png</c>, so a single wrong character means the
    /// remesh is silently never used - the game falls back to the auto-generated mesh and nothing
    /// reports an error;</item>
    /// <item><b>Aliases.</b> Simitone splits several object families that FreeSO has merged, so
    /// files have to be renamed (or, for the tree families, redirected to a different drawgroup
    /// range) or they would target objects that do not exist here;</item>
    /// <item><b>Collisions.</b> the output folder is flat, so two groups claiming the same
    /// destination name has to resolve deterministically instead of by filesystem order.</item>
    /// </list>
    /// </summary>
    public sealed class RemeshPackageTests
    {
        /// <summary>
        /// Where the real community pack would be checked out. Deliberately uncommitted (see the
        /// root .gitignore), so every test here that needs it skips when it is absent.
        /// </summary>
        private const string RealPackDir = @"D:\Simitone\FSO.Remeshes-main\remeshes";

        private const string RealWorldRootMetadata = @"{
  ""name"": ""FreeSO Official Remesh Pack"",
  ""alias"": {
    ""simitone"": [
      { ""from"": ""doors3_iff"", ""to"": ""doors3_sp_iff"" },
      { ""from"": ""fenceflowersnow_iff"", ""to"": ""plantboxfence_iff"" },
      { ""from"": ""trees3_iff"", ""split"": [
        { ""name"": ""Mulberry Tree"", ""to"": ""trees_iff"", ""dgrpFrom"": 1600, ""range"": 4, ""dgrpTo"": 400 },
        { ""name"": ""Fir Tree"", ""to"": ""treeshd_iff"", ""dgrpFrom"": 400, ""range"": 4, ""dgrpTo"": 400 }
      ]}
    ]
  }
}";

        [Fact]
        public void SimpleAliasRewritesTheObjectPrefix()
        {
            // The real-world case: Simitone's Sims 2 doors live in doors3_sp_iff, not doors3_iff.
            var aliases = RemeshPackage.ParseAliases(RealWorldRootMetadata);

            Assert.Equal("doors3_sp_iff_1100.fsom", RemeshPackage.MapFileName("doors3_iff_1100.fsom", aliases));
        }

        [Fact]
        public void SimpleAliasAlsoRewritesTheTexturePrefix()
        {
            // A simple alias only moves the prefix, so the texture id is untouched - the texture
            // follows its mesh to the new family name. The result is lowercased like every other
            // destination; the loader resolves it case-insensitively on Windows.
            var aliases = RemeshPackage.ParseAliases(RealWorldRootMetadata);

            Assert.Equal("doors3_sp_iff_tex_1.png", RemeshPackage.MapFileName("doors3_iff_TEX_1.png", aliases));
            Assert.Equal("plantboxfence_iff_tex_12.png",
                RemeshPackage.MapFileName("fenceflowersnow_iff_TEX_12.png", aliases));
        }

        [Fact]
        public void UnmatchedNamesAreKeptAndLowercased()
        {
            var aliases = RemeshPackage.ParseAliases(RealWorldRootMetadata);

            Assert.Equal("newspaper_iff_100.fsom", RemeshPackage.MapFileName("Newspaper_iff_100.fsom", aliases));
            Assert.Equal("phonepayneighborhood_iff_tex_1.png",
                RemeshPackage.MapFileName("PhonePayneighborhood_iff_TEX_1.png", aliases));
        }

        [Fact]
        public void APrefixOnlyMatchesOnAWholeSegment()
        {
            // "trees3_iff" must not swallow "trees3_iff_extra"; and an alias whose "from" is a
            // prefix of another family's name must not steal it.
            var aliases = RemeshPackage.ParseAliases(RealWorldRootMetadata);

            Assert.Equal("trees3_iff_extra_100.fsom",
                RemeshPackage.MapFileName("trees3_iff_extra_100.fsom", aliases));
        }

        [Fact]
        public void SplitAliasRedirectsDrawgroupsInRange()
        {
            var aliases = RemeshPackage.ParseAliases(RealWorldRootMetadata);

            // trees3_iff 1600..1603 is Simitone's mulberry tree, published as trees_iff 400..403.
            Assert.Equal("trees_iff_400.fsom", RemeshPackage.MapFileName("trees3_iff_1600.fsom", aliases));
            Assert.Equal("trees_iff_403.fsom", RemeshPackage.MapFileName("trees3_iff_1603.fsom", aliases));
            // ...and trees3_iff 400..403 is Simitone's fir tree, published as treeshd_iff 400..403.
            Assert.Equal("treeshd_iff_400.fsom", RemeshPackage.MapFileName("trees3_iff_400.fsom", aliases));
            Assert.Equal("treeshd_iff_403.fsom", RemeshPackage.MapFileName("trees3_iff_403.fsom", aliases));
        }

        [Fact]
        public void SplitAliasLeavesDrawgroupsOutsideEveryRangeAlone()
        {
            var aliases = RemeshPackage.ParseAliases(RealWorldRootMetadata);

            // Not covered by any listed range: published unchanged rather than guessed at.
            Assert.Equal("trees3_iff_100.fsom", RemeshPackage.MapFileName("trees3_iff_100.fsom", aliases));
            // range is half-open, so the id just past the end is not covered either.
            Assert.Equal("trees3_iff_1604.fsom", RemeshPackage.MapFileName("trees3_iff_1604.fsom", aliases));
            Assert.Equal("trees3_iff_399.fsom", RemeshPackage.MapFileName("trees3_iff_399.fsom", aliases));
        }

        [Fact]
        public void SplitAliasLeavesTexturesAlone()
        {
            // Documented decision: the number after "_TEX_" is a texture slot within the group, not
            // a drawgroup id (the pack has food_iff_TEX_6 .. _TEX_15 serving drawgroups 119..315),
            // so a drawgroup-ranged alias must not rewrite it. A simple alias still would, because
            // that only moves the prefix.
            var aliases = RemeshPackage.ParseAliases(RealWorldRootMetadata);

            Assert.Equal("trees3_iff_tex_1600.png", RemeshPackage.MapFileName("trees3_iff_TEX_1600.png", aliases));
            Assert.Equal("trees3_iff_tex_601.png", RemeshPackage.MapFileName("trees3_iff_TEX_601.png", aliases));
        }

        [Fact]
        public void UnparseableOrAbsentAliasMetadataYieldsNoAliases()
        {
            Assert.Empty(RemeshPackage.ParseAliases(null).Aliases);
            Assert.Empty(RemeshPackage.ParseAliases("   ").Aliases);
            Assert.Empty(RemeshPackage.ParseAliases("{ not json").Aliases);
            Assert.Empty(RemeshPackage.ParseAliases("{}").Aliases);
            Assert.Empty(RemeshPackage.ParseAliases(@"{""alias"":{}}").Aliases);
            Assert.Empty(RemeshPackage.ParseAliases(@"{""alias"":{""simitone"":[]}}").Aliases);
        }

        [Fact]
        public void MalformedAliasEntriesAreSkippedRatherThanFatal()
        {
            // One contributor's typo must not cost us the whole pack.
            var aliases = RemeshPackage.ParseAliases(@"{
  ""alias"": { ""simitone"": [
    { ""to"": ""orphan_iff"" },
    { ""from"": ""no_target_iff"" },
    { ""from"": ""split_forever_iff"", ""split"": [ { ""to"": ""x_iff"" } ] },
    { ""from"": ""split_zero_iff"", ""split"": [ { ""to"": ""y_iff"", ""dgrpFrom"": 10, ""range"": 0, ""dgrpTo"": 20 } ] },
    { ""from"": ""doors3_iff"", ""to"": ""doors3_sp_iff"" }
  ] } }");

            Assert.Equal(new[] { "doors3_iff" }, aliases.Aliases.Select(a => a.From));
        }

        [Theory]
        [InlineData(@"{""game"":""freeso""}", false)]
        [InlineData(@"{""game"":""FreeSO""}", false)]
        [InlineData(@"{""game"":""none""}", false)]
        [InlineData(@"{""game"":""NONE""}", false)]
        [InlineData(@"{""game"":""simitone""}", true)]
        [InlineData(@"{""name"":""No Game Key""}", true)]
        [InlineData(@"{""game"":""something-new""}", true)]
        [InlineData("{}", true)]
        [InlineData(null, true)]
        [InlineData("{ broken", true)]
        public void FreeSoOnlyAndPlaceholderGroupsAreExcluded(string metadata, bool expected)
        {
            Assert.Equal(expected, RemeshPackage.IsGroupIncluded(metadata));
        }

        [Fact]
        public void GroupPriorityDefaultsToZero()
        {
            Assert.Equal(0, RemeshPackage.GroupPriority(null));
            Assert.Equal(0, RemeshPackage.GroupPriority("{}"));
            Assert.Equal(0, RemeshPackage.GroupPriority(@"{""game"":""simitone""}"));
            Assert.Equal(0, RemeshPackage.GroupPriority(@"{""priority"":""high""}"));
            Assert.Equal(0, RemeshPackage.GroupPriority("{ broken"));
            Assert.Equal(7, RemeshPackage.GroupPriority(@"{""game"":""simitone"",""priority"":7}"));
        }

        [Fact]
        public void AMissingPackYieldsAnEmptyPlan()
        {
            Assert.Empty(RemeshPackage.BuildPlan(null));
            Assert.Empty(RemeshPackage.BuildPlan(string.Empty));
            Assert.Empty(RemeshPackage.BuildPlan(Path.Combine(Path.GetTempPath(), "simitone-no-such-pack-" + Guid.NewGuid().ToString("N"))));
        }

        [Fact]
        public void BuildPlanFlattensFiltersAndResolvesCollisions()
        {
            var root = CreateTempPack();
            try
            {
                var plan = RemeshPackage.BuildPackPlan(root);

                // 6 surviving files, ordered by destination name.
                var names = plan.Entries.Select(e => e.DestinationName).ToList();
                Assert.Equal(new[]
                {
                    "chairs_iff_100.fsom",
                    "doors3_sp_iff_1100.fsom",
                    "doors3_sp_iff_1400.fsom",
                    "doors3_sp_iff_tex_1.png",
                    "sofas3_iff_100.fsom",
                    "trees_iff_400.fsom"
                }, names);

                // The real-world rename made it through, and the FreeSO name is gone.
                Assert.Contains(plan.Entries,
                    e => e.SourcePath == Path.Combine(root, "Author A", "Group One", "doors3_iff_1100.fsom")
                         && e.DestinationName == "doors3_sp_iff_1100.fsom");
                Assert.DoesNotContain(plan.Entries, e => e.DestinationName == "doors3_iff_1100.fsom");

                // The split alias redirected trees3_iff 1600 to trees_iff 400.
                Assert.Contains(plan.Entries,
                    e => e.DestinationName == "trees_iff_400.fsom"
                         && e.SourcePath == Path.Combine(root, "Author A", "Group One", "trees3_iff_1600.fsom"));

                // "game": "freeso" and "game": "none" groups are excluded.
                Assert.DoesNotContain(plan.Entries, e => e.DestinationName == "doors3_sp_iff_1200.fsom");
                Assert.DoesNotContain(plan.Entries, e => e.DestinationName == "doors3_sp_iff_1300.fsom");
                // ...but "game": "simitone" is included.
                Assert.Contains(plan.Entries, e => e.DestinationName == "doors3_sp_iff_1400.fsom");

                // source/ and alt_* are authoring space, and .obj is not a format we can ship.
                Assert.DoesNotContain(plan.Entries, e => e.DestinationName == "doors3_sp_iff_9999.fsom");
                Assert.DoesNotContain(plan.Entries, e => e.DestinationName == "doors3_sp_iff_8888.fsom");
                Assert.DoesNotContain(plan.Entries, e => e.DestinationName == "doors3_sp_iff_notes.obj");

                // Higher priority wins; the tie goes to the first group in the sorted walk.
                var sofas = plan.Entries.Single(e => e.DestinationName == "sofas3_iff_100.fsom");
                Assert.Equal(Path.Combine(root, "Author B", "Winner", "sofas3_iff_100.fsom"), sofas.SourcePath);
                Assert.Equal(5, sofas.Priority);

                var chair = plan.Entries.Single(e => e.DestinationName == "chairs_iff_100.fsom");
                Assert.Equal(Path.Combine(root, "Author C", "TieA", "chairs_iff_100.fsom"), chair.SourcePath);

                // Both collisions are reported rather than silently swallowed.
                Assert.Equal(2, plan.ConflictCount);
                var ties = plan.Conflicts.Where(c => c.DestinationName == "chairs_iff_100.fsom").ToList();
                Assert.Single(ties);
                Assert.Equal(Path.Combine(root, "Author C", "TieA", "chairs_iff_100.fsom"), ties[0].KeptSourcePath);
                Assert.Equal(Path.Combine(root, "Author C", "TieB", "chairs_iff_100.fsom"), ties[0].DiscardedSourcePath);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [Fact]
        public void BuildPlanIsDeterministic()
        {
            var root = CreateTempPack();
            try
            {
                var first = RemeshPackage.BuildPackPlan(root).Entries.Select(e => e.SourcePath + " -> " + e.DestinationName).ToList();
                var second = RemeshPackage.BuildPackPlan(root).Entries.Select(e => e.SourcePath + " -> " + e.DestinationName).ToList();
                Assert.Equal(first, second);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [Fact]
        public void BuildPlanReturnsTheSameEntriesAsTheFullPlan()
        {
            var root = CreateTempPack();
            try
            {
                var plan = RemeshPackage.BuildPackPlan(root);
                Assert.Equal(plan.Entries.Select(e => e.DestinationName).ToList(),
                    RemeshPackage.BuildPlan(root).Select(e => e.DestinationName).ToList());
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [Fact]
        public void AnAliaslessPackStillPublishesEverythingLowercased()
        {
            var root = Path.Combine(Path.Combine(Path.GetTempPath(), "simitone-remesh-" + Guid.NewGuid().ToString("N")), "remeshes");
            try
            {
                WriteFile(root, "metadata.json", @"{""name"":""No aliases here""}");
                WriteFile(root, Path.Combine("Someone", "Group", "Newspaper_iff_100.fsom"), "mesh");
                WriteFile(root, Path.Combine("Someone", "Group", "Newspaper_iff_TEX_4.png"), "png");

                var plan = RemeshPackage.BuildPlan(root);
                Assert.Equal(new[] { "newspaper_iff_100.fsom", "newspaper_iff_tex_4.png" },
                    plan.Select(e => e.DestinationName));
            }
            finally
            {
                DeleteDirectory(Path.GetDirectoryName(root));
            }
        }

        [Fact]
        public void TheRealCommunityPackFlattensToTheNamesTheLoaderAsksFor()
        {
            // The pack is not committed to this repository, so on a machine without it there is
            // nothing to assert against. Returning (rather than failing) keeps the suite green
            // either way; the build target that actually consumes the pack is what matters, and
            // it silently does nothing in exactly this case too.
            if (!Directory.Exists(RealPackDir) || !File.Exists(Path.Combine(RealPackDir, "metadata.json"))) return;

            var plan = RemeshPackage.BuildPlan(RealPackDir);
            var names = new HashSet<string>(plan.Select(e => e.DestinationName), StringComparer.Ordinal);

            Assert.True(plan.Count >= 2000,
                "expected the whole community pack to flatten, got only " + plan.Count + " files");

            // The alias that motivated this whole exercise, on a real file from the real pack.
            Assert.Contains("doors3_sp_iff_1100.fsom", names);
            Assert.DoesNotContain("doors3_iff_1100.fsom", names);

            // A split alias actually rewrote a drawgroup range.
            Assert.Contains("trees_iff_400.fsom", names);
            Assert.Contains("treeshd_iff_400.fsom", names);
            Assert.DoesNotContain("trees3_iff_1600.fsom", names);

            // Only formats the loader can consume, flat names, already lowercased.
            foreach (var entry in plan)
            {
                Assert.Equal(Path.GetFileName(entry.DestinationName), entry.DestinationName);
                Assert.True(RemeshPackage.IsPackableExtension(Path.GetExtension(entry.DestinationName)),
                    "unexpected extension on " + entry.DestinationName);
                Assert.Equal(entry.DestinationName.ToLowerInvariant(), entry.DestinationName);
            }
        }

        /// <summary>
        /// Builds a miniature pack with the same shape as the real one: two authors holding several
        /// groups, one simple and one split alias, a FreeSO-only group, a placeholder group, a
        /// Simitone-only group, authoring/alternate directories, and two kinds of collision.
        /// </summary>
        private static string CreateTempPack()
        {
            var root = Path.Combine(Path.Combine(Path.GetTempPath(), "simitone-remesh-" + Guid.NewGuid().ToString("N")), "remeshes");

            WriteFile(root, "metadata.json", RealWorldRootMetadata);

            // Author A: the interesting group, plus the two groups that must be filtered out.
            WriteFile(root, Path.Combine("Author A", "metadata.json"), @"{""name"":""Author A""}");
            WriteFile(root, Path.Combine("Author A", "Group One", "metadata.json"), @"{""name"":""Group One""}");
            WriteFile(root, Path.Combine("Author A", "Group One", "doors3_iff_1100.fsom"), "mesh");
            WriteFile(root, Path.Combine("Author A", "Group One", "doors3_iff_TEX_1.png"), "png");
            WriteFile(root, Path.Combine("Author A", "Group One", "trees3_iff_1600.fsom"), "mesh");
            WriteFile(root, Path.Combine("Author A", "Group One", "notes.obj"), "authoring");
            // Authoring space: never packaged, even though these are perfectly good .fsom files.
            WriteFile(root, Path.Combine("Author A", "Group One", "Source", "doors3_iff_9999.fsom"), "mesh");
            WriteFile(root, Path.Combine("Author A", "Group One", "alt_simpler", "doors3_iff_8888.fsom"), "mesh");

            WriteFile(root, Path.Combine("Author A", "FreeSo Only", "metadata.json"), @"{""name"":""FreeSo Only"",""game"":""freeso""}");
            WriteFile(root, Path.Combine("Author A", "FreeSo Only", "doors3_iff_1200.fsom"), "mesh");

            WriteFile(root, Path.Combine("Author A", "Placeholder", "metadata.json"), @"{""name"":""Placeholder"",""game"":""none""}");
            WriteFile(root, Path.Combine("Author A", "Placeholder", "doors3_iff_1300.fsom"), "mesh");

            WriteFile(root, Path.Combine("Author A", "Simitone Only", "metadata.json"), @"{""name"":""Simitone Only"",""game"":""simitone""}");
            WriteFile(root, Path.Combine("Author A", "Simitone Only", "doors3_iff_1400.fsom"), "mesh");

            // Author B: a priority collision. "Loser" sorts first and loses anyway.
            WriteFile(root, Path.Combine("Author B", "metadata.json"), @"{""name"":""Author B""}");
            WriteFile(root, Path.Combine("Author B", "Loser", "metadata.json"), @"{""name"":""Loser"",""priority"":0}");
            WriteFile(root, Path.Combine("Author B", "Loser", "sofas3_iff_100.fsom"), "mesh");
            WriteFile(root, Path.Combine("Author B", "Winner", "metadata.json"), @"{""name"":""Winner"",""priority"":5}");
            WriteFile(root, Path.Combine("Author B", "Winner", "sofas3_iff_100.fsom"), "mesh");

            // Author C: a tie, which must go to whichever group the sorted walk reaches first.
            WriteFile(root, Path.Combine("Author C", "metadata.json"), @"{""name"":""Author C""}");
            WriteFile(root, Path.Combine("Author C", "TieA", "metadata.json"), @"{""name"":""TieA""}");
            WriteFile(root, Path.Combine("Author C", "TieA", "chairs_iff_100.fsom"), "mesh");
            WriteFile(root, Path.Combine("Author C", "TieB", "metadata.json"), @"{""name"":""TieB""}");
            WriteFile(root, Path.Combine("Author C", "TieB", "chairs_iff_100.fsom"), "mesh");

            return root;
        }

        private static void WriteFile(string root, string relativePath, string contents)
        {
            var full = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, contents);
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (IOException)
            {
                // A leftover temp directory must never fail a test run.
            }
        }
    }
}
