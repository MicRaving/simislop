using FSO.SimAntics;
using FSO.SimAntics.Utils;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Simitone.Client.UI.Panels.Cheats
{
    /// <summary>
    /// What a cheat actually does, as far as the typed line is concerned.
    /// </summary>
    public enum CheatAction
    {
        /// <summary>The line named nothing this game knows how to do.</summary>
        Unknown = 0,

        /// <summary>Set the lot's clock to an hour of the day.</summary>
        SetHour,

        /// <summary>Fill every decaying need to the top.</summary>
        MaxMotives,

        /// <summary>Zero every skill.</summary>
        KillSkills,

        /// <summary>Zero every personality trait.</summary>
        KillTraits,

        /// <summary>Stop needs decaying, for one sim or for the household.</summary>
        FreezeNeeds,

        /// <summary>Print the list of cheats this game answers to.</summary>
        ListCheats
    }

    /// <summary>
    /// A line typed into the cheat box, worked out before anything touches the engine.
    /// </summary>
    /// <remarks>
    /// The parse is a pure function of the text, so the whole table - which keywords exist, what
    /// they take, and what is out of range - is testable with no VM, no lot and no graphics
    /// device. That is the part worth testing: a keyword that stops being recognised is not a
    /// crash, it is a cheat that silently does nothing, which is the same answer the game gives
    /// for a line it does not understand.
    /// </remarks>
    public sealed class CheatCommand
    {
        /// <summary>What the line asks for. <see cref="CheatAction.Unknown"/> if it asks for nothing.</summary>
        public CheatAction Action { get; }

        /// <summary>The keyword the line used, as it was typed.</summary>
        public string Keyword { get; }

        /// <summary>
        /// The keyword that was recognised, which is the one to put in the log: a player who
        /// typed <c>setallmotives</c> should not be told about <c>allmotives</c>.
        /// </summary>
        public string Canonical { get; }

        /// <summary>The number the line carried, when it carried one.</summary>
        public int? Number { get; }

        /// <summary>Whether the line said on or off, when it said either.</summary>
        public bool? OnOff { get; }

        /// <summary>How many times the cheat console's <c>!</c> repeated the line.</summary>
        public int Repetitions { get; }

        public CheatCommand(CheatAction action, string keyword, string canonical,
            int? number, bool? onOff, int repetitions)
        {
            Action = action;
            Keyword = keyword;
            Canonical = canonical;
            Number = number;
            OnOff = onOff;
            Repetitions = repetitions;
        }

        /// <summary>Whether the line named something the game can do.</summary>
        public bool IsKnown => Action != CheatAction.Unknown;

        /// <summary>
        /// Whether the line asked for the cheat to be switched off. A cheat with an on/off
        /// argument and none given is on, which is the shape the money cheats have always had.
        /// </summary>
        public bool WantsOff => OnOff == false;
    }

    /// <summary>
    /// The cheat keywords Simitone answers to, and the parse of a typed line.
    ///
    /// <para>
    /// <b>Why this is a table and not a switch.</b> The two halves of a cheat - the word the
    /// player types and the thing the engine is asked to do - had been in two places (a
    /// dictionary of keywords and a <c>VMCheatContext</c> enum) with nothing tying them together,
    /// so a keyword could exist that no handler did. Here a keyword and its
    /// <see cref="CheatAction"/> are one entry, and <see cref="CheatRunner"/> is a switch over
    /// that enum, which is what turns "the keyword is accepted" into a compile error rather than
    /// a line that does nothing.
    /// </para>
    ///
    /// <para>
    /// The money cheats and <c>moveobjects</c> stay where they were. They are not here because
    /// they work: they go out as a <see cref="VMCheatContext"/> over the VM's own net command, and
    /// the VM - not the client - is what executes them. <see cref="NetCheats"/> holds that
    /// keyword table, so the two halves of the console live in one file and the cheat list below
    /// can name all of them.
    /// </para>
    ///
    /// <para>
    /// Keywords are matched without regard to case, and the repetition marks the console
    /// understands (<c>!</c> to repeat, <c>;</c> to stack) are stripped before anything else,
    /// exactly as <c>WeatherCommandParser</c> strips them.
    /// </para>
    /// </summary>
    public static class CheatCommands
    {
        /// <summary>The word that prints the list of cheats.</summary>
        public const string ListKeyword = "cheats";

        /// <summary>
        /// The lowest hour <see cref="CheatAction.SetHour"/> accepts, and the value the lot's
        /// clock starts on.
        /// </summary>
        public const int MinHour = 0;

        /// <summary>The highest hour <see cref="CheatAction.SetHour"/> accepts.</summary>
        public const int MaxHour = 23;

        /// <summary>
        /// The keywords that go out to the VM as a <see cref="VMCheatContext"/>, and the cheat
        /// type each one asks the VM for.
        /// </summary>
        /// <remarks>
        /// Case-insensitive, because a cheat is typed by hand and the one that matters most
        /// (<c>moveobjects on</c>) is two words that people write both ways - which also means
        /// <c>givemoney</c> and <c>giveMoney</c> are one entry rather than two, and adding both
        /// would throw at type-initialisation. <c>move_objects</c> is the one genuine alias: it
        /// is the spelling most cheat lists print, and it differs in punctuation, which case
        /// folding does not reach.
        /// </remarks>
        public static readonly IReadOnlyDictionary<string, VMCheatContext.VMCheatType> NetCheats =
            new Dictionary<string, VMCheatContext.VMCheatType>(StringComparer.OrdinalIgnoreCase)
            {
                { "moveobjects", VMCheatContext.VMCheatType.MoveObjects },
                { "move_objects", VMCheatContext.VMCheatType.MoveObjects },
                { "motherlode", VMCheatContext.VMCheatType.Budget },
                { "klapaucius", VMCheatContext.VMCheatType.Budget },
                { "rosebud", VMCheatContext.VMCheatType.Budget },
                { "giveMoney", VMCheatContext.VMCheatType.Budget }
            };

        /// <summary>
        /// The keywords this game runs itself; the help list and the parser both read it, so a
        /// cheat cannot be playable but undocumented or documented but unplayable. Declared in
        /// the order the help list reads them: keyword, the action it runs, and what it does.
        /// A null summary marks an alias, which is playable but not listed - a list with
        /// three spellings of one cheat is a list that makes the surface look bigger than it is.
        /// </summary>
        private static readonly (string Keyword, CheatAction Action, string Summary)[] Definitions =
        {
            ("set_hour", CheatAction.SetHour, "Set the clock to an hour of the day (0-23)"),
            ("sethour", CheatAction.SetHour, null),      // alias
            ("sethours", CheatAction.SetHour, null),     // alias
            ("setallmotives", CheatAction.MaxMotives, "Fill every Sim's needs to the top"),
            ("allmotives", CheatAction.MaxMotives, null),// alias
            ("killskills", CheatAction.KillSkills, "Set every Sim's skills back to nothing"),
            ("killtraits", CheatAction.KillTraits, "Set every Sim's personality traits back to nothing"),
            ("freezeneeds", CheatAction.FreezeNeeds, "Stop needs decaying [on|off]"),
            ("freeze_needs", CheatAction.FreezeNeeds, null), // alias
            (ListKeyword, CheatAction.ListCheats, "Show this list")
        };

        /// <summary>
        /// The cheats that are worth printing, in the order they are listed: one entry per
        /// action, with the first keyword that reaches it.
        /// </summary>
        public static IEnumerable<(string Keyword, CheatAction Action, string Summary)> Documented =>
            Definitions.Where(x => x.Summary != null);

        /// <summary>
        /// Works out what a typed line asks for.
        /// </summary>
        /// <remarks>
        /// A line that is not a cheat at all comes back as
        /// <see cref="CheatAction.Unknown"/> rather than as an exception, because the caller
        /// answers an unrecognised line with the error sound and then falls through to the VM's
        /// own cheat path - and a line for the VM is a perfectly ordinary thing to type.
        /// </remarks>
        /// <param name="line">The typed line, repetition marks and all.</param>
        public static CheatCommand Parse(string line)
        {
            var parts = Tokenize(line);
            if (parts.Count == 0) return Unknown(line);

            var keyword = parts[0];
            var match = Definitions.FirstOrDefault(x => string.Equals(x.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
            if (match.Keyword == null) return Unknown(line);

            // The canonical spelling is the first keyword that reaches this action, which is not
            // the alias the player happened to type: the help list and the log both name the
            // cheat, and naming it `sethour` when it is listed as `set_hour` reads as a typo.
            var canonical = Definitions.First(x => x.Action == match.Action).Keyword;

            var argument = parts.Count > 1 ? parts[1] : null;
            var repetitions = Repetitions(line);
            switch (match.Action)
            {
                case CheatAction.SetHour:
                    if (!int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour))
                        return Unknown(line);
                    // Out of range is "did not do anything" rather than "clamp": a player who
                    // asked for hour 30 wants to know, and hour 23 is not what they typed.
                    if (hour < MinHour || hour > MaxHour) return Unknown(line);
                    return new CheatCommand(match.Action, keyword, canonical, hour, null, repetitions);

                case CheatAction.FreezeNeeds:
                    // No argument means on, which is how the money cheats have always read.
                    if (argument == null)
                        return new CheatCommand(match.Action, keyword, canonical, null, true, repetitions);
                    if (IsOn(argument)) return new CheatCommand(match.Action, keyword, canonical, null, true, repetitions);
                    if (IsOff(argument)) return new CheatCommand(match.Action, keyword, canonical, null, false, repetitions);
                    return Unknown(line);

                default:
                    // A cheat that takes nothing and was given something is a line the game does
                    // not understand, for the same reason the hour is not clamped.
                    if (argument != null) return Unknown(line);
                    return new CheatCommand(match.Action, keyword, canonical, null, null, repetitions);
            }
        }

        /// <summary>Whether the word is one the console answers with "on".</summary>
        public static bool IsOn(string word) =>
            string.Equals(word, "on", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether the word is one the console answers with "off".</summary>
        public static bool IsOff(string word) =>
            string.Equals(word, "off", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// How many times the line was repeated with the console's <c>!;</c> pairs.
        /// </summary>
        /// <remarks>
        /// The console's own rule, which is stricter than it first looks: retail TS1's repeat
        /// syntax is a <c>!</c> and a <c>;</c> <em>together</em> - <c>rosebud;!;!;</c> is
        /// <c>rosebud</c> three more times - and the count is only taken when the two marks
        /// appear equally often. A line with a stray <c>!</c> and no <c>;</c> is a half-typed
        /// one, and the money cheats' "silent when unsure" answer is to run the line once, which
        /// is what returning zero does.
        /// </remarks>
        /// <param name="line">The typed line.</param>
        public static int Repetitions(string line)
        {
            if (string.IsNullOrEmpty(line)) return 0;
            var bangs = 0;
            var semis = 0;
            foreach (var c in line)
            {
                if (c == '!') bangs++;
                else if (c == ';') semis++;
            }
            return bangs == semis ? bangs : 0;
        }

        /// <summary>
        /// The list of cheats, as the cheat box shows it: the header the box is captioned with,
        /// then one line per cheat, then the key the box is opened with.
        /// </summary>
        public static string HelpText()
        {
            var text = new StringBuilder();
            text.AppendLine(SimitoneStrings.Get("980", "Cheats"));
            foreach (var (keyword, action, summary) in Documented)
            {
                text.Append(keyword);
                text.Append(" - ");
                text.AppendLine(summary);
            }
            // The money and moveobjects cheats are the VM's, but they are still cheats this
            // console answers to, so they belong in the list the player reads.
            foreach (var (keyword, summary) in new (string, string)[]
            {
                ("motherlode", SimitoneStrings.Get("981", "Add §1,000,000")),
                ("klapaucius", SimitoneStrings.Get("982", "Add §1,000")),
                ("rosebud", SimitoneStrings.Get("982", "Add §1,000")),
                ("moveobjects on|off", SimitoneStrings.Get("983", "Let objects be picked up and moved")),
                ("giveMoney <n>", SimitoneStrings.Get("984", "Add §n"))
            })
            {
                text.Append(keyword);
                text.Append(" - ");
                text.AppendLine(summary);
            }
            text.Append(SimitoneStrings.Get("985",
                "Press Ctrl+Alt+Shift+C to close this box, and ! or ; to repeat a cheat."));
            return text.ToString();
        }

        private static CheatCommand Unknown(string line) =>
            new(CheatAction.Unknown, line, null, null, null, Repetitions(line));

        /// <summary>
        /// Splits a typed line into lower-case words with the repetition marks taken out.
        /// Whitespace of any kind separates, because a line typed on a soft keyboard carries
        /// tabs.
        /// </summary>
        private static List<string> Tokenize(string line)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(line)) return parts;
            foreach (var raw in line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw.Replace("!", string.Empty).Replace(";", string.Empty).ToLowerInvariant();
                if (word.Length > 0) parts.Add(word);
            }
            return parts;
        }
    }
}
