using System;
using System.IO;
using System.Linq;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="NeighbourhoodNames"/> - the one-read-per-refresh snapshot the neighbourhood
    /// management page uses instead of re-parsing <c>names.json</c> per row.
    ///
    /// <para>
    /// The page used to call <see cref="NeighbourhoodManager.GetName"/> once per row title, once
    /// more per row detail line and twice more in the button refresh: about twenty
    /// <c>File.Exists</c> + <c>File.ReadAllText</c> + <c>JsonDocument.Parse</c> passes over the
    /// same small file on construction, on every window resize and after every rename, create and
    /// delete - all on the UI thread, and the resize is while the player is dragging an edge.
    /// </para>
    ///
    /// <para>
    /// The snapshot is the fix, and its contract is the interesting part: it is a snapshot. The
    /// middle test is the one that proves it - deleting the file under a live snapshot and finding
    /// every name still answers means nothing downstream of <c>Read</c> touches the disk, which
    /// is the property that turns twenty reads into one. Taking a fresh snapshot per refresh is
    /// what keeps it correct.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodNamesTests : IDisposable
    {
        private readonly string _root;

        public NeighbourhoodNamesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-names-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                //best effort
            }
            catch (UnauthorizedAccessException)
            {
                //best effort
            }
        }

        private string Rename(int id, string name)
        {
            // Rename refuses a slot with no folder - the panel only offers it for a neighbourhood
            // that exists - so the folder is made first. That is all "exists" means to the
            // sidecar, and the sidecar is what this test is about.
            Directory.CreateDirectory(NeighbourhoodManager.SlotPath(_root, id));
            Assert.True(NeighbourhoodManager.Rename(_root, id, name), "the sidecar should have been written");
            return name;
        }

        [Fact]
        public void ASnapshotAnswersTheSameAsThePerSlotReadForEverySlot()
        {
            Rename(0, "Home");
            Rename(4, "The Old Place");
            var snapshot = NeighbourhoodNames.Read(_root);

            for (int id = NeighbourhoodManager.MinSlotId; id <= NeighbourhoodManager.MaxSlotId; id++)
            {
                Assert.Equal(NeighbourhoodManager.GetName(_root, id), snapshot.NameOf(id));
                Assert.Equal(NeighbourhoodManager.GetDisplayName(_root, id), snapshot.DisplayNameOf(id));
            }
        }

        [Fact]
        public void ASnapshotDoesNotTouchTheDiskAgainUntilTheNextOneIsTaken()
        {
            // The whole point. Reading the file again under a live snapshot would make every name
            // answer "no name" as soon as anything happened to the sidecar - and the only way to
            // know it is not re-reading is to remove the thing it would re-read.
            Rename(0, "Home");
            var snapshot = NeighbourhoodNames.Read(_root);
            File.Delete(NeighbourhoodManager.NamesPath(_root));

            Assert.Equal("Home", snapshot.NameOf(0));
            Assert.Equal("Home", snapshot.DisplayNameOf(0));
            //an unnamed slot still answers its default label rather than nothing
            Assert.Equal("Neighbourhood 2", snapshot.DisplayNameOf(1));
        }

        [Fact]
        public void TheNextSnapshotPicksUpAnEditMadeInBetween()
        {
            // ...and the other half: a refresh after a rename really does see the new name, so
            // "one read per refresh" is not "one read per session".
            Rename(0, "Home");
            Assert.Equal("Home", NeighbourhoodNames.Read(_root).NameOf(0));

            Rename(0, "Villa");
            Assert.Equal("Villa", NeighbourhoodNames.Read(_root).NameOf(0));

            Assert.True(NeighbourhoodManager.ClearName(_root, 0));
            Assert.Null(NeighbourhoodNames.Read(_root).NameOf(0));
        }

        [Fact]
        public void AnUnnamedSlotHasNoNameAndTheDefaultLabel()
        {
            var snapshot = NeighbourhoodNames.Read(_root);

            Assert.Equal(0, snapshot.Count);
            Assert.Null(snapshot.NameOf(0));
            Assert.Equal("Neighbourhood 1", snapshot.DisplayNameOf(0));
        }

        [Fact]
        public void ASlotTheGameDoesNotHaveIsNeitherNamedNorLabelled()
        {
            Rename(0, "Home");
            var snapshot = NeighbourhoodNames.Read(_root);

            Assert.Null(snapshot.NameOf(NeighbourhoodManager.MaxSlotId + 1));
            Assert.Null(snapshot.NameOf(-5));
            Assert.Null(snapshot.DisplayNameOf(NeighbourhoodManager.MaxSlotId + 1));
        }

        [Fact]
        public void EverySlotInTheListCanBeNamedAtOnce()
        {
            // The refresh the snapshot exists for: nine rows, each with a title and a detail line,
            // plus the button column. One read answers all of it.
            for (int id = NeighbourhoodManager.MinSlotId; id <= NeighbourhoodManager.MaxSlotId; id++)
            {
                Rename(id, "Slot name " + id);
            }

            var snapshot = NeighbourhoodNames.Read(_root);
            Assert.Equal(NeighbourhoodManager.SlotCount, snapshot.Count);
            for (int id = NeighbourhoodManager.MinSlotId; id <= NeighbourhoodManager.MaxSlotId; id++)
            {
                Assert.Equal("Slot name " + id, snapshot.NameOf(id));
                Assert.Equal("Slot name " + id, snapshot.DisplayNameOf(id));
            }
        }

        [Fact]
        public void AMissingUserDirectoryIsAnEmptySnapshotRatherThanAnException()
        {
            // A game with no user directory still has to build the page.
            var snapshot = NeighbourhoodNames.Read(null);
            Assert.Equal(0, snapshot.Count);

            snapshot = NeighbourhoodNames.Read(Path.Combine(_root, "does-not-exist"));
            Assert.Equal(0, snapshot.Count);
        }

        [Fact]
        public void AMalformedSidecarIsAnEmptySnapshotRatherThanAnException()
        {
            Directory.CreateDirectory(NeighbourhoodManager.NamesPath(_root));
            var snapshot = NeighbourhoodNames.Read(_root);
            Assert.Equal(0, snapshot.Count);
        }

        [Fact]
        public void TheWholeMapIsAvailableForACallerThatWantsIt()
        {
            Rename(2, "Middle");
            var all = NeighbourhoodNames.Read(_root).All;

            Assert.Single(all);
            Assert.Equal("Middle", all[2]);
        }
    }
}
