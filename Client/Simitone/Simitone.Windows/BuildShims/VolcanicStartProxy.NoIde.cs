// Compile-time stand-in for FreeSO's FSO.IDE.VolcanicStartProxy.
//
// WHY THIS FILE EXISTS
// ====================
// Simitone.Windows wants to build the game without compiling FreeSO's IDE host or the
// The Sims Online server closure behind it - see the "BuildIde" comment block in
// ../../Directory.Build.props and the B-12 finding in docs/AUDIT_BUILD_DEPS.md. With
// -p:BuildIde=false, Simitone.Windows drops its ProjectReference to FSO.IDE and
// FSO.Client, and the 19 projects behind them stop being restored and compiled on every
// build. Nothing that survives into $(OutDir) is affected: the TrimSingleplayerOutput
// target was already deleting all of it, and _SimitoneAssertNoIdeReferences fails the
// build if anything left in the output still points at FSO.IDE or FSO.Client.
//
// It exists only because of one line in Program.cs:
//
//     if (ide) new FSO.IDE.VolcanicStartProxy().InitVolcanic(args);
//
// That is the entire -ide feature - the "using FSO.Client;" lines above it are unused -
// and it is not behind a preprocessor symbol, so dropping the reference without
// replacing the type would not compile. Suppressing the flag instead, or leaving the
// default on, would have meant the split could not be implemented at all.
//
// WHY IT THROWS
// =============
// The alternative is a no-op. A no-op is the wrong answer twice over: the -ide path
// would appear to work and then silently not start the IDE, and the person who hit it
// would have no reason to suspect the build rather than the flag. Throwing means the
// failure is loud, happens on the only command line that can reach it (-ide, which
// Simitone - a singleplayer The Sims 1 game - has no reason ever to use), and carries
// the fix in the message.
//
// The alternative would be to make this build outright fail when -ide is requested, but
// the switch is parsed out of argv[] inside Main, so the only place that can see it is
// the code that is calling into this type. Throwing here is the earliest point at which
// the build can react to it.
//
// SHAPE
// =====
// Kept deliberately minimal: same namespace, same type name, same public ctor, same
// one method with the same signature. If FSO.IDE's VolcanicStartProxy ever grows a
// second member that Simitone.Windows starts using, this stops compiling and the build
// says so - which is the correct outcome, because a shim that quietly stopped matching
// would be worse than no shim.

using System;

namespace FSO.IDE
{
    /// <summary>
    /// Build-time stand-in for FreeSO's IDE start-up proxy, compiled only when this
    /// project is built with <c>-p:BuildIde=false</c>.
    /// </summary>
    /// <remarks>
    /// Not part of the game. Simitone is singleplayer, offline and Windows-only, and
    /// never starts FreeSO's Volcanic IDE host. See the file header.
    /// </remarks>
    public sealed class VolcanicStartProxy
    {
        /// <summary>
        /// Always throws. Present so that the -ide call site in <c>Program.Main</c>
        /// compiles; unreachable in any Simitone session that was not started with
        /// <c>-ide</c>.
        /// </summary>
        /// <exception cref="PlatformNotSupportedException">Always.</exception>
        public void InitVolcanic(string[] args)
        {
            throw new PlatformNotSupportedException(
                "Simitone was built without the FreeSO IDE host (-p:BuildIde=false, the default), so " +
                "'-ide' is not available in this binary. That is the intended configuration for the game: " +
                "the IDE host and the The Sims Online server closure behind it are compiled only so that " +
                "TrimSingleplayerOutput can delete them, and neither is reachable from the game. To get an " +
                "-ide-capable Simitone.exe, rebuild with: dotnet build Client/Simitone/Simitone.sln " +
                "-p:BuildIde=true");
        }
    }
}
