using System;
using System.IO;

namespace Simitone.Windows.Utils
{
    class MonogameLinker
    {
        // Chooses which pre-copied MonoGame backend assembly is loaded at runtime.
        // Nothing is copied here: the CopyMonoGameDLLs target in Simitone.Windows.csproj
        // has already placed MonoGame.Framework.dll under Monogame\Windows and
        // Monogame\WindowsGL by the time this runs. This only picks the folder.

        public static string AssemblyDir = "./";

        public static bool Link(bool preferDX11)
        {
            // This project targets net10.0-windows, so the only backend folders that can
            // ever exist in the output directory are Monogame\Windows and Monogame\WindowsGL.
            // Anything else means we are running on a host this build does not support; we still
            // set a defined AssemblyDir (instead of silently leaving it at "./") and say so loudly.
            bool windows = Environment.OSVersion.Platform != PlatformID.MacOSX
                        && Environment.OSVersion.Platform != PlatformID.Unix;

            string monogameDir = preferDX11 ? "Monogame/Windows/" : "Monogame/WindowsGL/";

            // Always set AssemblyDir on every path, including the unsupported-platform fallback,
            // so assembly resolution never silently falls back to the process directory.
            AssemblyDir = monogameDir;

            if (!windows)
            {
                Console.WriteLine(
                    "Simitone.Windows targets net10.0-windows and only ships the Monogame/Windows " +
                    "and Monogame/WindowsGL backends. This platform is not supported; falling back to '" +
                    monogameDir + "'. DirectX is also usually only available on Windows.");
                return preferDX11;
            }

            try
            {
                if (!Directory.Exists(monogameDir))
                {
                    Console.WriteLine(
                        "Expected MonoGame backend folder '" + monogameDir + "' is missing. " +
                        "The build's CopyMonoGameDLLs target may not have run; startup will probably fail.");
                }

                // Only one MonoGame.Framework.dll may be resolvable at a time, so drop any stray
                // copy sitting next to the executable. The real one lives in monogameDir.
                if (File.Exists("MonoGame.Framework.dll")) File.Delete("MonoGame.Framework.dll");
            }
            catch (Exception e)
            {
                Console.WriteLine("Unable to link Monogame. Continuing... (" + e.ToString() + ")");
            }

            return preferDX11;
        }
    }
}
