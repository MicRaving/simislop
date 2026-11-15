using System;
using System.Globalization;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The cross-session half of travel-lot persistence: where a travel lot's save lives, who is
    /// allowed to claim it, and the small header that makes a stale slot recognisable.
    ///
    /// <para>
    /// A travel lot (Downtown 21-31, Vacation 40-49, Studio 81-89, Magic 90-99) is a copy of a
    /// template shipped with the game, so <see cref="LotPersistence.IsPersistableLot"/> forbids
    /// writing it back to its own <c>House{NN}.iff</c>. That leaves the lot with nowhere to
    /// persist, which used to mean "does not support saving on vacation". Instead the lot's
    /// <c>VMMarshal</c> is written to a private slot under the user's own Simitone folder:
    /// </para>
    ///
    /// <code>&lt;UserDir&gt;/TravelLots/House{NN}.fsov</code>
    ///
    /// <para>
    /// The file is deliberately <em>not</em> an IFF and deliberately lives outside the
    /// neighbourhood's <c>Houses</c> folder: nothing that walks the user's house files can pick
    /// it up, and nothing can mistake it for a real lot. It is written and read with the same
    /// stage/backup/commit contract as the home-lot save (<see cref="AtomicFileCommit"/>), so a
    /// crash mid-write cannot corrupt the previous slot.
    /// </para>
    ///
    /// <para>
    /// Precedence, stated once here because both halves of the flow depend on it: the in-session
    /// <c>TS1GameScreen.SessionLots</c> stash is checked <em>first</em> by the travel switch, so
    /// a lot that is being held in memory in this session is always resumed from memory; this
    /// on-disk slot is only consulted when no in-memory copy exists, i.e. on a first visit in a
    /// later session. Saving on a travel lot refreshes the slot but never touches the template.
    /// </para>
    ///
    /// Nothing in this class touches the filesystem, Content or the VM - it is path arithmetic,
    /// integer comparison and byte layout, all of it testable headlessly.
    /// </summary>
    public static class TravelSave
    {
        /// <summary>Folder under the user's Simitone directory that holds every travel slot.</summary>
        public const string SlotFolderName = "TravelLots";

        /// <summary>File extension of a travel slot. Matches the sim engine's own lot blob extension.</summary>
        public const string SlotExtension = ".fsov";

        /// <summary>
        /// File extension of the house file a slot shadows - the lot's own <c>House{NN}.iff</c>,
        /// which for a travel lot is a shipped template that is never written.
        /// </summary>
        public const string HouseFileExtension = ".iff";

        /// <summary>Filename stem of a slot, identical to the house file it shadows.</summary>
        public const string HouseFilePrefix = "House";

        /// <summary>
        /// Four ASCII bytes at offset 0 of every slot: "SLTT" (Simitone Lot TraveL).
        ///
        /// A slot is a bare binary blob with no IFF wrapper, so this is the only thing standing
        /// between a truncated file, a foreign file and a real slot. It is checked before the
        /// version and before the version is checked before the family, so garbage fails on the
        /// cheapest possible test.
        /// </summary>
        public const string Magic = "SLTT";

        /// <summary>
        /// Slot format version. Written as a 32-bit int at offset 4 and compared for exact
        /// equality on read: a slot written by a future (or older) build is ignored rather than
        /// fed to a <c>VMMarshal</c> that may not understand it.
        /// </summary>
        public const int FormatVersion = 1;

        /// <summary>Size of the fixed header: 4 magic bytes + int version + int family number.</summary>
        public const int HeaderLength = 12;

        /// <summary>
        /// Family number stored when the slot was written by a session with no active family.
        /// It is deliberately a value no real lot can have, so such a slot can never satisfy
        /// <see cref="ShouldRestoreSlot"/> against a real family.
        /// </summary>
        public const int UnknownFamily = int.MinValue;

        /// <summary>
        /// The four ASCII bytes <c>VMMarshal.Deserialize</c> requires at the start of its own
        /// stream ("FSOv"), which is the first thing the marshal payload of a slot must carry.
        /// </summary>
        /// <remarks>
        /// This exists because of a genuine trap in the marshal reader: when those four bytes are
        /// absent it <c>return</c>s silently, leaving a <c>VMMarshal</c> with a null context, null
        /// entity array and null platform state rather than throwing. Handing that object to
        /// <c>VM.Load</c> fails somewhere deep in the lot, long after the real cause. A slot is
        /// therefore checked for the preamble here, at the byte level, before anything is
        /// deserialised from it.
        /// </remarks>
        public const string MarshalMagic = "FSOv";

        /// <summary>ASCII encoding of <see cref="Magic"/>, laid out little-endian-safe as raw bytes.</summary>
        private static readonly byte[] MagicBytes = { (byte)'S', (byte)'L', (byte)'T', (byte)'T' };

        /// <summary>
        /// The filename stem a lot's house file and its travel slot share: <c>House05</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One owner for the name, because two places used to spell it: this class built the slot
        /// path from <see cref="HouseFilePrefix"/> plus an invariant <c>PadLeft(2, '0')</c>, while
        /// <c>SaveService.WriteTravelSlot</c>'s log line - which names the shipped template the
        /// save deliberately did <em>not</em> touch - rebuilt the same stem with a bare
        /// culture-sensitive <c>int.ToString()</c>. A number rendered on a machine whose culture
        /// substitutes digits would have put a file name in the log that does not exist on disk,
        /// and in a locale with a native <c>NumberFormatInfo</c> the two spellings can differ
        /// outright. The convention everywhere in this area is invariant
        /// (<see cref="SlotPathFor"/>, the house-file paths, the zoning lines), so the log line now
        /// asks this instead of formatting the number itself.
        /// </para>
        ///
        /// <para>
        /// Total by construction: it never throws and never clamps, because the padding is exactly
        /// what <c>TS1NeighbourhoodProvider.GetHousePath</c> does and a "helpful" clamp here would
        /// be a second naming rule. A lot number outside the two-digit range therefore renders as
        /// itself with no padding ("House100", "House-1") and a single-digit one is padded to two
        /// ("House05", and "House00" for lot 0) - the same strings the provider's own path builder
        /// would produce for them.
        /// </para>
        /// </remarks>
        /// <param name="houseId">The lot number.</param>
        public static string HouseFileStem(int houseId)
        {
            return HouseFilePrefix + houseId.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
        }

        /// <summary>
        /// The path of a travel lot's cross-session slot:
        /// <c>&lt;userDir&gt;/TravelLots/House{NN}.fsov</c>.
        /// </summary>
        /// <param name="userDir">The user's Simitone folder, <c>FSOEnvironment.UserDir</c>.
        /// Forward slashes are the norm there and backslashes are accepted.</param>
        /// <param name="houseId">The lot number, zero-padded to two digits exactly like the house
        /// files themselves (<c>GetHousePath</c> uses the same <c>PadLeft(2, '0')</c>, so lot 5 is
        /// "House05" on both sides).</param>
        /// <returns>
        /// The full slot path, or <c>null</c> when <paramref name="userDir"/> is null or empty -
        /// there is no sane place to write a slot in that case, and returning null lets the
        /// caller skip the save instead of throwing on a malformed string.
        /// </returns>
        public static string SlotPathFor(string userDir, int houseId)
        {
            if (string.IsNullOrEmpty(userDir)) return null;

            //UserDir arrives with forward slashes and usually a trailing one (".../Simitone/").
            //Normalise both so the joined path never gains a doubled separator and never trips
            //over a stray backslash.
            var dir = userDir.Replace('\\', '/').TrimEnd('/');
            return dir + "/" + SlotFolderName + "/" + HouseFileStem(houseId) + SlotExtension;
        }

        /// <summary>
        /// Whether a slot found on disk may be loaded into the session that is starting now.
        /// </summary>
        /// <remarks>
        /// The slot records the family it was saved for. Travel lots are shared destinations
        /// with one slot per lot, not per family, so a slot written by one family must never be
        /// handed to another - otherwise family B would start its vacation standing in family A's
        /// house, with A's Sims, A's budget and A's relationship state already loaded. Requiring
        /// an exact match on two non-null values means "no active family" also refuses, which is
        /// what keeps a simless (no-family) lot load off the slot path entirely.
        /// </remarks>
        /// <param name="slotFamilyNumber">Family number recorded in the slot's header.</param>
        /// <param name="activeFamilyNumber">House number of the family now being played.</param>
        /// <returns><c>true</c> only when both numbers are known and equal.</returns>
        public static bool ShouldRestoreSlot(int? slotFamilyNumber, int? activeFamilyNumber)
        {
            return slotFamilyNumber.HasValue
                && activeFamilyNumber.HasValue
                && slotFamilyNumber.Value == activeFamilyNumber.Value;
        }

        /// <summary>
        /// Whether the lot the player is standing on is a travel destination rather than the
        /// family's own home - the exact complement of
        /// <see cref="LotPersistence.IsPersistableLot"/>, and therefore the condition for
        /// "write to a slot, never to <c>House{NN}.iff</c>".
        /// </summary>
        /// <remarks>
        /// This is a negation, not a range check, so it stays true for Downtown, Vacation, Studio
        /// and Magic, for a neighbour's house, and for the unknown-family case. Callers that also
        /// need a family on both sides (i.e. anything that would actually restore a slot) must
        /// also call <see cref="ShouldRestoreSlot"/>; the two checks are complementary and neither
        /// subsumes the other.
        /// </remarks>
        public static bool IsTravelLot(int lotHouseId, int? activeFamilyHouseNumber)
        {
            return !LotPersistence.IsPersistableLot(lotHouseId, activeFamilyHouseNumber);
        }

        /// <summary>
        /// Recovers a lot number from a house path, so a lot can be keyed before it is loaded.
        /// </summary>
        /// <remarks>
        /// Loading a lot needs the slot decision <em>before</em> the lot exists, because
        /// <c>InitializeLot(string, bool)</c> has to choose between the slot and the template
        /// before it builds a VM. The VM's global 10 only becomes the lot's own number once the
        /// lot is loaded, so it cannot answer the question. The two callers that do know the
        /// number pass it in; this is the fallback for a path that is already a house path.
        /// It is deliberately strict - "House26" yields 26, while "House", "Houses", "House.iff"
        /// and "House26.iff.bak" (an <c>AtomicFileCommit</c> side file) all fail, because a
        /// wrong number here would load the wrong lot's state.
        /// </remarks>
        /// <param name="lotPath">A house or lot path, with either separator.</param>
        /// <param name="houseId">The recovered lot number, or 0 on failure.</param>
        /// <returns><c>true</c> when a plain numeric <c>House{NN}</c> stem was found.</returns>
        public static bool TryParseHouseIdFromPath(string lotPath, out int houseId)
        {
            houseId = 0;
            if (string.IsNullOrEmpty(lotPath)) return false;

            var name = lotPath;
            var cut = name.LastIndexOfAny(new[] { '/', '\\' });
            if (cut >= 0) name = name.Substring(cut + 1);

            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(0, dot);

            if (name.Length <= HouseFilePrefix.Length) return false;
            if (!name.StartsWith(HouseFilePrefix, StringComparison.OrdinalIgnoreCase)) return false;

            var digits = name.Substring(HouseFilePrefix.Length);
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] < '0' || digits[i] > '9') return false;
            }

            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out houseId);
        }

        /// <summary>
        /// Builds the exact bytes of a travel slot: the fixed header followed by an opaque
        /// <c>VMMarshal</c> payload, copied verbatim.
        /// </summary>
        /// <remarks>
        /// Layout, all little-endian (the same byte order <c>VMMarshal.SerializeInto</c> uses, so
        /// the whole file is one endianness):
        /// <code>
        ///   0  char[4]  "SLTT"            magic
        ///   4  int32    FormatVersion    bumped if the payload layout ever changes
        ///   8  int32    family number    UnknownFamily when there was no active family
        ///  12  byte[]   VMMarshal payload, the output of VMMarshal.SerializeInto
        /// </code>
        /// Keeping the marshal payload at a fixed offset means the reader seeks once and hands
        /// <c>VMMarshal.Deserialize</c> a reader already sitting on its own "FSOv" preamble.
        /// </remarks>
        /// <param name="familyNumber">Home lot of the family that saved the slot, or null for
        /// "unknown", which is stored as <see cref="UnknownFamily"/>.</param>
        /// <param name="marshalBytes">The serialised <c>VMMarshal</c>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="marshalBytes"/> is null.</exception>
        public static byte[] BuildSlot(int? familyNumber, byte[] marshalBytes)
        {
            if (marshalBytes == null) throw new ArgumentNullException(nameof(marshalBytes));

            var family = familyNumber ?? UnknownFamily;
            var slot = new byte[HeaderLength + marshalBytes.Length];

            Buffer.BlockCopy(MagicBytes, 0, slot, 0, MagicBytes.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(FormatVersion), 0, slot, MagicBytes.Length, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(family), 0, slot, MagicBytes.Length + 4, 4);
            Buffer.BlockCopy(marshalBytes, 0, slot, HeaderLength, marshalBytes.Length);

            return slot;
        }

        /// <summary>
        /// Reads and validates a slot's fixed header. The payload is not touched - the caller
        /// seeks past <see cref="HeaderLength"/> and deserialises the marshal itself.
        /// </summary>
        /// <remarks>
        /// All three failure modes are deliberately non-throwing and all three mean the same
        /// thing to the caller, "this is not a slot I wrote, load the template instead": a file
        /// that is too short to hold a header, a file whose first four bytes are not the magic
        /// (a truncated write, or some other binary that happens to live in the folder), and a
        /// file whose version is not the one this build understands.
        /// </remarks>
        /// <param name="data">The whole slot file.</param>
        /// <param name="version">The version read from the header, or 0 on failure.</param>
        /// <param name="familyNumber">The family recorded in the header, or
        /// <see cref="UnknownFamily"/> on failure.</param>
        /// <returns><c>true</c> when the file starts with a header this build can read.</returns>
        public static bool IsValidHeader(byte[] data, out int version, out int familyNumber)
        {
            version = 0;
            familyNumber = UnknownFamily;

            if (data == null || data.Length < HeaderLength) return false;

            for (int i = 0; i < MagicBytes.Length; i++)
            {
                if (data[i] != MagicBytes[i]) return false;
            }

            version = BitConverter.ToInt32(data, MagicBytes.Length);
            if (version != FormatVersion) return false;

            familyNumber = BitConverter.ToInt32(data, MagicBytes.Length + 4);
            return true;
        }

        /// <summary>
        /// Whether a slot's marshal payload starts with the preamble
        /// <c>VMMarshal.Deserialize</c> insists on, i.e. whether deserialising it can produce
        /// anything at all.
        /// </summary>
        /// <remarks>
        /// This is deliberately a cheap byte comparison rather than an attempted deserialisation.
        /// The marshal reader does not fail loudly on a wrong preamble - it returns and hands back
        /// a hollow object - so the caller has to establish this <em>before</em> it builds a
        /// <c>VMMarshal</c>, and the only way to do that without a VM is to look at the bytes.
        /// </remarks>
        /// <param name="data">The whole slot file.</param>
        /// <param name="offset">Where the marshal payload begins, i.e.
        /// <see cref="HeaderLength"/>.</param>
        /// <returns><c>true</c> when the four bytes at <paramref name="offset"/> are
        /// <see cref="MarshalMagic"/>, and false for null input, a negative offset, an offset
        /// that leaves fewer than four bytes, or a wrong preamble.</returns>
        public static bool HasMarshalPreamble(byte[] data, int offset)
        {
            if (data == null || offset < 0 || offset + 4 > data.Length) return false;

            for (int i = 0; i < 4; i++)
            {
                if (data[offset + i] != (byte)MarshalMagic[i]) return false;
            }

            return true;
        }
    }
}
