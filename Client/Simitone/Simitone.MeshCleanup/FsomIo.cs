using FSO.Files.RC;
using FSO.Files.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace Simitone.MeshCleanup
{
    /// <summary>
    /// One geometry inside an FSOM: vertices and triangles plus the texture selector the runtime
    /// resolves later. <see cref="PixelDir"/> 0..3 means "the drawing group's own sprite for that
    /// world rotation, sprite index <see cref="PixelSPR"/>"; 65535 means the custom-texture slot
    /// (<c>&lt;iff&gt;_TEX_&lt;id&gt;.png</c> next to the pack file).
    /// </summary>
    public sealed class FsomGeometry
    {
        public ushort PixelDir;
        public ushort PixelSPR;
        public List<DGRP3DVert> Verts = new List<DGRP3DVert>();
        public List<int> Indices = new List<int>();
        public bool CustomTexture => PixelDir == 65535;
    }

    /// <summary>
    /// Device-free reader for the packed mesh formats. <c>FSOm</c> is what the generator writes
    /// (<see cref="DGRP3DMesh.WriteFSOM"/>); <c>FSOa</c> is the animated container
    /// (<see cref="DGRP3DAnim"/>), which is presented as a single chosen frame so the same
    /// export/render/check code can inspect it.
    ///
    /// <para>Reading it here rather than through <see cref="DGRP3DMesh"/> is deliberate: that
    /// constructor wants a <c>GraphicsDevice</c> to upload buffers and resolve textures, and this
    /// tool runs headless. The payload layout is pinned by the tools that write it, and the
    /// <c>check</c> command re-validates every invariant the old <c>meshcheck</c> harness did.</para>
    /// </summary>
    public sealed class FsomFile
    {
        public string Name;
        public int Version;
        public int ReconstructVersion;
        public List<List<FsomGeometry>> Groups = new List<List<FsomGeometry>>();
        public BoundingBox Bounds;
        public bool IsAnimated;
        public int Frame;
        public int FrameCount = 1;
        public string SourcePath;

        public IEnumerable<FsomGeometry> AllGeoms => Groups.SelectMany(g => g);
        public int TriangleCount => AllGeoms.Sum(g => g.Indices.Count / 3);
        public int VertexCount => AllGeoms.Sum(g => g.Verts.Count);

        public static FsomFile Read(string path, int frame = 0)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                ms.Position = 0;
                using (var io = IoBuffer.FromStream(ms, ByteOrder.LITTLE_ENDIAN))
                {
                    var magic = io.ReadCString(4);
                    if (magic == "FSOm")
                    {
                        var result = ReadFsom(io);
                        result.SourcePath = path;
                        return result;
                    }
                    if (magic == "FSOa")
                    {
                        // DGRP3DAnim.Read decompresses internally, so it wants the raw file, not
                        // the already-decompressed payload that fetched the magic above.
                        var result = new FsomFile();
                        using (var raw = File.OpenRead(path))
                        {
                            var anim = DGRP3DAnim.Read(raw);
                            result = FromAnim(anim, frame);
                        }
                        result.SourcePath = path;
                        return result;
                    }
                    throw new InvalidDataException("not an FSO mesh: magic '" + magic + "'");
                }
            }
        }

        public static FsomFile ReadFsom(IoBuffer io)
        {
            var result = new FsomFile
            {
                Version = io.ReadInt32(),
                ReconstructVersion = io.ReadInt32(),
                Name = io.ReadPascalString(),
            };

            int groupCount = io.ReadInt32();
            for (int g = 0; g < groupCount; g++)
            {
                int geomCount = io.ReadInt32();
                var group = new List<FsomGeometry>(geomCount);
                for (int j = 0; j < geomCount; j++)
                {
                    var geom = new FsomGeometry
                    {
                        PixelSPR = io.ReadUInt16(),
                        PixelDir = io.ReadUInt16(),
                    };
                    int vertCount = io.ReadInt32();
                    for (int v = 0; v < vertCount; v++)
                    {
                        var pos = new Vector3(io.ReadFloat(), io.ReadFloat(), io.ReadFloat());
                        var uv = new Vector2(io.ReadFloat(), io.ReadFloat());
                        var nrm = new Vector3(io.ReadFloat(), io.ReadFloat(), io.ReadFloat());
                        geom.Verts.Add(new DGRP3DVert(pos, nrm, uv));
                    }
                    int indexCount = io.ReadInt32();
                    for (int k = 0; k < indexCount; k++) geom.Indices.Add(io.ReadInt32());
                    group.Add(geom);
                }
                result.Groups.Add(group);
            }

            if (result.Version > 2)
            {
                int maskType = io.ReadInt32();
                if (maskType > 0)
                {
                    // A depth mask is real geometry with its own sprite selector; read it so the
                    // stream stays aligned, but keep it out of the renderable groups in this tool.
                    io.ReadUInt16(); io.ReadUInt16();
                    int vertCount = io.ReadInt32();
                    for (int v = 0; v < vertCount; v++) io.Seek(SeekOrigin.Current, 32);
                    int indexCount = io.ReadInt32();
                    io.Seek(SeekOrigin.Current, indexCount * 4);
                }
            }

            result.Bounds = new BoundingBox(
                new Vector3(io.ReadFloat(), io.ReadFloat(), io.ReadFloat()),
                new Vector3(io.ReadFloat(), io.ReadFloat(), io.ReadFloat()));
            return result;
        }

        public static FsomFile FromAnim(DGRP3DAnim anim, int frame)
        {
            if (frame < 0 || frame >= anim.Frames.Count)
                throw new ArgumentOutOfRangeException("frame " + frame + " out of range (0.." + (anim.Frames.Count - 1) + ")");

            var result = new FsomFile
            {
                Name = anim.Name,
                Version = DGRP3DMesh.CURRENT_VERSION,
                ReconstructVersion = DGRP3DMesh.CURRENT_RECONSTRUCT,
                Bounds = anim.Bounds,
                IsAnimated = true,
                Frame = frame,
                FrameCount = anim.Frames.Count,
            };

            foreach (var fg in anim.Frames[frame])
            {
                var block = anim.GroupBlocks[fg.Block];
                var group = new List<FsomGeometry>();
                for (int j = 0; j < block.Verts.Count && j < fg.Selectors.Count; j++)
                {
                    group.Add(new FsomGeometry
                    {
                        PixelDir = fg.Selectors[j][0],
                        PixelSPR = fg.Selectors[j][1],
                        Verts = block.Verts[j],
                        Indices = block.Indices[j],
                    });
                }
                result.Groups.Add(group);
            }
            return result;
        }

        /// <summary>
        /// Writes an FSOm file from flattened geometry. Bounds are recomputed from the vertices
        /// rather than trusted from an input file: the runtime culls and frustums with the stored
        /// box, so a cleaned mesh that moved any vertex must not inherit stale bounds.
        /// </summary>
        public static BoundingBox WriteFsom(string path, string name, List<List<FsomGeometry>> groups)
        {
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            var geoms = new List<List<DGRP3DGeometry>>();
            foreach (var group in groups)
            {
                var outGroup = new List<DGRP3DGeometry>();
                foreach (var geom in group)
                {
                    if (geom.Indices.Count == 0 || geom.Verts.Count == 0) continue;
                    foreach (var v in geom.Verts) { lo = Vector3.Min(lo, v.Position); hi = Vector3.Max(hi, v.Position); }
                    outGroup.Add(new DGRP3DGeometry
                    {
                        PixelDir = geom.PixelDir,
                        PixelSPR = geom.PixelSPR,
                        SVerts = geom.Verts,
                        SIndices = geom.Indices,
                    });
                }
                geoms.Add(outGroup);
            }

            var bounds = lo.X <= hi.X ? new BoundingBox(lo, hi) : new BoundingBox();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var fs = File.Create(path))
                DGRP3DMesh.WriteFSOM(fs, name, geoms, bounds);
            return bounds;
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
