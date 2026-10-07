using System;
using System.Collections.Generic;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// What startup should do about choosing a The Sims 1 installation.
    /// </summary>
    public enum SimsInstallDecisionKind
    {
        /// <summary>
        /// <c>-path</c> was given on the command line. Use it and do nothing else: no
        /// discovery, no remembering, no questions. A command line flag outranks a stored
        /// preference, which is the same rule the graphics flags follow.
        /// </summary>
        CommandLinePath = 0,

        /// <summary>
        /// Simitone was configured before and the configured directory is still a Sims
        /// install. Use it without asking.
        /// </summary>
        RememberedPath,

        /// <summary>
        /// Simitone was configured before, but the configured directory is no longer a Sims
        /// install - the player moved it, deleted it, or the install was reinstalled
        /// elsewhere. The stored path is reported so it can be named in the prompt; the
        /// decision is otherwise the same as <see cref="MultipleCandidates"/> or
        /// <see cref="SingleCandidate"/>.
        /// </summary>
        StaleRememberedPath,

        /// <summary>
        /// Exactly one plausible install was found, so there is no question to ask. This is
        /// the ordinary case on a normal machine and must stay silent.
        /// </summary>
        SingleCandidate,

        /// <summary>
        /// Two or more plausible installs were found. The player has to say which one, because
        /// the editions are not interchangeable - their save data is not even stored in the
        /// same place.
        /// </summary>
        MultipleCandidates,

        /// <summary>
        /// Nothing was found and nothing was remembered.
        /// </summary>
        None
    }

    /// <summary>
    /// The outcome of <see cref="TS1InstallSelection.Resolve"/>: what kind of decision was
    /// reached, the path to boot on if it is already settled, and the roster it was reached
    /// from.
    ///
    /// Deliberately says nothing about <i>what</i> the previously stored path was. That value
    /// lives in the caller's settings, and the caller is the only thing that knows whether it
    /// survived validation - which is the difference between "you moved your Sims folder" and
    /// "you have never told me where your Sims is", two very different things to say to
    /// somebody whose install cannot be found.
    /// </summary>
    public sealed class SimsInstallDecision
    {
        private SimsInstallDecision(SimsInstallDecisionKind kind, string path, IReadOnlyList<SimsInstallCandidate> candidates)
        {
            Kind = kind;
            Path = path;
            Candidates = candidates ?? Array.Empty<SimsInstallCandidate>();
        }

        public SimsInstallDecisionKind Kind { get; }

        /// <summary>
        /// The directory to boot on, or null when the player still has to choose (or when
        /// there is nothing to choose from). Always in the forward-slash, trailing-slash form
        /// the caller normalises everything else to.
        /// </summary>
        public string Path { get; }

        /// <summary>Everything discovery found, in precedence order. Never null.</summary>
        public IReadOnlyList<SimsInstallCandidate> Candidates { get; }

        /// <summary>Is the boot path already decided, with nothing to ask the player?</summary>
        public bool IsSettled =>
            (Kind == SimsInstallDecisionKind.CommandLinePath || Kind == SimsInstallDecisionKind.RememberedPath
                || Kind == SimsInstallDecisionKind.SingleCandidate) && Path != null;

        /// <summary>
        /// Does the decision require showing a dialog? True for the ambiguous and empty cases;
        /// false for everything that can be booted straight through.
        /// </summary>
        public bool NeedsPrompt => Kind == SimsInstallDecisionKind.MultipleCandidates
            || Kind == SimsInstallDecisionKind.None
            || Kind == SimsInstallDecisionKind.StaleRememberedPath;

        internal static SimsInstallDecision Settled(SimsInstallDecisionKind kind, string path)
        {
            return new SimsInstallDecision(kind, path, null);
        }

        internal static SimsInstallDecision Single(SimsInstallCandidate candidate)
        {
            // The roster is carried through even here: the caller has to remember what it
            // resolved to, and the candidate is where the edition and the label live.
            return new SimsInstallDecision(SimsInstallDecisionKind.SingleCandidate, candidate.Path, new[] { candidate });
        }

        internal static SimsInstallDecision Ask(SimsInstallDecisionKind kind, IReadOnlyList<SimsInstallCandidate> candidates)
        {
            return new SimsInstallDecision(kind, null, candidates);
        }
    }

    /// <summary>
    /// The single authority for <i>which</i> The Sims installation Simitone boots on, and for
    /// where that installation's save data is read from.
    ///
    /// <para>
    /// <see cref="GameInstallLadder"/> answers a narrower question - given three probes, in
    /// what order do we consult them - and that ordering is unchanged. What this adds is the
    /// layer above it: a machine can legitimately have more than one of The Sims on it, and
    /// the first hit is not always the one the player means (a leftover
    /// <c>Maxis\The Sims\InstallPath</c> registry entry pointing at a copy they stopped
    /// using, alongside a Steam Legacy Collection they actually play). When more than one
    /// install is plausible the answer is no longer a policy the game can invent, so the game
    /// asks - and remembers the answer so it only ever has to ask once.
    /// </para>
    ///
    /// <para>
    /// Everything here is pure: the caller does the probing, the dialogs and the file IO, and
    /// hands the results in. That is what makes the rules testable, which matters because the
    /// rules that are easy to get wrong are the ones that only ever fire on a machine with an
    /// unusual install layout.
    /// </para>
    /// </summary>
    public static class TS1InstallSelection
    {
        /// <summary>
        /// Folder inside a The Sims install directory that holds the game data. Part of the
        /// sentinel below rather than a separate concept.
        /// </summary>
        public const string GameDataFolderName = "GameData";

        /// <summary>
        /// The one file whose presence proves a directory is a The Sims install.
        /// <c>Behavior.iff</c> is the behaviour graph the whole game runs on, so there is no
        /// Sims installation without one.
        /// </summary>
        public const string InstallSentinelName = "Behavior.iff";

        /// <summary>
        /// Is this directory a The Sims installation that can actually be booted?
        /// </summary>
        /// <remarks>
        /// The one place the sentinel is checked, shared by discovery (a stale
        /// <c>Maxis\The Sims\InstallPath</c> pointing at a deleted install is not a candidate)
        /// and by startup (a remembered path is only honoured while it still answers true).
        ///
        /// This does touch the disk, deliberately, and is the one member of this class that
        /// does. Splitting "decide" from "check" would push the checks back into the caller
        /// and leave the rule that matters - a stored path counts as a completed
        /// configuration only if it still validates - untestable, since it is the validation
        /// that makes the statement true. Callers pass the result straight into
        /// <see cref="Resolve"/> rather than passing a raw string and hoping.
        /// </remarks>
        public static bool IsUsableInstallPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return File.Exists(Path.Combine(path, GameDataFolderName, InstallSentinelName));
        }

        /// <summary>
        /// Decides how to pick an installation, given everything the caller could find.
        /// </summary>
        /// <param name="pathProvidedByCommandLine">
        /// Whether a <c>-path</c> argument was present <i>and</i> carried a usable value. An
        /// empty <c>-path</c> counts as absent, which is what
        /// <see cref="GamePathArgument.Normalize"/> already returns for it.
        /// </param>
        /// <param name="commandLinePath">The normalised value of that argument, if any.</param>
        /// <param name="wasConfigured">
        /// Whether Simitone has been configured for this installation before - see
        /// <see cref="IsAlreadyConfigured"/>, which is the rule for deriving this.
        /// </param>
        /// <param name="rememberedPath">
        /// The previously configured directory, but only if the caller has just verified it is
        /// still a Sims install. Pass null if it is stale, unknown, or was never set: this
        /// argument is a claim that has already been checked, and a checked-but-false claim
        /// must not be second-guessed here.
        /// </param>
        /// <param name="candidates">
        /// Every install discovery found, in precedence order. May be null or empty when
        /// nothing was found.
        /// </param>
        public static SimsInstallDecision Resolve(
            bool pathProvidedByCommandLine,
            string commandLinePath,
            bool wasConfigured,
            string rememberedPath,
            IReadOnlyList<SimsInstallCandidate> candidates)
        {
            if (pathProvidedByCommandLine && !string.IsNullOrEmpty(commandLinePath))
            {
                return SimsInstallDecision.Settled(SimsInstallDecisionKind.CommandLinePath, commandLinePath);
            }

            var roster = candidates ?? Array.Empty<SimsInstallCandidate>();
            var hasRememberedPath = !string.IsNullOrEmpty(rememberedPath);

            if (wasConfigured && hasRememberedPath)
            {
                return SimsInstallDecision.Settled(SimsInstallDecisionKind.RememberedPath, rememberedPath);
            }

            if (roster.Count == 0)
            {
                // A configuration that has gone stale and a machine with nothing on it end up
                // in the same place - there is no directory to boot - but they need different
                // messages, so the stale case keeps its own decision.
                return wasConfigured
                    ? SimsInstallDecision.Ask(SimsInstallDecisionKind.StaleRememberedPath, roster)
                    : SimsInstallDecision.Ask(SimsInstallDecisionKind.None, roster);
            }

            if (roster.Count == 1)
            {
                // Nothing to decide. Reported as settled so the caller does not put a
                // "did you mean...?" dialog in front of a player who has only one install -
                // including the player whose old choice has gone stale, where the only
                // candidate is still obviously the only one.
                return SimsInstallDecision.Single(roster[0]);
            }

            return SimsInstallDecision.Ask(SimsInstallDecisionKind.MultipleCandidates, roster);
        }

        /// <summary>
        /// Has this player already been through installation selection?
        /// </summary>
        /// <param name="installationConfiguredFlag">
        /// <c>GlobalSettings.Default.TS1InstallationConfigured</c>, written the first time a
        /// choice is made.
        /// </param>
        /// <param name="validatedRememberedPath">
        /// The stored path (<c>TS1HybridPath</c>), but only if the caller has just verified it
        /// is still a Sims install. Null otherwise.
        /// </param>
        /// <remarks>
        /// The flag is new in the config file, so it is absent - and false - for every player
        /// who configured Simitone before this feature existed. Treating that as "not
        /// configured" would drop a dialog in front of every existing player on their next
        /// launch, most of whom have exactly one install and would answer with the same
        /// directory the game was already booting. A stored path that still validates is
        /// therefore taken as proof of a completed configuration: it is the outcome the flag
        /// would have recorded, and honouring it keeps the upgrade silent.
        /// </remarks>
        public static bool IsAlreadyConfigured(bool installationConfiguredFlag, string validatedRememberedPath)
        {
            return installationConfiguredFlag || !string.IsNullOrEmpty(validatedRememberedPath);
        }

        /// <summary>
        /// Does this installation keep its save data in Saved Games (Steam/Legacy Collection)
        /// or next to the game (everything else)?
        /// </summary>
        /// <remarks>
        /// This is the value Simitone hands to <c>FSO.Content.Content.TS1SteamInstall</c>, which
        /// is what <c>TS1NeighbourhoodProvider</c> uses to decide where it copies its first
        /// neighbourhood from. Getting it wrong is not a cosmetic mistake: the two locations
        /// hold incompatible saves, so a Steam install bootstrapped from its own game folder
        /// would either fail (the folder is empty) or copy a stale retail neighbourhood.
        /// </remarks>
        public static bool IsSteamInstall(string installPath, bool discoveredAsSteam, bool pathCameFromThePlayer)
        {
            if (discoveredAsSteam) return true;
            // A directory the player typed with -path (or picked in a folder browser) has no
            // discovery result to take the edition from, so fall back to the shape of the path:
            // every Steam library stores games under <library>/steamapps/common.
            return pathCameFromThePlayer && SimsInstallCandidate.LooksLikeSteamLibraryPath(installPath);
        }

        /// <summary>
        /// The directory Simitone copies its first neighbourhood from: Saved Games for a
        /// Legacy Collection install, the install directory itself otherwise. Null when there
        /// is no install to read from.
        /// </summary>
        public static string BootstrapSourceDirectory(string installPath, bool isSteamInstall, string userProfile)
        {
            return isSteamInstall
                ? TS1UserData.SteamNeighborhoodPath(userProfile)
                : TS1UserData.InstallNeighborhoodPath(installPath);
        }

        /// <summary>
        /// Can Simitone seed its first neighbourhood from this installation?
        /// </summary>
        /// <remarks>
        /// The Steam-aware form of <see cref="TS1UserData.IsBootstrappable"/>, which is
        /// delegated to for the three editions whose saves are next to the game. Before the
        /// engine learned to source a Legacy Collection install's saves from Saved Games,
        /// checking only the install directory was right for every edition; now it is right
        /// for three of the four, and a Steam install would fail the pre-flight for the very
        /// reason it is supposed to help with. Same single file probe as before, aimed at
        /// whichever directory this edition actually uses.
        /// </remarks>
        public static bool IsBootstrappable(string installPath, bool isSteamInstall, string userProfile)
        {
            if (!isSteamInstall) return TS1UserData.IsBootstrappable(installPath);

            var source = TS1UserData.SteamNeighborhoodPath(userProfile);
            return source != null && File.Exists(Path.Combine(source, TS1UserData.NeighborhoodResourceName));
        }
    }
}
