using System;
using System.Globalization;
using System.Threading;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The culture coupling in <see cref="LotZoning"/>'s read/write pair.
    ///
    /// <para>
    /// A rezone is only a rezone if the line Simitone writes is the line the provider reads back
    /// the same way. The provider parses with <c>short.Parse</c>, which is the <em>current</em>
    /// culture, so <see cref="LotZoning.TryParse"/> is too - deliberately, and with the reason
    /// written down on both sides of the pair. The trap the review named is that the pair is
    /// correct only for as long as both halves stay on the culture the provider uses, which is a
    /// coupling nothing enforced.
    /// </para>
    ///
    /// <para>
    /// These tests settle whether that coupling can fire, rather than asserting that it cannot.
    /// They pin the claim the fix rests on: <b>the round trip is culture-independent</b>. .NET
    /// formats an integer with ASCII digits and no group separator in every culture, so the line
    /// <see cref="LotZoning.Format"/> writes is the same bytes everywhere, and the only way a
    /// non-invariant culture can change the answer is a hand-edited line carrying a sign that
    /// culture spells differently. The last two tests are that boundary, recorded as a boundary
    /// rather than left as folklore.
    /// </para>
    ///
    /// <para>
    /// Culture is set on the current thread rather than the process and restored in a finally, so
    /// a failure here cannot leave the rest of the suite formatting numbers in Arabic.
    /// </para>
    /// </summary>
    public sealed class LotZoningCultureTests
    {
        /// <summary>
        /// Cultures that do the interesting things to number formatting: digit substitution,
        /// different group separators, a negative sign that is not a hyphen, and calendars whose
        /// digits are not Western.
        /// </summary>
        private static readonly string[] Cultures =
        {
            "en-US", "de-DE", "fr-FR", "ar-SA", "fa-IR", "hi-IN", "th-TH", "tr-TR",
        };

        /// <summary>
        /// A culture whose sign is not <c>+</c>. Built by hand rather than named, because no
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

        [Fact]
        public void EveryCultureTheTheoriesUseIsOneThisRuntimeHas()
        {
            // A renamed or dropped culture would otherwise turn the theories below into a test
            // that quietly proves nothing, which is worse than not having them.
            foreach (var name in Cultures)
            {
                Assert.NotNull(CultureInfo.GetCultureInfo(name));
            }
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("de-DE")]
        [InlineData("ar-SA")]
        [InlineData("fa-IR")]
        [InlineData("th-TH")]
        public void TheWrittenLineIsTheSameBytesInEveryCulture(string name)
        {
            // The whole reason the round trip survives a locale: nothing about the *written* half
            // of the pair can depend on it. A change here - a format string, a group separator -
            // is the change that would make a rezone write a line the provider reads differently.
            InCulture(new CultureInfo(name), () =>
            {
                Assert.Equal("50, residential", LotZoning.Format(50, LotZoning.ZoneResidential));
                Assert.Equal("54, community", LotZoning.Format(54, LotZoning.ZoneCommunity));
                //and both ends of the lot range, which is where a group separator would show up
                Assert.Equal("1, residential", LotZoning.Format(1, LotZoning.ZoneResidential));
                Assert.Equal("130, community", LotZoning.Format(130, LotZoning.ZoneCommunity));
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
        public void ARezoneLineSurvivesTheRoundTripInEveryCulture(string name)
        {
            // The pair, end to end, under each culture: what Format writes is what TryParse reads
            // back as the same lot in the same zone. If this ever fails, the write and the read
            // have drifted onto different cultures and a player on that machine would get a
            // residential lot where they asked for community.
            InCulture(new CultureInfo(name), () =>
            {
                foreach (var lot in new[] { 1, 2, 50, 54, 89, 90, 130 })
                {
                    foreach (var zone in new[] { LotZoning.ZoneResidential, LotZoning.ZoneCommunity })
                    {
                        var line = LotZoning.Format(lot, zone);
                        Assert.True(LotZoning.TryParse(line, out var parsedLot, out var parsedZone),
                            "the provider would not have read '" + line + "' in " + name);
                        Assert.Equal(lot, (int)parsedLot);
                        Assert.Equal(zone, parsedZone);
                        Assert.True(LotZoning.IsZoningEntry(line));
                        Assert.Equal(zone, LotZoning.EffectiveZone(new[] { line }, lot));
                    }
                }
            });
        }

        [Fact]
        public void TheReaderFollowsTheProvidersCultureRatherThanInvariants()
        {
            // The one place the two really can disagree, recorded so nobody "fixes" it by making
            // the reader invariant on its own: a sign spelled the way this culture spells it is a
            // line the provider can read, and an invariant reader would refuse it. Refusing is the
            // documented, safe answer (the rewrite leaves an unreadable line exactly as it found
            // it) - but the provider would not have refused, which is the whole reason the reader
            // is not invariant.
            InCulture(new CultureInfo("en-US"), () =>
            {
                Assert.True(LotZoning.TryParse("+54, community", out var plusLot, out var plusZone));
                Assert.Equal(54, (int)plusLot);
                Assert.Equal(LotZoning.ZoneCommunity, plusZone);

                Assert.True(LotZoning.TryParse("-1, residential", out var negLot, out _));
                Assert.Equal(-1, (int)negLot);
            });

            InCulture(OddSigns(), () =>
            {
                //invariant-shaped digits, so this parses exactly as it would anywhere
                Assert.True(LotZoning.TryParse("54, community", out var lot, out var zone));
                Assert.Equal(54, (int)lot);
                Assert.Equal(LotZoning.ZoneCommunity, zone);

                //and the culture's own sign is accepted, which an invariant reader would not do
                Assert.True(LotZoning.TryParse("»54, community", out var signedLot, out _));
                Assert.Equal(54, (int)signedLot);
            });
        }

        [Fact]
        public void AnUnreadableLineIsStillLeftAlone()
        {
            // The tolerance that comes with mirroring the provider: a line the provider would have
            // thrown on is not a line to overwrite. Culture is part of "the provider can read it",
            // so this is the same rule under a locale that changes what a sign looks like.
            InCulture(OddSigns(), () =>
            {
                Assert.False(LotZoning.TryParse("nonsense, residential", out _, out _));
                Assert.False(LotZoning.TryParse("50 residential", out _, out _));
                Assert.False(LotZoning.TryParse("50", out _, out _));
                Assert.False(LotZoning.TryParse(null, out _, out _));
                Assert.False(LotZoning.TryParse("", out _, out _));

                //and the decision that depends on it: a line that cannot be read is not a zoning
                //entry, so a rezone will not pick it to rewrite
                Assert.False(LotZoning.IsZoningEntry("nonsense, residential"));

                //while the camera-coordinate line the file also carries stays a non-entry in any
                //culture, which is the shadowing rule the rewrite depends on
                Assert.False(LotZoning.IsZoningEntry("2, 636, 102"));
            });
        }
    }
}
