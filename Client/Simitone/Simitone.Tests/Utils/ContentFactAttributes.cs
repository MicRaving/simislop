using System;
using System.IO;
using Xunit;

namespace Simitone.Tests
{
    /// <summary>
    /// Whether the game's Content folder was copied next to the test assembly. It is gitignored
    /// (Client/Simitone/Simitone.Client/Content/), so a fresh checkout such as the CI runner has
    /// none of it, and tests that assert on shipped files cannot run there.
    /// </summary>
    internal static class ShippedContent
    {
        public const string SkipReason = "The game's Content folder is not present (it is not tracked in git); run locally to exercise this test.";

        public static bool Present =>
            Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Content", "UI", "uitext", "english.dir"));
    }

    /// <summary>A [Fact] that is skipped, not failed, when the shipped Content folder is absent.</summary>
    public sealed class ContentFactAttribute : FactAttribute
    {
        public ContentFactAttribute()
        {
            if (!ShippedContent.Present) Skip = ShippedContent.SkipReason;
        }
    }

    /// <summary>A [Theory] that is skipped, not failed, when the shipped Content folder is absent.</summary>
    public sealed class ContentTheoryAttribute : TheoryAttribute
    {
        public ContentTheoryAttribute()
        {
            if (!ShippedContent.Present) Skip = ShippedContent.SkipReason;
        }
    }
}
