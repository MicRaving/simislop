/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FSO.Common;
using FSO.LotView;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The Part B capability switches: seven booleans, one ini key each, every one of them off
    /// until something turns it on.
    ///
    /// <para>
    /// <b>What is worth pinning here.</b> Four things, and the first three are all about a
    /// failure that produces no error at all. <b>The default is off</b>, because a Part B
    /// capability that is on because nobody looked at the value is how a world that is not
    /// finished reaches a player. <b>Garbage is off</b> too - a missing key, an empty one, or a
    /// hand-edited <c>banana</c> - because a switch nobody can predict is worse than a switch that
    /// is off until a build says otherwise. <b>Writing one flag writes one key</b>: these are seven
    /// keys in a sidecar that <see cref="SunCycleSetting"/> and
    /// <see cref="NeighbourhoodViewSetting"/> already share, and the file has exactly one writer
    /// (<see cref="SimitoneIniFile"/>) for the reason that it used to have two truncating ones -
    /// when a write dropped the other keys, every reader treated the loss as "never configured" and
    /// the player's choices silently reverted. <b>And a flag that is off round trips</b>: setting
    /// one and reading it back has to be a fact, not an inference from the default.
    /// </para>
    ///
    /// <para>
    /// <b>Why this class is in the non-parallel collection.</b> Every test here points
    /// <c>FSOEnvironment.UserDir</c> at a throwaway directory, which is a process-global, so
    /// anything else reading the sidecar while this class runs would read the temp folder.
    /// <c>GlobalMutationGuardTests</c> checks that membership by reading this file's source.
    /// </para>
    ///
    /// <para>
    /// <b>The live read, and the two things it must not cost.</b> The cache is keyed on the
    /// sidecar's last-write time and length, so a hand edit is picked up without a restart; and
    /// both ways that could go wrong are pinned here - a read of a file that has not changed is
    /// answered out of memory rather than off the disk, and a value this build recorded in
    /// memory is not undone by a save the same build made a moment later.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class NeighbourhoodWorldFlagsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _originalUserDir;
        private readonly bool _originalSunCycle;
        private readonly bool _originalSunCycleConfig;
        private readonly NeighbourhoodView _originalView;

        public NeighbourhoodWorldFlagsTests()
        {
            //Captured before the redirect, and put back in Dispose: all four are process-wide, and
            //three of them are somebody else's cached setting.
            _originalUserDir = FSOEnvironment.UserDir;
            _originalSunCycle = SunCycleSetting.Enabled;
            _originalSunCycleConfig = WorldConfig.Current.SunCycle;
            _originalView = NeighbourhoodViewSetting.Current;

            _root = Path.Combine(Path.GetTempPath(), "simitone-neigh-flags-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            FSOEnvironment.UserDir = _root;
            NeighbourhoodWorldFlags.Reset();
        }

        public void Dispose()
        {
            FSOEnvironment.UserDir = _originalUserDir;
            SunCycleSetting.SetEnabled(_originalSunCycle);
            WorldConfig.Current.SunCycle = _originalSunCycleConfig;
            NeighbourhoodViewSetting.Set(_originalView);
            NeighbourhoodWorldFlags.Reset();

            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                //best effort - a leftover temp dir must never fail the suite
            }
            catch (UnauthorizedAccessException)
            {
                //same: a locked handle on Windows must not turn into a test failure
            }
        }

        /// <summary>Full path of the sidecar the flags are read from and written to.</summary>
        private string Path_ => NeighbourhoodWorldFlags.ConfigFilePath(_root);

        /// <summary>
        /// Puts a file in the sidecar by hand and forgets the cache, so the next read has to come
        /// out of it. This is how the garbage and the "a key a future build wrote" cases are set
        /// up: neither of them is something the writer can produce.
        /// </summary>
        private void Seed(params string[] lines)
        {
            File.WriteAllLines(Path_, lines.Length == 0
                ? new[] { "# Simitone Settings File. Simitone's own keys; FreeSO's are in config.ini." }
                : lines);
            NeighbourhoodWorldFlags.Reset();
        }

        /// <summary>One stored value, read the way the game reads it.</summary>
        private string Raw(string key) => SunCycleSetting.Ini(Path_).Get(key);

        /// <summary>
        /// Writes the sidecar the way an editor does - straight over the top, with no
        /// <see cref="NeighbourhoodWorldFlags.Reset"/> afterwards - and moves its last-write time
        /// forward.
        /// </summary>
        /// <remarks>
        /// The timestamp is set explicitly because the read path keys on it, and a filesystem's
        /// clock is coarse enough that two writes in a row can land on the same tick. Without the
        /// explicit stamp these tests would be asserting the granularity of the disk the suite
        /// happens to be running on rather than the behaviour of the code; with it, "the file
        /// changed and the read noticed" is the only thing left to fail.
        /// </remarks>
        private void HandEdit(params string[] lines)
        {
            File.WriteAllLines(Path_, lines);
            File.SetLastWriteTimeUtc(Path_, DateTime.UtcNow.AddSeconds(2));
        }

        // ---- the table ---------------------------------------------------------------------------

        [Fact]
        public void EveryFlagHasTheKeyTheHandoffNames()
        {
            // The §7 table is a contract with the milestone notes, the build log and whoever reads
            // simitone.ini in an editor. A key that is spelled differently in the file than in the
            // doc is a flag that cannot be turned on by hand.
            Assert.Equal("NeighbourhoodWorld", NeighbourhoodWorldFlags.KeyFor(NeighbourhoodWorldFlag.World));
            Assert.Equal("NeighbourhoodWorldSurroundings", NeighbourhoodWorldFlags.KeyFor(NeighbourhoodWorldFlag.NeighbourSubWorlds));
            Assert.Equal("NeighbourhoodWorldTravel", NeighbourhoodWorldFlags.KeyFor(NeighbourhoodWorldFlag.Travel));
            Assert.Equal("NeighbourhoodWorldServices", NeighbourhoodWorldFlags.KeyFor(NeighbourhoodWorldFlag.Services));
            Assert.Equal("NeighbourhoodWorldOffscreen", NeighbourhoodWorldFlags.KeyFor(NeighbourhoodWorldFlag.Offscreen));
            Assert.Equal("NeighbourhoodWorldPersist", NeighbourhoodWorldFlags.KeyFor(NeighbourhoodWorldFlag.Persist));

            Assert.Equal(6, NeighbourhoodWorldFlags.All.Count);
        }

        [Fact]
        public void NoFlagKeyIsAnybodyElsesKey()
        {
            // The three settings share one file, so a collision would mean one flag stored over
            // another's value, silently, with no error on either side.
            var keys = NeighbourhoodWorldFlags.All.Select(NeighbourhoodWorldFlags.KeyFor).ToList();

            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.DoesNotContain(NeighbourhoodViewSetting.Key, keys);
            Assert.DoesNotContain(SunCycleSetting.Key, keys);
            Assert.DoesNotContain(null, keys);

            // and it is literally the same file, so a future rename of one of the two existing
            // settings cannot quietly split the sidecar in three
            Assert.Equal(SunCycleSetting.FileName, NeighbourhoodWorldFlags.FileName);
            Assert.Equal(NeighbourhoodViewSetting.FileName, NeighbourhoodWorldFlags.FileName);
        }

        [Fact]
        public void EveryFlagSaysWhatOffMeans()
        {
            var described = NeighbourhoodWorldFlags.All
                .Select(NeighbourhoodWorldFlags.Describe)
                .ToList();

            foreach (var text in described)
            {
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
            Assert.Equal(described.Count, described.Distinct().Count());
        }

        // ---- the default is off -------------------------------------------------------------------

        [Fact]
        public void TheDefaultIsOff()
        {
            Assert.False(NeighbourhoodWorldFlags.Default);
            Assert.False(NeighbourhoodWorldFlags.Resolve(null));
        }

        [Fact]
        public void AnInstallWithNoSidecarHasEveryFlagOff()
        {
            // Nothing has been asked for, so nothing is on. This is the state a Part A build's
            // player is in when B1 first lands, and it has to be "nothing changed".
            Assert.False(File.Exists(Path_));

            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
            }
        }

        [Fact]
        public void ASidecarWithNoFlagKeysHasEveryFlagOff()
        {
            // The realistic case rather than the empty one: the file exists because the player has
            // picked a view, and nothing in it mentions Part B yet.
            Seed("# Simitone Settings File",
                NeighbourhoodViewSetting.Key + "=" + NeighbourhoodViewSetting.Bitmap2DToken,
                SunCycleSetting.Key + "=True");

            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
            }
        }

        // ---- the round trip, one flag at a time ----------------------------------------------------

        [Theory]
        [InlineData(NeighbourhoodWorldFlag.World)]
        [InlineData(NeighbourhoodWorldFlag.NeighbourSubWorlds)]
        [InlineData(NeighbourhoodWorldFlag.Travel)]
        [InlineData(NeighbourhoodWorldFlag.Services)]
        [InlineData(NeighbourhoodWorldFlag.Offscreen)]
        [InlineData(NeighbourhoodWorldFlag.Persist)]
        public void ASavedFlagReachesTheFileAndComesBack(NeighbourhoodWorldFlag flag)
        {
            // on, and off again. The "off" half matters as much as the "on" half: a flag that can
            // be turned on but not off is a capability that can only be abandoned by editing the
            // file, and §7's whole promise is that a milestone is abandonable.
            var key = NeighbourhoodWorldFlags.KeyFor(flag);

            NeighbourhoodWorldFlags.Set(flag, true, save: true);
            Assert.Equal("True", Raw(key));
            Assert.True(NeighbourhoodWorldFlags.IsOn(flag));

            // and a fresh read of the file, which is what the next launch does
            NeighbourhoodWorldFlags.Reset();
            Assert.True(NeighbourhoodWorldFlags.IsOn(flag));

            NeighbourhoodWorldFlags.Set(flag, false, save: true);
            Assert.Equal("False", Raw(key));

            NeighbourhoodWorldFlags.Reset();
            Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
        }

        [Theory]
        [InlineData(NeighbourhoodWorldFlag.World)]
        [InlineData(NeighbourhoodWorldFlag.NeighbourSubWorlds)]
        [InlineData(NeighbourhoodWorldFlag.Travel)]
        [InlineData(NeighbourhoodWorldFlag.Services)]
        [InlineData(NeighbourhoodWorldFlag.Offscreen)]
        [InlineData(NeighbourhoodWorldFlag.Persist)]
        public void SettingAFlagWithoutSavingOnlyChangesThisRun(NeighbourhoodWorldFlag flag)
        {
            // A caller that is speculating about a flag mid-frame must not leave a mark the player
            // did not ask for. This is why saving is the caller's decision.
            NeighbourhoodWorldFlags.Set(flag, true, save: false);

            Assert.True(NeighbourhoodWorldFlags.IsOn(flag));
            Assert.Null(Raw(NeighbourhoodWorldFlags.KeyFor(flag)));

            NeighbourhoodWorldFlags.Reset();
            Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
        }

        [Fact]
        public void AllSevenKeysAreSevenSeparateLines()
        {
            // One key each, not one key holding a list: that is what lets a write touch one flag
            // and leave the other six alone, which is the next test.
            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                NeighbourhoodWorldFlags.Set(flag, true, save: true);
            }

            var lines = File.ReadAllLines(Path_);
            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                var key = NeighbourhoodWorldFlags.KeyFor(flag);
                var line = Assert.Single(lines,
                    l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(key + "=True", line);
                Assert.Equal("True", Raw(key));
            }
        }

        // ---- garbage is off -----------------------------------------------------------------------

        [Theory]
        [InlineData("true", true)]
        [InlineData("True", true)]
        [InlineData("TRUE", true)]
        [InlineData("  true  ", true)]
        [InlineData("false", false)]
        [InlineData("False", false)]
        [InlineData("FALSE", false)]
        public void ResolveReadsTheObviousValues(string stored, bool expected)
        {
            // The same spellings IniConfig wrote, and the only ones it wrote.
            Assert.Equal(expected, NeighbourhoodWorldFlags.Resolve(stored));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("1")]
        [InlineData("0")]
        [InlineData("on")]
        [InlineData("yes")]
        [InlineData("banana")]
        [InlineData("True False")]
        public void ResolveTreatsAnythingElseAsOff(string stored)
        {
            // No bare 1/0 and no "on": a flag is a switch somebody flips by hand, and accepting a
            // spelling this build never wrote is how a typo comes to look like it worked.
            Assert.False(NeighbourhoodWorldFlags.Resolve(stored));
        }

        [Theory]
        [InlineData("")]
        [InlineData("1")]
        [InlineData("on")]
        [InlineData("banana")]
        [InlineData("False")]
        public void AStoredValueThatIsNotTheTruthIsOff(string stored)
        {
            var key = NeighbourhoodWorldFlags.KeyFor(NeighbourhoodWorldFlag.Travel);
            Seed("# Simitone Settings File", key + "=" + stored);

            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel));

            // and one neighbour's garbage does not leak into another's answer
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));
        }

        [Fact]
        public void AKeyThisBuildDoesNotKnowIsNotAFlag()
        {
            // B8 has no flag and must not acquire one by being written: the key is simply not read,
            // and it is left alone on disk.
            Seed("# Simitone Settings File", "NeighbourhoodWorldFutureStage=True");
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));

            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Persist, true, save: true);

            Assert.Contains("NeighbourhoodWorldFutureStage=True", File.ReadAllLines(Path_));
            Assert.Equal("True", Raw("NeighbourhoodWorldFutureStage"));
        }

        [Fact]
        public void AStoredWalkKeyFromAnOlderBuildIsIgnoredAndLeftAlone()
        {
            // B4 (Walk) was cut. A sidecar that still says NeighbourhoodWorldWalk=True must not turn
            // anything on, must not trip the reader, and must survive a save of some other flag.
            Seed("# Simitone Settings File", "NeighbourhoodWorldWalk=True");
            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
            }

            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Persist, true, save: true);

            Assert.Equal("True", Raw("NeighbourhoodWorldWalk"));
            Assert.Equal("True", Raw(NeighbourhoodWorldFlags.PersistKey));
        }

        // ---- writing one flag writes one key -------------------------------------------------------

        [Fact]
        public void WritingOneFlagLeavesTheOtherFiveAlone()
        {
            var on = new[] { NeighbourhoodWorldFlag.World, NeighbourhoodWorldFlag.Travel,
                NeighbourhoodWorldFlag.Offscreen };
            foreach (var flag in on) NeighbourhoodWorldFlags.Set(flag, true, save: true);

            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Services, true, save: true);

            foreach (var flag in on)
            {
                Assert.Equal("True", Raw(NeighbourhoodWorldFlags.KeyFor(flag)));
            }
            Assert.Equal("True", Raw(NeighbourhoodWorldFlags.KeyFor(NeighbourhoodWorldFlag.Services)));

            // the two that were never turned on are still off, in memory and on disk
            foreach (var flag in new[] { NeighbourhoodWorldFlag.NeighbourSubWorlds,
                NeighbourhoodWorldFlag.Persist })
            {
                Assert.Null(Raw(NeighbourhoodWorldFlags.KeyFor(flag)));
            }

            NeighbourhoodWorldFlags.Reset();
            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                var wanted = flag == NeighbourhoodWorldFlag.NeighbourSubWorlds
                    || flag == NeighbourhoodWorldFlag.Persist;
                Assert.Equal(!wanted, NeighbourhoodWorldFlags.IsOn(flag));
            }
        }

        [Fact]
        public void WritingAFlagDoesNotLoseTheViewOrTheSunCycleChoice()
        {
            // The regression that is still the reason this file has one writer. Both settings write
            // through the same read-merge-write, so each of them has to leave the other two
            // settings' keys exactly as they found them.
            NeighbourhoodViewSetting.SetAndSave(NeighbourhoodView.Off);
            SunCycleSetting.SetEnabled(true, save: true);

            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.NeighbourSubWorlds, true, save: true);
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Services, true, save: true);

            Assert.Equal(NeighbourhoodViewSetting.OffToken, Raw(NeighbourhoodViewSetting.Key));
            Assert.Equal("True", Raw(SunCycleSetting.Key));
            Assert.Equal("True", Raw(NeighbourhoodWorldFlags.NeighbourSubWorldsKey));
            Assert.Equal("True", Raw(NeighbourhoodWorldFlags.ServicesKey));

            // the other two settings still answer with what the player chose
            Assert.Equal(NeighbourhoodView.Off,
                NeighbourhoodViewSetting.Resolve(Raw(NeighbourhoodViewSetting.Key)));
            Assert.True(SunCycleSetting.Resolve(Raw(SunCycleSetting.Key)));

            // and the other way round: their writes must not lose the flags either
            NeighbourhoodViewSetting.SetAndSave(NeighbourhoodView.Bitmap2D);
            SunCycleSetting.SetEnabled(false, save: true);

            Assert.Equal("2d", Raw(NeighbourhoodViewSetting.Key));
            Assert.Equal("False", Raw(SunCycleSetting.Key));
            Assert.Equal("True", Raw(NeighbourhoodWorldFlags.NeighbourSubWorldsKey));
            Assert.Equal("True", Raw(NeighbourhoodWorldFlags.ServicesKey));
        }

        [Fact]
        public void AllOffWritesEveryKeyAndKeepsTheOtherSettings()
        {
            NeighbourhoodViewSetting.SetAndSave(NeighbourhoodView.Bitmap2D);
            SunCycleSetting.SetEnabled(false, save: true);
            foreach (var flag in new[] { NeighbourhoodWorldFlag.World, NeighbourhoodWorldFlag.Persist })
            {
                NeighbourhoodWorldFlags.Set(flag, true, save: true);
            }

            NeighbourhoodWorldFlags.AllOff(save: true);

            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
                Assert.Equal("False", Raw(NeighbourhoodWorldFlags.KeyFor(flag)));
            }
            Assert.Equal("2d", Raw(NeighbourhoodViewSetting.Key));
            Assert.Equal("False", Raw(SunCycleSetting.Key));
        }

        [Fact]
        public void AllOffWithoutSavingOnlyChangesThisRun()
        {
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Persist, true, save: true);

            NeighbourhoodWorldFlags.AllOff();

            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));
            Assert.Equal("True", Raw(NeighbourhoodWorldFlags.PersistKey));

            // which is what makes it safe to call between milestones without touching a file
            NeighbourhoodWorldFlags.Reset();
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));
        }

        // ---- the cache, the unknown, and the absent file --------------------------------------------

        [Fact]
        public void ResetForcesTheNextReadToComeFromTheFile()
        {
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Travel, true, save: false);
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel));

            //Nothing has touched the sidecar, so the file has nothing to disagree with yet - which
            //is the whole of what the cache is allowed to hold on to.
            Assert.Null(Raw(NeighbourhoodWorldFlags.TravelKey));

            //Reset is the unconditional half of the seam: it drops the cache whatever the file
            //says, so a value that was never written down is gone and the file's silence is the
            //answer. Refresh is the same thing done eagerly.
            NeighbourhoodWorldFlags.Reset();
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel));
        }

        // ---- the live read: a hand edit, and a read that must stay cheap ---------------------------

        [Theory]
        [InlineData(NeighbourhoodWorldFlag.World)]
        [InlineData(NeighbourhoodWorldFlag.NeighbourSubWorlds)]
        [InlineData(NeighbourhoodWorldFlag.Travel)]
        [InlineData(NeighbourhoodWorldFlag.Services)]
        [InlineData(NeighbourhoodWorldFlag.Offscreen)]
        [InlineData(NeighbourhoodWorldFlag.Persist)]
        public void AFlagHandEditedIntoTheFileIsPickedUpWithoutARestart(NeighbourhoodWorldFlag flag)
        {
            //The whole reason the cache is keyed on the file. Before, a player who edited
            //simitone.ini with the game open had to close it and open it again to find out whether
            //they had spelled the key right, which is not a thing you can iterate on.
            var key = NeighbourhoodWorldFlags.KeyFor(flag);

            HandEdit("# Simitone Settings File");
            Assert.False(NeighbourhoodWorldFlags.IsOn(flag));

            HandEdit("# Simitone Settings File", key + "=True");
            Assert.True(NeighbourhoodWorldFlags.IsOn(flag));

            //and off again, which is the half that matters: a switch that can be turned on by hand
            //but not off by hand is not a switch a player has.
            HandEdit("# Simitone Settings File", key + "=False");
            Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
        }

        [Fact]
        public void AHandEditLeavesTheOtherSixFlagsAlone()
        {
            //The reload re-reads all seven keys from the file rather than re-reading the one that
            //moved, so the other six keep saying what the file says - which is only true if they
            //were on disk to begin with.
            HandEdit("# Simitone Settings File",
                NeighbourhoodWorldFlags.TravelKey + "=True",
                NeighbourhoodWorldFlags.PersistKey + "=False");
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel));
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));

            HandEdit("# Simitone Settings File",
                NeighbourhoodWorldFlags.TravelKey + "=False",
                NeighbourhoodWorldFlags.PersistKey + "=True");
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel));
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));
        }

        [Fact]
        public void AFileThatHasNotChangedIsNotReadAgain()
        {
            //The other half of the trade, and the one that decides whether watching the file is
            //affordable: the world's tick and the render decision both ask about flags every
            //frame, so a read that re-parsed seven keys each time would be a cost nobody would
            //accept. A value recorded in memory stands, because a file that has not moved cannot
            //be able to say otherwise.
            HandEdit("# Simitone Settings File", NeighbourhoodWorldFlags.TravelKey + "=True");
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel));

            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Travel, false, save: false);

            for (var i = 0; i < 50; i++)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel));
            }
        }

        [Fact]
        public void ASaveThisBuildMadeDoesNotUndoTheValuesThisBuildRecorded()
        {
            //The regression the stamp has to be got right about. NeighbourhoodWorldRow.Apply writes
            //all seven keys in a loop, and each write moves the sidecar's last-write time - so a
            //cache that simply reloaded whenever the file moved would drop the values the loop had
            //not reached yet, and ticking the master row would save one key and lose six.
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.World, true, save: false);
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Travel, true, save: false);
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));

            //one turn of Apply's loop: re-read the flag, record it, write it down
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.World,
                NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World), save: true);

            //the file now carries World and says nothing about Travel, so a reload would report
            //Travel off. It must still report what this build recorded.
            Assert.Equal("True", Raw(NeighbourhoodWorldFlags.WorldKey));
            Assert.Null(Raw(NeighbourhoodWorldFlags.TravelKey));
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Travel));
        }

        [Fact]
        public void AllOffIsNotUndoneByTheSaveThatFollowsIt()
        {
            //The same trap one call further on, and the reason the re-stamp is not a special case
            //bolted onto Set: "every capability is off" is a decision, and the write that records
            //it is not an outside party coming in and changing the answer.
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Persist, true, save: true);

            NeighbourhoodWorldFlags.AllOff(save: true);

            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
                Assert.Equal("False", Raw(NeighbourhoodWorldFlags.KeyFor(flag)));
            }
        }

        [Fact]
        public void RefreshReReadsAFileThatDidNotEvenMove()
        {
            //The explicit half of the seam, and the case the stamp cannot reach: this edit keeps
            //the file's length and puts its last-write time back where it was, so the only thing
            //that changed is the file's contents. Watching the file cannot see that - which is the
            //limit of watching the file, stated as a test - so a caller that has just written the
            //sidecar itself asks outright instead.
            var key = NeighbourhoodWorldFlags.PersistKey;
            //the trailing space is what makes "=True " and "=False" the same number of bytes, and
            //the value is trimmed on the way in, so both spell the same key
            HandEdit("# Simitone Settings File", key + "=True ");
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));

            var stamp = File.GetLastWriteTimeUtc(Path_);
            var length = new FileInfo(Path_).Length;
            File.WriteAllLines(Path_, new[] { "# Simitone Settings File", key + "=False" });
            File.SetLastWriteTimeUtc(Path_, stamp);

            //the fixture itself: if these two ever stop holding, the test below would be passing
            //for the wrong reason - the reload would be the stamp noticing, not Refresh forcing it
            Assert.Equal(length, new FileInfo(Path_).Length);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path_));

            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));
            Assert.True(NeighbourhoodWorldFlags.Refresh());
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));
        }

        [Fact]
        public void RefreshGivesUpAValueThatWasNeverWrittenDown()
        {
            //The other half of what Refresh does, stated rather than left as a surprise. It is the
            //read the Options page makes after it has written its keys down, so an unsaved
            //in-memory value is exactly what it is allowed to cost - and Reset has always been the
            //way to ask for the same thing without waiting for the next read.
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Persist, true, save: false);
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));
            Assert.Null(Raw(NeighbourhoodWorldFlags.PersistKey));

            NeighbourhoodWorldFlags.Refresh();
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));
        }

        [Fact]
        public void RefreshSaysWhetherThereWasASidecarToRead()
        {
            //The return value is a report rather than a precondition, because all three ways of
            //getting no - no user directory, no file, a file that will not open - leave every
            //flag off, which is the answer the class promises in every one of them.
            Assert.False(NeighbourhoodWorldFlags.Refresh());
            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
            }

            HandEdit("# Simitone Settings File", NeighbourhoodWorldFlags.PersistKey + "=True");
            Assert.True(NeighbourhoodWorldFlags.Refresh());
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Persist));

            //and gone again, which is the direction that has to work: a player who deletes the
            //file to start over gets Part A behaviour back without having to restart.
            File.Delete(Path_);
            Assert.False(NeighbourhoodWorldFlags.Refresh());
            foreach (var flag in NeighbourhoodWorldFlags.All)
            {
                Assert.False(NeighbourhoodWorldFlags.IsOn(flag));
            }
        }

        [Fact]
        public void AGameWithNoSidecarAtAllIsStillAnsweredFromMemory()
        {
            //The absent file is a stamp like any other, and a stable one - so a game with no
            //simitone.ini is asked about its flags every frame without touching the disk seven
            // times deep, and a value recorded against nothing still stands.
            Assert.False(File.Exists(Path_));
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World));

            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.World, true, save: false);
            for (var i = 0; i < 20; i++)
            {
                Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World));
            }

            //still nothing on disk, and still the value this build recorded
            Assert.False(File.Exists(Path_));
        }

        [Fact]
        public void AnUnknownFlagIsOffAndIsNeverWritten()
        {
            // A flag value that got here by a cast rather than from the enum is a bug upstream.
            // The one thing it must not do is put a line named after it in somebody's settings.
            var bogus = (NeighbourhoodWorldFlag)99;
            Assert.False(NeighbourhoodWorldFlags.IsKnown(bogus));
            Assert.Null(NeighbourhoodWorldFlags.KeyFor(bogus));
            Assert.False(NeighbourhoodWorldFlags.IsOn(bogus));

            NeighbourhoodWorldFlags.Set(bogus, true, save: true);

            Assert.False(File.Exists(Path_));
            Assert.Contains("unknown", NeighbourhoodWorldFlags.Describe(bogus),
                StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NoUserDirectoryMeansNoPath(string userDir)
        {
            // Null so the result can go straight into File.Exists, which treats null as missing.
            Assert.Null(NeighbourhoodWorldFlags.ConfigFilePath(userDir));
        }

        [Fact]
        public void AGameWithNoUserDirectoryStillRuns()
        {
            // Nothing here can block the game starting: a missing, unreadable or unwritable sidecar
            // is the default, which is off.
            FSOEnvironment.UserDir = null;
            NeighbourhoodWorldFlags.Reset();

            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.World, true, save: true);
            Assert.True(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World));

            NeighbourhoodWorldFlags.AllOff(save: true);
            Assert.False(NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World));
        }

        [Fact]
        public async Task TheCachedReadsAreThreadSafe()
        {
            // The world is ticked and drawn from the game thread but the panel asks about flags
            // while a worker is finishing, so a torn read here would be a flag that flips for one
            // frame. Many readers, one answer, whatever order they arrive in.
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.World, true, save: false);
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Persist, true, save: false);
            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Services, false, save: false);

            var reads = new Task<bool>[64];
            for (var i = 0; i < reads.Length; i++)
            {
                var index = i;
                reads[i] = Task.Run(() => NeighbourhoodWorldFlags.IsOn(
                    NeighbourhoodWorldFlags.All[index % NeighbourhoodWorldFlags.All.Count]));
            }
            var answers = await Task.WhenAll(reads);

            for (var i = 0; i < answers.Length; i++)
            {
                var flag = NeighbourhoodWorldFlags.All[i % NeighbourhoodWorldFlags.All.Count];
                var wanted = flag == NeighbourhoodWorldFlag.World || flag == NeighbourhoodWorldFlag.Persist;
                Assert.True(answers[i] == wanted,
                    flag + " read back as " + answers[i] + " on reader " + i);
            }
        }

        [Fact]
        public void TheSetIsNamedForTheLog()
        {
            Assert.Contains("no neighbourhood world capability is on",
                NeighbourhoodWorldFlags.DescribeSet(), StringComparison.OrdinalIgnoreCase);

            NeighbourhoodWorldFlags.Set(NeighbourhoodWorldFlag.Travel, true, save: false);
            var text = NeighbourhoodWorldFlags.DescribeSet();

            Assert.Contains(NeighbourhoodWorldFlags.TravelKey, text);
            Assert.Contains("1 of 6", text);
        }
    }
}
