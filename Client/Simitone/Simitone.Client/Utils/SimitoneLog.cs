
using FSO.Common;
using System;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Severity of a <see cref="SimitoneLog"/> entry.
    /// </summary>
    public enum SimitoneLogLevel
    {
        Info,
        Warn,
        Error
    }

    /// <summary>
    /// Minimal, dependency-free file logger for Simitone.
    ///
    /// The game has no logging framework, but content loading and the UI/game thread
    /// both need somewhere to report problems, so this writes one timestamped line per
    /// entry to "&lt;UserDir&gt;/logs/simitone.log".
    ///
    /// Design notes:
    /// - The file is opened and closed per entry. This is slightly wasteful, but it keeps
    ///   the logger stateless so it can be called from any thread without flushing logic,
    ///   and it means a crash can never lose buffered entries.
    /// - All file IO is wrapped in try/catch and failures are swallowed silently. A broken
    ///   log must never take the game down with it.
    /// </summary>
    public static class SimitoneLog
    {
        private static readonly object WriteLock = new object();

        /// <summary>Roll the log over once it grows past 2 MiB. One generation is kept.</summary>
        private const long MaxLogBytes = 2 * 1024 * 1024;

        /// <summary>
        /// Absolute path of the log file, resolved from <see cref="FSOEnvironment.UserDir"/>
        /// on every call. That property is a mutable static that is populated part way
        /// through startup, so it is deliberately not cached here.
        /// </summary>
        public static string LogPath
        {
            get
            {
                try
                {
                    return Path.Combine(FSOEnvironment.UserDir, "logs", "simitone.log");
                }
                catch (Exception)
                {
                    //could not resolve the user dir - fall back to a relative path so the
                    //logger still attempts something rather than throwing at the call site.
                    return Path.Combine("logs", "simitone.log");
                }
            }
        }

        public static void Info(string message)
        {
            Write(SimitoneLogLevel.Info, message, null);
        }

        public static void Info(string message, Exception ex)
        {
            Write(SimitoneLogLevel.Info, message, ex);
        }

        public static void Warn(string message)
        {
            Write(SimitoneLogLevel.Warn, message, null);
        }

        public static void Warn(string message, Exception ex)
        {
            Write(SimitoneLogLevel.Warn, message, ex);
        }

        public static void Error(string message)
        {
            Write(SimitoneLogLevel.Error, message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write(SimitoneLogLevel.Error, message, ex);
        }

        private static void Write(SimitoneLogLevel level, string message, Exception ex)
        {
            try
            {
                var line = Format(level, message, ex);
                var path = LogPath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                //FileShare.Read so an external log tailer (or the player looking at the file
                //in an editor) cannot stop us from appending.
                lock (WriteLock)
                {
                    RotateIfOversized(path);
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.WriteLine(line);
                    }
                }
            }
            catch (Exception)
            {
                //deliberately swallowed. logging must never crash the game.
            }
        }

        /// <summary>
        /// Caps the log at <see cref="MaxLogBytes"/> by shifting the current file to
        /// "&lt;name&gt;.1". A single generation is kept, so a long play session cannot
        /// grow the log without bound. Must be called under <see cref="WriteLock"/>.
        /// </summary>
        private static void RotateIfOversized(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxLogBytes) return;

                var previous = path + ".1";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            catch (Exception)
            {
                //rotation is best-effort; if it fails we simply keep appending
            }
        }

        private static string Format(SimitoneLogLevel level, string message, Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(" [");
            sb.Append(level.ToString().ToUpperInvariant());
            sb.Append("] ");
            sb.Append(message ?? string.Empty);

            if (ex != null)
            {
                sb.Append(" | ");
                sb.Append(ex.GetType().Name);
                sb.Append(": ");
                sb.Append(ex.Message);
                if (ex.StackTrace != null)
                {
                    sb.Append(Environment.NewLine);
                    sb.Append(ex.StackTrace);
                }
            }

            return sb.ToString();
        }
    }
}
