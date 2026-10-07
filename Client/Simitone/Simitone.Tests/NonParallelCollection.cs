using Xunit;

namespace Simitone.Tests
{
    /// <summary>
    /// A collection whose tests never run alongside anything else.
    /// </summary>
    ///
    /// <para>
    /// <b>For tests that assert on what lands in <c>&lt;UserDir&gt;/logs/simitone.log</c>.</b>
    /// <c>SimitoneLog</c> resolves its path from <c>FSOEnvironment.UserDir</c>, which is a
    /// process-global: a test redirects it at a temp directory and then counts the lines it
    /// finds there, but any <em>other</em> test whose code under test logs will write into
    /// whichever directory the redirect currently points at. That makes line counts a race
    /// between test classes rather than a property of the logger.
    /// </para>
    ///
    /// <para>
    /// Several services log (save, neighbourhood, catalogue, update check, weather), so this
    /// is not hypothetical - it has shown up as an intermittent
    /// <c>Log_IsRotatedOnceItReachesTwoMebibytes</c> failure where the freshly rotated log
    /// held four lines instead of one. Serialising is the only fix that does not involve
    /// giving the logger a second, injectable sink purely so tests can be isolated.
    /// </para>
    ///
    /// <para>
    /// <b>And for tests that assign one.</b> The other half of the same problem, and the reason
    /// membership is a rule rather than a habit. <c>WorldConfig.Current</c> is a process-wide
    /// singleton, so a test that sets <c>SunCycle</c> on it changes what every other test sees.
    /// So does a test that writes <c>FSOEnvironment.UserDir</c>, or that resolves a Simitone
    /// sidecar setting and so pins that setting's own resolved value. xUnit runs test
    /// <em>classes</em> in parallel by default, and there is no <c>xunit.runner.json</c> and no
    /// assembly-level <c>[CollectionBehavior]</c>, so a class that assigns any of those is a live
    /// flake - which is exactly what <c>SunCycleSettingTests</c> was until it joined.
    /// <c>GlobalMutationGuardTests</c> checks the membership by reflection, so the next class
    /// that assigns one fails a test rather than waiting to flake in CI.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class NonParallelCollection
    {
        public const string Name = "non-parallel";
    }
}
