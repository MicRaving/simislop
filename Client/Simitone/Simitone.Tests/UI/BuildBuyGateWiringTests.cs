using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The wiring that makes the lot's build/buy gate impossible to leave stuck, checked where it
    /// is written rather than run.
    ///
    /// <para>
    /// <c>VMBuildBuyGateTests</c> drives the decision itself - who holds a disable, and what
    /// build/buy reads as - with strings standing in for objects. That covers the rule and
    /// nothing else. What it cannot reach is the half that was actually broken: that the rule is
    /// <em>consulted</em>. None of that needs a lot to look at, and a test process cannot build one
    /// (no content, no graphics device, and a running VM is four walls and a scheduler), so these
    /// assertions read the sources.
    /// </para>
    ///
    /// <para>
    /// Each one guards a specific way the fix can decay with nothing failing:
    /// </para>
    /// <list type="bullet">
    /// <item>the two bhav calls going back to writing a bare bool, which is the original bug
    /// wearing a different name;</item>
    /// <item>the removal net - <c>VMEntity.Dead</c>'s setter - quietly becoming a field again,
    /// which would leave deletion paths unable to give a hold up;</item>
    /// <item>the lot load going back to trusting the marshalled flag, which is what let a wedged
    /// lot be saved wedged and come back wedged;</item>
    /// <item>either of the two Buy/Build button groups going back to refreshing only on
    /// <c>TS1BuildBuyChange</c>, the signal that no longer covers the case the bug was.</item>
    /// </list>
    ///
    /// <para>
    /// Source reading is a blunt instrument, and the assertions are deliberately coarse: a call's
    /// name appearing in the right window, a signature not becoming a field again. They would not
    /// survive a refactor that renamed things, which is the intended trade - a rename fails loudly
    /// and a human reads the message, where a silently detached hold fails in a playtest.
    /// </para>
    /// </summary>
    public sealed class BuildBuyGateWiringTests
    {
        // ---- the bhav calls ---------------------------------------------------------------

        [Fact]
        public void TheDisableAndEnableCallsTakeAndReleasePerObjectHolds()
        {
            var call = Source(FreeSource("tso.simantics", "Primitives", "VMGenericTS1Call.cs"));

            // Between the two case labels, so neither assertion can be satisfied by the other's
            // call - which is what a window over the whole switch would allow.
            Assert.Contains("AcquireBuildBuyHold", Window(call, "case VMGenericTS1CallMode.DisableBuildBuy", "case VMGenericTS1CallMode.EnableBuildBuy"));
            Assert.Contains("ReleaseBuildBuyHold", Window(call, "case VMGenericTS1CallMode.EnableBuildBuy", "case VMGenericTS1CallMode.GetDistanceToCameraInTemp0"));
        }

        [Fact]
        public void BothCallsAttributeTheHoldToTheCallingObject()
        {
            // Not to the lot, and not to a counter nobody owns. If either call site stopped naming
            // context.Caller the hold would belong to nothing and the flag would latch again, with
            // no test failing - Acquire and Release on their own still compile and still balance.
            var call = Source(FreeSource("tso.simantics", "Primitives", "VMGenericTS1Call.cs"));

            Assert.Contains("context.Caller",
                Window(call, "case VMGenericTS1CallMode.DisableBuildBuy", "case VMGenericTS1CallMode.EnableBuildBuy"));
            Assert.Contains("context.Caller",
                Window(call, "case VMGenericTS1CallMode.EnableBuildBuy", "case VMGenericTS1CallMode.GetDistanceToCameraInTemp0"));
        }

        [Fact]
        public void TheTwoCallsStillAnnounceTheChangeToTheUi()
        {
            // TS1GameScreen listens for this and refreshes both button groups. The per-frame
            // refresh below is the addition, not a replacement - a genuine fire should show up as
            // promptly as it ever did.
            var call = Source(FreeSource("tso.simantics", "Primitives", "VMGenericTS1Call.cs"));

            Assert.Equal(2, Regex.Matches(call, "SignalGenericVMEvt\\(VMEventType\\.TS1BuildBuyChange").Count);
        }

        [Fact]
        public void NoEngineCodeWritesTheBuildBuyFlagDirectly()
        {
            // The flag is derived, so it is get-only. The only writes left anywhere in
            // tso.simantics are to the marshal's own field, which is a different symbol - what
            // has to be assignment-free is Architecture.BuildBuyEnabled.
            var offenders = new List<string>();
            foreach (var file in Directory.GetFiles(FreeSource(), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;

                if (Regex.IsMatch(Source(file), @"Architecture\s*\.\s*BuildBuyEnabled\s*=[^=]")) offenders.Add(file);
            }

            Assert.Empty(offenders);
        }

        // ---- the removal net --------------------------------------------------------------

        [Fact]
        public void BeingRemovedIsWhatGivesUpAHold()
        {
            // Every way the engine takes an object off the lot - VM.RemoveEntity for a sim or NPC
            // leaving, VMEntity.Delete for a destroyed object, VM.LoadAsync for the outgoing
            // lot's objects - goes through VMEntity.Dead, and none of them runs bhav. That makes
            // Dead's setter the one place a hold can be given up for all of them, so it has to be
            // a property that releases rather than the field it used to be.
            var text = Source(FreeSource("tso.simantics", "Entities", "VMEntity.cs"));

            Assert.False(Regex.IsMatch(text, @"public\s+bool\s+Dead\s*;"),
                "VMEntity.Dead is a field again, so removing an object can no longer give up the"
                + " lot-wide build/buy disable it took - and a disabling object that is deleted"
                + " leaves the player unable to build or buy for good.");

            Assert.Contains("ReleaseBuildBuyHold", MethodBody(text, "public bool Dead", "set"));
        }

        [Fact]
        public void TheLotPrunesStaleHoldsOnEveryTickAndOnEveryRead()
        {
            // The net under the removal one: a hold whose object went away without going through
            // Dead's setter - a removal path outside this assembly, say - is still caught, because
            // the flag prunes when it is read and once per lot tick, and a save prunes before it
            // records the value.
            var architecture = Source(FreeSource("tso.simantics", "VMArchitecture.cs"));

            Assert.Contains("PruneBuildBuyHolders", MethodBody(architecture, "public bool BuildBuyEnabled", "get"));
            Assert.Contains("PruneBuildBuyHolders", MethodBody(architecture, "public void Tick()"));
            Assert.Contains("PruneBuildBuyHolders", MethodBody(architecture, "public virtual VMArchitectureMarshal Save()"));
        }

        [Fact]
        public void TheLotOwnsTheHoldsAsASetAndNeverAsABareFlag()
        {
            // Two holders, one leaving, build/buy still off - the per-object ownership that keeps
            // a fire from being re-enabled by something else's bhav. The set is what makes that
            // expressible at all.
            var architecture = Source(FreeSource("tso.simantics", "VMArchitecture.cs"));

            Assert.Contains("HashSet<VMEntity>", architecture);
            Assert.Contains("VMBuildBuyGate.Recompute", architecture);
            Assert.Contains("buildBuyEnabled = VMBuildBuyGate.IsEnabled(BuildBuyHolders.Count)",
                MethodBody(architecture, "public void AcquireBuildBuyHold(VMEntity entity)"));
            Assert.Contains("buildBuyEnabled = VMBuildBuyGate.IsEnabled(BuildBuyHolders.Count)",
                MethodBody(architecture, "public void ReleaseBuildBuyHold(VMEntity entity)"));
        }

        // ---- the lot load -----------------------------------------------------------------

        [Fact]
        public void LoadingALotDoesNotTrustTheMarshalledFlag()
        {
            // This is the save-load half of the bug: the flag was marshalled, so a lot saved while
            // its disabler had already gone away came back blocked, and every later save wrote the
            // block back out. The field is still read off disk and still written - the format is
            // unchanged - but Load re-derives instead of restoring it.
            var load = MethodBody(Source(FreeSource("tso.simantics", "VMArchitecture.cs")),
                "public virtual void Load(VMArchitectureMarshal input)");

            Assert.DoesNotContain("input.BuildBuyEnabled", load);
            Assert.Contains("buildBuyEnabled = true", load);
            Assert.Contains("BuildBuyHolders.Clear()", load);
        }

        [Fact]
        public void SavingALotWritesTheDerivedFlagNotTheStoredOne()
        {
            var save = MethodBody(Source(FreeSource("tso.simantics", "VMArchitecture.cs")),
                "public virtual VMArchitectureMarshal Save()");

            Assert.Contains("BuildBuyEnabled = buildBuyEnabled", save);
        }

        // ---- the two button groups ---------------------------------------------------------

        [Fact]
        public void BothBuyBuildButtonGroupsRefreshFromThePerFrameUpdate()
        {
            // Both panels also refresh on TS1BuildBuyChange, from the two bhav calls. That signal
            // does not cover the case the bug was: a hold released by the engine, or dropped by a
            // reload, runs no bhav and so signals nothing. Reading the flag each frame - and only
            // touching the buttons on a change - is the safety net, and it has to be on both
            // groups or a player watches one of them stay greyed out.
            foreach (var panel in BothPanels())
            {
                var name = Path.GetFileName(panel);
                var text = Source(panel);

                var update = MethodBody(text, "public override void Update(UpdateState state)");
                Assert.True(update.Contains("RefreshBuildBuyIfChanged()"),
                    name + " never re-reads the lot's build/buy state from its own update, so a hold"
                    + " the engine released - which signals nothing - would leave its buttons greyed"
                    + " out for good.");

                var refresh = MethodBody(text, "private void RefreshBuildBuyIfChanged()");
                Assert.True(refresh.Contains("Game?.vm?.Context?.Architecture?.BuildBuyEnabled"),
                    name + " does not read the lot's build/buy state where a null VM or architecture"
                    + " would have to be allowed for, so its refresh cannot run outside a loaded lot.");
                Assert.True(refresh.Contains("UpdateBuildBuy()"),
                    name + " reads the flag but never pushes it to its buttons.");
            }
        }

        [Fact]
        public void TheRefreshOnlyTouchesTheButtonsWhenTheValueChanged()
        {
            // Per frame means per frame. Without the comparison this runs four button property
            // sets sixty times a second, and each of those invalidates the element and re-runs
            // CalculateState.
            foreach (var panel in BothPanels())
            {
                var refresh = MethodBody(Source(panel), "private void RefreshBuildBuyIfChanged()");

                Assert.Contains("LastBuildBuyEnabled.HasValue", refresh);
                Assert.Contains("LastBuildBuyEnabled.Value == bbEnable", refresh);
                Assert.Contains("LastBuildBuyEnabled = bbEnable", refresh);
            }
        }

        [Fact]
        public void TheDesktopStillBlocksTheButtonsThroughTheOrdinaryDisabledPath()
        {
            // The per-frame refresh must not become the only thing standing between the player and
            // Buy/Build: the gate is still UIButton.Disabled, which swallows the click. A
            // "re-enable" that went round the gate would leave a mode that could be entered while
            // the lot says it may not be.
            foreach (var panel in BothPanels())
            {
                var text = Source(panel);
                var update = MethodBody(text, "public void UpdateBuildBuy()");

                Assert.Contains("Disabled = !bbEnable", update);
            }
        }

        // ---- helpers ----------------------------------------------------------------------

        private static IEnumerable<string> BothPanels()
        {
            yield return SimitoneSource("UI", "Panels", "UIModeSwitcher.cs");
            yield return SimitoneSource("UI", "Panels", "Desktop", "UIDesktopUCP.cs");
        }

        /// <summary>
        /// A C# method body, located by its opening anchors. The anchors are matched
        /// <em>in order</em>, so <c>("public bool Dead", "set")</c> finds that property's setter
        /// rather than the first <c>set</c> in the file, and the body is the braces that balance
        /// from the first one after the last anchor.
        /// </summary>
        private static string MethodBody(string source, params string[] anchors)
        {
            var at = -1;
            foreach (var anchor in anchors)
            {
                at = source.IndexOf(anchor, at + 1, StringComparison.Ordinal);
                Assert.True(at >= 0, "no '" + anchor + "' after the previous anchor in the source under test");
            }

            var open = source.IndexOf('{', at);
            Assert.True(open >= 0, "'" + anchors[0] + "' has no body");

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }

            throw new InvalidOperationException("unbalanced braces after '" + anchors[0] + "'");
        }

        /// <summary>
        /// The text between two markers, exclusive. Used where the construct under test has no
        /// braces of its own - a <c>switch</c> case body, for instance, so the next brace in the
        /// file belongs to something else entirely.
        /// </summary>
        private static string Window(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "no '" + from + "' in the source under test");
            start += from.Length;

            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end >= 0, "no '" + to + "' after '" + from + "'");

            return source.Substring(start, end - start);
        }

        /// <summary>
        /// C# with comments removed, so an assertion about what a file <em>does</em> cannot be
        /// satisfied - or broken - by what it explains. The comment above <c>Load</c> in
        /// <c>VMArchitecture</c> names <c>input.BuildBuyEnabled</c> precisely in order to say it
        /// is not read, and would otherwise fail the test that checks exactly that.
        /// </summary>
        private static string Source(string file)
        {
            var text = File.ReadAllText(file);
            var withoutBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
        }

        /// <summary>A file in Simitone's own client project, e.g. UI/Panels/UIModeSwitcher.cs.</summary>
        private static string SimitoneSource(params string[] parts)
        {
            return Path.Combine(new[] { ClientDir(), "Simitone.Client" }.Concat(parts).ToArray());
        }

        /// <summary>A file in the FreeSO client tree, e.g. tso.simantics/VMArchitecture.cs.</summary>
        private static string FreeSource(params string[] parts)
        {
            return Path.Combine(new[] { SemanticsDir() }.Concat(parts).ToArray());
        }

        /// <summary>The tso.simantics source root, the directory walked by the "no direct writes" scan.</summary>
        private static string FreeSource()
        {
            return SemanticsDir();
        }

        private static string SemanticsDir()
        {
            return Path.Combine(FreeSoDir(), "FreeSO", "TSOClient");
        }

        /// <summary>
        /// The folder holding the Simitone projects, found by walking up out of the test output
        /// rather than by a fixed number of levels, so a build-layout change turns into a clear
        /// failure instead of "file not found".
        /// </summary>
        private static string ClientDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows", "Simitone.Windows.csproj"))) return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows.csproj"))) return dir.FullName;

                dir = dir.Parent;
            }

            throw new FileNotFoundException("Could not find Simitone.Windows.csproj above " + AppContext.BaseDirectory);
        }

        /// <summary>
        /// The folder that contains the FreeSO checkout - a submodule sitting beside the Simitone
        /// solution folder. Found by looking for it rather than by counting levels, for the same
        /// reason.
        /// </summary>
        private static string FreeSoDir()
        {
            var dir = new DirectoryInfo(ClientDir());
            for (var i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO", "TSOClient"))) return dir.FullName;
            }

            throw new DirectoryNotFoundException("Could not find FreeSO/TSOClient above " + ClientDir());
        }
    }
}
