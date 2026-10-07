using System;
using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// One parsed entry out of a neighbourhood's lot-location STR table.
    /// </summary>
    /// <remarks>
    /// <see cref="X"/> and <see cref="Y"/> are the lot's <em>centre</em> on the 800x600 backdrop
    /// bitmap, in pixels - see <see cref="LotLocationParser"/>.
    /// </remarks>
    public struct LotLocation
    {
        /// <summary>The lot/house number (first field).</summary>
        public int Number;
        /// <summary>The x centre of the lot on the neighbourhood screen bitmap (second field).</summary>
        public int X;
        /// <summary>The y centre of the lot on the neighbourhood screen bitmap (third field).</summary>
        public int Y;

        public LotLocation(int number, int x, int y)
        {
            Number = number;
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Parses the comma-separated lot location strings stored in the neighbourhood's
    /// lot-location STR chunk (one string per line, "number,x,y").
    ///
    /// This used to live inline in <c>UINeighborhoodSelectionPanel.PopulateScreen</c>. It was
    /// lifted out so the exact skipping rules have a regression test: a malformed entry, a
    /// short entry, or lot 99 (a sentinel that must never get a clickable button) are all
    /// skipped without aborting the rest of the table.
    ///
    /// <para>
    /// <b>The x and y are the lot's centre in the 800x600 bitmap, not a corner.</b>
    /// <c>SimitoneNeighOBJExporter</c> treats them as a centre and so does
    /// <c>NeighbourhoodPixelTransform</c>, which is where the 3D world now gets its lot positions
    /// from. That is stated here, on the one place in the tree that decides what the two numbers
    /// mean, because a "position" that turns out to be a corner is a neighbourhood where every lot
    /// is half a lot too far up and left of where the player clicked.
    /// </para>
    /// </summary>
    public static class LotLocationParser
    {
        /// <summary>
        /// Parse a single "number,x,y" entry.
        /// </summary>
        /// <remarks>
        /// Semantics deliberately mirror the original inline code:
        /// <list type="bullet">
        /// <item>fewer than three comma-separated fields -> fail;</item>
        /// <item>only <see cref="string.TrimStart()"/> is applied to each field (trailing
        /// whitespace would already have been consumed by <see cref="int.TryParse"/>);</item>
        /// <item>the lot-99 check happens after the number is parsed but before x/y are parsed,
        /// so a 99 entry is rejected even if x/y are malformed.</item>
        /// </list>
        /// </remarks>
        public static bool TryParseLotLocation(string raw, out int number, out int x, out int y)
        {
            number = 0;
            x = 0;
            y = 0;

            if (raw == null) return false;

            var fields = raw.Split(',');
            if (fields.Length < 3) return false;
            if (!int.TryParse(fields[0].TrimStart(), out number)) return false;
            if (number == 99) return false;
            if (!int.TryParse(fields[1].TrimStart(), out x)) return false;
            if (!int.TryParse(fields[2].TrimStart(), out y)) return false;
            return true;
        }

        /// <summary>
        /// Parse a whole location table, preserving the original ordering and dropping every
        /// entry <see cref="TryParseLotLocation"/> rejects. Duplicate lot numbers are kept (the
        /// caller's dictionary assignment decides which position wins, exactly as before).
        /// </summary>
        public static List<LotLocation> ParseLotLocationTable(IEnumerable<string> rawLocations)
        {
            var result = new List<LotLocation>();
            if (rawLocations == null) return result;

            foreach (var raw in rawLocations)
            {
                if (TryParseLotLocation(raw, out var number, out var x, out var y))
                {
                    result.Add(new LotLocation(number, x, y));
                }
            }

            return result;
        }
    }
}
