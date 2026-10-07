/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The join between the lot the player is standing on and the neighbourhood around it: the
    /// terrain reaching the property line, the bank that ties the two grounds together, and where
    /// the neighbouring facades are put relative to the lots they belong to.
    ///
    /// <para>
    /// A player reported the surroundings as "rendered in a very crude, incomplete way (no details,
    /// no accurate line-up, no proper floor textures etc.)". Two of those three are arithmetic this
    /// file pins, and both are the same arithmetic the bug was: the surround stopped its ground at
    /// the property line with nothing behind it, and nothing in the log said where a neighbouring
    /// house had been placed relative to its own plot.
    /// </para>
    ///
    /// <para>
    /// All of it is pure. A bank is a strip of vertices, a facade's position is a matrix, and
    /// neither of them needs a graphics device to be checked - which is the only reason either of
    /// them can be checked at all before a human looks at the screen.
    /// </para>
    /// </summary>
    public class SimitoneSurroundingsSeamTests
    {
        private const int MapSize = NeighbourhoodCityMeshBuilder.MapSize;

        private static NeighbourhoodLot Lot(
            short number, int worldX, int worldY, int size = 64)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                WorldX = worldX,
                WorldY = worldY,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = 0xABCD,
                HasHouse = true
            };
        }

        private static NeighbourhoodMap FlatMap(int size, byte elevation)
        {
            var map = NeighbourhoodMap.Create(size);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = elevation;
            return map;
        }

        /// <summary>
        /// A map whose property ring is at <paramref name="ring"/> and whose whole surface is one
        /// step higher, which is the shape a slope makes at a property line: the surround is
        /// measured to the lowest tile on the ring, so everything else stands above the lot.
        /// </summary>
        private static NeighbourhoodMap SteppedMap(NeighbourhoodLot lot, byte ring)
        {
            var map = FlatMap(MapSize, (byte)(ring + 1));
            for (int z = lot.WorldY; z < lot.WorldY + lot.Size; z++)
            {
                for (int x = lot.WorldX; x < lot.WorldX + lot.Size; x++)
                {
                    var border = x == lot.WorldX || x == lot.WorldX + lot.Size - 1
                        || z == lot.WorldY || z == lot.WorldY + lot.Size - 1;
                    if (border) map.ElevationData[map.Index(x, z)] = ring;
                }
            }
            return map;
        }

        private static NeighbourhoodCityMesh MeshWith(params Vector2[] centres)
        {
            var mesh = new NeighbourhoodCityMesh();
            mesh.Layers[NeighbourhoodTerrain.Grass] = Quads(centres);
            return mesh;
        }

        /// <summary>
        /// A mesh with one grass quad per listed tile centre, standing at an elevation byte rather
        /// than at zero - so the crop sees the height a real tile would have.
        /// </summary>
        private static NeighbourhoodCityMesh MeshAt(byte elevation, params Vector2[] centres)
        {
            var mesh = MeshWith(centres);
            var layer = mesh.Layers[NeighbourhoodTerrain.Grass];
            var height = elevation / (float)NeighbourhoodCityMeshBuilder.ElevationScale;
            for (int i = 0; i < layer.Vertices.Length; i++)
            {
                layer.Vertices[i].Position = new Vector3(
                    layer.Vertices[i].Position.X, height, layer.Vertices[i].Position.Z);
            }
            return mesh;
        }

        /// <summary>
        /// A map whose property ring stands <paramref name="ring"/> and whose whole surface is one
        /// step <em>below</em> it, which is the shape that makes the surround's ground proud of the
        /// lot's own plane and the bank worth cutting.
        /// </summary>
        private static NeighbourhoodMap BankedMap(NeighbourhoodLot lot, byte ring)
        {
            var map = FlatMap(MapSize, (byte)(ring - 1));
            for (int z = lot.WorldY; z < lot.WorldY + lot.Size; z++)
            {
                for (int x = lot.WorldX; x < lot.WorldX + lot.Size; x++)
                {
                    var border = x == lot.WorldX || x == lot.WorldX + lot.Size - 1
                        || z == lot.WorldY || z == lot.WorldY + lot.Size - 1;
                    if (border) map.ElevationData[map.Index(x, z)] = ring;
                }
            }
            return map;
        }

        private static NeighbourhoodMeshLayer Quads(params Vector2[] centres)
        {
            var vertices = new List<NeighbourhoodTerrainVertex>();
            var indices = new List<int>();
            foreach (var centre in centres)
            {
                var first = vertices.Count;
                var x = centre.X - 0.5f;
                var z = centre.Y - 0.5f;
                vertices.Add(Vertex(x, z, 0f));
                vertices.Add(Vertex(x + 1, z, 0f));
                vertices.Add(Vertex(x + 1, z + 1, 0f));
                vertices.Add(Vertex(x, z + 1, 0f));
                indices.Add(first);
                indices.Add(first + 1);
                indices.Add(first + 2);
                indices.Add(first);
                indices.Add(first + 2);
                indices.Add(first + 3);
            }
            return new NeighbourhoodMeshLayer
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray()
            };
        }

        private static NeighbourhoodTerrainVertex Vertex(float x, float z, float y)
        {
            return new NeighbourhoodTerrainVertex
            {
                Position = new Vector3(x, y, z),
                Normal = Vector3.Up,
                Transparency = 1f,
                TextureCoord = Vector2.Zero,
                MaskTextureCoord = new Vector2(-1, -1)
            };
        }

        // ---- the ground reaches the lot -----------------------------------------------------------

        [Fact]
        public void TheGeneratedGroundIsDrawnRightUpToTheLotViewsOwnEdge()
        {
            // The lot view draws its own ground at its own height and clears the depth buffer
            // before it does, so whatever the surround draws inside the property is behind it. The
            // surround used to draw nothing there, which meant the neighbourhood's ground stopped
            // at the property line: on any lot where the two heights disagree, that is a cut edge
            // with nothing behind it, all the way round.
            var lot = Lot(68, 200, 300);
            var map = SteppedMap(lot, 48);
            var cull = SimitoneSurroundingsCull.For(map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);

            //a tile in the middle of the property, and the one immediately outside it
            Assert.True(cull.KeepsTile(232.5f, 332.5f));
            Assert.True(cull.KeepsTile(199.5f, 332.5f));
            //and the ground beyond the region still is not drawn
            Assert.False(cull.KeepsTile(159.5f, 332.5f));

            //and the two meet at the property line. The surround is measured down to the lot's own
            //centre-four mean - its base level, the one every sub-world in the surround is hung from
            //- and a generated map stamps a lot's whole footprint with its own altitude, so the ring
            //and the middle of the lot are on that plane and the join is flush all the way round.
            var flat = Lot(68, 200, 300);
            var placement = SimitoneSurroundingsPlacement.For(FlatMap(MapSize, 48), flat);
            for (int i = 0; i < flat.Size; i++)
            {
                var ring = new[]
                {
                    new Vector3(200 + i, 48f, 300),
                    new Vector3(200 + i, 48f, 364),
                    new Vector3(200, 48f, 300 + i),
                    new Vector3(264, 48f, 300 + i)
                };
                foreach (var tile in ring)
                {
                    var height = placement.ToLotView(new Vector3(tile.X,
                        tile.Y / NeighbourhoodCityMeshBuilder.ElevationScale, tile.Z)).Y;
                    Assert.Equal(0f, height, 5);
                }
            }
        }

        [Fact]
        public void TheBankIsOnlyCutWhereThereIsAStepToBridge()
        {
            // A lot whose own footprint is all one altitude - which is what TerrainFill stamps, and
            // nearly every neighbourhood - has no step and pays nothing for the bank. One whose
            // border ring stands a bank above its own plane gets a strip of its own ground for every
            // side of every ring tile. The count is the arithmetic: a 64x64 lot has
            // 4 * 64 - 4 = 252 ring tiles, of which four are corners with two sides each, so
            // 252 + 4 = 256 sides, and each side is two triangles.
            var flat = Lot(68, 200, 300);
            var flatCull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), flat, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var flatCrop = SimitoneSurroundingsCrop.Crop(MeshAt(48,
                new Vector2(200.5f, 300.5f), new Vector2(232.5f, 332.5f)), flatCull);
            Assert.Equal(0, flatCrop.BankPrimitives);

            var stepped = Lot(68, 200, 300);
            var steppedMap = BankedMap(stepped, 48);
            var steppedCull = SimitoneSurroundingsCull.For(
                steppedMap, stepped, SimitoneSurroundingsPlacement.CullRadiusTiles);
            //one ring corner: it is on two sides of the property line, so it gets two banks
            var cornerCrop = SimitoneSurroundingsCrop.Crop(MeshAt(48,
                new Vector2(200.5f, 300.5f)), steppedCull);
            Assert.Equal(4, cornerCrop.BankPrimitives);

            //one ring edge tile: one side, so one bank
            var edgeCrop = SimitoneSurroundingsCrop.Crop(MeshAt(48,
                new Vector2(210.5f, 300.5f)), steppedCull);
            Assert.Equal(2, edgeCrop.BankPrimitives);

            //a tile in the middle of the property: none
            var middleCrop = SimitoneSurroundingsCrop.Crop(MeshAt(48,
                new Vector2(232.5f, 332.5f)), steppedCull);
            Assert.Equal(0, middleCrop.BankPrimitives);

            //a tile outside the property: none, and none of it is dropped either
            var outsideCrop = SimitoneSurroundingsCrop.Crop(MeshAt(48,
                new Vector2(199.5f, 332.5f)), steppedCull);
            Assert.Equal(0, outsideCrop.BankPrimitives);
            Assert.Equal(2, outsideCrop.LayerPrimitives[NeighbourhoodTerrain.Grass]);
        }

        [Fact]
        public void TheBankRunsFromTheLotsOwnGroundUpToTheGroundItIsHoldingUp()
        {
            // The strip has to close the gap and no more: its bottom is the height the surround was
            // measured down to - the lot's own base level, the one the sub-worlds are hung from - and
            // its top is the ground already there. A bank that started anywhere else would either
            // float or be buried.
            var lot = Lot(68, 200, 300);
            var map = BankedMap(lot, 48);
            var cull = SimitoneSurroundingsCull.For(map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var scale = NeighbourhoodCityMeshBuilder.ElevationScale;

            //the mesh's own quad for the north-west corner of the property, on the raised ring
            var mesh = MeshWith(new Vector2(200.5f, 300.5f));
            var quads = mesh.Layers[NeighbourhoodTerrain.Grass];
            for (int i = 0; i < quads.Vertices.Length; i++)
            {
                quads.Vertices[i].Position.Y = 48f / scale;
            }
            var crop = SimitoneSurroundingsCrop.Crop(mesh, cull);

            //one quad for the ground, two more for the two sides of the property line
            var vertices = crop.LayerVertices[NeighbourhoodTerrain.Grass];
            Assert.Equal(3 * 4, vertices.Length);
            Assert.Equal(6, crop.LayerPrimitives[NeighbourhoodTerrain.Grass]);

            //the lot's own plane is the interior of the footprint, one step below the raised ring
            var plane = 47f / scale;
            for (int quad = 1; quad < 3; quad++)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    var y = vertices[quad * 4 + corner].Position.Y;
                    Assert.True(Math.Abs(y - plane) < 1e-4f || Math.Abs(y - 48f / scale) < 1e-4f,
                        "bank vertex " + corner + " of quad " + quad + " is at " + y
                        + ", which is neither the lot's own plane (" + plane + ") nor the ground it"
                        + " holds up (" + 48f / scale + ")");
                }
            }

            //and the faces look away from the property, so the bank is lit as a bank
            for (int quad = 1; quad < 3; quad++)
            {
                var normal = vertices[quad * 4].Normal;
                Assert.True(Math.Abs(normal.Y) < 1e-6f, "a bank wall has a horizontal normal");
                Assert.Equal(1f, normal.Length(), 4);
            }
        }

        // ---- the neighbouring facades line up with their lots ---------------------------------------

        [Fact]
        public void ANeighboursFacadeIsDrawnOnItsOwnLot()
        {
            // The facade cache and the surround terrain work in the same generated-map tile space
            // and are drawn through the same matrix, so "on its own lot" is arithmetic rather than
            // agreement: this is the check that a house is not one lot width out, which is the kind
            // of mistake a screenshot shows and a log never mentions.
            var map = FlatMap(MapSize, 48);
            var here = Lot(68, 200, 300);
            var neighbour = Lot(69, 266, 300);
            var placement = SimitoneSurroundingsPlacement.For(map, here);

            //the neighbour's own origin, in lot-view units, is 66 tiles east of the player's
            var origin = SimitoneSurroundingsPlacement.FacadePointInLotView(
                placement, neighbour, Vector3.Zero);
            Assert.Equal(66 * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, origin.X, 3);
            Assert.Equal(0f, origin.Z, 3);

            //and so is its far corner, on the lot's own scale
            var far = SimitoneSurroundingsPlacement.FacadePointInLotView(
                placement, neighbour, new Vector3(neighbour.Size, 0f, neighbour.Size));
            Assert.Equal((66 + neighbour.Size) * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, far.X, 3);
            Assert.Equal(neighbour.Size * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, far.Z, 3);
        }

        [Fact]
        public void AFacadeIsTurnedWithItsLot()
        {
            // A house can face a different way from its plot, and the surround draws both: the
            // facade's quarter turn about its own centre has to survive the translation, or the
            // house is rotated about the wrong point and sits half off its lot.
            var map = FlatMap(MapSize, 48);
            var here = Lot(68, 200, 300);
            var neighbour = Lot(69, 266, 300);
            neighbour.Rotation = 1;
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var half = neighbour.Size / 2f;

            //the centre of the neighbour's plot is unmoved by its own rotation
            var centre = SimitoneSurroundingsPlacement.FacadePointInLotView(
                placement, neighbour, new Vector3(half, 0f, half));
            Assert.Equal((66 + half) * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, centre.X, 3);
            Assert.Equal(half * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, centre.Z, 3);

            //and a corner that is north-west of the centre is carried past the centre line by a
            //quarter turn: the rotation is about the lot's own centre, so the corners move
            var corner = SimitoneSurroundingsPlacement.FacadePointInLotView(
                placement, neighbour, new Vector3(0f, 0f, 0f));
            var turned = SimitoneSurroundingsPlacement.FacadePointInLotView(
                placement, neighbour, new Vector3(half, 0f, 0f));
            Assert.True(turned.Z < corner.Z,
                "a quarter turn should have carried the north-west corner past the centre line,"
                + " not left it on the near edge");
        }

        [Fact]
        public void AFacadeStandsOnTheSameGroundTheSurroundDrawsUnderIt()
        {
            // The other half of "on its own lot": the height. Both numbers come from one question
            // asked of one map and one translation, so they agree by construction - and a house
            // floating above the street or sunk into it is the crudest thing a surround can do.
            var lot = Lot(68, 200, 300);
            var map = SteppedMap(lot, 48);
            //a neighbour one lot to the east, whose own corner tile is the same height
            var neighbour = Lot(69, 266, 300);
            var placement = SimitoneSurroundingsPlacement.For(map, lot);

            var expected = NeighbourhoodFacadeTransform.GroundHeightFor(map, neighbour);
            var drawn = SimitoneSurroundingsPlacement.FacadeGroundInLotView(placement, map, neighbour);

            //the placement's translation and scale are the only things between the two numbers
            var translated = placement.ToLotView(
                new Vector3(neighbour.WorldX, expected, neighbour.WorldY)).Y;
            Assert.Equal(translated, drawn, 5);
            Assert.Equal((expected - placement.MapTileOrigin.Y)
                * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, drawn, 5);
        }

        [Fact]
        public void TheNeighbourTheFacadeQueueIsGivenIsNotThePlayersOwnLot()
        {
            // "8 neighbouring facades" - one of the eight was the house the player is standing in.
            // The lot view draws that one, in full, from the lot's own house file; a surround copy
            // of it is a whole World and a VM stood up to build the house visible through the
            // window, and where the lot's ground is translucent it is a second house standing in
            // the middle of the player's own.
            var map = FlatMap(MapSize, 48);
            var here = Lot(68, 200, 300);
            var lots = new List<NeighbourhoodLot> { here };
            for (int i = 1; i < 9; i++) lots.Add(Lot((short)(68 + i), 200 + (i % 3) * 66, 300 + (i / 3) * 66));
            var layout = new NeighbourhoodLayout(4, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                MapSize, lots.ToArray());
            var cull = SimitoneSurroundingsCull.For(map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);

            var inRange = cull.LotsInRange(layout, (short)68);
            Assert.NotEmpty(inRange);
            foreach (var lot in inRange) Assert.NotEqual((short)68, lot.Number);

            //and the overload that does not exclude anything still says the lot is in its own
            //region, which is what the old caller used to do
            Assert.Contains(here, cull.LotsInRange(layout));
        }
    }
}
