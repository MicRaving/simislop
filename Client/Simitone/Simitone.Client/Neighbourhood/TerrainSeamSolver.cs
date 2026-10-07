/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>Tunables for <see cref="TerrainSeamSolver"/>.</summary>
    public readonly struct TerrainSeamOptions
    {
        /// <summary>
        /// How many Gauss-Seidel sweeps the free region gets. The loop stops the moment a sweep
        /// changes nothing, so a flat neighbourhood pays for exactly one.
        /// </summary>
        public int MaxIterations { get; }

        /// <summary>
        /// The steepest height difference, in <c>ElevationData</c> units (1/12 of a tile), that a
        /// free tile's neighbour may contribute across one edge. This is the "bounded" in bounded
        /// relaxation, and it is what stops a distant high lot from leaking a spike through a field
        /// that has no reason to be steep.
        /// </summary>
        public int MaxSlope { get; }

        /// <summary>
        /// Two adjacent lots whose border heights differ by at least this much get an embankment
        /// ramp across the margin between them. Below it the difference is left to the relaxation,
        /// which spreads it over the whole gap and reads as a gentle swell rather than a bank.
        /// </summary>
        public int StepThreshold { get; }

        /// <summary>The generator's settings.</summary>
        public static TerrainSeamOptions Default
        {
            get { return new TerrainSeamOptions(24, 4, 2); }
        }

        /// <summary>Creates a set of options.</summary>
        /// <param name="maxIterations">Gauss-Seidel sweeps allowed.</param>
        /// <param name="maxSlope">Steepest free-to-free edge, in elevation units.</param>
        /// <param name="stepThreshold">Height difference that earns an embankment ramp.</param>
        public TerrainSeamOptions(int maxIterations, int maxSlope, int stepThreshold)
        {
            MaxIterations = maxIterations < 0 ? 0 : maxIterations;
            MaxSlope = maxSlope < 0 ? 0 : maxSlope;
            StepThreshold = stepThreshold < 0 ? 0 : stepThreshold;
        }
    }

    /// <summary>What <see cref="TerrainSeamSolver.Solve"/> did, so a test can assert on the work.</summary>
    public readonly struct SeamSolveReport
    {
        /// <summary>The base plane the solve started from: the lowest lot's height, or 0.</summary>
        public byte BaseHeight { get; }

        /// <summary>How many tiles the embankment ramps rewrote.</summary>
        public int RampTiles { get; }

        /// <summary>How many height differences earned a ramp.</summary>
        public int RampsBuilt { get; }

        /// <summary>How many sweeps ran, including the one that changed nothing.</summary>
        public int Iterations { get; }

        /// <summary>Whether the relaxation reached a fixed point inside its budget.</summary>
        public bool Converged { get; }

        /// <summary>How many tiles were held at a lot's own height and were never relaxed.</summary>
        public int FixedTiles { get; }

        internal SeamSolveReport(
            byte baseHeight, int rampTiles, int rampsBuilt, int iterations, bool converged, int fixedTiles)
        {
            BaseHeight = baseHeight;
            RampTiles = rampTiles;
            RampsBuilt = rampsBuilt;
            Iterations = iterations;
            Converged = converged;
            FixedTiles = fixedTiles;
        }

        /// <summary>
        /// One line describing what the solve did, for a diagnostic sink or a failing assertion's
        /// message. <see cref="Converged"/> is the part that matters: a relaxation that ran out of
        /// sweeps has left a slope somewhere that no test would otherwise notice.
        /// </summary>
        public string Describe()
        {
            return "seam: base " + BaseHeight + ", " + Iterations + " sweep(s), "
                + (Converged ? "converged" : "DID NOT CONVERGE")
                + ", " + RampsBuilt + " ramp(s) over " + RampTiles + " tile(s), "
                + FixedTiles + " fixed tile(s)";
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// §6.3: making the generated ground meet every lot edge without a visible step.
    ///
    /// <para><b>The five stages, in order - and the order is the contract.</b></para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Base plane.</b> Everything starts at the lowest lot's <c>BaseHeight</c>: the plan's
    ///     "base plane at min over lots of BaseHeight". Using the minimum rather than zero or a
    ///     mean means the surface never has to be pushed below what a lot says its own ground is
    ///     at.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Dirichlet borders.</b> Every lot's interior <em>and its one-tile border ring</em> is
    ///     pinned to that lot's <c>BaseHeight</c> and marked fixed. The ring is the boundary
    ///     condition; the interior is pinned too, so the relaxation cannot pull a lot's own ground
    ///     away from what the lot itself will render. Where two lots overlap, the lower-indexed
    ///     one wins the shared tile - a layout M1 should never produce, but a deterministic answer
    ///     beats a coin toss.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Flattened roads and sidewalks.</b> A road or sidewalk tile that stage 2 did not
    ///     already pin is pinned to the height of the nearest lot. Pinning rather than relaxing is
    ///     what "flattened" means here: a road that followed the relaxation would ripple between
    ///     two lots, and a kerb that waves is worse than one that steps.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Bounded relaxation of the free region.</b> Everything else is free, and is swept
    ///     Gauss-Seidel: each free tile takes the integer mean of its in-bounds neighbours, with
    ///     each free neighbour's contribution first clamped to within
    ///     <see cref="TerrainSeamOptions.MaxSlope"/> of the tile being updated, and the result
    ///     clamped into the range of the tile's <em>fixed</em> neighbours so the surface cannot
    ///     overshoot past a lot and come back down.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Embankment ramps.</b> Run last, and allowed to overwrite tiles stage 3 pinned,
    ///     because that is the point: where two lots' borders disagree by at least
    ///     <see cref="TerrainSeamOptions.StepThreshold"/>, the margin between their rings is
    ///     rewritten as an evenly graded bank from one lot's height to the other's. A road crossing
    ///     that margin is carried up the bank with it, which is what an embankment under a road
    ///     is.
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>The degradation the plan insists on.</b> "Because TS1 lots are usually near-flat,
    /// this must degrade to 'copy the lot ring height' with no visible work." It does, and it is a
    /// <em>literal</em> no-op: when every lot has the same <c>BaseHeight</c>, the base plane
    /// already equals every lot's height, so every fixed neighbour a free tile can see is that
    /// height, the first sweep computes the value each tile already has, changes nothing, and the
    /// loop exits after one iteration. The resulting <c>ElevationData</c> is that one value in
    /// every one of its slots. The solver exists for the general case, and is written so the
    /// common case does not pay for it.</para>
    ///
    /// <para><b>Border rings are preserved unconditionally.</b> A ramp band is strictly between
    /// the two rings, never on them, and stages 3 to 5 skip every tile stage 2 pinned. That
    /// ordering is the reason a road running along a lot's own sidewalk ring sits at <em>that
    /// lot's</em> height and not at a neighbour's: the ring is a lot's own ground, and a sidewalk
    /// that steps away from the house it belongs to is worse than a road that steps across the
    /// street.</para>
    ///
    /// <para><b>Idempotent.</b> Solving an already-solved map with the same input produces the
    /// same bytes, because the solve reads no state beyond the input and the lot mask and writes
    /// the whole array from the base plane up.</para>
    /// </summary>
    public static class TerrainSeamSolver
    {
        /// <summary>
        /// Solves <paramref name="map"/>'s <see cref="NeighbourhoodMap.ElevationData"/> from the
        /// lot rectangles and the road graph. The map's elevation is fully overwritten.
        /// </summary>
        /// <param name="input">The generation input.</param>
        /// <param name="graph">The rasterised road graph, for the road and sidewalk masks.</param>
        /// <param name="map">The map to solve.</param>
        /// <param name="options">Tunables; use <see cref="TerrainSeamOptions.Default"/>.</param>
        /// <returns>What the solve did.</returns>
        public static SeamSolveReport Solve(
            NeighbourhoodMapInput input,
            RoadGraph graph,
            NeighbourhoodMap map,
            TerrainSeamOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (map == null) throw new ArgumentNullException(nameof(map));

            var size = input.MapSize;
            var count = size * size;
            var lots = input.UsableLots;
            var mask = graph.LotMask;
            var height = new byte[count];
            var isFixed = new bool[count];

            // 1. Base plane: the lowest lot's own height, so nothing ever has to be pushed below a
            //    lot. A neighbourhood with no lots is flat at 0.
            byte baseHeight = 0;
            if (lots.Count > 0)
            {
                baseHeight = lots[0].BaseHeight;
                for (int i = 1; i < lots.Count; i++)
                {
                    if (lots[i].BaseHeight < baseHeight) baseHeight = lots[i].BaseHeight;
                }
            }
            for (int i = 0; i < count; i++) height[i] = baseHeight;

            // 2. Dirichlet borders, in two passes so that a lot's own ground always wins. Two lots
            //    packed hard against each other have overlapping rings, and only one of them can
            //    own the shared tile; the lower-indexed lot does. Interiors are pinned first
            //    because a sidewalk apron losing a tile to a neighbour's ground is a far smaller
            //    problem than a house sitting on the wrong height.
            int fixedTiles = 0;
            for (int i = 0; i < lots.Count; i++)
            {
                fixedTiles += PinLot(lots[i], height, isFixed, size, true);
            }
            for (int i = 0; i < lots.Count; i++)
            {
                fixedTiles += PinLot(lots[i], height, isFixed, size, false);
            }

            // 3. Flattened roads and sidewalks, at the nearest lot's height. A tile stage 2
            //    already pinned keeps its own lot's height - that is the ring-preservation rule.
            var owners = TerrainFill.NearestLotOwners(lots, size);
            for (int i = 0; i < count; i++)
            {
                if (isFixed[i]) continue;
                if (!graph.Road[i] && !graph.Sidewalk[i]) continue;
                height[i] = owners[i] >= 0 ? lots[owners[i]].BaseHeight : baseHeight;
                isFixed[i] = true;
                fixedTiles++;
            }

            // 4. Bounded Gauss-Seidel over the free region.
            int iterations = 0;
            bool converged = false;
            for (int pass = 0; pass < options.MaxIterations; pass++)
            {
                iterations++;
                if (Sweep(height, isFixed, size, options.MaxSlope) == 0)
                {
                    converged = true;
                    break;
                }
            }

            // 5. Embankment ramps, which may overwrite tiles stage 3 pinned. That is the point.
            int rampsBuilt = 0, rampTiles = 0;
            for (int i = 0; i < lots.Count; i++)
            {
                for (int j = i + 1; j < lots.Count; j++)
                {
                    int written;
                    if (BuildRamp(lots[i], lots[j], height, mask, size, options, out written))
                    {
                        rampsBuilt++;
                        rampTiles += written;
                    }
                }
            }

            Array.Copy(height, 0, map.ElevationData, 0, count);
            return new SeamSolveReport(baseHeight, rampTiles, rampsBuilt, iterations, converged, fixedTiles);
        }

        /// <summary>
        /// One Gauss-Seidel pass over the free tiles. Returns how many changed, which is the
        /// convergence test.
        /// </summary>
        private static int Sweep(byte[] height, bool[] isFixed, int size, int maxSlope)
        {
            int changed = 0;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var i = y * size + x;
                    if (isFixed[i]) continue;

                    int sum = 0, neighbours = 0;
                    int lowestFixed = int.MaxValue, highestFixed = int.MinValue;

                    for (int step = 0; step < 4; step++)
                    {
                        int dx, dy;
                        RoadMask.GetStep((byte)(step + 1), out dx, out dy);
                        var nx = x + dx;
                        var ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                        var ni = ny * size + nx;
                        var contribution = (int)height[ni];

                        if (isFixed[ni])
                        {
                            if (contribution < lowestFixed) lowestFixed = contribution;
                            if (contribution > highestFixed) highestFixed = contribution;
                        }
                        else
                        {
                            // Bounded: a free neighbour further than maxSlope away cannot drag
                            // this tile with it. (Stored heights are bytes, so the lower bound can
                            // never actually bite - but the upper one does, and a solver that only
                            // works while every lot happens to be tall enough is not a solver.)
                            var low = height[i] - maxSlope;
                            var high = height[i] + maxSlope;
                            if (contribution < low) contribution = low;
                            if (contribution > high) contribution = high;
                        }

                        sum += contribution;
                        neighbours++;
                    }

                    if (neighbours == 0) continue;

                    var relaxed = sum / neighbours;
                    if (relaxed < 0) relaxed = 0;
                    if (relaxed > 255) relaxed = 255;
                    // No overshoot past a boundary condition: a tile between a low lot and a high
                    // lot can only ever land between the two, never outside.
                    if (lowestFixed != int.MaxValue)
                    {
                        if (relaxed < lowestFixed) relaxed = lowestFixed;
                        if (relaxed > highestFixed) relaxed = highestFixed;
                    }
                    // Bounded per iteration as well as per neighbour. The neighbour clamp above
                    // stops a spike being pulled in, but on its own it does nothing about how far
                    // the surface may move in one sweep - and a tile with no fixed neighbour at
                    // all (the open ground beyond the last lot) would snap from the base plane
                    // straight to a lot's height, leaving a cliff one tile wide. Bounding the
                    // change makes the seam spread at most maxSlope per sweep, so how far the
                    // graded region reaches is MaxIterations * MaxSlope tiles, which is why
                    // MaxIterations is a quality knob and not just a stopping rule.
                    if (relaxed < height[i] - maxSlope) relaxed = height[i] - maxSlope;
                    if (relaxed > height[i] + maxSlope) relaxed = height[i] + maxSlope;
                    if (relaxed < 0) relaxed = 0;
                    if (relaxed > 255) relaxed = 255;

                    if (relaxed != height[i])
                    {
                        height[i] = (byte)relaxed;
                        changed++;
                    }
                }
            }

            return changed;
        }

        /// <summary>
        /// Pins one lot's interior, or its one-tile border ring, to that lot's height.
        /// </summary>
        /// <remarks>
        /// <b>A lot with its own <c>ARRY(0)</c> plane is pinned to the plane, tile by tile.</b>
        /// <see cref="NeighbourhoodLotInput.BaseHeight"/> is a single median, so a solver that only
        /// had it would pin every interior flat and the whole map would be one plane - which is the
        /// "the green is an empty plane, nothing regarding the heights works" report. The plane is
        /// the lot's real graded ground, so the interior takes it and the border ring takes the
        /// clamped edge of it, which is the boundary the connective ground relaxes away from. A lot
        /// with no plane keeps the old median, so a canned lot list is unchanged.
        /// </remarks>
        /// <param name="interior">True to pin the rectangle, false to pin the ring around it.</param>
        /// <returns>How many tiles it pinned.</returns>
        private static int PinLot(
            NeighbourhoodLotInput lot, byte[] height, bool[] isFixed, int size, bool interior)
        {
            var pinned = 0;
            var inset = interior ? 0 : 1;

            for (var y = Math.Max(0, lot.WorldY - inset); y < Math.Min(size, lot.Bottom + inset); y++)
            {
                for (var x = Math.Max(0, lot.WorldX - inset); x < Math.Min(size, lot.Right + inset); x++)
                {
                    if (!interior && lot.Contains(x, y)) continue;
                    var i = y * size + x;
                    if (isFixed[i]) continue;
                    height[i] = HeightFor(lot, x, y);
                    isFixed[i] = true;
                    pinned++;
                }
            }

            return pinned;
        }

        /// <summary>
        /// The generated-map height a lot's tile is pinned to: its own <c>ARRY(0)</c> height where
        /// it has a plane, and its median otherwise. The plane is indexed in lot-local tiles, so a
        /// border-ring tile (which is outside the rectangle) clamps to the nearest edge of the
        /// lot's own ground - which is what makes the connective ground start at the lot's real
        /// edge rather than at its median.
        /// </summary>
        /// <param name="lot">The lot.</param>
        /// <param name="x">Map tile x.</param>
        /// <param name="y">Map tile y.</param>
        private static byte HeightFor(NeighbourhoodLotInput lot, int x, int y)
        {
            var arry = lot.Arry;
            if (!arry.IsValid) return lot.BaseHeight;
            return NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(
                arry.At(x - lot.WorldX, y - lot.WorldY));
        }

        /// <summary>
        /// Builds one embankment between two lots whose border heights disagree.
        ///
        /// <para>
        /// The band is the tiles strictly <em>between</em> the two border rings, on the axis the
        /// lots are separated on, across the part of their spans that overlaps. A band tile inside
        /// some third lot is skipped rather than overwritten, so a ramp can never punch a hole
        /// through a neighbour's ground. If the two rings touch there is no band at all and the
        /// step is unavoidable: two lots packed hard against each other have no margin to put a
        /// ramp in, which is why the plan's layout step keeps a margin.
        /// </para>
        /// </summary>
        /// <param name="written">How many band tiles were written, if any.</param>
        /// <returns>True if a ramp was written.</returns>
        private static bool BuildRamp(
            NeighbourhoodLotInput a,
            NeighbourhoodLotInput b,
            byte[] height,
            NeighbourhoodLotMask mask,
            int size,
            TerrainSeamOptions options,
            out int written)
        {
            written = 0;

            if (Math.Abs(a.BaseHeight - b.BaseHeight) < options.StepThreshold) return false;

            var spanY0 = Math.Max(a.WorldY, b.WorldY);
            var spanY1 = Math.Min(a.Bottom, b.Bottom) - 1;
            var spanX0 = Math.Max(a.WorldX, b.WorldX);
            var spanX1 = Math.Min(a.Right, b.Right) - 1;

            if (a.Right < b.WorldX || b.Right < a.WorldX)
            {
                // Separated on x: the band is the columns between the two rings.
                if (spanY1 < spanY0) return false;
                var left = a.Right < b.WorldX ? a : b;
                var right = a.Right < b.WorldX ? b : a;
                var x0 = left.Right + 1;
                var x1 = right.WorldX - 2;
                if (x1 < x0) return false;
                for (var y = spanY0; y <= spanY1; y++)
                {
                    written += WriteRamp(height, mask, size, x0, x1, y, true,
                        left.BaseHeight, right.BaseHeight);
                }
                return written > 0;
            }

            if (a.Bottom < b.WorldY || b.Bottom < a.WorldY)
            {
                // Separated on y: the band is the rows between the two rings.
                if (spanX1 < spanX0) return false;
                var top = a.Bottom < b.WorldY ? a : b;
                var bottom = a.Bottom < b.WorldY ? b : a;
                var y0 = top.Bottom + 1;
                var y1 = bottom.WorldY - 2;
                if (y1 < y0) return false;
                for (var x = spanX0; x <= spanX1; x++)
                {
                    written += WriteRamp(height, mask, size, y0, y1, x, false,
                        top.BaseHeight, bottom.BaseHeight);
                }
                return written > 0;
            }

            return false;
        }

        /// <summary>
        /// Grades one line of an embankment from <paramref name="fromHeight"/> to
        /// <paramref name="toHeight"/> across <paramref name="a0"/>..<paramref name="a1"/>, where
        /// the ramp starts immediately outside the first ring and ends immediately outside the
        /// second - so a band of <c>n</c> tiles grades in <c>n + 1</c> steps and the step out of the
        /// ring and the step into the next one are each a single step of the same grade.
        /// </summary>
        private static int WriteRamp(
            byte[] height,
            NeighbourhoodLotMask mask,
            int size,
            int a0,
            int a1,
            int across,
            bool alongX,
            byte fromHeight,
            byte toHeight)
        {
            var steps = a1 - a0 + 1;
            if (steps < 1) return 0;
            var written = 0;

            for (var index = 0; index < steps; index++)
            {
                var along = a0 + index;
                int x, y;
                if (alongX) { x = along; y = across; }
                else { x = across; y = along; }
                if (x < 0 || y < 0 || x >= size || y >= size) continue;
                if (mask.IsInterior(x, y)) continue;

                height[y * size + x] = Grade(fromHeight, toHeight, index, steps);
                written++;
            }

            return written;
        }

        /// <summary>
        /// The <paramref name="index"/>th of <paramref name="steps"/> evenly graded values from
        /// <paramref name="from"/> to <paramref name="to"/>, in integers, rounding half away from
        /// zero. Integer-only on purpose: a lot's height is a byte, the seam has to survive a save
        /// and a reload, and a float ramp that rounds differently on two machines is not a seam
        /// solver, it is a coin toss.
        /// </summary>
        private static byte Grade(int from, int to, int index, int steps)
        {
            var span = (long)to - from;
            var numerator = span * (index + 1);
            var denominator = (long)steps + 1;
            // (2n + d) / 2d is n/d rounded half away from zero, for both signs of n.
            var offset = (2 * numerator + (numerator >= 0 ? denominator : -denominator)) / (2 * denominator);
            var value = from + offset;

            var low = Math.Min(from, to);
            var high = Math.Max(from, to);
            if (value < low) value = low;
            if (value > high) value = high;
            if (value < 0) value = 0;
            if (value > 255) value = 255;
            return (byte)value;
        }
    }
}
