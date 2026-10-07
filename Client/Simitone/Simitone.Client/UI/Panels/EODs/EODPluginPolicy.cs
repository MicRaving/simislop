/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FSO.Client;
using FSO.Common;
using Microsoft.Xna.Framework;

namespace Simitone.Client.UI.Panels.EODs
{
    /// <summary>
    /// One EOD plugin this build will not open, and the reason it will not.
    /// </summary>
    /// <remarks>
    /// The reason is part of the entry rather than a comment because it is the only thing that
    /// makes an exclusion reviewable later. "Simitone does not run this" stops being
    /// self-justifying the moment nobody remembers why, and each of these has a concrete reason
    /// that is still true: a global-link callback Simitone's stub never makes, a market with
    /// nothing in it, or a second player who is not there.
    /// </remarks>
    public sealed class EODExclusion
    {
        public uint PluginID { get; private set; }
        public string Name { get; private set; }
        public string Reason { get; private set; }

        public EODExclusion(uint pluginID, string name, string reason)
        {
            PluginID = pluginID;
            Name = name;
            Reason = reason;
        }

        public override string ToString()
        {
            return PluginID.ToString("X8") + " " + Name + ": " + Reason;
        }
    }

    /// <summary>Why the EOD framework did or did not come up on this lot.</summary>
    public enum EODStartOutcome
    {
        /// <summary>At least one plugin is both allowed and buildable. The host is live.</summary>
        Start,

        /// <summary>This VM has no EOD host, so there is nothing for a UI to talk to.</summary>
        NoHost,

        /// <summary>Every plugin in the build is on the exclusion list.</summary>
        NoEligiblePlugins,

        /// <summary>
        /// Some plugins are allowed, but not one of their UI layouts is on disk. The panels are
        /// built entirely out of <c>.uis</c> files, so without them the constructor throws and
        /// there is no window to show.
        /// </summary>
        NoLayouts
    }

    /// <summary>
    /// The outcome of the one decision that decides whether Simitone brings the EOD framework up,
    /// plus the evidence for it. Produced by <see cref="EODPluginPolicy.Decide"/>, which is pure,
    /// and consumed by <see cref="EODHost"/>, which is not.
    /// </summary>
    public sealed class EODStartDecision
    {
        public EODStartOutcome Outcome { get; private set; }
        public string Reason { get; private set; }
        public int EligiblePlugins { get; private set; }
        public ReadOnlyCollection<string> MissingLayouts { get; private set; }

        public bool CanStart { get { return Outcome == EODStartOutcome.Start; } }

        public EODStartDecision(EODStartOutcome outcome, string reason, int eligiblePlugins, IEnumerable<string> missingLayouts)
        {
            Outcome = outcome;
            Reason = reason;
            EligiblePlugins = eligiblePlugins;
            MissingLayouts = new ReadOnlyCollection<string>((missingLayouts ?? Enumerable.Empty<string>()).ToList());
        }
    }

    /// <summary>
    /// Which EOD (end-of-day minigame) plugins a Simitone TS1 lot may open.
    ///
    /// <para>
    /// The framework itself is not the problem: Simitone runs FreeSO's <em>server</em> VM in
    /// process, so <c>VMEODHost</c> is already constructed and ticked and the retail TS1
    /// minigame plugins are already running on the simulation thread. They were simply playing
    /// into a void, because nothing was subscribed to <c>VM.OnEODMessage</c>. What is missing is
    /// the client half - <c>FSO.Client.UI.Panels.EODs</c> - and it needs two things this class
    /// answers: which of the twenty-odd panels can work on a single-player TS1 lot at all, and
    /// whether the game has the files those panels are built from.
    /// </para>
    ///
    /// <para>
    /// Everything here is deliberately static and side-effect free. It is consulted once per lot
    /// load, it changes no game state, and its whole job is to be able to answer "which plugins
    /// are eligible, and why not the others" without a VM, a graphics device or a content
    /// directory - which is what makes it testable, and it is tested in
    /// <c>Simitone.Tests.UI.EODPluginPolicyTests</c>.
    /// </para>
    ///
    /// <para>
    /// <b>One exclusion that is deliberately not on the list.</b> The casino panels (blackjack,
    /// roulette, slots, hold'em) each hold a <c>UIManageEODObjectPanel</c> - FreeSO's admin object
    /// manager, which is exactly the kind of IDE-shaped panel the port is supposed to leave out.
    /// They are left eligible anyway, because it is only ever constructed from a panel's
    /// <em>owner</em> handler, and owner mode does not exist on a TS1 lot - for two reasons, both
    /// of them about the lot rather than about the port. A TS1 object has no owner id to compare
    /// against, because TS1 has no per-object ownership; and there would be nothing to manage even
    /// if there were, since the TS1 global link gives anything that is not an avatar
    /// <c>uint.MaxValue</c>. The plugins therefore ask <c>VMEODPlatformState.IsOwner</c>, which
    /// answers "no" and logs the reason once per plugin, so the admin panel is unreachable by
    /// design rather than by accident. Player/dealer mode, the mode that matters, is unaffected.
    /// </para>
    /// </summary>
    public static class EODPluginPolicy
    {
        // ---- plugin ids, named ---------------------------------------------------------------
        // The same values as VMEODServer.IDToHandler (the authoritative list) and
        // UIEODController.IDToHandler (the panels). Kept as named constants so a policy entry
        // reads as a sentence rather than as a bare hex literal.

        public const uint Signs = 0x2a6356a0;
        public const uint DanceFloor = 0x4a5be8ab;
        public const uint PizzaMaker = 0xea47ae39;
        public const uint PaperChase = 0xca418206;
        public const uint RackOwner = 0x2b58020b;
        public const uint Rack = 0xcb492685;
        public const uint Dresser = 0x8b300068;
        public const uint Scoreboard = 0x0949E698;
        public const uint PermissionDoor = 0x0A69F29F;
        public const uint Slots = 0xCB2819CB;
        public const uint Trunk = 0xAA5E36DC;
        public const uint WarGame = 0x2D642D39;
        public const uint Timer = 0xAA65FE9E;
        public const uint GameCompDrawACard = 0x895C1CEB;
        public const uint Band = 0x8ADFC7A2;
        public const uint Roulette = 0x0B2A6B83;
        public const uint SecureTrade = 0x897f82f5;
        public const uint Blackjack = 0x2B2FC514;
        public const uint TwoPersonJobObjectMaze = 0x4A245A22;
        public const uint NCDanceFloor = 0xEC55D705;
        public const uint DJStation = 0x6C5C7555;
        public const uint Newspaper = 0x00001000;
        public const uint HoldEmCasino = 0x00001001;
        public const uint Bulletin = 0x00001003;
        public const uint CooldownEvent = 0x00001004;
        public const uint GameshowBuzzerPlayer = 0x00001005;
        public const uint GameshowBuzzerHost = 0x00001006;
        public const uint PropertySelect = 0x00002000;

        private static readonly ReadOnlyCollection<EODExclusion> ExclusionList =
            new ReadOnlyCollection<EODExclusion>(new List<EODExclusion>
            {
                // The one exclusion that is about the lot rather than about the panel: secure
                // trade needs a second player AND a shard. VMTS1GlobalLinkStub leaves both
                // SecureTrade overloads and both FindLotAndValue overloads with empty bodies
                // (VMTS1GlobalLinkStub.cs:308,312,336,341), so even two Sims would get silence.
                // The panel is also the only one that needs FSO's own lot control, for its
                // QueryPanel.
                new EODExclusion(SecureTrade, "Secure Trade",
                    "needs a second player and a shard; both SecureTrade and FindLotAndValue are empty in the TS1 global link stub"),

                // The outfit market. GetOutfits hands back an empty array and every one of
                // PurchaseOutfit / UpdateOutfitSalePrice / DeleteOutfit / StockOutfit is a no-op
                // (VMTS1GlobalLinkStub.cs:277-298), so these three panels would open onto a
                // permanently empty list.
                new EODExclusion(RackOwner, "Rack (owner)",
                    "the outfit market is empty in the TS1 global link stub"),
                new EODExclusion(Rack, "Rack",
                    "the outfit market is empty in the TS1 global link stub"),
                new EODExclusion(Dresser, "Dresser",
                    "same empty outfit market as the racks; GetOutfits returns no rows and DeleteOutfit is a no-op"),

                // The two panels that wait on a global-link callback that never arrives. The
                // plugin calls GetBulletinState / GetDynPayouts during OnConnection and the stub
                // bodies are empty, so the panel opens and then sits there for ever, with the
                // sim's interaction thread still blocked on the plugin. Both are FreeSO
                // additions rather than retail TS1 EODs, so nothing is lost by leaving them out.
                new EODExclusion(Bulletin, "Bulletin Board",
                    "GetBulletinState never calls back, so the panel would open and wait for ever"),
                new EODExclusion(Newspaper, "Newspaper",
                    "GetDynPayouts never calls back, so the panel would open and wait for ever"),

                // Same shape: a generic cooldown-gated object whose panel can only appear once
                // GetObjectGlobalCooldown resolves. It also read vm.TSOState.CommunityLot, which is
                // null on a TS1 lot; that read is guarded now (VMEODPlatformState.IsCommunityLot)
                // and answers "no".
                new EODExclusion(CooldownEvent, "Cooldown Event",
                    "GetObjectGlobalCooldown and GetAccountIDFromAvatar never call back, and the plugin reads vm.TSOState"),

                // FreeSO's gameshow buzzer, which is two panels and two players. Refusing the
                // host half alone would leave the player half talking to nobody.
                new EODExclusion(GameshowBuzzerHost, "Gameshow Buzzer (host)",
                    "half of a two-player game; neither half can complete alone"),
                new EODExclusion(GameshowBuzzerPlayer, "Gameshow Buzzer (player)",
                    "half of a two-player game; neither half can complete alone"),

                // The freeso-archive feature. Its panel drives the archive's own property-select
                // controller, which has nothing to select in a TS1 lot.
                new EODExclusion(PropertySelect, "Property Select",
                    "a freeso-archive feature, and nothing in a TS1 lot is up for selection"),
            });

        /// <summary>
        /// The <c>.uis</c> layout each panel is built from, for the plugins that are not excluded.
        ///
        /// <para>
        /// This is the one thing in the whole port that Simitone
        /// used not to ship. The names are retail The Sims Online file names because the panels
        /// are FSO.Client's code and they ask for their layout by name - <c>UISignsEOD</c> calls
        /// <c>RenderScript("signseod.uis")</c>, and <c>UIScript.Parse</c> then opens that path
        /// with <c>File.OpenText</c>. The retail files themselves are artwork layouts whose
        /// pictures live in a TSO install: a The Sims 1 install has no copy of one (its
        /// <c>UIGraphics.far</c> is a TS1-era GZ resource catalogue with no <c>.uis</c> in it at
        /// all), FreeSO's content loader only extracts the TSO content tree in TSO mode, and
        /// this port may not ship an EA asset in any case.
        /// <para>
        /// So what sits behind each name is ours. <see cref="EODLayouts"/> declares which named
        /// control each panel has to find, <see cref="EODLayoutBuilder"/> turns that into the
        /// text of a <c>.uis</c>, and the result is committed as
        /// <c>Content/UI/uiscripts/*.uis</c> - so the game never writes a file at run time, and
        /// <see cref="IsAuthoredLayout"/> is the one place that answers "is this one of ours?".
        /// </para>
        ///
        /// <para>
        /// A panel whose layout is missing throws out of its own constructor
        /// (<c>UIScript.Parse</c> opens the file with <c>File.OpenText</c>), which is why
        /// <see cref="Decide"/> treats a missing layout as a reason not to start rather than as
        /// something to discover per interaction. Hold'em is the exception: it builds its whole
        /// interface in code and needs no layout at all.
        /// </para>
        /// </summary>
        private static readonly ReadOnlyDictionary<uint, string[]> Layouts =
            new ReadOnlyDictionary<uint, string[]>(new Dictionary<uint, string[]>
            {
                { Signs, new[] { "signseod.uis" } },
                { DanceFloor, new[] { "danceflooreod.uis" } },
                { PizzaMaker, new[] { "pizzamakereod.uis" } },
                { PaperChase, new[] { "paperchaseeod.uis" } },
                { Scoreboard, new[] { "scoreboardeod.uis" } },
                { PermissionDoor, new[] { "dooreod.uis" } },
                { Slots, new[] { "slotseod.uis" } },
                { Trunk, new[] { "trunkeod.uis" } },
                { WarGame, new[] { "wargameeod.uis" } },
                { Timer, new[] { "timereod.uis" } },
                { GameCompDrawACard, new[] { "gamecompdrawacardeod.uis" } },
                { Band, new[] { "jobobjband.uis" } },
                { Roulette, new[] { "rouletteeod.uis" } },
                { Blackjack, new[] { "blackjackeod.uis" } },
                { TwoPersonJobObjectMaze, new[] { "twopersonjobobjectmazecharisma.uis", "twopersonjobobjectmazelogic.uis" } },
                { NCDanceFloor, new[] { "danceplatformeod.uis" } },
                { DJStation, new[] { "djstationeod.uis" } },
                // FreeSO's hold'em table builds its interface in code - no .uis, and so no TSO
                // content dependency. It is a FreeSO-only object, so no TS1 lot can reach it
                // either way; it is left eligible because allowing it costs nothing.
                { HoldEmCasino, new string[0] },
            });

        /// <summary>Every plugin this build refuses, with the reason for each.</summary>
        public static ReadOnlyCollection<EODExclusion> Exclusions { get { return ExclusionList; } }

        /// <summary>Every plugin id the build knows about, excluded or not.</summary>
        public static ReadOnlyCollection<uint> KnownPlugins { get; private set; }

        /// <summary>Every plugin id that is neither excluded nor missing a layout.</summary>
        public static ReadOnlyCollection<uint> EligiblePlugins { get; private set; }

        /// <summary>Every distinct layout file an eligible plugin could need.</summary>
        public static ReadOnlyCollection<string> RequiredLayouts { get; private set; }

        /// <summary>
        /// The required layouts this build <em>authors</em>, as opposed to the ones it can only
        /// ever have if somebody drops a The Sims Online content tree in. Should be all of them;
        /// see <see cref="UnauthoredLayouts"/> for the ones that are not, which is a build
        /// mistake rather than a content one.
        /// </summary>
        public static ReadOnlyCollection<string> AuthoredLayouts { get; private set; }

        static EODPluginPolicy()
        {
            var known = new List<uint>();
            foreach (var e in ExclusionList) known.Add(e.PluginID);
            foreach (var id in Layouts.Keys) known.Add(id);
            known.Sort();
            KnownPlugins = new ReadOnlyCollection<uint>(known);

            var eligible = new List<uint>();
            var layouts = new List<string>();
            foreach (var pair in Layouts.OrderBy(x => x.Key))
            {
                if (IsExcluded(pair.Key)) continue;
                eligible.Add(pair.Key);
                layouts.AddRange(pair.Value);
            }
            EligiblePlugins = new ReadOnlyCollection<uint>(eligible);
            RequiredLayouts = new ReadOnlyCollection<string>(layouts.Distinct().ToList());
            AuthoredLayouts = new ReadOnlyCollection<string>(
                RequiredLayouts.Where(IsAuthoredLayout).ToList());
        }

        /// <summary>
        /// Whether Simitone authors a layout by that name, as opposed to only being able to have
        /// one if a The Sims Online install is present.
        /// </summary>
        /// <remarks>
        /// The answer is about the <em>build</em>, not about this install, which is what makes
        /// it checkable: a layout that is authored but missing from the content directory is a
        /// packaging problem <see cref="LayoutExistsOnDisk"/> reports, and a layout that is not
        /// authored at all is a hole in <see cref="EODLayouts"/> that no amount of installing
        /// anything will fill.
        /// </remarks>
        public static bool IsAuthoredLayout(string fileName)
        {
            return EODLayouts.IsAuthored(fileName);
        }

        /// <summary>Required layouts this build does not author. Empty in a correct build.</summary>
        public static ReadOnlyCollection<string> UnauthoredLayouts
        {
            get { return new ReadOnlyCollection<string>(RequiredLayouts.Where(x => !IsAuthoredLayout(x)).ToList()); }
        }

        /// <summary>True when <paramref name="pluginID"/> is on the exclusion list.</summary>
        public static bool IsExcluded(uint pluginID)
        {
            return Refusal(pluginID) != null;
        }

        /// <summary>
        /// Why <paramref name="pluginID"/> will not be opened, or null when it is allowed.
        /// </summary>
        public static string Refusal(uint pluginID)
        {
            foreach (var e in ExclusionList)
            {
                if (e.PluginID == pluginID) return e.Reason;
            }
            return null;
        }

        /// <summary>
        /// The gate handed to <c>UIEODController.PluginGate</c>, which refuses the exclusions and
        /// lets everything else through. The live-mode version of this decision, and the only
        /// place the refusal is seen from - a plugin that is refused here has already been told a
        /// client exists inside the VM, so the honest outcome is to leave the screen alone.
        /// </summary>
        public static string Gate(uint pluginID)
        {
            return Refusal(pluginID);
        }

        /// <summary>The layouts a given plugin needs, or an empty array.</summary>
        public static string[] LayoutsFor(uint pluginID)
        {
            string[] layouts;
            return Layouts.TryGetValue(pluginID, out layouts) ? layouts : new string[0];
        }

        /// <summary>
        /// The one decision: given that there is an EOD host to talk to, may the framework come
        /// up?
        /// </summary>
        /// <remarks>
        /// Ordered so the most fundamental failure is reported first. A VM with no host is not a
        /// Simitone problem at all, so saying "no layouts" instead would be a misleading log; and
        /// "every plugin is excluded" is a build problem, not a content problem, so it is worth
        /// distinguishing from "this install is missing some files".
        /// <para>
        /// <paramref name="layoutExists"/> is a seam rather than a direct <c>File.Exists</c> so
        /// this stays pure. <see cref="LayoutExistsOnDisk"/> is the real one.
        /// </para>
        /// </remarks>
        /// <param name="hostAvailable">Whether the VM has a <c>VMEODHost</c>.</param>
        /// <param name="layoutExists">Answers "is this .uis file on disk?".</param>
        public static EODStartDecision Decide(bool hostAvailable, Func<string, bool> layoutExists)
        {
            if (!hostAvailable)
            {
                return new EODStartDecision(EODStartOutcome.NoHost,
                    "this VM has no EOD host, so the minigame plugins have nothing to talk to",
                    0, null);
            }

            if (EligiblePlugins.Count == 0)
            {
                return new EODStartDecision(EODStartOutcome.NoEligiblePlugins,
                    "every EOD plugin in this build is excluded",
                    0, null);
            }

            if (layoutExists == null)
            {
                return new EODStartDecision(EODStartOutcome.NoLayouts,
                    "no layout probe was supplied, so no panel can be shown to be buildable",
                    EligiblePlugins.Count, RequiredLayouts);
            }

            var missing = RequiredLayouts.Where(x => !layoutExists(x)).ToList();
            if (missing.Count == RequiredLayouts.Count)
            {
                return new EODStartDecision(EODStartOutcome.NoLayouts,
                    "none of the EOD UI layout scripts are on disk; every EOD panel is built from one, and this build's own copies are missing from the content directory",
                    EligiblePlugins.Count, missing);
            }

            return new EODStartDecision(EODStartOutcome.Start,
                missing.Count == 0
                    ? "all EOD UI layout scripts are present"
                    : missing.Count + " of " + RequiredLayouts.Count + " EOD UI layout scripts are missing; those panels will not open",
                EligiblePlugins.Count, missing);
        }

        /// <summary>
        /// The folders a <c>.uis</c> is looked for in, in order, exactly as
        /// <c>UIContainer.RenderScript</c> looks for it: the content directory first, then the
        /// game folder's own <c>gamedata/uiscripts</c>. A null or empty root is skipped rather
        /// than turned into a path rooted at nothing.
        /// <para>
        /// The first of those two is where this build's own layouts live
        /// (<c>Content/UI/uiscripts</c>, copied out of <c>Simitone.Client/Content</c> by the
        /// csproj), which is the whole of the "wiring to add it to LayoutSearchPaths": the
        /// search order the parser uses already points at them, so the same probe that decides
        /// whether a layout "exists" is the one the panel reads from. The second root is where a
        /// player's own <c>GameData/uiscripts</c> would be, which no supported install has -
        /// kept because it is what the parser does, not because anything is expected there.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="LayoutExistsOnDisk"/> so the *order and the roots* - the part
        /// that has to agree with the parser, or the probe answers about a different file than
        /// the one the panel will try to read - can be asserted without a game install. The
        /// probe itself is then only a <c>File.Exists</c> over this list.
        /// </remarks>
        public static IEnumerable<string> LayoutSearchPaths(string contentDir, string startupPath)
        {
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(contentDir)) paths.Add(Path.Combine(contentDir, @"UI/uiscripts"));
            if (!string.IsNullOrEmpty(startupPath)) paths.Add(Path.Combine(startupPath, @"gamedata/uiscripts"));
            return paths;
        }

        /// <summary>
        /// Whether one <c>.uis</c> file is on disk, anywhere the parser would look for it. Both
        /// roots are relative paths, so this resolves against the process working directory -
        /// which is the game folder - the same way the parser resolves them.
        /// </summary>
        public static bool LayoutExistsOnDisk(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            try
            {
                foreach (var dir in LayoutSearchPaths(FSOEnvironment.ContentDir, GlobalSettings.Default.StartupPath))
                {
                    if (File.Exists(Path.Combine(dir, fileName))) return true;
                }
                return false;
            }
            catch (Exception)
            {
                //a path the game cannot even resolve is a missing layout, not a crash
                return false;
            }
        }

        /// <summary>
        /// Where an active panel goes on screen.
        /// </summary>
        /// <remarks>
        /// A <c>.uis</c> is an absolute layout: it says "this button is at 137, 402" and nothing
        /// about how big the window is. In TSO that was fine because the layout was written for
        /// the client's own resolution, and FreeSO's live mode drew the panel inside its own
        /// frame. Simitone has no live-mode frame, so the host centres the panel itself.
        /// <para>
        /// Clamped at zero rather than allowed to go negative: a panel authored for a window
        /// larger than the one in use should hang off the bottom-right, not off the top-left,
        /// because the top-left is where its title and its first row of controls are.
        /// </para>
        /// </remarks>
        public static Vector2 Centre(Vector2 window, Vector2 panel)
        {
            return new Vector2(
                Math.Max(0f, (window.X - panel.X) / 2f),
                Math.Max(0f, (window.Y - panel.Y) / 2f));
        }
    }
}
