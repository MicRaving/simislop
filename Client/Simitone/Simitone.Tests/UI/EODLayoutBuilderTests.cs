using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels.EODs;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Covers the Simitone-authored EOD panel layouts: the catalogue, the generator, the
    /// committed <c>.uis</c> files, and the string-table block their captions come from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The problem this exists to make checkable is narrow and it is why every EOD panel used to
    /// fail to open. Each of them calls <c>UIContainer.RenderScript("name.uis")</c>, which opens
    /// that file by name and parses it; the names are The Sims Online's, a TS1 install has none
    /// of them, and a missing file throws out of the panel's constructor. So the layout is not
    /// decoration - a control the panel expects and the layout does not declare is a null
    /// reference, and a control the layout declares and the panel does not expect is a stray
    /// button on screen.
    /// </para>
    /// <para>
    /// The layouts Simitone ships are therefore generated from a declaration of what each panel
    /// needs, and the highest-value assertion here is the boring one: the committed files are
    /// byte-identical to what the generator produces. That makes a hand edit to a <c>.uis</c> a
    /// test failure rather than a silent divergence, and a change to the catalogue a reviewable
    /// diff of the <c>.uis</c> files it implies.
    /// </para>
    /// <para>
    /// The assertions about the committed content are skipped rather than failed when the
    /// source tree is not on disk (a packaged test run, say). Everything about the generator and
    /// the catalogue itself is pure and runs anywhere.
    /// </para>
    /// </remarks>
    public sealed class EODLayoutBuilderTests
    {
        /// <summary>
        /// The node names <c>UIScript.ProcessUIGroup</c> will actually dispatch on, from
        /// <c>UIScript.FUNCTIONS</c>. A generated node whose name is not in this list parses
        /// cleanly and then does nothing, so the builder and this list have to agree.
        /// </summary>
        private static readonly string[] ParserFunctions =
        {
            "DefineString", "DefineImage", "AddButton", "SetControlProperties",
            "AddText", "AddTextEdit", "AddSlider", "AddListBox", "AddFormatedText", "AddProgressBar",
        };

        #region the catalogue covers the policy

        [Fact]
        public void EveryLayoutThePolicyRequires_IsOneThisBuildAuthors()
        {
            //The whole point. If this fails, some panel still asks for a file that only a The Sims
            //Online install has, and it will still throw out of its constructor.
            Assert.Empty(EODPluginPolicy.UnauthoredLayouts);
            Assert.Equal(EODPluginPolicy.RequiredLayouts.OrderBy(x => x, StringComparer.Ordinal),
                EODPluginPolicy.AuthoredLayouts.OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void TheFrameworkStarts_BecauseEveryRequiredLayoutIsBuildable()
        {
            //The end of the chain, stated as one fact: given a host and a probe that answers for
            //the layouts this build authors, the policy says the minigames come up. Before the
            //layouts existed this was EODStartOutcome.NoLayouts on every TS1 lot.
            var decision = EODPluginPolicy.Decide(true, EODPluginPolicy.IsAuthoredLayout);

            Assert.Equal(EODStartOutcome.Start, decision.Outcome);
            Assert.True(decision.CanStart);
            Assert.Empty(decision.MissingLayouts);
        }

        [Fact]
        public void TheCatalogueHasNoLayoutThePolicyDoesNotAskFor()
        {
            //A generated layout nothing opens is dead weight that still has to be regenerated and
            //still gets reviewed, so it should be noticed rather than left.
            var asked = new HashSet<string>(EODPluginPolicy.RequiredLayouts, StringComparer.OrdinalIgnoreCase);
            var orphans = EODLayouts.All.Select(x => x.FileName).Where(x => !asked.Contains(x)).ToList();

            Assert.Empty(orphans);
        }

        [Fact]
        public void EveryCatalogueEntryIsWellFormed()
        {
            foreach (var spec in EODLayouts.All)
            {
                Assert.EndsWith(".uis", spec.FileName);
                Assert.False(string.IsNullOrWhiteSpace(spec.Title), spec.FileName + " has no title");
                Assert.NotEmpty(spec.Elements);
                Assert.True(spec.Columns > 0, spec.FileName);

                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in spec.Elements)
                {
                    Assert.True(ids.Add(element.Id),
                        spec.FileName + " declares \"" + element.Id + "\" twice; UIScript.WireUp adds every id to one dictionary and would throw");
                    Assert.False(element.Id.StartsWith(EODLayoutBuilder.StringIdPrefix, StringComparison.Ordinal),
                        spec.FileName + "/" + element.Id + " would collide with a generated DefineString id");
                    Assert.False(element.Id.StartsWith(EODLayoutBuilder.TextureIdPrefix, StringComparison.Ordinal),
                        spec.FileName + "/" + element.Id + " would collide with a generated DefineImage id");
                }
            }
        }

        [Fact]
        public void LookupsAreCaseInsensitive_BecauseTheParserLowerCasesIds()
        {
            var spec = EODLayouts.All[0];
            Assert.NotNull(EODLayouts.For(spec.FileName.ToUpperInvariant()));
            Assert.True(EODLayouts.IsAuthored(spec.FileName.ToUpperInvariant()));
            Assert.Null(EODLayouts.For("not-a-layout.uis"));
            Assert.False(EODLayouts.IsAuthored(null));
        }

        #endregion

        #region the generated text

        [Fact]
        public void EveryGeneratedLayoutOnlyUsesNodesTheParserDispatches()
        {
            foreach (var spec in EODLayouts.All)
            {
                //Comments are stripped first: the header mentions things like "<lang>" and a
                //comment is not a node to anybody, including the parser.
                var text = EODLayoutBuilder.Build(spec);
                foreach (var line in text.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                    foreach (Match m in Regex.Matches(line, "<([A-Za-z]+)"))
                    {
                        var node = m.Groups[1].Value;
                        if (node == "Begin" || node == "End") continue;    // grammar literals
                        Assert.True(node == "SetSharedProperties" || ParserFunctions.Contains(node),
                            spec.FileName + " uses <" + node + ">, which UIScript.FUNCTIONS does not list, so the parser would ignore it");
                    }
                }
            }
        }

        [Fact]
        public void EveryGeneratedLayoutBalancesItsBeginAndEnd()
        {
            foreach (var spec in EODLayouts.All)
            {
                var text = EODLayoutBuilder.Build(spec);
                var begins = Regex.Matches(text, "<Begin>").Count;
                var ends = Regex.Matches(text, "<End>").Count;

                Assert.True(begins >= 1, spec.FileName + " has no <Begin>");
                Assert.Equal(begins, ends);
            }
        }

        [Fact]
        public void NoGeneratedLayoutNamesAnEAArtworkAsset()
        {
            //The rule the whole exercise is bounded by. A "0x..." assetID is a TSO texture id and
            //nothing else; the only image a generated layout may name is the flat tile this port
            //draws itself. Checked over the text rather than trusted from the builder, so it
            //also catches a DefineImage added by hand.
            foreach (var spec in EODLayouts.All)
            {
                foreach (Match m in Regex.Matches(EODLayoutBuilder.Build(spec), @"assetID\s*=\s*""([^""]*)"""))
                {
                    var asset = m.Groups[1].Value;
                    Assert.False(asset.StartsWith("0x", StringComparison.OrdinalIgnoreCase),
                        spec.FileName + " references the EA/TSO asset id " + asset);
                    Assert.Equal(EODLayoutBuilder.PlaceholderImage, asset);
                    Assert.Contains("uigraphics/", asset, StringComparison.Ordinal);
                }
            }
        }

        /// <summary>
        /// The ids a generated layout declares with the given node, e.g. the DefineString ids.
        /// </summary>
        private static HashSet<string> Declared(string layout, string node)
        {
            var pattern = "<" + node + "[ \\t]+\"([^\"]+)\"";
            return new HashSet<string>(
                Regex.Matches(layout, pattern).Cast<Match>().Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);
        }

        [Fact]
        public void EveryStringAndImageAElementRefersToIsDeclaredInTheSameFile()
        {
            //UIScript resolves a text= through its own Strings table and an image= through its own
            //Textures table, both keyed by the DefineString / DefineImage id, and silently yields
            //"" or null for a miss. So a dangling reference is a button with no word on it rather
            //than an error, which is exactly the kind of thing that has to be asserted.
            foreach (var spec in EODLayouts.All)
            {
                var text = EODLayoutBuilder.Build(spec);
                var declaredStrings = Declared(text, "DefineString");
                var declaredImages = Declared(text, "DefineImage");

                foreach (Match m in Regex.Matches(text, "text=\"(str_[^\"]+)\""))
                {
                    Assert.True(declaredStrings.Contains(m.Groups[1].Value),
                        spec.FileName + " sets text=" + m.Groups[1].Value + " with no DefineString for it");
                }
                foreach (Match m in Regex.Matches(text, "(?:image|scrollbarImage)=\"(img_[^\"]+)\""))
                {
                    Assert.True(declaredImages.Contains(m.Groups[1].Value),
                        spec.FileName + " refers to " + m.Groups[1].Value + " with no DefineImage for it");
                }

                //The reverse: a DefineString nothing uses is a caption no button will ever show.
                foreach (var declared in declaredStrings)
                {
                    var references = Regex.Matches(text, "text=\"" + Regex.Escape(declared) + "\"").Count;
                    Assert.True(references > 0, spec.FileName + " declares " + declared + " and no control refers to it");
                }
            }
        }

        [Fact]
        public void GenerationIsDeterministic()
        {
            //The files are committed, so the same catalogue has to produce the same bytes every
            //time; otherwise a rebuild would churn the tree and the equality test below would be
            //useless.
            foreach (var spec in EODLayouts.All)
            {
                Assert.Equal(EODLayoutBuilder.Build(spec), EODLayoutBuilder.Build(spec));
            }
        }

        [Fact]
        public void EveryControlSitsInsideThePanelItsLayoutIsSizedFor()
        {
            //A .uis says nothing about how big the window is, so the size is derived from the
            //grid. A control outside it would be drawn by a panel the frame is not big enough to
            //hold, which is how a panel ends up with a row of buttons along the screen edge.
            foreach (var spec in EODLayouts.All)
            {
                var size = EODLayoutBuilder.Size(spec);
                for (var i = 0; i < spec.Elements.Count; i++)
                {
                    var element = spec.Elements[i];
                    var position = EODLayoutBuilder.Position(spec, element, i);
                    int w, h;
                    EODLayoutBuilder.DefaultSize(element.Kind, out w, out h);
                    if (element.Width.HasValue) w = element.Width.Value;
                    if (element.Height.HasValue) h = element.Height.Value;

                    Assert.True(position.X + w <= size.X + 0.5f,
                        spec.FileName + "/" + element.Id + " runs off the right edge (" + (position.X + w) + " > " + size.X + ")");
                    Assert.True(position.Y + h <= size.Y + 0.5f,
                        spec.FileName + "/" + element.Id + " runs off the bottom edge (" + (position.Y + h) + " > " + size.Y + ")");
                }
            }
        }

        [Fact]
        public void NoLayoutPutsTwoControlsInTheSameCell()
        {
            //Coarse, and deliberately so: the grid is what keeps a generated layout readable, and
            //two controls stacked on each other is the first thing to go wrong when a panel grows
            //a control.
            foreach (var spec in EODLayouts.All)
            {
                var cells = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < spec.Elements.Count; i++)
                {
                    var p = EODLayoutBuilder.Position(spec, spec.Elements[i], i);
                    Assert.True(cells.Add(((int)p.X) + "," + ((int)p.Y)),
                        spec.FileName + " puts two controls at " + (int)p.X + "," + (int)p.Y);
                }
            }
        }

        [Fact]
        public void TheGridFitsOnAPanelASimitoneWindowCanShow()
        {
            //A layout wider or taller than the lot view is not a layout, it is a way to make the
            //controls unreachable. 800x600 is the smallest window GlobalSettings will hold.
            foreach (var spec in EODLayouts.All)
            {
                var size = EODLayoutBuilder.Size(spec);
                Assert.True(size.X <= 800, spec.FileName + " is " + size.X + " wide");
                Assert.True(size.Y <= 600, spec.FileName + " is " + size.Y + " tall");
            }
        }

        [Fact]
        public void APinnedRowOrColumnIsStillInsideThePanel()
        {
            //Row/Column are the escape hatch for a panel that wants a control somewhere the
            //declaration order would not put it, so they are worth proving they cannot escape.
            var spec = new EODLayoutSpec("probe.uis", "Probe", new[]
            {
                new EODLayoutElement("A", EODControlKind.Button, null, null, null, 0, 0),
                new EODLayoutElement("B", EODControlKind.Button, null, null, null, 1, 1),
            });

            var text = EODLayoutBuilder.Build(spec);
            Assert.Contains("position=(10,10)", text);
            Assert.Contains("position=(114,40)", text);
        }

        #endregion

        #region the string table block

        [Fact]
        public void EveryCaptionHasAStableIdAndAUniqueSlug()
        {
            //The .uis refers to a caption by slug and the .cst by id, so the two have to line up
            //and a caption whose slug collides with another caption's would silently show the
            //wrong word on a button.
            var slugs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in EODLayoutStrings.Entries)
            {
                var slug = EODLayoutBuilder.Slug(entry.Value);
                Assert.True(slugs.Add(slug), "two EOD captions slug to \"" + slug + "\": " + entry.Value);
                Assert.DoesNotContain("^", entry.Value);
            }
            Assert.True(slugs.Count > 0);
        }

        [Fact]
        public void CaptionsAreIdempotent()
        {
            //Reserve is called from EODLayouts' static constructor, which the type system may run
            //again in a new AppDomain. Ids must not move, or the committed .cst block stops
            //matching the committed .uis files.
            var before = EODLayoutStrings.Entries.ToList();
            EODLayoutStrings.Reserve(EODLayouts.All);
            var after = EODLayoutStrings.Entries.ToList();

            Assert.Equal(before, after);
        }

        [Fact]
        public void GeneratedIdsStartAboveEveryHandWrittenOne()
        {
            //The generated block starts at 1000 and the hand-written one stops well below it. If a
            //hand-written id ever reached 1000 the two would collide and a translation would be
            //silently shadowed, so the floor is part of the contract.
            Assert.Equal(1000, EODLayoutStrings.FirstId);
            Assert.True(EODLayoutStrings.Entries.All(x => x.Key >= EODLayoutStrings.FirstId));
        }

        #endregion

        #region the committed content

        /// <summary>
        /// The Simitone source tree, or null when the tests are running somewhere it is not
        /// (a packaged run, a container with only the binaries). The content assertions below are
        /// then skipped rather than failed, because "the file is not here" is not the same claim
        /// as "the file is wrong".
        /// </summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Client", "Simitone", "Simitone.Client", "Simitone.Client.csproj")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        private static string ContentDir()
        {
            var root = RepoRoot();
            return root == null ? null : Path.Combine(root, "Client", "Simitone", "Simitone.Client", "Content");
        }

        [Fact]
        public void EveryAuthoredLayoutIsCommittedExactlyAsTheBuilderWritesIt()
        {
            var content = ContentDir();
            if (content == null) return;

            foreach (var spec in EODLayouts.All)
            {
                var path = Path.Combine(content, "UI", "uiscripts", spec.FileName);
                Assert.True(File.Exists(path),
                    spec.FileName + " is in the catalogue but not committed at " + path +
                    "; run EODLayoutWriter.WriteLayouts");
                Assert.Equal(EODLayoutBuilder.Build(spec), File.ReadAllText(path));
            }
        }

        [Fact]
        public void TheCommittedLayoutFolderHoldsNothingElse()
        {
            var content = ContentDir();
            if (content == null) return;

            var dir = Path.Combine(content, "UI", "uiscripts");
            if (!Directory.Exists(dir)) return;

            var onDisk = Directory.GetFiles(dir, "*.uis").Select(Path.GetFileName).ToList();
            var authored = EODLayouts.All.Select(x => x.FileName).ToList();

            Assert.Equal(authored.OrderBy(x => x, StringComparer.Ordinal), onDisk.OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void TheCommittedCaptionsMatchTheGeneratedBlock()
        {
            var content = ContentDir();
            if (content == null) return;

            //Read from the same relative location ContentStrings uses, so the test is asserting
            //about the table the game will actually load.
            var path = SimitoneStrings.ResolveTablePath(FindContentRoot(content));
            if (!File.Exists(path)) return;

            var table = SimitoneStrings.ParseTable(File.ReadAllText(path));
            foreach (var entry in EODLayoutStrings.Entries)
            {
                var id = entry.Key.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(table.ContainsKey(id),
                    "the shipped string table has no entry " + id + " (" + entry.Value + "); regenerate the EOD block");
                Assert.Equal(entry.Value, table[id]);
            }
        }

        [Fact]
        public void TheShippedStringTableStillParses()
        {
            //Cheap, and the thing every other string in the game depends on: a stray delimiter or
            //a repeated id in _f900_simitone.cst would break SimitoneStrings.ParseTable's
            //assumptions for every caller, not just the EOD ones.
            var content = ContentDir();
            if (content == null) return;

            var path = SimitoneStrings.ResolveTablePath(FindContentRoot(content));
            if (!File.Exists(path)) return;

            var table = SimitoneStrings.ParseTable(File.ReadAllText(path));
            Assert.True(table.Count > 0);
            foreach (var pair in table)
            {
                Assert.False(string.IsNullOrWhiteSpace(pair.Value), "id " + pair.Key + " has no text");
            }
        }

        /// <summary>
        /// The directory the game runs in, worked out from the content folder's own path, so the
        /// table is read from the same relative location the loader uses.
        /// </summary>
        private static string FindContentRoot(string contentDir)
        {
            var full = Path.GetFullPath(contentDir);
            var marker = full.LastIndexOf(Path.DirectorySeparatorChar + "Content", StringComparison.Ordinal);
            return marker <= 0 ? full : full.Substring(0, marker);
        }

        #endregion

        #region the real grammar

        /// <summary>
        /// <c>UIScriptParser</c>, reached reflectively.
        /// </summary>
        /// <remarks>
        /// The class is internal to FSO.UI and the test project does not - and should not -
        /// reference that assembly's internals. The parse itself is exactly what we want to run
        /// though, because a <c>.uis</c> is a GOLD grammar dialect and the only authority on
        /// whether one of these files is a <c>.uis</c> is the parser itself: a file that fails to
        /// parse is silently ignored by <c>UIScript.Parse</c>, which checks the return value and
        /// does nothing at all when it is false, so the panel is then built out of nothing and
        /// throws on the first control it expected to find.
        /// </remarks>
        private static object NewParser()
        {
            var type = typeof(UIElement).Assembly.GetType("FSO.Client.UI.Framework.Parser.UIScriptParser", true);
            return Activator.CreateInstance(type, true);
        }

        private static bool Parses(string text)
        {
            var parser = NewParser();
            parser.GetType().GetMethod("Setup", Type.EmptyTypes).Invoke(parser, null);
            using (var reader = new StringReader(text))
            {
                return (bool)parser.GetType().GetMethod("Parse", new[] { typeof(TextReader) }).Invoke(parser, new object[] { reader });
            }
        }

        [Fact]
        public void EveryGeneratedLayoutIsAcceptedByTheRealParser()
        {
            //Needs Content/UIScript.egt relative to the working directory, which is where
            //UIScriptParser.Setup looks for it. Absent - a trimmed or unusual test host - the
            //assertion is skipped rather than failed: "the grammar tables are not here" is not
            //the same claim as "the file is not a layout".
            if (!File.Exists(Path.Combine("Content", "UIScript.egt"))) return;

            foreach (var spec in EODLayouts.All)
            {
                var text = EODLayoutBuilder.Build(spec);
                Assert.True(Parses(text),
                    spec.FileName + " does not parse as a .uis; UIScript.Parse ignores a file that does not, and the panel then builds out of nothing");
            }
        }

        [Theory]
        [InlineData("this is not a layout at all")]
        [InlineData("<Begin>\n    <AddButton \"X\" position=???\n<End>\n")]
        [InlineData("<Begin>\n    <AddButton \"X\n<End>\n")]
        public void TheParserRejectsWhatItShouldReject(string text)
        {
            //Guards the test above from passing vacuously. If any of these ever start parsing,
            //the parse assertion is not testing anything and should be deleted rather than
            //trusted - GOLD's error recovery is lenient, and a "reject" that does not is worse
            //than no check at all.
            if (!File.Exists(Path.Combine("Content", "UIScript.egt"))) return;

            Assert.False(Parses(text));
        }

        #endregion

        #region the frame around a panel

        [Fact]
        public void ExtentMeasuresWhatIsActuallyInThePanel()
        {
            //UIContainer.Size is a stub that is always zero, so anything that centres a panel has
            //to walk it. Getting this wrong puts a wide panel's top-left corner in the middle of
            //the screen and pushes the rest of it off the right-hand edge.
            var panel = new UIContainer();
            panel.Add(MakeImage(10, 20, 100, 40));
            panel.Add(MakeImage(0, 90, 30, 30));

            Assert.Equal(new Vector2(110, 120), EODPanelFrame.Extent(panel));
        }

        [Fact]
        public void ExtentFollowsNestedContainers()
        {
            //Several panels put their sub-panels inside the panel rather than beside it - a card
            //hand, the maze's wall and cell containers - so their contents are what the player
            //sees and the frame has to be big enough for them.
            var inner = new UIContainer();
            inner.Add(MakeImage(0, 0, 200, 50));

            var panel = new UIContainer();
            panel.Add(MakeImage(5, 5, 10, 10));

            var holder = new UIContainer { Position = new Vector2(20, 30) };
            holder.Add(inner);
            panel.Add(holder);

            //The holder's own extent is 200x50 at (20,30), so the far corner is (220, 80).
            Assert.Equal(new Vector2(220, 80), EODPanelFrame.Extent(panel));
        }

        [Fact]
        public void ExtentTreatsASizelessChildAsAPoint()
        {
            //Most of what these layouts contain is a picture with no texture, whose Size is zero.
            //It still needs a frame around it, so its position counts even though its size does
            //not.
            var panel = new UIContainer();
            panel.Add(MakeImage(300, 400, 0, 0));

            Assert.Equal(new Vector2(300, 400), EODPanelFrame.Extent(panel));
        }

        [Fact]
        public void ExtentOfNothingIsNothing()
        {
            Assert.Equal(Vector2.Zero, EODPanelFrame.Extent(null));
            Assert.Equal(Vector2.Zero, EODPanelFrame.Extent(new UIContainer()));
        }

        [Fact]
        public void ExtentStopsAtACycle()
        {
            //A panel that contains itself - or a fork's panel that does - must not be an infinite
            //loop in the middle of a lot load. Add() re-parents, so this is a sibling pair rather
            //than a true cycle, but the recursion guard is what stops a real one.
            var outer = new UIContainer();
            var inner = new UIContainer();
            outer.Add(inner);
            inner.Add(outer);

            var extent = EODPanelFrame.Extent(outer);
            Assert.True(extent.X >= 0 && extent.Y >= 0);
        }

        [Fact]
        public void ExtentIsBoundedForADeeplyNestedPanel()
        {
            //The depth limit is four, so a chain of sub-panels deeper than that stops being
            //counted rather than going on for ever. The point is only that it terminates and
            //still measures the top of the chain.
            UIContainer top = new UIContainer();
            var parent = top;
            for (var i = 0; i < 10; i++)
            {
                var child = new UIContainer { Position = new Vector2(1, 0) };
                parent.Add(child);
                parent = child;
            }
            parent.Add(MakeImage(0, 0, 50, 50));

            var extent = EODPanelFrame.Extent(top);
            Assert.True(extent.X > 0, "a deep chain measured as nothing at all");
            Assert.True(extent.X <= 60, "the recursion did not stop where it says it does");
        }

        private static UIImage MakeImage(float x, float y, float w, float h)
        {
            return new UIImage { Position = new Vector2(x, y), Size = new Vector2(w, h) };
        }

        #endregion
    }
}
