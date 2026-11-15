using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The EOD panel bridge loads FSO.Client by name (Type.GetType), so nothing in the compiler's
    /// reference graph keeps FSO.Client.dll in the shipped output. A2 dropped it and silently
    /// switched every minigame EOD off. These tests pin the two halves of the guard that stops a
    /// repeat: the unconditional ProjectReference, and the build-time list that fails the build
    /// when the assembly is missing from the output.
    /// </summary>
    public class RuntimeProbedAssemblyContractTests
    {
        private static string ReadWindowsProject()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "Simitone.Windows", "Simitone.Windows.csproj");
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            throw new FileNotFoundException("Could not find Simitone.Windows.csproj above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void TheBuildFailsWhenTheEodHostAssemblyIsNotInTheOutput()
        {
            var csproj = ReadWindowsProject();
            var m = Regex.Match(csproj, "<SimitoneRuntimeProbedAssembly Include=\"([^\"]+)\"");
            Assert.True(m.Success, "Simitone.Windows.csproj lost its SimitoneRuntimeProbedAssembly list");
            Assert.Contains("FSO.Client.dll", m.Groups[1].Value.Split(';'));
            Assert.Contains("Name=\"_SimitoneRuntimeProbeContract\"", csproj);
        }

        [Fact]
        public void FsoClientIsReferencedWithoutAnyCondition()
        {
            var csproj = ReadWindowsProject();
            var m = Regex.Match(csproj, "<ProjectReference Include=\"[^\"]*FSO\\.Client\\.csproj\"([^>]*)/>");
            Assert.True(m.Success, "Simitone.Windows.csproj no longer references FSO.Client");
            Assert.DoesNotContain("Condition", m.Groups[1].Value);
        }
    }
}
