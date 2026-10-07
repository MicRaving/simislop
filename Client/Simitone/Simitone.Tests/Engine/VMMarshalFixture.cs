using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Threads;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TS1Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// A real, complete <see cref="VMMarshal"/> byte stream, with the byte offset of each of the
    /// format's count fields recorded as it is written - so a test can produce a damaged save
    /// by overwriting four bytes of a valid one rather than by assembling a stream by hand and
    /// hoping it is a marshal.
    ///
    /// <para>
    /// The stream is written field by field rather than by calling
    /// <c>VMMarshal.SerializeInto</c>, because that is the only way to know where the counts
    /// are. The two have to agree, and that is checked rather than assumed: every test built on
    /// this reads the bytes back through <c>VMMarshal.Deserialize</c> first, so a layout that
    /// has drifted from the format fails there instead of making the guard look effective.
    /// </para>
    ///
    /// <para>
    /// The lot is a real one - architecture, terrain, walls, floors, objects with platform
    /// state, threads, groups, globals and a SIMI - because the cheapest way to be wrong about
    /// this format is to build something that is nearly a marshal. It has no VM, no world, no
    /// graphics device and no TS1 install behind it; every type it touches is a plain data
    /// class.
    /// </para>
    /// </summary>
    internal static class VMMarshalFixture
    {
        /// <summary>
        /// A serialised lot, and the byte offset of each count within it. The keys are the
        /// field names <c>VMMarshal</c>'s own error messages use, so a test can assert on the
        /// message without keeping a second list in step.
        /// </summary>
        public static byte[] Build(out Dictionary<string, int> offsets,
            int objectCount = 3, int groupCount = 2, int globalCount = 38)
        {
            var marshal = SyntheticLot(objectCount, groupCount, globalCount);

            offsets = new Dictionary<string, int>();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new char[] { 'F', 'S', 'O', 'v' });
                writer.Write(VMMarshal.LATEST_VERSION);
                writer.Write(false);                 // Compressed
                writer.Write(true);                  // TS1, because LATEST_VERSION > 26

                marshal.Context.SerializeInto(writer);

                offsets["entity count"] = (int)stream.Position;
                writer.Write(marshal.Entities.Length);
                foreach (var ent in marshal.Entities)
                {
                    writer.Write((byte)((ent is VMAvatarMarshal) ? 1 : 0));
                    ent.SerializeInto(writer);
                }

                offsets["thread count"] = (int)stream.Position;
                writer.Write(marshal.Threads.Length);
                foreach (var thr in marshal.Threads) thr.SerializeInto(writer);

                offsets["multitile group count"] = (int)stream.Position;
                writer.Write(marshal.MultitileGroups.Length);
                foreach (var grp in marshal.MultitileGroups) grp.SerializeInto(writer);

                offsets["global count"] = (int)stream.Position;
                writer.Write(marshal.GlobalState.Length);
                foreach (var val in marshal.GlobalState) writer.Write(val);

                marshal.PlatformState.SerializeInto(writer);
                writer.Write(marshal.ObjectId);
                writer.Write(marshal.Tuning != null);
                if (marshal.Tuning != null) marshal.Tuning.SerializeInto(writer);

                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>
        /// The same lot behind the compressed framing: the flag, the length, and a gzipped
        /// body. This is the only path in the format that is not a plain little-endian field
        /// walk, and the one where a corrupt number asks for a huge allocation.
        /// </summary>
        /// <remarks>
        /// What is gzipped is everything <i>after</i> the nine framing bytes, because that is
        /// what <c>SerializeInto</c> compresses: the flag, the version and the "is it
        /// compressed" byte are written to the outer stream, and the lot itself - the TS1 flag,
        /// the context, the four counts and the lot state - is what goes in.
        /// </remarks>
        public static byte[] BuildCompressed(out int lengthOffset)
        {
            var full = Build(out _);
            var bodyStart = FramingLength;

            byte[] payload;
            using (var buffer = new MemoryStream())
            {
                using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
                {
                    gzip.Write(full, bodyStart, full.Length - bodyStart);
                }
                payload = buffer.ToArray();
            }

            lengthOffset = FramingLength;             // "FSOv", the version, the compressed flag
            var framed = new byte[lengthOffset + 4 + payload.Length];
            Array.Copy(Encoding.ASCII.GetBytes("FSOv"), 0, framed, 0, 4);
            Array.Copy(BitConverter.GetBytes(VMMarshal.LATEST_VERSION), 0, framed, 4, 4);
            framed[8] = 1;                                       // Compressed
            Array.Copy(BitConverter.GetBytes((int)payload.Length), 0, framed, lengthOffset, 4);
            Array.Copy(payload, 0, framed, lengthOffset + 4, payload.Length);
            return framed;
        }

        /// <summary>"FSOv", the version, and the compressed flag: nine bytes, little-endian.</summary>
        public const int FramingLength = 4 + 4 + 1;

        /// <summary>The same bytes with the four bytes at <paramref name="offset"/> replaced.</summary>
        public static byte[] WithCount(byte[] good, int offset, int value)
        {
            var copy = (byte[])good.Clone();
            Array.Copy(BitConverter.GetBytes(value), 0, copy, offset, 4);
            return copy;
        }

        public static VMMarshal SyntheticLot(int objectCount, int groupCount, int globalCount)
        {
            const int size = 4;
            var tiles = size * size;

            var marshal = new VMMarshal
            {
                TS1 = true,
                Context = new VMContextMarshal
                {
                    Clock = new VMClockMarshal(),
                    Architecture = new VMArchitectureMarshal
                    {
                        Width = size,
                        Height = size,
                        Stories = 1,
                        Terrain = new VMArchitectureTerrain(size, size),
                        Walls = new[] { new WallTile[tiles] },
                        Floors = new[] { new FloorTile[tiles] },
                        FineBuildableArea = new bool[tiles],
                    },
                    Ambience = new VMAmbientSoundMarshal(),
                },
                GlobalState = new short[globalCount],
                Tuning = null,
            };

            var entities = new VMEntityMarshal[objectCount];
            var threads = new VMThreadMarshal[objectCount];
            for (int i = 0; i < objectCount; i++)
            {
                entities[i] = new VMGameObjectMarshal(marshal.Version, true)
                {
                    ObjectID = (short)(i + 1),
                    PersistID = (uint)(i + 1),
                    GUID = (uint)(0x1000 + i),
                    ObjectData = new short[10],
                    MyList = new short[0],
                    Contained = new short[0],
                    Attributes = new short[0],
                    MeToObject = new VMEntityRelationshipMarshal[0],
                    MeToPersist = new VMEntityPersistRelationshipMarshal[0],
                    Position = new LotTilePos((short)i, 0, 1),
                    PlatformState = new VMTS1ObjectState(),
                };
                threads[i] = new VMThreadMarshal(marshal.Version)
                {
                    Stack = new VMStackFrameMarshal[0],
                    Queue = new VMQueuedActionMarshal[0],
                    ActiveQueueBlock = -1,
                    TempRegisters = new short[20],
                    TempXL = new int[2],
                };
            }
            marshal.Entities = entities;
            marshal.Threads = threads;

            var groups = new VMMultitileGroupMarshal[groupCount];
            for (int i = 0; i < groupCount; i++)
            {
                groups[i] = new VMMultitileGroupMarshal(marshal.Version)
                {
                    MultiTile = false,
                    Name = "group" + i,
                    Objects = new[] { (short)(i + 1) },
                    Offsets = new[] { new LotTilePos() },
                    Price = 100 * (i + 1),
                    SalePrice = -1,
                };
            }
            marshal.MultitileGroups = groups;

            marshal.ObjectId = (short)(objectCount + 1);
            marshal.PlatformState = new VMTS1LotState { SimulationInfo = SyntheticSimi() };
            return marshal;
        }

        /// <summary>A values chunk with the parts that get written already filled in.</summary>
        public static SIMI SyntheticSimi()
        {
            return new SIMI
            {
                ChunkID = 1,
                ChunkType = "SIMI",
                ChunkLabel = "Lot Information",
                ChunkProcessed = true,
                LotValue = 1000,
                ObjectsValue = 8500,
                ArchitectureValue = 4000,
                GlobalData = new short[38],
                BudgetDays = Enumerable.Range(0, 6)
                    .Select(_ => new SIMI.SIMIBudgetDay())
                    .ToArray(),
            };
        }

        public static VMMarshal Read(byte[] bytes)
        {
            var marshal = new VMMarshal();
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                marshal.Deserialize(reader);
            }
            return marshal;
        }
    }
}
