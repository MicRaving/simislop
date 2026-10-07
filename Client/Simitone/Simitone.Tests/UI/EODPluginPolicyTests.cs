using System;
using System.Collections.Generic;
using System.Linq;
using FSO.SimAntics.NetPlay.Model.Commands;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels.EODs;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="EODPluginPolicy"/> and <see cref="EODPanelBridge"/>: the two pure
    /// decisions behind Simitone's end-of-day minigame back-port.
    ///
    /// <para>
    /// The framework is two halves. The server half - <c>FSO.SimAntics.NetPlay.EODs</c> - was
    /// already running in Simitone before any of this, because a TS1 lot is driven by FreeSO's
    /// server VM; the retail TS1 minigame plugins load and play themselves into a void. The client
    /// half is <c>FSO.Client.UI.Panels.EODs</c>, and it needs two questions answered before it
    /// can be switched on: which of the twenty-odd panels can work on a single-player TS1 lot at
    /// all, and whether this install has the files those panels are built from. Those are the
    /// exclusion list and <see cref="EODPluginPolicy.Decide"/>, and both are static and
    /// side-effect free precisely so they can be pinned here.
    /// </para>
    ///
    /// <para>
    /// The invariant worth protecting above all is that the policy and the controller cannot
    /// disagree. The exclusion list is only enforced because it is handed to
    /// <c>UIEODController.PluginGate</c> as a delegate, so if the two ever answered differently
    /// the excluded panels would open anyway - which is the failure mode where a sim gets stuck
    /// behind a bulletin board waiting for a shard callback that will never arrive.
    /// </para>
    ///
    /// <para>
    /// Everything here is pure or reflection over a type that is deliberately absent, so there is
    /// no graphics device, no content, no VM and no IO.
    /// </para>
    /// </summary>
    public sealed class EODPluginPolicyTests
    {
        // ---- the exclusion list --------------------------------------------------------------

        [Fact]
        public void EveryExclusion_CarriesANameAndAReason()
        {
            //A refusal with no reason is a refusal nobody can revisit. Every entry here has a
            //concrete cause - an empty global-link callback, an empty market, a second player -
            //and the reason is part of the entry so it cannot be quietly lost.
            Assert.NotEmpty(EODPluginPolicy.Exclusions);
            foreach (var e in EODPluginPolicy.Exclusions)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Name), e + " has no name");
                Assert.False(string.IsNullOrWhiteSpace(e.Reason), e + " has no reason");
                Assert.Contains(" ", e.ToString());     // "ID Name: reason"
            }
        }

        [Fact]
        public void NoPluginIdIsExcludedTwice()
        {
            var dupes = EODPluginPolicy.Exclusions
                .GroupBy(x => x.PluginID)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key.ToString("X8"))
                .ToList();

            Assert.Empty(dupes);
        }

        [Theory]
        //the four the brief names outright
        [InlineData(0x897f82f5)]     // Secure Trade   - two players and a shard
        [InlineData(0x00001003)]     // Bulletin       - GetBulletinState never calls back
        [InlineData(0x00001000)]     // Newspaper      - GetDynPayouts never calls back
        [InlineData(0x2b58020b)]     // Rack (owner)   - empty outfit market
        [InlineData(0xcb492685)]     // Rack           - empty outfit market
        //and the rest of the same three shapes
        [InlineData(0x8b300068)]     // Dresser        - same empty outfit market
        [InlineData(0x00001004)]     // Cooldown Event - GetObjectGlobalCooldown never calls back
        [InlineData(0x00001005)]     // Gameshow (player)
        [InlineData(0x00001006)]     // Gameshow (host)
        [InlineData(0x00002000)]     // Property Select - a freeso-archive feature
        public void RefusedPlugins_AreExcluded(uint pluginID)
        {
            Assert.True(EODPluginPolicy.IsExcluded(pluginID));
            Assert.False(string.IsNullOrWhiteSpace(EODPluginPolicy.Refusal(pluginID)));
        }

        [Fact]
        public void AnExcludedPlugin_NeverAppearsInTheEligibleSet()
        {
            var excluded = EODPluginPolicy.Exclusions.Select(x => x.PluginID).ToHashSet();
            var leaked = EODPluginPolicy.EligiblePlugins.Where(excluded.Contains).ToList();

            Assert.Empty(leaked);
        }

        [Fact]
        public void TheExclusionList_AndTheEligibleSet_PartitionTheKnownPlugins()
        {
            //Anything not excluded has to be accounted for: a known plugin that is neither
            //excluded nor eligible is one whose layout was forgotten, which is a silent hole.
            var excluded = EODPluginPolicy.Exclusions.Select(x => x.PluginID).ToHashSet();
            var eligible = EODPluginPolicy.EligiblePlugins.ToHashSet();

            Assert.Equal(EODPluginPolicy.KnownPlugins.Count, excluded.Count + eligible.Count);
            foreach (var id in EODPluginPolicy.KnownPlugins)
            {
                Assert.True(excluded.Contains(id) || eligible.Contains(id), id.ToString("X8") + " is neither excluded nor eligible");
            }
        }

        // ---- which plugins are eligible --------------------------------------------------------

        [Theory]
        [InlineData(0x2a6356a0)]     // Signs
        [InlineData(0x4a5be8ab)]     // Dance Floor
        [InlineData(0xea47ae39)]     // Pizza Maker
        [InlineData(0xca418206)]     // Paper Chase
        [InlineData(0x0949E698)]     // Scoreboard
        [InlineData(0x0A69F29F)]     // Permission Door
        [InlineData(0xCB2819CB)]     // Slots
        [InlineData(0xAA5E36DC)]     // Trunk
        [InlineData(0x2D642D39)]     // War Game
        [InlineData(0xAA65FE9E)]     // Timer
        [InlineData(0x895C1CEB)]     // Game Comp Draw A Card
        [InlineData(0x8ADFC7A2)]     // Band
        [InlineData(0x0B2A6B83)]     // Roulette
        [InlineData(0x2B2FC514)]     // Blackjack
        [InlineData(0x4A245A22)]     // Two Person Job Object Maze
        [InlineData(0xEC55D705)]     // NC Dance Floor
        [InlineData(0x6C5C7555)]     // DJ Station
        [InlineData(0x00001001)]     // Hold'em - built in code, needs no layout
        public void RetailMinigames_AreEligible(uint pluginID)
        {
            Assert.False(EODPluginPolicy.IsExcluded(pluginID));
            Assert.Contains(pluginID, EODPluginPolicy.EligiblePlugins);
        }

        [Fact]
        public void TheCasinos_AreEligible_BecauseTheirEconomyAlreadyWorks()
        {
            //Blackjack, roulette, slots and hold'em are the reason this port is worth doing: the
            //TS1 global link stub moves money through TS1State.CurrentFamily.Budget, so they pay
            //real family funds with no economy code at all. (Owner mode is a separate matter and
            //does not exist on a TS1 lot - no per-object owner id, and an object has no money to
            //manage - so VMEODPlatformState.IsOwner answers no and the plugins run as player/
            //dealer, with the reason logged once per plugin.)
            foreach (var casino in new[] { EODPluginPolicy.Blackjack, EODPluginPolicy.Roulette, EODPluginPolicy.Slots, EODPluginPolicy.HoldEmCasino })
            {
                Assert.Contains(casino, EODPluginPolicy.EligiblePlugins);
            }
        }

        [Fact]
        public void HoldEm_NeedsNoLayoutScript()
        {
            //Every other panel is built by UIContainer.RenderScript off a .uis. Hold'em builds its
            //whole interface in code, which is why it is the one panel in the tree that needs no
            //layout - and the one that was already reachable before Simitone authored any.
            Assert.Empty(EODPluginPolicy.LayoutsFor(EODPluginPolicy.HoldEmCasino));
        }

        [Theory]
        [InlineData(0x2a6356a0, "signseod.uis")]               // Signs
        [InlineData(0x4a5be8ab, "danceflooreod.uis")]          // Dance Floor
        [InlineData(0xea47ae39, "pizzamakereod.uis")]          // Pizza Maker
        [InlineData(0xca418206, "paperchaseeod.uis")]          // Paper Chase
        [InlineData(0x0949E698, "scoreboardeod.uis")]          // Scoreboard
        [InlineData(0x0A69F29F, "dooreod.uis")]                // Permission Door
        [InlineData(0xCB2819CB, "slotseod.uis")]               // Slots
        [InlineData(0xAA5E36DC, "trunkeod.uis")]               // Trunk
        [InlineData(0x2D642D39, "wargameeod.uis")]             // War Game
        [InlineData(0xAA65FE9E, "timereod.uis")]               // Timer
        [InlineData(0x895C1CEB, "gamecompdrawacardeod.uis")]   // Game Comp Draw A Card
        [InlineData(0x8ADFC7A2, "jobobjband.uis")]             // Band
        [InlineData(0x0B2A6B83, "rouletteeod.uis")]            // Roulette
        [InlineData(0x2B2FC514, "blackjackeod.uis")]           // Blackjack
        [InlineData(0xEC55D705, "danceplatformeod.uis")]       // NC Dance Floor
        [InlineData(0x6C5C7555, "djstationeod.uis")]           // DJ Station
        public void EachLayoutPanel_KnowsTheScriptItIsBuiltFrom(uint pluginID, string layout)
        {
            Assert.Equal(new[] { layout }, EODPluginPolicy.LayoutsFor(pluginID));
        }

        [Fact]
        public void TheMaze_NeedsBothOfItsLayouts()
        {
            //UITwoPersonJobObjectMazeEOD is two panels - charisma and logic - behind one plugin
            //id, and a build with only one of them would open a maze with no rules.
            Assert.Equal(
                new[] { "twopersonjobobjectmazecharisma.uis", "twopersonjobobjectmazelogic.uis" },
                EODPluginPolicy.LayoutsFor(EODPluginPolicy.TwoPersonJobObjectMaze));
        }

        [Fact]
        public void RequiredLayouts_IsExactlyTheUnionOverEligiblePlugins()
        {
            var union = EODPluginPolicy.EligiblePlugins
                .SelectMany(EODPluginPolicy.LayoutsFor)
                .Distinct()
                .OrderBy(x => x, System.StringComparer.Ordinal)
                .ToList();

            var declared = EODPluginPolicy.RequiredLayouts.OrderBy(x => x, System.StringComparer.Ordinal).ToList();

            Assert.Equal(union, declared);
            Assert.Equal(declared.Count, EODPluginPolicy.RequiredLayouts.Count);   // no duplicates
            Assert.All(EODPluginPolicy.RequiredLayouts, x => Assert.EndsWith(".uis", x));
        }

        // ---- the gate -------------------------------------------------------------------------

        [Fact]
        public void TheGate_AgreesWithTheExclusionList()
        {
            //The gate is only a delegate because UIEODController.PluginGate takes one. If it ever
            //answered differently from IsExcluded, the excluded panels would open anyway.
            foreach (var id in EODPluginPolicy.KnownPlugins)
            {
                var refusal = EODPluginPolicy.Gate(id);
                Assert.Equal(EODPluginPolicy.IsExcluded(id), refusal != null);
                Assert.Equal(EODPluginPolicy.Refusal(id), refusal);
            }
        }

        [Fact]
        public void TheGate_LetsAnUnknownPluginThrough()
        {
            //Permissive by default: a plugin added to the fork after this policy was written
            //should be tried rather than refused for the sin of being unknown. It will still fail
            //to build if its layout is missing, which is the correct place for that to fail.
            Assert.Null(EODPluginPolicy.Gate(0xDEADBEEF));
            Assert.False(EODPluginPolicy.IsExcluded(0xDEADBEEF));
            Assert.Empty(EODPluginPolicy.LayoutsFor(0xDEADBEEF));
        }

        // ---- the wiring decision --------------------------------------------------------------

        private static Func<string, bool> Present(params string[] present)
        {
            var set = new HashSet<string>(present);
            return x => set.Contains(x);
        }

        [Fact]
        public void Decide_Refuses_WhenTheVmHasNoEODHost()
        {
            //Reported first, and on its own, because "this VM has no host" is a different problem
            //from "this install is missing files" and a log that confuses them sends whoever reads
            //it looking in the wrong place.
            var decision = EODPluginPolicy.Decide(false, _ => true);

            Assert.Equal(EODStartOutcome.NoHost, decision.Outcome);
            Assert.False(decision.CanStart);
            Assert.Contains("EOD host", decision.Reason);
            Assert.Empty(decision.MissingLayouts);
        }

        [Fact]
        public void Decide_Starts_WhenEveryLayoutIsPresent()
        {
            var decision = EODPluginPolicy.Decide(true, _ => true);

            Assert.Equal(EODStartOutcome.Start, decision.Outcome);
            Assert.True(decision.CanStart);
            Assert.Equal(EODPluginPolicy.EligiblePlugins.Count, decision.EligiblePlugins);
            Assert.Empty(decision.MissingLayouts);
        }

        [Fact]
        public void Decide_Refuses_WhenNotOneLayoutIsPresent()
        {
            //This is what a The Sims 1 install was in before Simitone authored its own layouts:
            //the .cst string tables ship with the game, but the .uis layout scripts came out of a
            //The Sims Online install and a TS1 lot had none of them. Simitone now generates its
            //own (see EODLayoutBuilderTests), so this is a packaging failure rather than the state
            //a player's install is in - and it still has to be answered "no".
            var decision = EODPluginPolicy.Decide(true, _ => false);

            Assert.Equal(EODStartOutcome.NoLayouts, decision.Outcome);
            Assert.False(decision.CanStart);
            Assert.Equal(EODPluginPolicy.RequiredLayouts.ToList(), decision.MissingLayouts.ToList());
            Assert.Contains("content directory", decision.Reason);
        }

        [Fact]
        public void EveryRequiredLayout_IsOneThisBuildAuthors()
        {
            //The decision above only ever answered "no" because the layouts were not on disk. This
            //is the other half of that: the reason they are on disk is that Simitone generates
            //them, and the only names it can generate are these. A plugin added to the fork whose
            //layout nobody has authored is the case this catches.
            Assert.Empty(EODPluginPolicy.UnauthoredLayouts);
            Assert.All(EODPluginPolicy.RequiredLayouts, x => Assert.True(EODPluginPolicy.IsAuthoredLayout(x), x));
        }

        [Fact]
        public void LayoutOrigin_IsAboutTheBuild_NotAboutThisInstall()
        {
            //IsAuthoredLayout is answered from the catalogue and never from the disk, which is what
            //makes it testable and what lets a packaging failure be reported as a packaging
            //failure rather than as "we do not have it".
            Assert.False(EODPluginPolicy.IsAuthoredLayout("this-file-does-not-exist.uis"));
            Assert.False(EODPluginPolicy.IsAuthoredLayout(null));
            Assert.Equal(EODPluginPolicy.RequiredLayouts.Count, EODPluginPolicy.AuthoredLayouts.Count);
        }

        [Fact]
        public void Decide_StillStarts_WhenOnlySomeLayoutsAreMissing()
        {
            //Partial content is a normal thing to hit - one plugin's layout extracted, the rest
            //not - and it must not take the minigames that do have their files down with it. The
            //ones that do not are caught per plugin, because UIEODController.OnEODMessage
            //refuses to build a panel whose constructor throws.
            var present = EODPluginPolicy.LayoutsFor(EODPluginPolicy.Signs);
            var decision = EODPluginPolicy.Decide(true, Present(present));

            Assert.True(decision.CanStart);
            Assert.DoesNotContain("signseod.uis", decision.MissingLayouts);
            Assert.Contains("slotseod.uis", decision.MissingLayouts);
            Assert.Contains("missing", decision.Reason);
        }

        [Fact]
        public void Decide_Refuses_WhenNoProbeIsSupplied()
        {
            //"I did not check" is not "it is fine". A null probe means no panel has been shown to
            //be buildable, so the answer has to be no.
            var decision = EODPluginPolicy.Decide(true, null);

            Assert.Equal(EODStartOutcome.NoLayouts, decision.Outcome);
            Assert.False(decision.CanStart);
            Assert.Equal(EODPluginPolicy.RequiredLayouts.Count, decision.MissingLayouts.Count);
        }

        [Fact]
        public void Decide_IsPure_SoTheAnswerCanBeReliedOn()
        {
            //Called twice with the same probe: no caching, no ordering effect, no first-call
            //bonus. The decision is made once per lot load, so a stale answer would outlive
            //whatever changed.
            var first = EODPluginPolicy.Decide(true, _ => false);
            var second = EODPluginPolicy.Decide(true, _ => false);

            Assert.Equal(first.Outcome, second.Outcome);
            Assert.Equal(first.Reason, second.Reason);
            Assert.Equal(first.MissingLayouts, second.MissingLayouts);
        }

        [Fact]
        public void Decide_NeverClaimsToStartWithoutAFileItCanName()
        {
            //The one invariant that ties the two halves of the policy together: if the framework
            //says it can start, at least one panel must actually be buildable. Checked against
            //the real probe, so it holds whatever the install happens to contain.
            var decision = EODPluginPolicy.Decide(true, EODPluginPolicy.LayoutExistsOnDisk);

            if (decision.CanStart)
            {
                Assert.NotEmpty(EODPluginPolicy.RequiredLayouts);
                Assert.Contains(EODPluginPolicy.RequiredLayouts, x => EODPluginPolicy.LayoutExistsOnDisk(x));
            }
        }

        // ---- the layout probe -----------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("this-file-does-not-exist-anywhere.uis")]
        public void LayoutProbe_SaysNoForAFileThatIsNotThere(string name)
        {
            //Must not throw. A path the game cannot resolve is a missing layout, not a crash in
            //the middle of opening a lot.
            Assert.False(EODPluginPolicy.LayoutExistsOnDisk(name));
        }

        [Fact]
        public void TheLayoutProbe_LooksWhereTheParserLooks()
        {
            //The order and the roots have to agree with UIContainer.RenderScript, or the probe
            //reports on a different file than the one the panel will try to read: content
            //directory first - which is where this build's own generated layouts are copied - then
            //the game folder's own gamedata, which no supported install has but which the parser
            //still falls back to.
            var paths = EODPluginPolicy.LayoutSearchPaths("mycontent", "mygame").ToList();

            Assert.Equal(2, paths.Count);
            Assert.StartsWith("mycontent", paths[0]);
            Assert.EndsWith("uiscripts", paths[0]);
            Assert.StartsWith("mygame", paths[1]);
            Assert.Contains("gamedata", paths[1]);
        }

        [Fact]
        public void TheLayoutProbe_SkipsARootItCannotResolve()
        {
            //A null root must not become a path rooted at nothing, which File.Exists would then
            //answer for the current directory.
            Assert.Empty(EODPluginPolicy.LayoutSearchPaths(null, null));
            Assert.Single(EODPluginPolicy.LayoutSearchPaths("mycontent", null));
            Assert.Single(EODPluginPolicy.LayoutSearchPaths(null, "mygame"));
        }

        // ---- panel placement ------------------------------------------------------------------

        [Fact]
        public void Centre_PutsASmallerPanelInTheMiddle()
        {
            Assert.Equal(new Vector2(350, 250), EODPluginPolicy.Centre(new Vector2(1000, 500), new Vector2(300, 0)));
            Assert.Equal(new Vector2(250, 200), EODPluginPolicy.Centre(new Vector2(1000, 500), new Vector2(500, 100)));
        }

        [Fact]
        public void Centre_ClampsRatherThanGoingNegative()
        {
            //A panel authored for a bigger window than the one in use must hang off the
            //bottom-right, not off the top-left: the top-left is where its title and its first
            //row of controls are.
            Assert.Equal(Vector2.Zero, EODPluginPolicy.Centre(new Vector2(800, 600), new Vector2(1024, 768)));
            Assert.Equal(new Vector2(0, 100), EODPluginPolicy.Centre(new Vector2(800, 600), new Vector2(1024, 400)));
        }

        [Fact]
        public void Centre_IsTheIdentityForAnExactlyFittingPanel()
        {
            Assert.Equal(Vector2.Zero, EODPluginPolicy.Centre(new Vector2(800, 600), new Vector2(800, 600)));
        }

        // ---- the seam into FSO.Client ---------------------------------------------------------

        [Fact]
        public void ThePanelBridge_ReportsAReasonInsteadOfThrowing()
        {
            //Simitone.Client does not reference FSO.Client, so the controller is reached
            //reflectively and this is the off-safe floor under it: no VM, so no bridge, and a
            //reason in plain words rather than a TypeLoadException from inside a lot load.
            var bridge = EODPanelBridge.TryOpen(null, null);

            Assert.False(bridge.Available);
            Assert.Null(bridge.Controller);
            Assert.Contains("VM", bridge.UnavailableReason);
        }

        [Fact]
        public void ThePanelBridge_SwallowsMessagesWhenItIsNotOpen()
        {
            //This is what lets TS1GameScreen subscribe to VM.OnEODMessage unconditionally, rather
            //than branching on whether the framework started.
            var bridge = EODPanelBridge.TryOpen(null, null);

            bridge.OnEODMessage(null);
            bridge.OnEODMessage(new VMNetEODMessageCmd { PluginID = EODPluginPolicy.Signs, EventName = "eod_enter", TextData = "" });
            bridge.CloseActive();

            Assert.Null(bridge.ActivePanel);
        }
    }
}
