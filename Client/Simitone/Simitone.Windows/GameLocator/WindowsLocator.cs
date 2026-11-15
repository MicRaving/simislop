using Microsoft.Win32;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Simitone.Windows.GameLocator
{
    /// <summary>
    /// Locates a The Sims 1 installation on Windows, in the precedence order defined by
    /// <see cref="GameInstallLadder"/>: a portable <c>../The Sims/</c> folder, then the Maxis
    /// installer registry entry, then Steam's Legacy Collection, then the default retail path.
    /// </summary>
    public class WindowsLocator : ILocator
    {
        private const string MaxisInstallKey = @"SOFTWARE\Maxis\The Sims";
        private const string InstallPathValue = "InstallPath";

        private readonly IRegistryReader registry;

        /// <summary>
        /// Locates the game against the real Windows registry.
        /// </summary>
        public WindowsLocator() : this(new WindowsRegistryReader())
        {
        }

        /// <summary>
        /// Locates the game against a supplied registry, so the discovery order can be driven with
        /// canned answers instead of whatever happens to be installed on the machine running it.
        /// </summary>
        internal WindowsLocator(IRegistryReader reader)
        {
            registry = reader ?? new WindowsRegistryReader();
        }

        public string FindTheSims1()
        {
            // A lambda rather than a method group: FindTheSimsLegacySteam takes the app id with a
            // default, and an optional parameter does not make a method group convertible to
            // Func<string>. The default app id stays declared on the probe itself, where it is
            // documented.
            return GameInstallLadder.FirstHit(FindPortableSimsDir(), FindMaxisRegistryInstallDir, () => FindTheSimsLegacySteam());
        }

        /// <summary>
        /// Every The Sims 1 installation on this machine that discovery can justify, in the same
        /// precedence order <see cref="FindTheSims1"/> consults.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="FindTheSims1"/> answers "which one would we boot on if nobody asked", which
        /// only has one right answer when exactly one install exists. That is not a safe
        /// assumption to boot on: a machine can hold a boxed copy the player stopped using
        /// years ago, a Steam Legacy Collection they actually play, or both - and the Legacy
        /// Collection keeps its save data in a different place, so the first hit is not
        /// necessarily the one they meant. This method is the input to the installation
        /// selector, so it reports everything rather than the first thing it finds.
        /// </para>
        ///
        /// <para>
        /// Every rung is consulted rather than stopping at the first hit, which is why the
        /// probes are not run through <see cref="GameInstallLadder"/> here: the ladder
        /// short-circuits by design, and a roster assembled through it would by construction
        /// never contain more than one entry.
        /// </para>
        ///
        /// <para>
        /// Candidates are filtered through <see cref="IsSimsInstallDir"/>, so a stale Maxis
        /// <c>InstallPath</c> left over from a deleted install does not become a row the player
        /// can pick (or a row that stops a valid Steam install from being offered). That filter is
        /// the only one, applied once to everything the probes returned - including the last rung,
        /// which is a constant rather than a probe and so is the one answer that is not already
        /// known to be an install. Offering it unverified would put a path in the selector that
        /// cannot boot, and - worse - could be the only row, quietly replacing the "nothing found"
        /// message with a question whose only answer is the same failure. A retail install reached
        /// this way (a GOG or EA app install with no Maxis registry entry at all) is still found,
        /// because it does have the sentinel.
        /// </para>
        ///
        /// <para>
        /// This is where the two entry points legitimately differ:
        /// <see cref="FindTheSims1"/> will happily return a stale registry path, which the caller
        /// then rejects against the same sentinel; the roster simply omits it.
        /// </para>
        /// </remarks>
        public IReadOnlyList<SimsInstallCandidate> FindAllTheSims1Installations()
        {
            var probed = new List<SimsInstallCandidate>();

            SimsInstallCandidate.TryAddTo(probed, FindPortableSimsDir(), SimsInstallKind.Portable);
            SimsInstallCandidate.TryAddTo(probed, FindMaxisRegistryInstallDir(), SimsInstallKind.Registry);
            SimsInstallCandidate.TryAddTo(probed, FindTheSimsLegacySteam(), SimsInstallKind.Steam);
            SimsInstallCandidate.TryAddTo(probed, GameInstallLadder.DefaultInstallDir, SimsInstallKind.Registry);

            var roster = new List<SimsInstallCandidate>(probed.Count);
            foreach (var candidate in probed)
            {
                if (IsSimsInstallDir(candidate.Path)) roster.Add(candidate);
            }
            return roster;
        }

        /// <summary>
        /// Does this directory hold a The Sims installation? The
        /// <c>GameData\Behavior.iff</c> sentinel, shared with
        /// <see cref="TS1InstallSelection.IsUsableInstallPath"/> so that the portable probe,
        /// the multi-install roster and the remembered-choice check all agree on what "an
        /// install" means.
        /// </summary>
        public static bool IsSimsInstallDir(string path)
        {
            return TS1InstallSelection.IsUsableInstallPath(path);
        }

        /// <summary>
        /// Looks for a Sims install next to the executable, the same relative convention the
        /// macOS and Linux locators use, so a copy of the game folder is portable.
        /// </summary>
        /// <returns>The relative directory, or null when there is no GameData\Behavior.iff there.</returns>
        private static string FindPortableSimsDir()
        {
            string localDir = @"../The Sims/";
            return IsSimsInstallDir(localDir) ? localDir : null;
        }

        /// <summary>
        /// Reads the install path out of the classic Maxis installer's registry entry.
        /// </summary>
        /// <remarks>
        /// Registry32 is deliberate: a 32-bit game may have been installed by a 64-bit installer,
        /// in which case the entry is in the 32-bit view. Each step of the walk now answers
        /// "is it there?" instead of dereferencing whatever OpenSubKey returned, so a machine with
        /// no Maxis key at all falls through to the Steam rung instead of throwing at startup.
        /// </remarks>
        private string FindMaxisRegistryInstallDir()
        {
            if (!Contains(registry.GetSubKeyNames(RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"), "Maxis")) return null;
            if (!Contains(registry.GetSubKeyNames(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Maxis"), "The Sims")) return null;

            string installDir = registry.GetStringValue(RegistryHive.LocalMachine, RegistryView.Registry32, MaxisInstallKey, InstallPathValue);
            // An absent InstallPath used to fall out of here as "/" (null + "\"), which is a
            // truthy string that silently beat every remaining rung and then failed the caller's
            // GameData\Behavior.iff check. Reporting a miss lets the ladder keep looking.
            if (string.IsNullOrEmpty(installDir)) return null;

            return (installDir + "\\").Replace('\\', '/');
        }

        private static bool Contains(string[] names, string wanted)
        {
            return names != null && Array.Exists(names, delegate (string s) { return s.Equals(wanted, StringComparison.InvariantCultureIgnoreCase); });
        }

        /// <summary>
        /// Finds The Sims Legacy Collection installed via Steam
        /// </summary>
        /// <param name="steamAppId">Steam App ID, default is The Sims: Legacy Collection</param>
        /// <returns>Full path if found, null otherwise</returns>
        private string FindTheSimsLegacySteam(int steamAppId = 3314060)
        {
            try
            {
                return SteamGameLocator.GetGamePath(steamAppId) ?? null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while finding The Sims: Legacy Collection Steam path: {ex.Message}");
                return null;
            }
        }

        private static bool is64BitProcess = (IntPtr.Size == 8);
        private static bool is64BitOperatingSystem = is64BitProcess || InternalCheckIsWow64();

        [DllImport("kernel32.dll", SetLastError = true, CallingConvention = CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWow64Process(
            [In] IntPtr hProcess,
            [Out] out bool wow64Process
        );

        /// <summary>
        /// Determines if this process is run on a 64bit OS.
        /// </summary>
        /// <returns>True if it is, false otherwise.</returns>
        public static bool InternalCheckIsWow64()
        {
            if ((Environment.OSVersion.Version.Major == 5 && Environment.OSVersion.Version.Minor >= 1) ||
                Environment.OSVersion.Version.Major >= 6)
            {
                using (Process p = Process.GetCurrentProcess())
                {
                    bool retVal;
                    if (!IsWow64Process(p.Handle, out retVal))
                    {
                        return false;
                    }
                    return retVal;
                }
            }
            else
            {
                return false;
            }
        }
    }
}
