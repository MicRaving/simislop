using FSO.LotView.Model;
using Simitone.Client;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the cheat console's <c>weather</c> command and the packed value it produces.
    ///
    /// <para>
    /// <b>Why this is worth testing at all.</b> The value handed to
    /// <c>WeatherController.SetWeather</c> is a single short holding four separate facts - set by
    /// hand, what is falling, whether it is a storm, and how hard - and every one of them is
    /// read by the renderer as a bit at a fixed offset. A command that gets one bit wrong does
    /// not fail loudly: it silently snows when asked for rain, or leaves the lot on automatic
    /// weather, and the only symptom is that the console seemed to do nothing. The bit layout
    /// lives in exactly one method (<see cref="WeatherCommandParser.Encode"/>), so these tests
    /// pin that method against the layout the engine decodes.
    /// </para>
    ///
    /// <para>
    /// The decode helpers are tested against the same table, because they are what makes the
    /// layout readable in the log line and in a failing assertion - a decoder that disagreed with
    /// the encoder would make both lie.
    /// </para>
    ///
    /// <para>
    /// No VM, no blueprint, no audio device, no graphics device: the parser is a pure function of
    /// the typed line.
    /// </para>
    /// </summary>
    public sealed class WeatherCommandParserTests
    {
        // ------------------------------------------------------------------------- bit layout

        /// <summary>
        /// The layout the engine decodes, written out: bit 8 = set by hand, bits 9-10 = type
        /// (0 rain, 1 snow, 2 hail), bit 11 = thunder, bits 0-7 = intensity percent.
        /// </summary>
        [Theory]
        //rain, 50%: 0x100 | 50
        [InlineData(WeatherType.Rain, false, 50, 0x0132)]
        //rain, 0%: set by hand with nothing falling
        [InlineData(WeatherType.Rain, false, 0, 0x0100)]
        //snow, 100%: 0x100 | (1 << 9) | 100
        [InlineData(WeatherType.Snow, false, 100, 0x0364)]
        //hail, 50%: 0x100 | (2 << 9) | 50
        [InlineData(WeatherType.Hail, false, 50, 0x0532)]
        //rain storm, 75%: 0x100 | (1 << 11) | 75
        [InlineData(WeatherType.Rain, true, 75, 0x094B)]
        public void Encode_ProducesThePackedValueTheEngineReads(WeatherType type, bool thunder, int intensity, int expected)
        {
            Assert.Equal((short)expected, WeatherCommandParser.Encode(type, thunder, intensity));
        }

        [Theory]
        [InlineData(150, 100)]  // the low byte is read as a percentage, so this has to clamp
        [InlineData(1000, 100)]
        [InlineData(-20, 0)]
        [InlineData(int.MinValue, 0)]
        public void Encode_ClampsIntensityIntoTheByteTheEngineReads(int intensity, int expected)
        {
            //unclamped, a strength over 100 would spill into the type bits and change the weather.
            var data = WeatherCommandParser.Encode(WeatherType.Rain, false, intensity);
            Assert.Equal(expected, data & WeatherCommandParser.IntensityMask);
            Assert.Equal(WeatherType.Rain, WeatherCommandParser.TypeOf(data));
        }

        [Theory]
        [InlineData(WeatherType.Rain, false, 0)]
        [InlineData(WeatherType.Rain, false, 37)]
        [InlineData(WeatherType.Snow, true, 61)]
        [InlineData(WeatherType.Hail, false, 88)]
        [InlineData(WeatherType.Snow, false, 100)]
        public void Decode_RoundTripsWhatEncodeWrote(WeatherType type, bool thunder, int intensity)
        {
            //the decoders are what describe() and the log lines read, so a disagreement here
            //would make a correctly-set weather print as something else.
            var data = WeatherCommandParser.Encode(type, thunder, intensity);
            Assert.Equal(type, WeatherCommandParser.TypeOf(data));
            Assert.Equal(thunder, WeatherCommandParser.IsThunder(data));
            Assert.Equal(intensity / 100f, WeatherCommandParser.IntensityOf(data));
            Assert.True(WeatherCommandParser.IsManual(data));
        }

        [Fact]
        public void Decode_OfZero_IsTheEngineAutomaticWeather()
        {
            //"weather auto" writes a plain zero, which is exactly what a lot reads as "derive the
            //weather from the time of day".
            Assert.False(WeatherCommandParser.IsManual(0));
            Assert.Equal(WeatherType.Rain, WeatherCommandParser.TypeOf(0));
            Assert.False(WeatherCommandParser.IsThunder(0));
            Assert.Equal(0f, WeatherCommandParser.IntensityOf(0));
        }

        [Theory]
        [InlineData(0, "automatic")]
        [InlineData(0x0132, "rain at 50%")]
        [InlineData(0x094B, "rain storm at 75%")]
        [InlineData(0x0364, "snow at 100%")]
        [InlineData(0x0532, "hail at 50%")]
        public void Describe_ReadsBackAPackedValue(int packed, string expected)
        {
            //this string is what ends up in the log when a weather is set, so "rain storm" rather
            //than "rain" is the difference between a useful log line and a confusing one.
            Assert.Equal(expected, WeatherCommandParser.Describe((short)packed));
        }

        // ----------------------------------------------------------------- is it a weather command

        [Theory]
        [InlineData("weather rain")]
        [InlineData("Weather rain")]
        [InlineData("WEATHER")]
        [InlineData("  weather   snow  20 ")]
        [InlineData("weather!! rain!!")]
        [InlineData("weather\tsnow")]
        public void IsWeatherCommand_AcceptsTheCommand(string command)
        {
            Assert.True(WeatherCommandParser.IsWeatherCommand(command));
        }

        [Theory]
        [InlineData("motherlode")]
        [InlineData("moveobjects 5")]
        [InlineData("weatherly rain")]   // only the first word has to be "weather"
        [InlineData("weath")]
        [InlineData("")]
        [InlineData(null)]
        public void IsWeatherCommand_RejectsEverythingElse(string command)
        {
            //the cheat box routes on this, so a false positive would take a real cheat away from
            //the VM and answer it as a weather command instead.
            Assert.False(WeatherCommandParser.IsWeatherCommand(command));
        }

        // ------------------------------------------------------------------------- the commands

        [Fact]
        public void TryParse_RainWithNoStrengthUsesTheDefault()
        {
            Assert.True(WeatherCommandParser.TryParse("weather rain", out short data));
            Assert.Equal(WeatherCommandParser.Encode(WeatherType.Rain, false, WeatherCommandParser.DefaultIntensity), data);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(50)]
        [InlineData(100)]
        public void TryParse_RainReadsTheStrengthGiven(int intensity)
        {
            Assert.True(WeatherCommandParser.TryParse("weather rain " + intensity, out short data));
            Assert.Equal(WeatherCommandParser.Encode(WeatherType.Rain, false, intensity), data);
        }

        [Fact]
        public void TryParse_StormWithNoStrengthIsLouderThanTheOtherTypes()
        {
            //a storm is the one type the player wants to hear as well as see, so it gets its own
            //default rather than sharing the plain 50.
            Assert.True(WeatherCommandParser.TryParse("weather storm", out short data));
            Assert.Equal(WeatherCommandParser.Encode(WeatherType.Rain, true, WeatherCommandParser.DefaultStormIntensity), data);

            Assert.True(WeatherCommandParser.TryParse("weather thunder", out short thunderData));
            Assert.Equal(data, thunderData);
        }

        [Fact]
        public void TryParse_StormWithAStrengthGivenUsesThatStrength()
        {
            Assert.True(WeatherCommandParser.TryParse("weather storm 20", out short data));
            Assert.Equal(WeatherCommandParser.Encode(WeatherType.Rain, true, 20), data);
        }

        [Theory]
        [InlineData("snow", WeatherType.Snow)]
        [InlineData("hail", WeatherType.Hail)]
        [InlineData("rain", WeatherType.Rain)]
        public void TryParse_ReadsEachType(string type, WeatherType expected)
        {
            Assert.True(WeatherCommandParser.TryParse("weather " + type, out short data));
            Assert.Equal(expected, WeatherCommandParser.TypeOf(data));
        }

        [Fact]
        public void TryParse_ClearIsManualWeatherWithNothingFalling()
        {
            //"clear" is not a separate engine state: it is a set-by-hand weather at zero
            //strength, which is what stops the particles and the rain loop.
            Assert.True(WeatherCommandParser.TryParse("weather clear", out short data));
            Assert.True(WeatherCommandParser.IsManual(data));
            Assert.Equal(0, data & WeatherCommandParser.IntensityMask);
        }

        [Fact]
        public void TryParse_AutoClearsTheSetByHandBit()
        {
            Assert.True(WeatherCommandParser.TryParse("weather auto", out short data));
            Assert.Equal(0, data);
        }

        [Fact]
        public void TryParse_IsCaseInsensitiveAndIgnoresExtraSpacing()
        {
            Assert.True(WeatherCommandParser.TryParse("  WeAtHeR   SnOw   20 ", out short data));
            Assert.Equal(WeatherCommandParser.Encode(WeatherType.Snow, false, 20), data);
        }

        [Fact]
        public void TryParse_IgnoresTheRepetitionMarks()
        {
            //the console accepts "cheat!!" style repetition; repeating a weather change is
            //meaningless, but a line carrying the marks should still work rather than being
            //answered with the "not a command" sound.
            Assert.True(WeatherCommandParser.TryParse("weather!! rain!! 30", out short data));
            Assert.Equal(WeatherCommandParser.Encode(WeatherType.Rain, false, 30), data);
        }

        [Fact]
        public void TryParse_FallsBackToTheDefaultWhenTheStrengthIsNotANumber()
        {
            //"weather rain loud" is a mistyped strength, not an unknown command: the default is a
            //better answer than refusing to set anything.
            Assert.True(WeatherCommandParser.TryParse("weather rain loud", out short data));
            Assert.Equal(WeatherCommandParser.Encode(WeatherType.Rain, false, WeatherCommandParser.DefaultIntensity), data);
        }

        [Fact]
        public void TryParse_ClampsAStrengthOutsideThePercentageRange()
        {
            Assert.True(WeatherCommandParser.TryParse("weather rain 150", out short over));
            Assert.Equal(100, over & WeatherCommandParser.IntensityMask);

            Assert.True(WeatherCommandParser.TryParse("weather rain -5", out short under));
            Assert.Equal(0, under & WeatherCommandParser.IntensityMask);
        }

        [Theory]
        [InlineData("weather")]           // no type at all
        [InlineData("weather sideways")]  // not a type anyone has
        [InlineData("rain 50")]           // not a weather command
        [InlineData("")]
        [InlineData(null)]
        public void TryParse_RejectsALineItCannotUse(string command)
        {
            //every one of these gets the same "not a command" sound, so the parser is the only
            //thing that has to tell them apart from a good one.
            Assert.False(WeatherCommandParser.TryParse(command, out short data));
            Assert.Equal(0, data);
        }

        [Fact]
        public void TryParse_TrailingWordsAreIgnoredRatherThanRejected()
        {
            //"weather snow 20 please" sets snow at 20%. Refusing it would be pedantic, and the
            //console has no way to say "that was not what I meant" other than the error sound.
            Assert.True(WeatherCommandParser.TryParse("weather snow 20 please", out short data));
            Assert.Equal(WeatherCommandParser.Encode(WeatherType.Snow, false, 20), data);
        }
    }
}
