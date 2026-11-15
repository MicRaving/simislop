/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// A small, fast, fully specified pseudo-random source: SplitMix64.
    ///
    /// <para><b>Why not <see cref="System.Random"/>.</b> Three reasons, and the first is the one
    /// that actually bites. "Same seed, same bytes" is a promise this generator has to keep
    /// across a rebuild on a different machine, and <c>System.Random(int)</c> does not make it:
    /// its algorithm is an implementation detail of the runtime, it has changed between .NET
    /// versions before, and seeding with an array hashes it with a per-process randomised
    /// string/array hash. <c>SplitMix64</c> is eight lines of published arithmetic over
    /// <see cref="ulong"/> with no platform, runtime or framework dependency at all. The other two
    /// reasons are that the derived streams below need a <em>specified</em> way to turn a label
    /// into a seed, and that a struct with no allocation is what a per-pass generator wants.</para>
    ///
    /// <para>
    /// <b>All arithmetic is explicitly unchecked.</b> SplitMix64's whole job is to wrap
    /// around; leaving that to the ambient overflow context would be a landmine for whoever wires
    /// this into a checked build.
    /// </para>
    ///
    /// <para>
    /// <b>It is a mutable struct, on purpose.</b> A generator has to advance, and a value type is
    /// what keeps a per-pass stream allocation-free. The consequence to be aware of is the usual
    /// one for a mutable value: assigning or passing a stream by value hands over a *copy* that
    /// then advances independently. Pass a stream by <c>ref</c> or keep it in a field when two
    /// places are meant to draw from the same sequence. <see cref="ForLabel"/> sidesteps the whole
    /// question, which is why every pass in the generator takes a labelled sub-stream instead of a
    /// position in a shared one.
    /// </para>
    /// </summary>
    public struct NeighbourhoodRng
    {
        private const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;
        private const ulong MixA = 0xBF58476D1CE4E5B9UL;
        private const ulong MixB = 0x94D049BB133111EBUL;

        private ulong _state;

        /// <summary>Creates a stream from an already-mixed seed value.</summary>
        /// <param name="seed">The state; two streams with different states diverge immediately.</param>
        public NeighbourhoodRng(ulong seed)
        {
            _state = seed;
        }

        /// <summary>
        /// The raw state. Only useful for a stream derived from a seed: two runs of the same
        /// pipeline must not read the same state twice.
        /// </summary>
        public ulong State
        {
            get { return _state; }
        }

        /// <summary>
        /// A stream derived from this one and a label - e.g.
        /// <c>generation.ForLabel("forest")</c>.
        ///
        /// <para>
        /// This is the property the plan asks for when it says the derived streams exist "so
        /// tuning one does not disturb the others": a pass that draws a different number of values,
        /// or is added, or is removed, cannot move any other pass's numbers, because the other pass
        /// is seeded from a <em>label</em> and not from a position in a shared sequence. A shared
        /// stream cannot promise that - insert one extra draw in the road pass and every tree after
        /// it lands somewhere else.
        /// </para>
        /// </summary>
        /// <param name="label">A stable, human-meaningful name for the sub-stream.</param>
        public NeighbourhoodRng ForLabel(string label)
        {
            return new NeighbourhoodRng(Mix(_state ^ Fingerprint(label)));
        }

        /// <summary>
        /// FNV-1a over the label's UTF-16 code units. A hand-rolled hash rather than
        /// <c>string.GetHashCode()</c>, which is randomised per process in .NET Core and would
        /// make a generated map differ between two runs of the same build.
        /// </summary>
        private static ulong Fingerprint(string label)
        {
            unchecked
            {
                var hash = 14695981039346656037UL;
                if (label != null)
                {
                    for (int i = 0; i < label.Length; i++)
                    {
                        var c = label[i];
                        hash = (hash ^ (byte)(c & 0xFF)) * 1099511628211UL;
                        hash = (hash ^ (byte)((c >> 8) & 0xFF)) * 1099511628211UL;
                    }
                }
                return hash;
            }
        }

        private static ulong Mix(ulong z)
        {
            unchecked
            {
                z = (z ^ (z >> 30)) * MixA;
                z = (z ^ (z >> 27)) * MixB;
                return z ^ (z >> 31);
            }
        }

        /// <summary>The next 64 random bits.</summary>
        public ulong NextULong()
        {
            unchecked
            {
                _state += GoldenGamma;
                return Mix(_state);
            }
        }

        /// <summary>
        /// A value in <c>[0, 1)</c> with 53 bits of resolution, built by shifting rather than by
        /// dividing so the result cannot round up to 1.
        /// </summary>
        public double NextDouble()
        {
            return (NextULong() >> 11) * (1.0 / 9007199254740992.0);
        }

        /// <summary>
        /// A value in <paramref name="minInclusive"/>..<paramref name="maxExclusive"/>. A range
        /// that is empty or inverted collapses to <paramref name="minInclusive"/> rather than
        /// throwing, because every caller here is deriving a bound from a clamped option and a
        /// throw at that point would be a crash over a tuning value.
        /// </summary>
        /// <param name="minInclusive">The lowest value that can come out.</param>
        /// <param name="maxExclusive">The first value that cannot come out.</param>
        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            var span = (ulong)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextULong() % span);
        }
    }

    /// <summary>
    /// The named random streams a world seed fans out into.
    ///
    /// <para>
    /// §15 of the plan asks for <c>worldSeed -&gt; {generation, simulation, foliage}</c> "so tuning
    /// one does not disturb the others", and it says of Scope A specifically: "A only uses
    /// <c>generation</c>". Both are honoured. All three exist and are derived deterministically -
    /// <see cref="Simulation"/> and <see cref="Foliage"/> exist so Part B can route on the same
    /// map without the generator's numbers shifting under it - and the generator consumes only
    /// <see cref="Generation"/>, which is in turn the root of the labelled sub-streams the passes
    /// actually draw from. So retuning the water mask cannot move a tree, and adding a pass that
    /// draws cannot move anything else.
    /// </para>
    /// <para>
    /// The derivation is <c>Mix(worldSeed ^ Fingerprint(label))</c>, which is a function of the
    /// seed and the label alone - no process state, no ordering, nothing to initialise.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodStreams
    {
        /// <summary>
        /// The stream the generator itself draws from. The only one Scope A consumes.
        /// </summary>
        public NeighbourhoodRng Generation { get; }

        /// <summary>
        /// Reserved for a future milestone's simulation. Derived and stable, never drawn from
        /// here, so a Part B that starts using it gets the numbers it was always going to get.
        /// </summary>
        public NeighbourhoodRng Simulation { get; }

        /// <summary>
        /// Reserved for a future milestone's animated foliage. Same contract as
        /// <see cref="Simulation"/>.
        /// </summary>
        public NeighbourhoodRng Foliage { get; }

        private NeighbourhoodStreams(NeighbourhoodRng generation, NeighbourhoodRng simulation, NeighbourhoodRng foliage)
        {
            Generation = generation;
            Simulation = simulation;
            Foliage = foliage;
        }

        /// <summary>
        /// Fans a world seed out into the three named streams.
        /// </summary>
        /// <param name="worldSeed">The seed stored in the layout sidecar.</param>
        public static NeighbourhoodStreams FromSeed(long worldSeed)
        {
            var root = new NeighbourhoodRng(unchecked((ulong)worldSeed));
            return new NeighbourhoodStreams(
                root.ForLabel("generation"),
                root.ForLabel("simulation"),
                root.ForLabel("foliage"));
        }

        /// <summary>
        /// Whether two derivations of the same seed produced the same streams. Not needed by the
        /// generator; it exists so a test can state the derivation's contract as an assertion
        /// rather than as a comment.
        /// </summary>
        /// <param name="other">The streams to compare with.</param>
        public bool Matches(NeighbourhoodStreams other)
        {
            return Generation.State == other.Generation.State
                && Simulation.State == other.Simulation.State
                && Foliage.State == other.Foliage.State;
        }
    }

    /// <summary>
    /// §6: the single deterministic pass that turns a seed and a lot list into a generated
    /// 512x512 world model.
    ///
    /// <para><b>One pass, in a fixed order, because the order is a dependency graph.</b></para>
    /// <list type="number">
    ///   <item><description>
    ///     <b><see cref="RoadGraph"/>.</b> Needs the lot rectangles and nothing else. Produces the
    ///     road nibbles, the sidewalk ring, the entrances, and - by flood fill and repair - the
    ///     connectivity invariant.
    ///   </description></item>
    ///   <item><description>
    ///     <b><see cref="TerrainFill"/>.</b> Needs the road and sidewalk masks, because foliage may
    ///     not grow on either, and the lot rectangles, for the nearest-lot-type fill.
    ///   </description></item>
    ///   <item><description>
    ///     <b><see cref="TerrainSeamSolver"/>.</b> Needs the road and sidewalk masks (to flatten
    ///     them) and the lot rectangles (for the Dirichlet borders). It does not need anything from
    ///     the fill, which is why the fill's output - ground types, water, foliage - never
    ///     influences elevation, and re-running only the solver over a filled map cannot disturb
    ///     the fill.
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>Determinism.</b> Same <see cref="NeighbourhoodMapInput"/> in, byte-identical
    /// <see cref="NeighbourhoodMap"/> out. Nothing here reads the clock, the environment, a hash
    /// code, a thread, a file, or a graphics device; every collection that could be enumerated in a
    /// different order is either an array or a <see cref="System.Collections.Generic.List{T}"/> walked
    /// by index; and every "pick one" is a minimum with an explicit tie-break. That is what
    /// <c>NeighbourhoodMapGeneratorTests.SameSeedAndInputProduceIdenticalArrays</c> asserts, and
    /// why a regenerated map can be thrown away and rebuilt rather than stored.</para>
    ///
    /// <para><b>Only an unbuildable network is a failure; a merely imperfect one is a log line.</b>
    /// <see cref="RoadGraph.VerifyDrawable"/> throws only when there are lots and not one road
    /// tile anywhere between them - there is nothing to draw, so the 2D bitmap fallback of §8 is
    /// the better answer. A network that is merely <em>split</em>, or that leaves a few lots
    /// without road access, still renders: a surviving cul-de-sac looks like a cul-de-sac, and
    /// <see cref="RoadGraph.VerifyConnected"/> remains the strict assertion for tests and for any
    /// caller that needs the §6.1 invariant as a hard contract. This split is deliberate - the
    /// earlier version threw on both, and threw away correct worlds over one unbridged street end.
    /// </para>
    ///
    /// <para><b>When it does throw, the message carries the whole network description</b>, so the
    /// log line that reports the fallback also says what the network looked like.</para>
    ///
    /// <para><b>Diagnostics.</b> <see cref="DiagnosticSink"/>, if set, is handed one line per
    /// generation: how many road tiles, how many street runs and how long the longest one is, how
    /// the network breaks into components, and how many driveway spurs there are. It is a sink
    /// rather than a log call on purpose - everything else in this file is a pure function of its
    /// arguments, and a generated map's interesting question is never "what did the generator
    /// decide" but "what did the streets come out as", which is what this line answers. The
    /// default is null, which means silent.</para>
    /// </summary>
    public static class NeighbourhoodMapGenerator
    {
        /// <summary>
        /// Bumped whenever a change here would produce different bytes for the same seed and
        /// layout. The layout sidecar stores it beside the seed, so an older map is regenerated
        /// rather than half-believed.
        /// </summary>
        /// <remarks>
        /// <b>276:</b> the road source and the lot positions changed, so older rasters are invalid.
        ///
        /// <b>277.</b> Not a generation change - the bytes for a given seed are identical - but a
        /// version bump anyway, because the set of maps that exist at all changed: generation used
        /// to throw for a split network, so those layouts have no cached raster to invalidate.
        /// Letting them through means a neighbourhood that was previously never written can now be,
        /// and a cache that never had an entry for them must not be trusted to keep not having one.
        /// </remarks>
        public const int GeneratorVersion = 277;

        /// <summary>
        /// Where the one-line-per-generation diagnostic goes. Null by default, so a normal run is
        /// silent; the game points it at the log, and a test points it at a list to assert what
        /// the line says.
        /// </summary>
        public static Action<string> DiagnosticSink { get; set; }

        /// <summary>
        /// Where each lot's own decoded <c>ARRY(0)</c> plane comes from, or <c>null</c> for the
        /// shipped reader - a lot's own <c>HouseNN.iff</c>, through
        /// <see cref="NeighbourHollowLoader.TryDecodeArryAltitude"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the seam that lets the generator grade a lot's ground.</b>
        /// <see cref="NeighbourhoodLotInput.BaseHeight"/> is a single median, so a map solved only
        /// from it is a plane; the lot's real per-tile grading is its <c>ARRY(0)</c> plane and
        /// nowhere else. The reader is a property rather than a hard call so a test can hand in a
        /// known plane with no Sims install present, and so the generator stays testable without a
        /// content manager - a null reader is the shipped path and an install-less run simply falls
        /// back to the median, which is the pre-existing behaviour.
        /// </para>
        /// <para>
        /// It is read once per generation, on the generation worker, through the same
        /// <c>GetHouse</c> the facade pass already calls - one file open and one chunk read per
        /// lot, no <c>World</c>, no <c>VM</c> and no device.
        /// </para>
        /// </remarks>
        public static Func<NeighbourhoodLotInput, SimitoneArryAltitude> ArryPlaneFor { get; set; }

        /// <summary>Generates a neighbourhood map with the default fill and seam settings.</summary>
        /// <param name="input">The generation input.</param>
        public static NeighbourhoodMap Generate(NeighbourhoodMapInput input)
        {
            return Generate(input, TerrainFillOptions.Default, TerrainSeamOptions.Default);
        }

        /// <summary>
        /// Generates a neighbourhood map, reporting what each stage produced.
        /// </summary>
        /// <param name="input">The generation input.</param>
        /// <param name="fillOptions">Terrain-fill tunables.</param>
        /// <param name="seamOptions">Seam-solver tunables.</param>
        public static NeighbourhoodMap Generate(
            NeighbourhoodMapInput input,
            TerrainFillOptions fillOptions,
            TerrainSeamOptions seamOptions)
        {
            return Generate(input, fillOptions, seamOptions, out _);
        }

        /// <summary>
        /// Generates a neighbourhood map and hands back the per-stage reports, for a diagnostic
        /// log line or a failing test's message.
        /// </summary>
        /// <param name="input">The generation input.</param>
        /// <param name="fillOptions">Terrain-fill tunables.</param>
        /// <param name="seamOptions">Seam-solver tunables.</param>
        /// <param name="report">What the terrain fill and the seam solver did.</param>
        public static NeighbourhoodMap Generate(
            NeighbourhoodMapInput input,
            TerrainFillOptions fillOptions,
            TerrainSeamOptions seamOptions,
            out NeighbourhoodGenerationReport report)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // Each lot's own graded ground, before anything reads the input. The seam solver is the
            // consumer: with a plane it pins the lot interior to the lot's real heights, and without
            // one it pins the median exactly as before. See ArryPlaneFor.
            input = WithArryPlanes(input);

            var streams = NeighbourhoodStreams.FromSeed(input.Seed);
            var map = NeighbourhoodMap.Create(input.MapSize);

            // 1. Streets. This is the stage that can fail, and it is meant to be the one. The mask
            //    rides on the input rather than on an argument here, so there is exactly one thing
            //    that decides whether these streets are the 2D map's own or measured from the lots.
            var graph = RoadGraph.Build(input, map);
            var road = graph.Check();
            // Not the same question as RoadGraph.VerifyConnected, which stays strict: this one is
            // "is there a world here", and only a world with lots and no roads between them has no
            // answer. Everything else is drawn, and said out loud.
            if (!road.Drawable)
            {
                throw new InvalidOperationException(road.Message);
            }

            // 2. Ground, water and foliage, off labelled sub-streams of "generation" so that
            //    retuning the water mask cannot move a single tree.
            var fill = TerrainFill.Fill(input, graph, map, streams.Generation, fillOptions);

            // 3. Height, from the lot borders and the road masks.
            var seam = TerrainSeamSolver.Solve(input, graph, map, seamOptions);

            report = new NeighbourhoodGenerationReport(graph, fill, seam, streams);

            var sink = DiagnosticSink;
            if (sink != null)
            {
                try
                {
                    sink(report.Describe());
                    // The map is being drawn, but the network did not meet the §6.1 invariant. Say
                    // so on the same sink: a warning that only exists as an exception the caller no
                    // longer throws is a warning nobody ever reads.
                    if (!road.SingleNetwork) sink(road.Message);
                }
                catch (Exception)
                {
                    // A diagnostic must never be the reason a neighbourhood fails to build. The
                    // one thing this generator is allowed to throw about is a world it cannot
                    // draw, and that has already been decided by the check above.
                }
            }

            return map;
        }

        /// <summary>
        /// The streams a given input generates from, without generating anything. A test uses it
        /// to state the derivation's contract; a later milestone that needs a stream Scope A does
        /// not use can get it without a side channel.
        /// </summary>
        /// <param name="input">The generation input.</param>
        public static NeighbourhoodStreams StreamsFor(NeighbourhoodMapInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return NeighbourhoodStreams.FromSeed(input.Seed);
        }

        /// <summary>
        /// The input with each lot's own <c>ARRY(0)</c> plane attached where one can be read, so the
        /// seam solver grades the lot interiors instead of pinning them flat.
        /// </summary>
        /// <remarks>
        /// A lot the reader cannot answer for keeps the record it had - <see cref="SimitoneArryAltitude.None"/> -
        /// and the solver falls back to the median, which is exactly the pre-change behaviour. The
        /// input is only rebuilt when at least one plane was found, so a canned lot list or an
        /// install-less run hands back the very same instance.
        /// </remarks>
        /// <param name="input">The generation input.</param>
        private static NeighbourhoodMapInput WithArryPlanes(NeighbourhoodMapInput input)
        {
            var lots = input.Lots;
            if (lots.Count == 0) return input;

            var reader = ArryPlaneFor;
            var enriched = new List<NeighbourhoodLotInput>(lots.Count);
            var changed = false;
            for (int i = 0; i < lots.Count; i++)
            {
                var lot = lots[i];
                var arry = reader != null ? reader(lot) : ReadHouseArry(lot);
                if (arry.IsValid)
                {
                    enriched.Add(lot.WithArry(arry));
                    changed = true;
                }
                else
                {
                    enriched.Add(lot);
                }
            }

            return changed
                ? new NeighbourhoodMapInput(input.Seed, input.MapSize, enriched)
                : input;
        }

        /// <summary>
        /// The shipped plane reader: a lot's own <c>HouseNN.iff</c>, decoded by
        /// <see cref="NeighbourHollowLoader.TryDecodeArryAltitude"/>. Never throws: a missing
        /// content manager, a missing file and a malformed chunk are all
        /// <see cref="SimitoneArryAltitude.None"/>, which the solver reads as "use the median".
        /// </summary>
        /// <param name="lot">The lot whose plane is wanted.</param>
        private static SimitoneArryAltitude ReadHouseArry(NeighbourhoodLotInput lot)
        {
            try
            {
                var provider = FSO.Content.Content.Get();
                if (provider == null || provider.Neighborhood == null) return SimitoneArryAltitude.None;
                var house = provider.Neighborhood.GetHouse(lot.Number);
                SimitoneArryAltitude arry;
                string reason;
                return NeighbourHollowLoader.TryDecodeArryAltitude(house, lot.Number, out arry, out reason)
                    ? arry
                    : SimitoneArryAltitude.None;
            }
            catch (Exception)
            {
                return SimitoneArryAltitude.None;
            }
        }
    }

    /// <summary>What one generation pass produced, stage by stage.</summary>
    public readonly struct NeighbourhoodGenerationReport
    {
        /// <summary>The road graph, including its repairs and its entrances.</summary>
        public RoadGraph RoadGraph { get; }

        /// <summary>What the terrain fill scattered and flooded.</summary>
        public TerrainFillReport TerrainFill { get; }

        /// <summary>What the seam solver did.</summary>
        public SeamSolveReport Seam { get; }

        /// <summary>The streams the pass drew from.</summary>
        public NeighbourhoodStreams Streams { get; }

        /// <summary>
        /// One line describing what the pass produced, stage by stage, with the road network first
        /// because it is the stage that can fail and the one whose shape decides whether the map
        /// looks like a neighbourhood.
        /// </summary>
        public string Describe()
        {
            return RoadGraph.DescribeNetwork() + "; " + TerrainFill.Describe() + "; " + Seam.Describe();
        }

        internal NeighbourhoodGenerationReport(
            RoadGraph roadGraph, TerrainFillReport terrainFill, SeamSolveReport seam, NeighbourhoodStreams streams)
        {
            RoadGraph = roadGraph;
            TerrainFill = terrainFill;
            Seam = seam;
            Streams = streams;
        }
    }
}
