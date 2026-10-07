using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests
{
    /// <summary>
    /// Checks that every test class which writes a process-global is in
    /// <see cref="NonParallelCollection"/>.
    /// </summary>
    ///
    /// <para>
    /// <b>Why a test about tests.</b> Because the failure this prevents is a flake, and a flake
    /// does not reproduce on the machine that is asked to explain it. xUnit runs test
    /// <em>classes</em> in parallel by default, and there is no <c>xunit.runner.json</c> and no
    /// assembly-level <c>[CollectionBehavior]</c> in this project, so a class that assigns
    /// <c>FSOEnvironment.UserDir</c>, <c>WorldConfig.Current</c> or a Simitone sidecar setting's
    /// resolved value changes what every other class sees while it runs. That has already happened
    /// here: <c>Log_IsRotatedOnceItReachesTwoMebibytes</c> failed intermittently with four lines
    /// in a freshly rotated log instead of one, which is what
    /// <see cref="NonParallelCollection"/> was introduced for, and
    /// <c>SunCycleSettingTests</c> was still doing the same thing outside it.
    /// </para>
    ///
    /// <para>
    /// <b>How it decides.</b> By reading this assembly's own source. The signals are deliberately
    /// narrow - an assignment to one of the named globals - because a false positive here means a
    /// correct class is serialised for no reason, and a false negative means the flake comes back.
    /// The three named symbols are the ones whose value is process-wide and whose mutation is
    /// observable: the two are the same object seen from two namespaces, so
    /// <c>SunCycleSetting</c> and <c>FreeWillSetting</c> are matched through their own type name.
    /// </para>
    ///
    /// <para>
    /// A class that <em>reads</em> a global is not flagged. Reading one is not the hazard, and
    /// most of this suite reads <c>FSOEnvironment.UserDir</c> legitimately - what it must not do
    /// is change it while somebody else is reading.
    /// </para>
    /// </summary>
    public sealed class GlobalMutationGuardTests
    {
        /// <summary>
        /// The three assignments that make a class a member of the collection, each written with
        /// its receiver.
        /// </summary>
        /// <remarks>
        /// <b>The receiver is part of the signal, and it has to be.</b> Matching the member name
        /// alone is far too loose: <c>BuildBuyEnabled = buildBuyEnabled</c> in a lot's
        /// architecture, <c>Enabled = 1</c> in a tutorial fixture, and
        /// <c>helpEnabled = true</c> in a parameter list all end in "Enabled =" and none of them
        /// is a process-global. Only <c>SunCycleSetting.Enabled</c> and
        /// <c>FreeWillSetting.Enabled</c> are - both are the resolved value of a Simitone sidecar
        /// setting, cached in a static.
        /// <para>
        /// The other two are properties rather than fields, so the member name is the only stable
        /// handle and the receiver is left open on purpose - the same reasoning
        /// <c>SunCycleSettingTests</c>'s own reflection tests use for the options page.
        /// </para>
        /// <para>
        /// The negative lookahead keeps a comparison (<c>==</c>) and an arrow (<c>=&gt;</c>) out
        /// of the result; a test that only <em>reads</em> a global is not a hazard and must not be
        /// flagged.
        /// </para>
        /// </remarks>
        private static readonly (string Pattern, string Description)[] Assignments =
        {
            (@"\bFSOEnvironment\s*\.\s*UserDir\s*\+?=(?!=)", "FSOEnvironment.UserDir"),
            //anything up to the assignment, because the write is usually through a property of the
            //singleton - `WorldConfig.Current.SunCycle = true` - and a pattern that stopped at
            //`Current` would miss every one of them
            (@"\bWorldConfig\s*\.\s*Current\b[^\r\n;]*?=[^=]", "WorldConfig.Current"),
            (@"\b(SunCycleSetting|FreeWillSetting)\s*\.\s*Enabled\s*\+?=(?!=)",
                "a Simitone sidecar setting's resolved value"),
            //the commit seam, which is a static delegate rather than a property, and which is the
            //one that cost the suite a flake: while it pointed at a throwing delegate, every other
            //class that committed a file went through it
            (@"\bAtomicFileCommit\s*\.\s*Replace\s*=(?!=)", "AtomicFileCommit.Replace"),
        };

        private static readonly Regex[] AssignmentPatterns = Assignments
            .Select(a => new Regex(a.Pattern, RegexOptions.Compiled))
            .ToArray();

        /// <summary>Every test class in this assembly, by its own name.</summary>
        private static IEnumerable<Type> TestClasses()
        {
            return typeof(GlobalMutationGuardTests).Assembly
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract)
                .Where(t => t.Namespace != null && t.Namespace.StartsWith("Simitone.Tests"))
                .Where(HasTests)
                .ToList();
        }

        private static bool HasTests(Type type)
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Any(m => m.GetCustomAttributes()
                    .Any(a => a.GetType().Name == "FactAttribute"
                        || a.GetType().Name == "TheoryAttribute"));
        }

        /// <summary>
        /// Whether a class declares itself a member of the non-parallel collection.
        /// </summary>
        /// <remarks>
        /// Read off <c>GetCustomAttributesData</c> rather than off an instantiated attribute,
        /// because the name a <c>CollectionAttribute</c> carries is a constructor argument and
        /// whether xUnit also exposes it as a readable property has changed between major versions.
        /// The constructor argument is the part the attribute is defined by.
        /// </remarks>
        private static bool InTheCollection(Type type)
        {
            return type.GetCustomAttributesData()
                .Where(a => a.AttributeType.Name == "CollectionAttribute")
                .SelectMany(a => a.ConstructorArguments)
                .Any(argument => argument.ArgumentType == typeof(string)
                    && (string)argument.Value == NonParallelCollection.Name);
        }

        /// <summary>
        /// The assembly's own source, read as text. There is no better way to see a test's body
        /// than the body, and the alternative - a marker attribute nobody remembers to add - is
        /// the thing this whole file exists to avoid needing.
        /// </summary>
        private static string SourceFor(Type type)
        {
            var root = FindSourceRoot();
            if (root == null)
            {
                //no source beside the binaries (a published test run, say). Nothing to check
                //rather than a failure, because the rule is about the source not the behaviour.
                return null;
            }

            var relative = (type.Namespace ?? "").Replace('.', Path.DirectorySeparatorChar);
            foreach (var folder in new[]
            {
                Path.Combine(root, relative, type.Name + ".cs"),
                Path.Combine(root, type.Name + ".cs"),
            })
            {
                if (File.Exists(folder)) return File.ReadAllText(folder);
            }
            return null;
        }

        private static string _root;

        private static string FindSourceRoot()
        {
            if (_root != null) return _root.Length == 0 ? null : _root;

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Simitone.Tests.csproj");
                if (File.Exists(candidate))
                {
                    _root = dir.FullName;
                    return _root;
                }
                dir = dir.Parent;
            }
            _root = string.Empty;
            return null;
        }

        [Fact]
        public void EveryTestClassThatWritesAProcessGlobalIsInTheNonParallelCollection()
        {
            var offenders = new List<string>();

            foreach (var type in TestClasses())
            {
                if (InTheCollection(type)) continue;

                //this class is exempt, and precisely because of what it is: it *contains* the
                //three assignments as string literals, so it can test that the sweep recognises
                //them without ever performing one. Nothing here mutates a global.
                if (type == typeof(GlobalMutationGuardTests)) continue;

                var source = SourceFor(type);
                if (source == null) continue;

                if (MutatesAGlobal(source))
                {
                    offenders.Add(type.FullName);
                }
            }

            Assert.True(offenders.Count == 0,
                "these test classes assign a process-global, so they have to declare [Collection("
                + NonParallelCollection.Name + ")] or they are a live flake: "
                + string.Join(", ", offenders));
        }

        [Fact]
        public void TheGuardItselfCanSeeTheSourceItNeeds()
        {
            // Without this, the guard above passes vacuously whenever it cannot find the source -
            // which is the one way it could be wrong and look right.
            var root = FindSourceRoot();
            Assert.NotNull(root);
            Assert.True(File.Exists(Path.Combine(root, "Simitone.Tests.csproj")));

            var self = SourceFor(typeof(GlobalMutationGuardTests));
            Assert.NotNull(self);
            Assert.Contains("GlobalMutationGuardTests", self);
        }

        [Fact]
        public void TheGuardActuallyFindsAMutationWhenThereIsOne()
        {
            // A guard that cannot fail is the exact defect this file's subject is about, so it
            // gets its own check: a snippet with a real assignment in it has to be recognised.
            const string mutating = @"
                public void Something()
                {
                    var original = FSOEnvironment.UserDir;
                    FSOEnvironment.UserDir = root;
                    try { } finally { FSOEnvironment.UserDir = original; }
                }";
            const string alsoMutating = @"
                public void SomethingElse()
                {
                    var original = WorldConfig.Current.SunCycle;
                    WorldConfig.Current.SunCycle = true;
                }";
            const string settingMutating = @"
                public void AndThis()
                {
                    SunCycleSetting.Enabled = true;
                }";
            const string seamMutating = @"
                public void AndThisOne()
                {
                    AtomicFileCommit.Replace = (src, dest, bak) => throw new IOException();
                }";
            const string merelyReading = @"
                public void ButThisDoesNot()
                {
                    var dir = FSOEnvironment.UserDir;
                    Assert.Equal(dir, other);
                    var config = WorldConfig.Current;
                    Assert.Equal(true, SunCycleSetting.Enabled);
                    Assert.NotNull(AtomicFileCommit.Replace);
                }";

            Assert.True(MutatesAGlobal(mutating));
            Assert.True(MutatesAGlobal(alsoMutating));
            Assert.True(MutatesAGlobal(settingMutating));
            Assert.True(MutatesAGlobal(seamMutating));
            Assert.False(MutatesAGlobal(merelyReading));
        }

        [Fact]
        public void TheKnownMutatorsAreAlreadyInTheCollection()
        {
            // Spelled out rather than left to the reflection sweep, so that a regression here has
            // a name in the failure message. Every one of these assigns FSOEnvironment.UserDir,
            // WorldConfig.Current or a sidecar setting's resolved value.
            AssertInTheCollection(typeof(UI.SunCycleSettingTests));
            AssertInTheCollection(typeof(Utils.SimitoneLogTests));
            AssertInTheCollection(typeof(Utils.NeighbourhoodManagerTests));
            AssertInTheCollection(typeof(Engine.VMEODDiagnosticsTests));
            AssertInTheCollection(typeof(Utils.LotZoningWriteFormatTests));
            AssertInTheCollection(typeof(UI.DamagedTravelSlotTests));
            AssertInTheCollection(typeof(UI.MoveInRequestTests));
            AssertInTheCollection(typeof(Utils.SettingsSaveFailureTests));
            AssertInTheCollection(typeof(Content.TS1AvatarSaveCommitTests));
            AssertInTheCollection(typeof(UI.LotLifecycleServiceTests));
            AssertInTheCollection(typeof(UI.NeighbourhoodManagementPanelTests));
            //the commit seam, and the one that produced a real flake: this class replaces it, and
            //while it is replaced every other class that commits a file goes through the
            //replacement
            AssertInTheCollection(typeof(Utils.AtomicFileCommitTests));
        }

        private static void AssertInTheCollection(Type type)
        {
            Assert.True(InTheCollection(type), type.Name + " writes a process-global and must be in "
                + NonParallelCollection.Name);
        }

        /// <summary>
        /// Whether a source file assigns one of the process-globals.
        /// </summary>
        /// <remarks>
        /// Any one of the three patterns is enough, and the receiver is required - see
        /// <see cref="Assignments"/>. A file that only reads a global is not a hazard, so the
        /// patterns are anchored on the assignment and not on the name.
        /// </remarks>
        private static bool MutatesAGlobal(string source)
        {
            if (source == null) return false;
            return AssignmentPatterns.Any(pattern => pattern.IsMatch(source));
        }
    }
}
