using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Frame-name and layer helpers for the animated overlays drawn on the neighbourhood
    /// selection screen (<see cref="UINeighborhoodSelectionPanel.Neighborhoods"/>).
    ///
    /// Three things live here that used to be pure data and are easy to get wrong:
    ///
    /// <list type="number">
    /// <item><b>Numbered frame series.</b> Almost every TS1 neighbourhood animation is a
    /// folder of <c>prefixNN.ext</c> files (<c>VIsland\visland_waves001.bmp</c>,
    /// <c>Studiotown\Nhood_superstar_car19.bmp</c>, <c>Magicland\Nhood_balloon16.bmp</c>).
    /// The index is sometimes zero-padded to a fixed width and sometimes not, and the width
    /// differs per series, so the lists are generated from a single description rather than
    /// typed out by hand. The names produced must match the FAR entry names
    /// <b>exactly</b>, case included: the panel resolves them through
    /// <c>TS1Provider</c> &#8594; <c>FAR1Provider.EntriesByName</c>, which is a plain
    /// case-sensitive dictionary lookup, and a name that misses simply yields a dropped frame.</item>
    /// <item><b>Layer counting.</b> <see cref="UINeighborhoodAnimationLayer"/> silently drops
    /// frames the content provider cannot resolve, so the number of frames a config
    /// <i>declares</i> and the number it actually gets can differ. Anything that reasons about
    /// "is this layer an animation" has to count the usable names, not the array length.</item>
    /// <item><b>Per-view folder matching.</b> Because a name in a folder the install does not
    /// have resolves to null and is dropped without a word, an overlay series pasted onto the
    /// wrong neighbourhood fails invisibly and identically to a typo.
    /// <see cref="ForeignFrames"/> is the check for that: each view's frames have to come from
    /// the same FAR folder as its own background.</item>
    /// </list>
    ///
    /// The positions below are in the 800x600 neighbourhood coordinate space, which is the
    /// unscaled size of every <c>*Screen</c> background the panel draws, and they are the one
    /// part of the table that is <i>not</i> recoverable from the game files - see the remarks on
    /// each constant.
    ///
    /// <para>
    /// One neighbourhood has no free-standing overlay art and therefore no position, and the
    /// reason is worth stating up front rather than leaving to be rediscovered: see
    /// <see cref="DowntownWavePosition"/>. Downtown's cars are painted into its background and
    /// its buyable cars are lot objects, so a "Downtown car overlay" would have to be invented
    /// art. StudioTown's is the only vehicle sprite the game ships, and it is already placed.
    /// </para>
    ///
    /// Everything here is pure: no graphics device, no content, no audio.
    /// </summary>
    public static class NeighbourhoodAnimations
    {
        /// <summary>Width of the unscaled neighbourhood background every position is relative to.</summary>
        public const int BackgroundWidth = 800;

        /// <summary>Height of the unscaled neighbourhood background every position is relative to.</summary>
        public const int BackgroundHeight = 600;

        // ----------------------------------------------------------------- the three added layers

        /// <summary>
        /// Community (The Sims: Unleashed) - the "Nessie" sea serpent in the river.
        ///
        /// Frames: <c>Community\ness01.bmp</c>, <c>ness02.bmp</c>, <c>ness03.bmp</c> (106x75,
        /// colour-keyed magenta). The shipped Complete Collection install stores byte-identical
        /// copies under <c>Nbhd\</c> and <c>Community\</c>. <b>The <c>VIsland\</c> folder contains
        /// no copy at all</b> (its whole animated content is <c>visland_waves001-005</c>,
        /// <c>visland_port</c> and <c>visland_trees</c>), so - despite the original code's
        /// <c>//TODO: nessie</c> sitting on the Vacation Island config - the art can only belong to
        /// the base or Community neighbourhoods. It is placed on Community, whose large diagonal
        /// river and <c>river_loop</c> ambience fit a river serpent far better than the base
        /// terrain (which only has water at its edges).
        ///
        /// The frame order is the surfacing cycle: head and neck out of the water, body
        /// following, then the tail disappearing; the Community config does not pulse, so the
        /// renderer plays 1,2,3,1,2,3,...
        ///
        /// Position is in the 800x600 neighbourhood coordinate space and was checked by
        /// compositing the sprite over the extracted <c>Community\NScreen_unleashed.bmp</c> and
        /// its wave layer: it sits in open water upstream of the road bridge, clear of the banks.
        /// </summary>
        public static readonly Vector2 NessiePosition = new Vector2(560, 330);

        /// <summary>StudioTown - the free parking bay in the studio lot, below the painted-in cars.</summary>
        /// <remarks>
        /// The StudioTown lot is a diagonal block in the lower middle of <c>Studiotown\DScreen.bmp</c>
        /// (roughly x 400-600, y 390-560) that already has several cars painted into the
        /// background. This slot is the empty bay immediately below the blue car at
        /// ~(402, 499), which keeps the new sprite clear of the baked-in ones and of the
        /// sidewalk along the lot's north-west edge.
        /// </remarks>
        public static readonly Vector2 StudioCarPosition = new Vector2(400, 517);

        /// <summary>MagicTown - the airship over the open meadow beside the river.</summary>
        /// <remarks>
        /// <c>Magicland\DScreen.bmp</c> is 800x600 with a colour-keyed river cut out of the
        /// north-west, filled at runtime by the <c>DScreen_waves1-6</c> layer at (0, 62). The
        /// position was checked by compositing the airship over the extracted background plus
        /// the wave layer at its real (0, 62) offset - which is 62px lower than a naive (0, 0)
        /// placement, and moves the apparent waterline; the chosen spot sits over the green
        /// meadow north-west of the river bend.
        /// </remarks>
        public static readonly Vector2 MagicBlimpPosition = new Vector2(200, 170);

        // ------------------------------------------------------- Downtown, and why it has no car

        /// <summary>
        /// Downtown - the animated water layer, and the whole of Downtown's overlay art. Read this
        /// before going looking for a Downtown car overlay; there is not one to place.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The vehicles on the Downtown map are painted into the background, not drawn as
        /// overlays.</b> <c>UIGraphics.far</c>'s <c>Downtown\</c> folder holds twenty files, and
        /// not one of them is a car: the map background <c>Downtown\DScreen.bmp</c> (800x600)
        /// has the traffic baked into it - the row of white cars and the red car on the upper
        /// right road, the yellow bulldozers by the water tower at the upper left. There is
        /// nothing to position.
        /// </para>
        /// <para>
        /// <b>Downtown's only animated layer is the bay.</b> The <c>Downtown\dscreen*</c> files
        /// are the lower-right corner of the map - the sea and the rock jetty, drawn
        /// colour-keyed magenta over the background - and <c>dscreen00</c> to
        /// <c>dscreen04</c> are the cycle. Because they are 800x600, i.e. the size of the
        /// whole map, the layer sits at the origin.
        /// </para>
        /// <para>
        /// <b><c>dscreen05.bmp</c> also ships, and is deliberately not a frame.</b> It is
        /// 400x250 - not the 800x600 of the other five - and every one of its 100,000 pixels is
        /// the colour key, so it is a blank padding plate rather than a sixth view of the
        /// water. The series therefore stops at 4. Adding it would put a full-size blank flash
        /// in the loop, and on the un-keyed path a solid rectangle over the map.
        /// </para>
        /// <para>
        /// <b>What cars Downtown does have are lot objects.</b> <c>GameData\Objects\Objects.far</c>
        /// carries <c>CarBentley.iff</c>, <c>CarJunk.iff</c>, <c>CarLimo.iff</c>,
        /// <c>CarMJeep.iff</c>, <c>CarSquad.iff</c>, <c>CarStaff.iff</c>, <c>CarStd.iff</c>,
        /// <c>CarSUV.iff</c> and <c>CarTown.iff</c> ("Car - Bentley", "Car - Limo", "Car - Town
        /// Car" and so on), which are placed and owned inside a Downtown lot; Downtown's own
        /// <c>ExpansionPack2.far</c> adds <c>NPCCar.iff</c>, a narrative prop. A player buys one
        /// by walking onto a Downtown lot, not by looking at the map camera, so none of it
        /// belongs in this table - and the car-purchase path the audit notes as unwired is a
        /// buy-mode concern, not a neighbourhood-overlay one.
        /// </para>
        /// <para>
        /// <b>StudioTown is the only neighbourhood with free-standing vehicle art.</b> A search
        /// of every <c>*.far</c> in a Complete Collection install for image files whose name
        /// contains "car" returns <c>Studiotown\Nhood_superstar_car0-19.bmp</c> and
        /// <c>Studiotown\Nhood_superstar_car.bmp</c> (a 24x15 sprite of the movie star's car in
        /// the studio's parking bay) plus a base-game <c>toycar3drt.bmp</c> texture and
        /// unrelated accessories and skins. Nothing under <c>Downtown\</c>. That is the whole
        /// of the answer to "the Downtown cars are missing or mispositioned": they were never a
        /// separate overlay, and adding one would mean inventing art the game does not have.
        /// </para>
        /// </remarks>
        public static readonly Vector2 DowntownWavePosition = Vector2.Zero;

        /// <summary>
        /// The five Downtown bay-wave frames, in cycle order, drawn at
        /// <see cref="DowntownWavePosition"/>.
        /// </summary>
        /// <remarks>
        /// <c>dscreen00</c>-<c>dscreen04</c>: the sea and the rock jetty in the lower right of
        /// the map, colour-keyed magenta so the background shows through everywhere else. The
        /// indices are zero-padded to two, which is the one thing that makes this series easy to
        /// get wrong - a single-digit <c>dscreen0</c> would be a name the provider cannot
        /// resolve and the layer would quietly lose a frame.
        ///
        /// Stops at 4 on purpose. <c>dscreen05.bmp</c> also ships (see
        /// <see cref="DowntownWavePosition"/> for why it is not a frame) and is deliberately
        /// left out rather than declared and dropped.
        /// </remarks>
        public static string[] DowntownFrames()
        {
            return NumberedFrameSeries("Downtown", "dscreen", ".bmp", 0, 4, 2);
        }

        /// <summary>
        /// The three Nessie frames, in surfacing order. Rebuilt per call so a caller cannot
        /// mutate a shared array out from under the config table.
        /// </summary>
        public static string[] NessieFrames()
        {
            return NumberedFrameSeries("Community", "ness", ".bmp", 1, 3, 2);
        }

        /// <summary>
        /// The two frames of a single StudioTown car (index 0). The pack's series is ten cars of
        /// two frames each (<c>car0/car1</c> the dark sedan, <c>car2/car3</c> the white one, ...),
        /// but the panel's overlay model has one <c>Position</c> per layer, so feeding all twenty
        /// frames would visibly morph between ten cars in one parking bay. One car is used; the
        /// rest remain available through <see cref="NumberedFrameSeries"/> if a future version can
        /// place ten.
        /// </summary>
        public static string[] StudioCarFrames()
        {
            return NumberedFrameSeries("Studiotown", "Nhood_superstar_car", ".bmp", 0, 1);
        }

        /// <summary>
        /// The sixteen MagicTown airship frames. They are a full 360 degree rotation of the
        /// blimp, so the order 1..16 is the animation order and the loop closes on itself.
        /// </summary>
        public static string[] MagicBlimpFrames()
        {
            return NumberedFrameSeries("Magicland", "Nhood_balloon", ".bmp", 1, 16);
        }

        // --------------------------------------------------------------------- frame-name series

        /// <summary>
        /// Build an ordered list of numbered frame names,
        /// <c>folder\stem{index}{extension}</c>, for <paramref name="firstIndex"/> through
        /// <paramref name="lastIndex"/> inclusive.
        /// </summary>
        /// <param name="folder">FAR entry folder, e.g. <c>"VIsland"</c>. A trailing backslash
        /// is tolerated so callers can pass either form.</param>
        /// <param name="stem">File name without the trailing index, e.g. <c>"visland_waves"</c>.</param>
        /// <param name="extension">File extension including the dot, e.g. <c>".bmp"</c>.</param>
        /// <param name="firstIndex">First index, inclusive. May be 0.</param>
        /// <param name="lastIndex">Last index, inclusive. Must not be below
        /// <paramref name="firstIndex"/>.</param>
        /// <param name="indexDigits">Minimum width of the zero-padded index. 0 (the default)
        /// means no padding at all, which is what <c>Nhood_superstar_car0.bmp</c> and
        /// <c>Nhood_balloon1.bmp</c> use; 2 gives <c>ness01.bmp</c>. The index is never
        /// truncated, so a value wider than <paramref name="indexDigits"/> still comes out in
        /// full.</param>
        /// <exception cref="ArgumentNullException">A folder, stem or extension was null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="lastIndex"/> is below
        /// <paramref name="firstIndex"/>, or <paramref name="indexDigits"/> is negative.</exception>
        public static string[] NumberedFrameSeries(
            string folder, string stem, string extension, int firstIndex, int lastIndex, int indexDigits = 0)
        {
            if (folder == null) throw new ArgumentNullException(nameof(folder));
            if (stem == null) throw new ArgumentNullException(nameof(stem));
            if (extension == null) throw new ArgumentNullException(nameof(extension));
            if (lastIndex < firstIndex)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lastIndex), lastIndex, "lastIndex must not be below firstIndex (" + firstIndex + ").");
            }
            if (indexDigits < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(indexDigits), indexDigits, "indexDigits must not be negative.");
            }

            var trimmedFolder = folder.TrimEnd('\\');
            var count = lastIndex - firstIndex + 1;
            var frames = new string[count];
            for (var i = 0; i < count; i++)
            {
                var index = (firstIndex + i).ToString(CultureInfo.InvariantCulture);
                if (index.Length < indexDigits) index = index.PadLeft(indexDigits, '0');
                frames[i] = trimmedFolder + "\\" + stem + index + extension;
            }
            return frames;
        }

        // ------------------------------------------------------------------------ layer counting

        /// <summary>
        /// The number of frame names in <paramref name="anim"/> that could actually be handed
        /// to the content provider: nulls and blanks are dropped, matching what
        /// <c>UINeighborhoodAnimationLayer</c> does with the resolved textures.
        /// </summary>
        public static int UsableFrameCount(NeighborhoodImageAnim anim)
        {
            if (anim?.Frames == null) return 0;

            var count = 0;
            foreach (var frame in anim.Frames)
            {
                if (!string.IsNullOrWhiteSpace(frame)) count++;
            }
            return count;
        }

        /// <summary>
        /// True when the layer will actually animate, i.e. it has at least two usable frame
        /// names. A single-frame layer is a static overlay - the panel already treats it that
        /// way, and this makes the same rule available to callers and tests.
        /// </summary>
        public static bool IsAnimated(NeighborhoodImageAnim anim)
        {
            return UsableFrameCount(anim) > 1;
        }

        /// <summary>All usable frame names of a layer, in declared order.</summary>
        public static string[] UsableFrames(NeighborhoodImageAnim anim)
        {
            if (anim?.Frames == null) return new string[0];

            var frames = new List<string>();
            foreach (var frame in anim.Frames)
            {
                if (!string.IsNullOrWhiteSpace(frame)) frames.Add(frame);
            }
            return frames.ToArray();
        }

        // ------------------------------------------------------------------------ config lookup

        /// <summary>
        /// The view config for a 1-based neighbourhood mode, or null when the mode is outside
        /// the table or the slot is empty. This mirrors the guard in
        /// <c>PopulateScreen</c> without the logging or the fallback assignment, so callers
        /// and tests can ask the same question the panel asks.
        /// </summary>
        public static NeighborhoodViewConfig ConfigForMode(NeighborhoodViewConfig[] configs, int mode)
        {
            if (configs == null || mode < 1 || mode > configs.Length) return null;
            return configs[mode - 1];
        }

        /// <summary>
        /// Every frame name declared by <paramref name="config"/>'s animation layers, in layer
        /// order then frame order. Used to check the table against a real install without
        /// needing a graphics device.
        /// </summary>
        public static string[] DeclaredFrames(NeighborhoodViewConfig config)
        {
            var all = new List<string>();
            if (config?.FullImageAnimations == null) return all.ToArray();

            foreach (var layer in config.FullImageAnimations)
            {
                if (layer == null) continue;
                all.AddRange(UsableFrames(layer));
            }
            return all.ToArray();
        }

        // ------------------------------------------------------------- per-view frame filtering

        /// <summary>
        /// The FAR folder a view draws from, taken from its own background
        /// <see cref="NeighborhoodViewConfig.Graphic"/>. Null when the config is null or has no
        /// background, which is the case for the deliberately empty slot 6.
        /// </summary>
        public static string ViewFolder(NeighborhoodViewConfig config)
        {
            if (config == null) return null;
            return FolderOf(config.Graphic);
        }

        /// <summary>
        /// The FAR folder part of a single file name, e.g. <c>"Studiotown"</c> for
        /// <c>"Studiotown\Nhood_superstar_car0.bmp"</c>, or null when the name carries no folder
        /// at all.
        /// </summary>
        /// <remarks>
        /// This is the split that decides whether a frame name can resolve. <c>TS1Provider</c>
        /// keys the FAR entries by their full in-archive path, so the folder is not decoration:
        /// a name with the wrong folder is simply a name that is not in the archive.
        /// </remarks>
        public static string FolderOf(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;
            var trimmed = fileName.Trim();
            var lastSeparator = trimmed.LastIndexOf('\\');
            if (lastSeparator < 0) return null;
            // a leading separator leaves an empty folder, which is not a folder at all
            if (lastSeparator == 0) return null;
            // trailing separators are trimmed because the split is the last one: a hand-typed
            // doubled separator would otherwise come out as "Downtown\", which never equals a
            // view's folder and so would report a perfectly good frame as foreign
            var folder = trimmed.Substring(0, lastSeparator).TrimEnd('\\');
            return folder.Length == 0 ? null : folder;
        }

        /// <summary>
        /// The frames <paramref name="config"/> declares that do <b>not</b> belong to its own
        /// view: either they name a different folder, or they name no folder at all. Empty for
        /// every shipped view.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every overlay in the table is a separate FAR folder from the rest of its
        /// neighbourhood - <c>Studiotown\DScreen.bmp</c> and
        /// <c>Studiotown\Nhood_superstar_car0.bmp</c>, <c>Community\NScreen_unleashed.bmp</c> and
        /// <c>Community\ness01.bmp</c> - because that is how the game itself ships them, one
        /// folder per map.
        /// </para>
        /// <para>
        /// The filter is a guard against the specific mistake this file's comments keep
        /// describing: an overlay series pasted onto the wrong neighbourhood. The symptom is
        /// invisible, because a name in a folder the install does not have resolves to null and
        /// <c>UINeighborhoodAnimationLayer</c> drops it without a word, so the layer animates
        /// short or not at all. Which is exactly what would happen if a Downtown car overlay
        /// were built by reusing <see cref="StudioCarFrames"/> - those frames live in
        /// <c>Studiotown\</c> and would never resolve on the Downtown view, whatever position
        /// they were given. See <see cref="DowntownWavePosition"/> for why the Downtown view
        /// has no car overlay to place in the first place.
        /// </para>
        /// <para>
        /// A view with no background and no frames reports nothing: there is nothing to be
        /// foreign to, which is the empty slot 6 and also a null config.
        /// </para>
        /// </remarks>
        public static string[] ForeignFrames(NeighborhoodViewConfig config)
        {
            var own = ViewFolder(config);
            if (own == null) return new string[0];

            var foreign = new List<string>();
            foreach (var frame in DeclaredFrames(config))
            {
                var folder = FolderOf(frame);
                if (folder == null || !string.Equals(folder, own, StringComparison.OrdinalIgnoreCase))
                {
                    foreign.Add(frame);
                }
            }
            return foreign.ToArray();
        }

        /// <summary>
        /// True when every frame <paramref name="config"/> declares comes from the same folder
        /// as its own background - the condition under which its overlay layers can actually
        /// load. Vacuously true for a config that declares nothing, including a null one.
        /// </summary>
        public static bool OverlaysMatchView(NeighborhoodViewConfig config)
        {
            return ForeignFrames(config).Length == 0;
        }

        /// <summary>
        /// The layers a 1-based neighbourhood mode draws, with null entries dropped. Empty for a
        /// mode outside the table, a null table, and the deliberately empty slot 6.
        /// </summary>
        /// <remarks>
        /// The filtering the panel's own loop used to skip: it walked
        /// <c>config.FullImageAnimations</c> directly, so a null entry in the array would have
        /// thrown inside the layer constructor while the screen was still being built. Reading
        /// the layers through here means the guard lives in one place and is testable without a
        /// graphics device.
        /// </remarks>
        public static NeighborhoodImageAnim[] OverlaysForMode(NeighborhoodViewConfig[] configs, int mode)
        {
            var config = ConfigForMode(configs, mode);
            if (config?.FullImageAnimations == null) return new NeighborhoodImageAnim[0];

            var layers = new List<NeighborhoodImageAnim>();
            foreach (var layer in config.FullImageAnimations)
            {
                if (layer != null) layers.Add(layer);
            }
            return layers.ToArray();
        }
    }
}
