using System.Collections.Generic;
using System.IO;
using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Round-trips the animated-object container (<see cref="DGRP3DAnim"/>). An object's animation
    /// states are consecutive drawgroups; the container packs them into one file and shares
    /// geometry blocks between frames that reconstruct identically (recolours, static poses). A
    /// format or dedupe regression would silently lose frames or duplicate every vertex, so both
    /// are pinned.
    /// </summary>
    public sealed class AnimMeshTests
    {
        private static FusionGeometry Geom(float yOffset, ushort dir, ushort spr)
        {
            var g = new FusionGeometry { PixelDir = dir, PixelSPR = spr };
            // a quad
            g.Verts.Add(new DGRP3DVert(new Vector3(0, yOffset, 0), Vector3.UnitY, new Vector2(0, 0)));
            g.Verts.Add(new DGRP3DVert(new Vector3(1, yOffset, 0), Vector3.UnitY, new Vector2(1, 0)));
            g.Verts.Add(new DGRP3DVert(new Vector3(1, yOffset, 1), Vector3.UnitY, new Vector2(1, 1)));
            g.Verts.Add(new DGRP3DVert(new Vector3(0, yOffset, 1), Vector3.UnitY, new Vector2(0, 1)));
            g.Indices.AddRange(new[] { 0, 1, 2, 0, 2, 3 });
            return g;
        }

        private static List<List<List<FusionGeometry>>> Frames()
        {
            // frame 0 and frame 2 are the same shape with different textures (a recolour);
            // frame 1 is a different pose.
            return new List<List<List<FusionGeometry>>>
            {
                new() { new() { Geom(0f, 0, 0) } },
                new() { new() { Geom(0.5f, 1, 1) } },
                new() { new() { Geom(0f, 2, 2) } },
            };
        }

        [Fact]
        public void FromFrames_SharesIdenticalGeometryBlocks()
        {
            var anim = DGRP3DAnim.FromFrames("test", Frames());
            Assert.Equal(3, anim.FrameCount);
            Assert.Equal(2, anim.BlockCount); // frames 0 and 2 share a block
            Assert.Equal(anim.Frames[0][0].Block, anim.Frames[2][0].Block);
            Assert.NotEqual(anim.Frames[0][0].Block, anim.Frames[1][0].Block);
        }

        [Fact]
        public void FromFrames_SharesIndividualGroupsWithinAFrame()
        {
            // Two frames whose first dynamic group is identical but whose second differs should
            // share the first group's block, not the whole frame's geometry.
            var frames = new List<List<List<FusionGeometry>>>
            {
                new() { new() { Geom(0f, 0, 0) }, new() { Geom(1f, 0, 1) } },
                new() { new() { Geom(0f, 0, 0) }, new() { Geom(2f, 0, 1) } },
            };
            var anim = DGRP3DAnim.FromFrames("groups", frames);
            Assert.Equal(3, anim.BlockCount); // {0}, {1}, {2} -> shared {0}
            Assert.Equal(anim.Frames[0][0].Block, anim.Frames[1][0].Block);
            Assert.NotEqual(anim.Frames[0][1].Block, anim.Frames[1][1].Block);
        }

        [Fact]
        public void AnimatedContainer_RoundTrips()
        {
            var anim = DGRP3DAnim.FromFrames("AlarmClock", Frames());

            using var ms = new MemoryStream();
            anim.Write(ms);
            ms.Position = 0;
            var back = DGRP3DAnim.Read(ms);

            Assert.Equal(DGRP3DAnim.VERSION, back.Version);
            Assert.Equal(anim.FrameCount, back.FrameCount);
            Assert.Equal(anim.BlockCount, back.BlockCount);

            for (int f = 0; f < anim.FrameCount; f++)
            {
                Assert.Equal(anim.Frames[f].Count, back.Frames[f].Count);
                for (int g = 0; g < anim.Frames[f].Count; g++)
                {
                    Assert.Equal(anim.Frames[f][g].Block, back.Frames[f][g].Block);
                    Assert.Equal(anim.Frames[f][g].Selectors.Count, back.Frames[f][g].Selectors.Count);
                    for (int j = 0; j < anim.Frames[f][g].Selectors.Count; j++)
                    {
                        Assert.Equal(anim.Frames[f][g].Selectors[j][0], back.Frames[f][g].Selectors[j][0]);
                        Assert.Equal(anim.Frames[f][g].Selectors[j][1], back.Frames[f][g].Selectors[j][1]);
                    }
                }
            }

            // geometry survives the trip, including the deduped block
            var block = back.GroupBlocks[back.Frames[1][0].Block];
            Assert.Single(block.Verts);
            Assert.Equal(4, block.Verts[0].Count);
            Assert.Equal(6, block.Indices[0].Count);
            Assert.Equal(0.5f, block.Verts[0][0].Position.Y, 4);
        }

        [Fact]
        public void IsAnimated_DetectsMagic()
        {
            using var yes = new MemoryStream(new byte[] { (byte)'F', (byte)'S', (byte)'O', (byte)'a', 1, 2, 3, 4 });
            Assert.True(DGRP3DAnim.IsAnimated(yes));
            Assert.Equal(0, yes.Position); // does not consume the header

            using var no = new MemoryStream(new byte[] { (byte)'F', (byte)'S', (byte)'O', (byte)'m', 1, 2, 3, 4 });
            Assert.False(DGRP3DAnim.IsAnimated(no));
        }
    }
}
