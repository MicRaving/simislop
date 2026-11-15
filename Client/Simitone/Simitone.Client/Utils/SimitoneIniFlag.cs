using FSO.Common;
using System;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// One on/off key in <c>simitone.ini</c>, read lazily and written through
    /// <see cref="SimitoneIniFile"/> (the one writer for that file, so setting one key never
    /// disturbs another).
    /// </summary>
    /// <remarks>
    /// The value is read from disk once, the first time it is asked for, and cached; editing
    /// <c>simitone.ini</c> by hand takes effect on the next launch. A missing, unreadable or
    /// unrecognised value is the flag's default.
    /// </remarks>
    public sealed class SimitoneIniFlag
    {
        /// <summary>The ini file's name, beside FreeSO's <c>config.ini</c>.</summary>
        public const string FileName = "simitone.ini";

        private readonly object gate = new object();
        private readonly Func<string> pathProvider;
        private bool? cached;

        /// <summary>The ini key this flag is stored under.</summary>
        public string Key { get; }

        /// <summary>The value when nothing (or nothing valid) is stored.</summary>
        public bool DefaultValue { get; }

        /// <param name="key">The ini key.</param>
        /// <param name="defaultValue">The value when the key is absent or unreadable.</param>
        /// <param name="pathProvider">
        /// Where the ini lives; null means <c>simitone.ini</c> in the user directory. Injectable so
        /// the flag can be tested without a game around it.
        /// </param>
        public SimitoneIniFlag(string key, bool defaultValue, Func<string> pathProvider = null)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("no key", nameof(key));
            Key = key;
            DefaultValue = defaultValue;
            this.pathProvider = pathProvider ?? DefaultPath;
        }

        /// <summary>The live value. Reads the file on first use.</summary>
        public bool Enabled
        {
            get
            {
                lock (gate)
                {
                    if (!cached.HasValue) cached = Load();
                    return cached.Value;
                }
            }
        }

        /// <summary>Sets the live value, and writes it to the ini when <paramref name="save"/> is set.</summary>
        public void SetEnabled(bool enabled, bool save = false)
        {
            lock (gate) { cached = enabled; }
            if (save) Save(enabled);
        }

        /// <summary>
        /// Drops the cached value so the next read goes back to the file. For tests, and for a
        /// caller that knows the file was edited.
        /// </summary>
        public void Reload()
        {
            lock (gate) { cached = null; }
        }

        /// <summary>
        /// A stored string turned into a decision. Only <c>true</c> / <c>false</c> (any case) are
        /// recognised; anything else, including empty or null, is the default.
        /// </summary>
        public static bool Resolve(string stored, bool defaultValue)
        {
            if (stored == null) return defaultValue;
            var trimmed = stored.Trim();
            if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return defaultValue;
        }

        private static string DefaultPath()
        {
            var dir = FSOEnvironment.UserDir;
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, FileName);
        }

        private SimitoneIniFile Ini(string path)
        {
            return new SimitoneIniFile(path, "Simitone Settings File. Simitone's own keys; FreeSO's are in config.ini.");
        }

        private bool Load()
        {
            try
            {
                var path = pathProvider();
                if (path == null) return DefaultValue;
                return Resolve(Ini(path).Get(Key), DefaultValue);
            }
            catch (Exception)
            {
                //A settings file that cannot be read is not worth refusing to start over.
                return DefaultValue;
            }
        }

        private void Save(bool value)
        {
            string path = null;
            try
            {
                path = pathProvider();
                if (path == null) return;
                Ini(path).Set(Key, SimitoneIniFile.FormatBool(value));
            }
            catch (Exception e)
            {
                SimitoneLog.Warn("SimitoneIniFlag: could not save '" + Key + "' to '"
                    + (path ?? "(no user directory)") + "'. It will reset next launch.", e);
            }
        }
    }

    /// <summary>The Simitone-owned switches that live in <c>simitone.ini</c> and have no Options row of their own.</summary>
    public static class SimitoneFlags
    {
        /// <summary>
        /// Developer hotkeys in the lot view (F8 lot-thumbnail dialog, F9 picture-in-picture
        /// toggle, F11 collision verification). Off by default so a shipped build cannot trigger
        /// them by accident; testers turn it on with <c>DebugKeys=true</c> in
        /// <c>simitone.ini</c> and restart.
        /// </summary>
        public static readonly SimitoneIniFlag DebugKeys = new SimitoneIniFlag("DebugKeys", false);

        /// <summary>
        /// Whether the picture-in-picture window pops up by itself for notable events (a sim
        /// elsewhere on the lot raising a headline). On by default; the Gameplay page of Options
        /// has the switch.
        /// </summary>
        public static readonly SimitoneIniFlag PictureInPicture = new SimitoneIniFlag("PictureInPicture", true);
    }
}
