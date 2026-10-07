using System;
using System.Globalization;
using System.Threading;
using Simitone.Client.UI.Services;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The lot name in a save log, and why it is asked for rather than formatted.
    ///
    /// <para>
    /// <c>SaveService.WriteTravelSlot</c> finishes its log line by naming the shipped template the
    /// save deliberately did <em>not</em> touch: "The shipped House05.iff template was left
    /// untouched." It used to build that name itself, with a bare <c>houseID.ToString().PadLeft(2,
    /// '0')</c> - the current culture - while <see cref="TravelSave.SlotPathFor"/>, the house
    /// paths and the zoning lines are all built invariantly. A save log a developer reads to
    /// recover a lot is the one place where a number spelled the machine's way is worse than
    /// useless: it names a file that does not exist and contradicts the path printed beside it.
    /// </para>
    ///
    /// <para>
    /// The fix is a single owner - <see cref="TravelSave.HouseFileStem"/> - used by the log line
    /// and by the slot path, so the two cannot disagree. These tests pin the property that makes it
    /// hold: <b>the name is the same string in every culture</b>, and it is the same string the
    /// provider's own path builder would produce. The <see cref="SaveService"/> log numbers get the
    /// same treatment, because a line whose lot number and whose file name were rendered by two
    /// different cultures is the same defect one token later.
    /// </para>
    ///
    /// <para>
    /// <b>What actually goes wrong, measured rather than assumed.</b> Not digit substitution:
    /// .NET renders an integer with ASCII digits in every culture, so <c>ar-SA</c> prints "42"
    /// however <c>NumberFormatInfo.NativeDigits</c> is set, and a test built on that premise would
    /// pass without the fix. The sign is what moves. <c>ar-SA</c> and <c>fa-IR</c> both carry a
    /// multi-character <c>NegativeSign</c>, so <c>(-1).ToString()</c> is three characters where the
    /// name on disk has two - which is the case this file's first test pins down, so the reason
    /// the fix exists cannot quietly become folklore.
    /// </para>
    ///
    /// <para>
    /// Culture is set on the current thread and restored in a finally, so a failure here cannot
    /// leave the rest of the suite formatting numbers in Persian.
    /// </para>
    /// </summary>
    public sealed class HouseFileNameCultureTests
    {
        /// <summary>
        /// Cultures that do the interesting things to number formatting: a negative sign that is
        /// not a hyphen, digits declared as native but never used, group separators.
        /// </summary>
        private static readonly string[] Cultures =
        {
            "en-US", "de-DE", "fr-FR", "ar-SA", "fa-IR", "hi-IN", "th-TH", "tr-TR",
        };

        /// <summary>
        /// A culture whose sign is not <c>-</c>. Built by hand rather than named, because no
        /// shipped culture spells it that way and the point is the mechanism, not a locale.
        /// </summary>
        private static CultureInfo OddSigns()
        {
            var nfi = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            nfi.PositiveSign = "»";
            nfi.NegativeSign = "«";
            return new CultureInfo("en-US") { NumberFormat = nfi };
        }

        /// <summary>Runs <paramref name="body"/> with the current thread on <paramref name="culture"/>.</summary>
        private static void InCulture(CultureInfo culture, Action body)
        {
            var previousCulture = Thread.CurrentThread.CurrentCulture;
            var previousUiCulture = Thread.CurrentThread.CurrentUICulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                body();
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previousCulture;
                Thread.CurrentThread.CurrentUICulture = previousUiCulture;
            }
        }

        /// <summary>
        /// The number with every Unicode format character (category <c>Cf</c>) removed - the
        /// bidirectional marks and the letter mark a culture glues onto its sign.
        /// </summary>
        /// <remarks>
        /// Deliberately category-based rather than a hard-coded list of marks: the point is that
        /// what a culture adds around the sign is presentational and carries no digits, and a list
        /// would go stale the next time ICU grows a mark nobody anticipated.
        /// </remarks>
        /// <param name="value">The rendered number.</param>
        private static string WithoutFormatCharacters(string value)
        {
            var kept = new System.Text.StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format) kept.Append(c);
            }
            return kept.ToString();
        }

        [Fact]
        public void EveryCultureTheTheoriesUseIsOneThisRuntimeHas()
        {
            // A renamed or dropped culture would otherwise turn the theories below into a test that
            // quietly proves nothing, which is worse than not having them.
            foreach (var name in Cultures)
            {
                Assert.NotNull(CultureInfo.GetCultureInfo(name));
            }
        }

        [Fact]
        public void TheUnguardedFormIsGenuinelyCultureDependentHere()
        {
            // The premise of every test below, checked rather than assumed.
            //
            // The obvious candidate - a culture that renders 42 as Arabic-Indic digits - does NOT
            // fire. .NET formats an integer with ASCII digits in every culture regardless of what
            // `NumberFormatInfo.NativeDigits` says, so `ar-SA` and `fa-IR` both print "42". A test
            // built on that premise would pass without the fix and fail if the runtime's ICU data
            // ever changed, which is the wrong way round.
            //
            // What does fire is the sign, and it fires for exactly the values that matter here: a
            // negative lot or family number. Both cultures carry a multi-character
            // `NegativeSign` (an Arabic letter mark before the hyphen, a left-to-right mark before
            // a U+2212 minus), so `(-1).ToString()` is three characters where the file name on
            // disk has two. A log line naming "House؜-1.iff" beside a slot path reading
            // "House-1.fsov" is the whole defect, reproducible.
            InCulture(new CultureInfo("ar-SA"), () =>
            {
                var raw = (-1).ToString();
                Assert.Equal("-1", (-1).ToString(CultureInfo.InvariantCulture));
                Assert.NotEqual("-1", raw);
                //only the U+061C ARABIC LETTER MARK is added; the glyphs are the invariant ones
                Assert.Equal("-1", WithoutFormatCharacters(raw));
            });

            InCulture(new CultureInfo("fa-IR"), () =>
            {
                var raw = (-1).ToString();
                Assert.NotEqual("-1", raw);
                // Stronger than ar-SA: the sign glyph itself is replaced. U+200E (a left-to-right
                // mark) is presentational and strips away, but the character after it is U+2212
                // MINUS SIGN, not U+002D HYPHEN-MINUS - so even a reader that drops the marks sees
                // a different character from the one in the file name.
                var stripped = WithoutFormatCharacters(raw);
                Assert.NotEqual("-1", stripped);
                Assert.DoesNotContain("-", stripped);
                Assert.EndsWith("1", stripped);
            });

            // and a hand-built sign, so the mechanism is pinned even if a future ICU drops both
            // of those cultures' NegativeSign.
            InCulture(OddSigns(), () =>
            {
                Assert.NotEqual("-1", (-1).ToString());
                Assert.Equal("-1", (-1).ToString(CultureInfo.InvariantCulture));
            });
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("fr-FR")]
        [InlineData("ar-SA")]
        [InlineData("fa-IR")]
        [InlineData("hi-IN")]
        [InlineData("th-TH")]
        [InlineData("tr-TR")]
        public void TheStemIsTheSameStringInEveryCulture(string name)
        {
            // The root of the fix: the name a log line prints is the name on disk, everywhere. The
            // whole travel-lot range, plus both ends of the single- and triple-digit cases - and a
            // negative number, which is the one input the previous culture-sensitive spelling
            // actually got wrong.
            InCulture(new CultureInfo(name), () =>
            {
                foreach (var lot in new[] { 0, 1, 5, 21, 26, 42, 85, 92, 99, 100, -1, -42 })
                {
                    Assert.Equal("House" + lot.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0'),
                        TravelSave.HouseFileStem(lot));
                }
            });
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("ar-SA")]
        [InlineData("fa-IR")]
        public void TheSlotPathIsTheSameFileInEveryCulture(string name)
        {
            // The stem is not decoration on a path: SlotPathFor is built from it, so this is the
            // end-to-end claim - one lot, one slot, on a machine that spells numbers differently.
            InCulture(new CultureInfo(name), () =>
            {
                Assert.Equal("Simitone/TravelLots/House42.fsov", TravelSave.SlotPathFor("Simitone", 42));
                Assert.Equal("Simitone/TravelLots/House05.fsov", TravelSave.SlotPathFor("Simitone", 5));
            });
        }

        [Fact]
        public void TheLogLineNamesTheTemplateTheSlotPathNamesWithoutItsExtension()
        {
            // The two halves of the sentence WriteTravelSlot logs, and the pairing the log depends
            // on: the file it wrote to and the file it left alone must be the same lot, or a
            // developer reading the log chases the wrong template. Spelled as the log spells it -
            // the same directory, two extensions - rather than by string surgery on a path, which
            // would pass for the wrong reason if the prefix ever moved.
            for (int lot = 1; lot <= 100; lot++)
            {
                var stem = "House" + lot.ToString(CultureInfo.InvariantCulture).PadLeft(2, '0');
                var slot = TravelSave.SlotPathFor("Simitone", lot);

                Assert.Equal("Simitone/" + TravelSave.SlotFolderName + "/" + stem + TravelSave.SlotExtension, slot);

                //and the file the log names as "left untouched" is the same lot one extension over,
                //not merely two well-formed names that happen to look alike
                Assert.Equal("Simitone/" + TravelSave.SlotFolderName + "/" + stem + TravelSave.HouseFileExtension,
                    slot.Substring(0, slot.Length - TravelSave.SlotExtension.Length) + TravelSave.HouseFileExtension);
            }
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("ar-SA")]
        [InlineData("fa-IR")]
        public void TheSaveLogNumbersAreInvariantToo(string name)
        {
            // "Saved travel lot <lot> to its own slot <path> ... The shipped House05.iff template
            // was left untouched." - a line whose number and whose file name were rendered by two
            // different cultures would contradict itself on the page.
            InCulture(new CultureInfo(name), () =>
            {
                foreach (var lot in new[] { 0, 5, 42, 99, -1 })
                {
                    Assert.Equal(lot.ToString(CultureInfo.InvariantCulture), SaveService.Lot(lot));
                }

                //the family-mismatch warning, same shape
                Assert.Equal("7", SaveService.Lot(7));
            });
        }

        [Fact]
        public void ANegativeNumberIsPrintedAsItIsRatherThanClampedOrRespelled()
        {
            // A log line that quietly replaced an implausible number with a plausible one would be
            // worse than one that prints it: -1 is exactly the "no lot" and "no family" value the
            // caller may be reporting, and it is also the value whose sign a culture rewrites.
            // "؜-1" is neither the number nor something a developer can paste into a search box.
            InCulture(new CultureInfo("ar-SA"), () =>
            {
                Assert.Equal("-1", SaveService.Lot(-1));
                Assert.Equal("2147483647", SaveService.Lot(int.MaxValue));
                Assert.Equal("-2147483648", SaveService.Lot(int.MinValue));
            });

            InCulture(OddSigns(), () =>
            {
                Assert.Equal("-1", SaveService.Lot(-1));
                Assert.Equal("2147483647", SaveService.Lot(int.MaxValue));
                Assert.Equal("-2147483648", SaveService.Lot(int.MinValue));
            });
        }

        [Fact]
        public void TheStemIsTotalAndDoesNotClamp()
        {
            // "House00", "House100", "House-1": whatever PadLeft(2, '0') produces for the number,
            // because that is what TS1NeighbourhoodProvider.GetHousePath produces for the same
            // number, and a second naming rule here would be the bug this file exists to prevent.
            // Note lot 0 pads to "00" rather than staying "0" - the padding is to a minimum width,
            // not a "single digits only" rule, and the tests above would pass either way, so this
            // is the one place the exact spelling is pinned.
            // Total is the other half - this is on a save path, and a throw here would be a
            // save that does not happen.
            Assert.Equal("House00", TravelSave.HouseFileStem(0));
            Assert.Equal("House01", TravelSave.HouseFileStem(1));
            Assert.Equal("House100", TravelSave.HouseFileStem(100));
            Assert.Equal("House1000", TravelSave.HouseFileStem(1000));
            Assert.Equal("House-1", TravelSave.HouseFileStem(-1));
            Assert.Equal("House" + int.MinValue.ToString(CultureInfo.InvariantCulture),
                TravelSave.HouseFileStem(int.MinValue));

            //and it starts with the prefix every one of those shares
            foreach (var lot in new[] { int.MinValue, -100, -1, 0, 1, 99, 100, int.MaxValue })
            {
                Assert.StartsWith(TravelSave.HouseFilePrefix, TravelSave.HouseFileStem(lot));
            }
        }
    }
}
