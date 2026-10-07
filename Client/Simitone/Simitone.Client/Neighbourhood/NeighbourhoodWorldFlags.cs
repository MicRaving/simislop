/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using FSO.Common;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// One capability of the Part B neighbourhood world, as a value. The seven members are the
    /// seven rows of the handoff's §7 flag table, B1 to B7, and nothing else may be added without
    /// a milestone to switch on.
    ///
    /// <para>
    /// <b>Why the member names are not the key names.</b> The ini key is the capability's own
    /// name - <c>NeighbourhoodWorldSurroundings</c>, not <c>Surroundings</c> - because a settings
    /// file is read by a human with an editor, and because the handoff, the milestone notes and the
    /// build log all name the flag the same way. The mapping is in
    /// <see cref="NeighbourhoodWorldFlags.KeyFor"/> and nowhere else.
    /// </para>
    ///
    /// <para>
    /// <b>The order is the order of §7</b> and is only ever a list, never an index that means
    /// something: the stored form is a per-key boolean, so reordering this enum cannot re-interpret
    /// an existing <c>simitone.ini</c>.
    /// </para>
    /// </summary>
    public enum NeighbourhoodWorldFlag
    {
        /// <summary>
        /// B1. One authority for time, the active lot and the stored lots. Off, Part A behaviour.
        /// </summary>
        World = 0,

        /// <summary>
        /// B2. The neighbours you can see are real hollow lots rather than Part A's facades. Off,
        /// facades only.
        /// </summary>
        Surroundings = 1,

        /// <summary>
        /// B3. Discrete cross-lot travel through the world. Off, today's <c>SwitchLot</c> path.
        /// </summary>
        Travel = 2,

        /// <summary>
        /// B4. Continuous walkable streets between lots, with a following camera. Off, B3's
        /// discrete travel, which is the shippable result.
        /// </summary>
        Walk = 3,

        /// <summary>
        /// B5. Build/buy, lot ownership, jobs and leave-a-lot go through the world. Off,
        /// <c>VMTS1GlobalLinkStub</c>.
        /// </summary>
        Services = 4,

        /// <summary>
        /// B6. The world moves on while the player is elsewhere. Off, world time frozen off-lot.
        /// </summary>
        Offscreen = 5,

        /// <summary>
        /// B7. World state is persisted, migrated and recovered. Off, Part A's layout sidecar.
        /// </summary>
        Persist = 6
    }

    /// <summary>
    /// The Part B capability switches: seven booleans, one ini key each, every one of them
    /// <b>off</b> unless something has been recorded.
    ///
    /// <para>
    /// <b>Why a flag per capability and not one "part B" switch.</b> The handoff's §7 is a
    /// dependency graph - B2 needs B1, B4 needs B3, B6 needs B1 and B3 - and each milestone's
    /// acceptance test is stated as "with the flag off, the previous stage's behaviour is
    /// byte-identical". A single switch cannot be turned on halfway, so a half-built milestone
    /// could only be tested by breaking the stage before it, which is the one thing the graph is
    /// arranged to make unnecessary. Each flag is a separate switch so each milestone is
    /// independently gate-able and independently abandonable, which §7 says is the point of having
    /// the table at all.
    /// </para>
    ///
    /// <para>
    /// <b>All seven default off, and that is a promise rather than a starting value.</b> The
    /// default is <c>false</c> for every flag because "nothing has asked for this" and "a player
    /// turned it off" must give the same answer, and because the alternative - a feature that is
    /// on because nobody looked at the value - is how a world that is not finished reaches a
    /// player. A key that is missing, empty, or holds something this build does not recognise
    /// resolves to <see cref="Default"/>, on the same garbage-in-known-good-out contract as
    /// <see cref="SunCycleSetting.Resolve"/> and
    /// <see cref="NeighbourhoodViewSetting.Resolve(string)"/>: this is a file a person can edit.
    /// </para>
    ///
    /// <para>
    /// <b>Same file, same writer, no truncation.</b> Each flag is its own key in
    /// <c>simitone.ini</c> - the sidecar <see cref="NeighbourhoodViewSetting"/> and
    /// <see cref="SunCycleSetting"/> already share - and every read and write goes through
    /// <see cref="SunCycleSetting.Ini"/>, which is a read-merge-write: a write changes the one key
    /// it was asked about and leaves every other line exactly as it was. That is the whole reason
    /// the sidecar has one writer (<see cref="SimitoneIniFile"/>), because the old per-subclass
    /// <c>IniConfig.Save</c> truncated the file and turned every one of those keys back to its
    /// default without a word.
    /// </para>
    ///
    /// <para>
    /// <b>Lazy, cached and locked, for the same reason the other two settings are.</b> The world
    /// asks about a flag where a branch is taken, not at a moment where an eager load would be more
    /// correct, and a lazy load is what lets <see cref="Resolve"/> and the file round trip be
    /// tested with no game and no user directory around. Nothing here can block the game starting:
    /// an unreadable, absent or unwritable file is the default.
    /// </para>
    ///
    /// <para>
    /// <b>Cached, but not for ever - the cache is keyed on the file.</b> A cache that is filled
    /// once and never looked at again means a player who edits <c>simitone.ini</c> with the game
    /// running has to restart to see it, which for a file whose whole purpose is hand-editable is
    /// the difference between a switch and a rumour. So every read asks one question of the disk -
    /// <b>is this the same file I read?</b> - and only reopens the sidecar when the answer is no.
    /// The question is a single stat: the file's last-write time and its length, compared against
    /// the two numbers the cached values were read at. That is orders of magnitude cheaper than
    /// parsing seven keys out of a file, it cannot be wrong about a file that has not changed, and
    /// it costs nothing to be wrong about one that has: the reload happens on the next read rather
    /// than at some ticker's discretion. See <see cref="SidecarStamp"/> and
    /// <see cref="Refresh"/>.
    /// </para>
    ///
    /// <para>
    /// <b>What the stamp does <em>not</em> do is overrule a decision this build made.</b>
    /// <see cref="Set"/> and <see cref="AllOff"/> record values in memory, and they re-stamp the
    /// cache with the file as it is <em>after</em> them. A save this class made therefore never
    /// reads back as somebody else having edited the sidecar and throw away a value that has not
    /// reached the disk yet - which is the failure that a naive "reload whenever the file moved"
    /// rule walks straight into, because every save moves the file.
    /// </para>
    /// </summary>
    public static class NeighbourhoodWorldFlags
    {
        /// <summary>The sidecar ini the flags live in, the same one the view and sun use.</summary>
        public const string FileName = SunCycleSetting.FileName;

        /// <summary>
        /// What a flag is when nothing has chosen: off, for every flag. See the type remarks.
        /// </summary>
        public const bool Default = false;

        /// <summary>The ini key for <see cref="NeighbourhoodWorldFlag.World"/>.</summary>
        public const string WorldKey = "NeighbourhoodWorld";

        /// <summary>The ini key for <see cref="NeighbourhoodWorldFlag.Surroundings"/>.</summary>
        public const string SurroundingsKey = "NeighbourhoodWorldSurroundings";

        /// <summary>The ini key for <see cref="NeighbourhoodWorldFlag.Travel"/>.</summary>
        public const string TravelKey = "NeighbourhoodWorldTravel";

        /// <summary>The ini key for <see cref="NeighbourhoodWorldFlag.Walk"/>.</summary>
        public const string WalkKey = "NeighbourhoodWorldWalk";

        /// <summary>The ini key for <see cref="NeighbourhoodWorldFlag.Services"/>.</summary>
        public const string ServicesKey = "NeighbourhoodWorldServices";

        /// <summary>The ini key for <see cref="NeighbourhoodWorldFlag.Offscreen"/>.</summary>
        public const string OffscreenKey = "NeighbourhoodWorldOffscreen";

        /// <summary>The ini key for <see cref="NeighbourhoodWorldFlag.Persist"/>.</summary>
        public const string PersistKey = "NeighbourhoodWorldPersist";

        private static readonly object Gate = new object();

        /// <summary>
        /// The live values, indexed by <see cref="NeighbourhoodWorldFlag"/>; <c>null</c> until the
        /// first read, which is when the sidecar is opened once for all seven keys. Always
        /// <see cref="stamp"/>'s companion: a value and the file it was read out of, or neither.
        /// </summary>
        private static bool[] cached;

        /// <summary>
        /// The sidecar as it looked the moment <see cref="cached"/> was filled: which file it was,
        /// when it was last written and how long it was. A read that finds this same stamp is
        /// answered out of memory; a read that finds any other one goes back to the file first.
        /// </summary>
        private static SidecarStamp stamp;

        /// <summary>Every flag, in the order §7 lists them.</summary>
        public static IReadOnlyList<NeighbourhoodWorldFlag> All
        {
            get
            {
                return new[]
                {
                    NeighbourhoodWorldFlag.World,
                    NeighbourhoodWorldFlag.Surroundings,
                    NeighbourhoodWorldFlag.Travel,
                    NeighbourhoodWorldFlag.Walk,
                    NeighbourhoodWorldFlag.Services,
                    NeighbourhoodWorldFlag.Offscreen,
                    NeighbourhoodWorldFlag.Persist
                };
            }
        }

        /// <summary>
        /// Whether a flag is on. Read from the sidecar the first time any flag is asked for, and
        /// cached after that; a flag this build does not know is <see cref="Default"/>.
        /// </summary>
        /// <remarks>
        /// The cache is not for ever, and that is the whole of the difference between a switch and
        /// a rumour: before every read the sidecar is asked whether it is the same file the cached
        /// values came from, and if it is not, it is reopened and the seven keys are read again.
        /// So editing <c>simitone.ini</c> in an editor while the game is running takes effect on
        /// the next read rather than at the next launch, and the cost of noticing is one stat -
        /// see the class remarks and <see cref="SidecarStamp"/>.
        /// </remarks>
        /// <param name="flag">The capability to ask about.</param>
        public static bool IsOn(NeighbourhoodWorldFlag flag)
        {
            if (!IsKnown(flag)) return Default;
            lock (Gate)
            {
                var values = LiveValues();
                return values[(int)flag];
            }
        }

        /// <summary>
        /// The seven values, reopening the sidecar only if it is not the file the cache came from.
        /// The one place the read path goes through, so "when does a read touch the disk" has a
        /// single answer in this class rather than one per caller.
        /// </summary>
        private static bool[] LiveValues()
        {
            var path = ConfigFilePath(FSOEnvironment.UserDir);
            if (cached != null && stamp.Equals(StampOf(path))) return cached;
            cached = LoadAll(path);
            return cached;
        }

        /// <summary>
        /// Records a flag's value, and writes it to the sidecar when asked.
        /// </summary>
        /// <remarks>
        /// Saving is the caller's decision, exactly as it is for
        /// <see cref="SunCycleSetting.SetEnabled"/>: a caller turning several flags on in a row
        /// wants one write rather than one per flag, and a caller that is only speculating about a
        /// flag mid-frame wants none.
        /// </remarks>
        /// <param name="flag">The capability to record.</param>
        /// <param name="on">The value to record.</param>
        /// <param name="save">Whether to write the sidecar now.</param>
        public static void Set(NeighbourhoodWorldFlag flag, bool on, bool save)
        {
            if (!IsKnown(flag))
            {
                // Not a thrown ArgumentOutOfRangeException: a flag value that reached this class
                // from a cast rather than from the enum is a bug somewhere upstream, and the one
                // thing it must not do is write a key named after it into somebody's settings file.
                SimitoneLog.Warn("NeighbourhoodWorldFlags: ignoring the unknown capability '"
                    + ((int)flag).ToString() + "'. Nothing was recorded and nothing was written.");
                return;
            }

            lock (Gate)
            {
                cached = LiveValues();
                cached[(int)flag] = on;
            }

            if (save) Save(flag);
        }

        /// <summary>
        /// Drops the cached values and re-reads the sidecar now, so the next question is answered
        /// by the file whatever its last-write time says.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this exists when the cache already watches the file.</b> The watch is one stat,
        /// and a stat is a comparison of two numbers, and the two numbers come from the operating
        /// system's idea of when a file was last written. That is good enough for a player who
        /// saved <c>simitone.ini</c> a minute ago and is not good enough for a caller that has
        /// just written the file itself and wants the value it wrote to be the value it reads back
        /// - a <see cref="File.SetLastWriteTimeUtc"/>-style timestamp restore, a filesystem whose
        /// clock is coarse enough that two writes land on the same tick, or a test that wants to
        /// say "re-read this" rather than "re-read this because I moved the clock". So the seam is
        /// public, and it is the same seam <see cref="Reset"/> has always been.
        /// </para>
        /// <para>
        /// <b>It is a read, not a write, and it cannot lose anything.</b> Every value comes from
        /// the same <see cref="Resolve"/> the first load used, so a missing, unreadable or
        /// hand-mangled sidecar is <see cref="Default"/> here exactly as it was there. The one
        /// thing it does throw away is a value <see cref="Set"/> recorded in memory without
        /// saving - which is what "re-read the file" means, and is why the Options page calls
        /// this after it has written its keys down rather than before.
        /// </para>
        /// </remarks>
        /// <returns>
        /// Whether a sidecar was there to read. False for no user directory, no file, or a file
        /// that would not open - in all three of which every flag is <see cref="Default"/> on the
        /// way out, so the return value is a report and not a precondition.
        /// </returns>
        public static bool Refresh()
        {
            lock (Gate)
            {
                cached = LoadAll(ConfigFilePath(FSOEnvironment.UserDir));
                return stamp.Exists;
            }
        }

        /// <summary>
        /// Records every flag off, and optionally writes all seven keys in one read-merge-write.
        ///
        /// <para>
        /// <b>Why this exists.</b> Turning a capability <em>off</em> has to be as cheap and as
        /// complete as turning it on, and "off" is the state every flag starts and must return to
        /// between milestones. A per-key loop would be correct and would rewrite the sidecar seven
        /// times, so the keys go down together and the file is written once.
        /// </para>
        /// </summary>
        /// <param name="save">Whether to write the sidecar now.</param>
        public static void AllOff(bool save = false)
        {
            lock (Gate)
            {
                cached = DefaultAll();
                //Same re-stamp as Set, and for the same reason: "every flag is off" is a decision
                //this build has just made, and a file that has not changed since must not be able
                //to say otherwise.
                stamp = StampOf(ConfigFilePath(FSOEnvironment.UserDir));
            }

            if (!save) return;
            TryWriteAll(DefaultAll());
            Restamp();
        }

        /// <summary>
        /// Forgets the cached values, so the next read goes back to the sidecar. This is the
        /// "the file changed underneath us" seam - a test that writes a file and asks again.
        /// </summary>
        /// <remarks>
        /// The older of the two seams, and still the one a test wants: it makes no claim about the
        /// file at all, so it cannot be defeated by a timestamp that did not move. A caller that
        /// has just written the sidecar wants <see cref="Refresh"/> instead, which does the read
        /// now rather than deferring it.
        /// </remarks>
        public static void Reset()
        {
            lock (Gate)
            {
                cached = null;
                stamp = SidecarStamp.None;
            }
        }

        /// <summary>Whether a value is one of the seven flags this build knows.</summary>
        /// <param name="flag">The value to check.</param>
        public static bool IsKnown(NeighbourhoodWorldFlag flag)
        {
            var index = (int)flag;
            return index >= 0 && index < All.Count;
        }

        /// <summary>
        /// The ini key a flag is stored under, or <c>null</c> for a value outside the seven.
        /// </summary>
        /// <param name="flag">The capability to name.</param>
        public static string KeyFor(NeighbourhoodWorldFlag flag)
        {
            switch (flag)
            {
                case NeighbourhoodWorldFlag.World: return WorldKey;
                case NeighbourhoodWorldFlag.Surroundings: return SurroundingsKey;
                case NeighbourhoodWorldFlag.Travel: return TravelKey;
                case NeighbourhoodWorldFlag.Walk: return WalkKey;
                case NeighbourhoodWorldFlag.Services: return ServicesKey;
                case NeighbourhoodWorldFlag.Offscreen: return OffscreenKey;
                case NeighbourhoodWorldFlag.Persist: return PersistKey;
                // Null rather than a name built from the value: an unknown flag has no key, and
                // inventing one would put a line in a player's settings file that no build wrote.
                default: return null;
            }
        }

        /// <summary>
        /// A flag as the ini spells it, the way <c>IniConfig</c> wrote it. Stated here rather than
        /// left to <c>ToString</c> so the spelling of a stored flag is one line and not something
        /// a reader has to guess at.
        /// </summary>
        /// <param name="on">The value to format.</param>
        public static string TokenFor(bool on)
        {
            return SimitoneIniFile.FormatBool(on);
        }

        /// <summary>
        /// A stored value turned into a decision. A hand-edited, empty or unrecognised value is
        /// <see cref="Default"/> - off - for the reason every other ini key in the game gives one:
        /// this is user input, and a capability nobody can predict is worse than a capability that
        /// is off until a build turns it on.
        /// </summary>
        /// <param name="stored">The raw ini value, or <c>null</c>.</param>
        public static bool Resolve(string stored)
        {
            if (stored == null) return Default;
            var trimmed = stored.Trim();
            if (trimmed.Length == 0) return Default;
            if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)) return false;

            // No bare 1/0 and no "on"/"yes": the other two settings in this file are forgiving
            // about those because a closed set of values has an obvious number for each. A flag
            // is a switch somebody flips by hand, and "1" is a value this build never wrote - so
            // accepting it would make a typo look like it worked.
            return Default;
        }

        /// <summary>What a flag means when it is off, for the log and for the milestone notes.</summary>
        /// <param name="flag">The capability to name.</param>
        public static string Describe(NeighbourhoodWorldFlag flag)
        {
            switch (flag)
            {
                case NeighbourhoodWorldFlag.World:
                    return "B1 world state - off means Part A behaviour";
                case NeighbourhoodWorldFlag.Surroundings:
                    return "B2 hollow neighbours - off means Part A facades only";
                case NeighbourhoodWorldFlag.Travel:
                    return "B3 discrete travel - off means today's SwitchLot path";
                case NeighbourhoodWorldFlag.Walk:
                    return "B4 walkable streets - off means B3 discrete travel";
                case NeighbourhoodWorldFlag.Services:
                    return "B5 world services - off means VMTS1GlobalLinkStub";
                case NeighbourhoodWorldFlag.Offscreen:
                    return "B6 off-screen simulation - off means world time frozen off-lot";
                case NeighbourhoodWorldFlag.Persist:
                    return "B7 world persistence - off means Part A's layout sidecar";
                default:
                    return "unknown capability " + ((int)flag).ToString() + " - off";
            }
        }

        /// <summary>One line naming every flag that is on, for the log at startup.</summary>
        public static string DescribeSet()
        {
            var on = 0;
            var names = new List<string>();
            foreach (var flag in All)
            {
                if (!IsOn(flag)) continue;
                on++;
                names.Add(KeyFor(flag));
            }
            if (on == 0) return "no neighbourhood world capability is on (" + All.Count + " off)";
            return on + " of " + All.Count + " neighbourhood world capabilit"
                + (on == 1 ? "y" : "ies") + " on: " + string.Join(", ", names.ToArray());
        }

        /// <summary>Full path of the sidecar for a user directory, or <c>null</c> for none.</summary>
        /// <param name="userDir">The game's user directory.</param>
        public static string ConfigFilePath(string userDir)
        {
            return SunCycleSetting.ConfigFilePath(userDir);
        }

        /// <summary>
        /// What a flag's key says in the ini at <paramref name="path"/>, without touching the
        /// cache. A missing, unreadable or unrecognised value is <see cref="Default"/>.
        ///
        /// <remarks>
        /// The explicit-path half of the API, and the reason the rules above are testable at all:
        /// a test can put a file with garbage in it in a temp folder and ask what that means
        /// without redirecting <c>FSOEnvironment.UserDir</c>, which is a process-global.
        /// </remarks>
        /// </summary>
        /// <param name="path">Full path of a sidecar ini, or <c>null</c>.</param>
        /// <param name="flag">The capability to read.</param>
        public static bool ReadFrom(string path, NeighbourhoodWorldFlag flag)
        {
            var key = KeyFor(flag);
            if (key == null || string.IsNullOrEmpty(path)) return Default;
            try
            {
                return Resolve(SunCycleSetting.Ini(path).Get(key));
            }
            catch (Exception)
            {
                // A settings file that cannot be read is the same as one nobody has written yet.
                return Default;
            }
        }

        /// <summary>
        /// Writes one flag's key into the ini at <paramref name="path"/>, changing nothing else in
        /// the file. False when there was no path or the write failed - which is logged, not
        /// thrown, because the flag is already live in memory and a sidecar that cannot be written
        /// is not worth refusing to run over.
        /// </summary>
        /// <remarks>
        /// The read-merge-write is what makes this safe for a file the other two settings share:
        /// see <see cref="SimitoneIniFile"/>. The cached values are not touched here, and are not
        /// consulted against <paramref name="path"/> either - this is the explicit-path half of the
        /// API. A write to the live sidecar is noticed by the next read on its own, because the
        /// file's <see cref="SidecarStamp"/> has moved, and <see cref="Refresh"/> is the way to ask
        /// for the value back now rather than at the next question.
        /// </remarks>
        /// <param name="path">Full path of a sidecar ini, or <c>null</c>.</param>
        /// <param name="flag">The capability to write.</param>
        /// <param name="on">The value to store.</param>
        /// <returns>Whether the value reached the file.</returns>
        public static bool TryWriteTo(string path, NeighbourhoodWorldFlag flag, bool on)
        {
            var key = KeyFor(flag);
            if (key == null) return false;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                SunCycleSetting.Ini(path).Set(key, TokenFor(on));
                return true;
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodWorldFlags: could not save '" + key + "' to '"
                    + path + "'. It will reset next launch.", e);
                return false;
            }
        }

        private static void Save(NeighbourhoodWorldFlag flag)
        {
            TryWriteTo(ConfigFilePath(FSOEnvironment.UserDir), flag, IsOn(flag));
            //The write above moved the file's last-write time, so the cache is now describing a
            //file that no longer exists. Re-stamping is what stops the next read from treating
            //this class's own save as somebody else's edit and reloading - which matters most
            //exactly where it would hurt: NeighbourhoodWorldRow.Apply writes all seven keys in a
            //loop, and without this the second key's write would reload the file and throw away
            //the in-memory values the loop had not reached yet.
            Restamp();
        }

        /// <summary>
        /// Records the sidecar as it is now, so the cached values are not treated as stale
        /// against a file this class has just written.
        /// </summary>
        private static void Restamp()
        {
            lock (Gate)
            {
                stamp = StampOf(ConfigFilePath(FSOEnvironment.UserDir));
            }
        }

        /// <summary>
        /// Writes every flag's key in one read-merge-write, so <see cref="AllOff"/> touches the
        /// file once rather than seven times.
        /// </summary>
        private static void TryWriteAll(bool[] values)
        {
            var path = ConfigFilePath(FSOEnvironment.UserDir);
            if (string.IsNullOrEmpty(path)) return;
            var flags = All;
            var pairs = new List<KeyValuePair<string, string>>(flags.Count);
            for (var i = 0; i < flags.Count; i++)
            {
                pairs.Add(new KeyValuePair<string, string>(KeyFor(flags[i]), TokenFor(values[i])));
            }
            try
            {
                SunCycleSetting.Ini(path).SetAll(pairs);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("NeighbourhoodWorldFlags: could not save the capability set to '"
                    + path + "'. They will reset next launch.", e);
            }
        }

        /// <summary>Every flag read from the sidecar at <paramref name="path"/> once.</summary>
        /// <remarks>
        /// The stamp is taken <em>after</em> the read, not before, and the order is the whole
        /// trick: a read that stamped first would record a file that has since been rewritten and
        /// then believe it was current, so an edit landing between the stamp and the read would be
        /// missed until the file moved again. Stamping afterwards means the worst case is one
        /// redundant read of an unchanged file, which is the cheap direction to be wrong in.
        /// </remarks>
        private static bool[] LoadAll(string path)
        {
            var result = DefaultAll();
            var flags = All;
            for (var i = 0; i < flags.Count; i++)
            {
                result[i] = ReadFrom(path, flags[i]);
            }
            stamp = StampOf(path);
            return result;
        }

        /// <summary>
        /// The sidecar at <paramref name="path"/> as a stamp to compare against the last one.
        /// </summary>
        /// <remarks>
        /// <b>A file that is absent, is a directory, or cannot be stat'ed is all three fields
        /// zero</b>, which is the same answer <see cref="ReadFrom"/> gives it and therefore the
        /// same answer for the flags: <see cref="Default"/>. It also means an unreadable file is
        /// not retried on every read, because a stamp of all zeros is a stamp the next read will
        /// produce as well.
        /// </remarks>
        private static SidecarStamp StampOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return SidecarStamp.None;
            try
            {
                //FileInfo rather than two static calls because it asks the filesystem once: a
                //stat is cheap and a stat per flag per frame is not free, but a second stat to
                //learn the same two numbers is pure waste.
                var info = new FileInfo(path);
                if (!info.Exists) return SidecarStamp.None;
                return new SidecarStamp(path, info.LastWriteTimeUtc.Ticks, info.Length);
            }
            catch (Exception)
            {
                //A settings file that cannot be stat'ed is one nobody has written yet.
                return SidecarStamp.None;
            }
        }

        /// <summary>Seven <see cref="Default"/>s, so no array is ever shared between two callers.</summary>
        private static bool[] DefaultAll()
        {
            var result = new bool[All.Count];
            for (var i = 0; i < result.Length; i++) result[i] = Default;
            return result;
        }
    }

    /// <summary>
    /// The identity of one sidecar at one moment: which file, when it was last written, how long
    /// it was. Three fields rather than one hash, because they come from a single stat the
    /// filesystem has already answered for something else and a hash of them would be a second
    /// pass to get the same answer more briefly.
    ///
    /// <para>
    /// <b>The length is there because a timestamp alone is not an edit.</b> A file's last-write
    /// time is recorded in whatever granularity the filesystem's clock ticks at, and two saves
    /// inside one tick leave it identical - so a player who toggles a flag twice quickly, or a
    /// test that writes twice in a row, can produce a file whose contents have changed and whose
    /// stamp has not. Pairing the time with the length catches every change that alters the file's
    /// size, which is nearly all of them: a key being added, a value flipping from
    /// <c>False</c> to <c>True</c>, a comment being edited. What is left over is a same-length
    /// rewrite inside one clock tick, and the way out of that is <see cref="NeighbourhoodWorldFlags.Refresh"/>,
    /// which asks the file outright rather than asking whether it moved.
    /// </para>
    ///
    /// <para>
    /// <b>An absent file is a real value, not a missing one.</b> <see cref="None"/> is the stamp of
    /// a sidecar that is not there, and it is deliberately equal to itself: every read of a game
    /// with no <c>simitone.ini</c> produces the same stamp, so the cache holds and nothing is
    /// retried seven keys deep on a file that does not exist. The flags read from it are
    /// <see cref="NeighbourhoodWorldFlags.Default"/>, which is the contract the whole class rests
    /// on: no sidecar, no capability, and no error.
    /// </para>
    /// </summary>
    internal readonly struct SidecarStamp : IEquatable<SidecarStamp>
    {
        /// <summary>
        /// The stamp of a sidecar that is not there: no path, no write time, no length. Also what
        /// a file that cannot be stat'ed produces, which is the same answer by the same argument.
        /// </summary>
        public static readonly SidecarStamp None = default;

        private readonly string _path;
        private readonly long _writtenUtcTicks;
        private readonly long _length;

        /// <summary>Stamps a file that was read.</summary>
        /// <param name="path">The sidecar's full path.</param>
        /// <param name="writtenUtcTicks">Its last write time, in ticks.</param>
        /// <param name="length">Its length in bytes.</param>
        public SidecarStamp(string path, long writtenUtcTicks, long length)
        {
            _path = path;
            _writtenUtcTicks = writtenUtcTicks;
            _length = length;
        }

        /// <summary>Whether this stamp names a file that was there to be stat'ed.</summary>
        public bool Exists
        {
            get { return _path != null; }
        }

        /// <summary>Two stamps are the same file untouched, and nothing else counts.</summary>
        /// <param name="other">The stamp to compare against.</param>
        public bool Equals(SidecarStamp other)
        {
            return _writtenUtcTicks == other._writtenUtcTicks
                && _length == other._length
                && string.Equals(_path, other._path, StringComparison.Ordinal);
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is SidecarStamp other && Equals(other);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = _writtenUtcTicks.GetHashCode();
                hash = (hash * 397) ^ _length.GetHashCode();
                hash = (hash * 397) ^ (_path == null ? 0 : _path.GetHashCode());
                return hash;
            }
        }

        /// <summary>One line for a failing assertion's message.</summary>
        public override string ToString()
        {
            return Exists
                ? "simitone.ini at " + _writtenUtcTicks + " ticks, " + _length + " byte(s)"
                : "no sidecar";
        }
    }
}
