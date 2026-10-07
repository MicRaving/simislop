/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client.UI.Panels;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the opt-in gate on the 3D neighbourhood OBJ export
    /// (<see cref="UINeighborhoodSelectionPanel.IsObjExportRequested"/> and
    /// <see cref="UINeighborhoodSelectionPanel.ObjExportPath"/>).
    ///
    /// <para>
    /// The export was commented out of the map builder rather than deleted, because it works. What
    /// it does is stand a 3D world and a VM up for every lot in the neighbourhood in turn and write
    /// a <c>.obj</c>, a <c>.mtl</c> and one PNG per floor material into the player's documents
    /// folder - minutes on the game thread and tens of megabytes, every time the map is opened. So
    /// the switch has exactly one job: be off unless somebody deliberately turned it on, and be
    /// unambiguous when they did.
    /// </para>
    ///
    /// <para>
    /// That makes the permissive cases the dangerous ones. An empty variable, a bare "true", a
    /// typo - each of those is a way to write something nobody chose, and treating any of them as
    /// "yes" is how a debug switch starts filling a player's home directory on their own.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodObjExportTests
    {
        // ---- the switch ---------------------------------------------------------------------------

        [Theory]
        [InlineData("1")]
        [InlineData(" 1 ")]
        [InlineData("\t1\n")]
        public void OnlyAnExplicitOneTurnsTheExportOn(string value)
        {
            Assert.True(UINeighborhoodSelectionPanel.IsObjExportRequested(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("0")]
        [InlineData("true")]
        [InlineData("TRUE")]
        [InlineData("yes")]
        [InlineData("on")]
        [InlineData("2")]
        [InlineData("01")]
        [InlineData("enabled")]
        [InlineData("1 1")]
        public void AnythingElseLeavesTheExportOff(string value)
        {
            Assert.False(UINeighborhoodSelectionPanel.IsObjExportRequested(value));
        }

        /// <summary>
        /// The default, i.e. what every player actually runs with. Worth its own assertion: the
        /// whole design rests on this being the answer for an unset variable.
        /// </summary>
        [Fact]
        public void AnUnsetVariableMeansTheExportIsOff()
        {
            Assert.False(UINeighborhoodSelectionPanel.IsObjExportRequested(
                Environment.GetEnvironmentVariable(UINeighborhoodSelectionPanel.ExportEnvironmentVariable)));
        }

        [Fact]
        public void TheSwitchIsNamedDistinctlyEnoughToBeFound()
        {
            // An undocumented switch is a switch nobody will ever use, which defeats the point of
            // re-enabling the feature at all.
            var name = UINeighborhoodSelectionPanel.ExportEnvironmentVariable;
            Assert.Contains("SIMITONE", name);
            Assert.Contains("EXPORT", name);
        }

        // ---- where the output goes -------------------------------------------------------------------

        [Fact]
        public void EachNeighbourhoodGetsItsOwnFolderUnderTheUserDirectory()
        {
            // One folder per mode, so exporting Downtown does not overwrite Vacation. The old
            // commented-out call spelled the same shape; this is the assertion that it stays.
            var userDir = "C:/Users/someone/Documents/Simitone/";
            for (var mode = 1; mode <= 8; mode++)
            {
                var path = UINeighborhoodSelectionPanel.ObjExportPath(userDir, mode);
                Assert.Contains("NeighModel" + mode, path);
                Assert.Contains("Documents", path);
            }
        }

        [Fact]
        public void TheFolderIsInsideTheUserDirectoryAndNotBesideTheGame()
        {
            // An export that lands next to the executable is one the player cannot find, and on a
            // Program Files install is one they cannot delete.
            var userDir = "C:/Users/someone/Documents/Simitone/";
            var path = UINeighborhoodSelectionPanel.ObjExportPath(userDir, 1);

            // FSOEnvironment.UserDir carries forward slashes and Path.Combine produces the
            // platform's, so both sides are normalised before the comparison - otherwise this
            // would be a test of Windows' separator choice.
            var actual = Path.GetFullPath(path).Replace('\\', '/');
            var expected = Path.GetFullPath(userDir).Replace('\\', '/');

            Assert.StartsWith(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AMissingUserDirectoryStillProducesAFolderNameRatherThanThrowing()
        {
            // The map builder asks for this while a neighbourhood screen is being constructed, so a
            // null here must not be the thing that takes the screen down - the export is off in
            // every normal run anyway, and this only runs when somebody asked for it.
            var path = UINeighborhoodSelectionPanel.ObjExportPath(null, 3);
            Assert.Contains("NeighModel3", path);

            path = UINeighborhoodSelectionPanel.ObjExportPath("", 4);
            Assert.Contains("NeighModel4", path);
        }

        [Fact]
        public void EveryNeighbourhoodModeGetsADistinctPath()
        {
            // A collision would mean the second export silently overwrites the first, which is
            // exactly the sort of thing that is invisible until somebody goes looking for a file.
            var seen = new HashSet<string>();
            for (var mode = 1; mode <= 8; mode++)
            {
                Assert.True(seen.Add(UINeighborhoodSelectionPanel.ObjExportPath("C:/u/", mode)),
                    "two neighbourhood modes share the export folder");
            }
        }
    }
}
