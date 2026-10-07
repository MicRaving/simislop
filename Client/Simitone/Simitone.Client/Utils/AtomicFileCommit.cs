using System;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Stages a file write and commits it over an existing file without ever leaving a
    /// half-written file where the real one used to be.
    ///
    /// The ordering below is a crash-safety contract, not an implementation detail. Save
    /// originally did this inline in <c>TS1GameScreen.Save</c>; the steps were extracted so
    /// they could be regression-tested without a VM, a graphics device or a real lot on disk.
    ///
    /// The sequence is:
    /// <list type="number">
    /// <item>refuse a relative path - a save path that silently resolved against the process
    /// working directory (<c>Program.cs</c> sets that to the install folder) would write a
    /// player's house file into the program directory;</item>
    /// <item>make sure the parent directory exists;</item>
    /// <item>write the whole payload to "&lt;path&gt;.tmp" and flush it to disk - a failure
    /// here leaves the original untouched, and a crash leaves a complete (but unreferenced)
    /// .tmp;</item>
    /// <item>commit: <c>File.Replace</c> over the target when there is one, which is a single
    /// rename that also writes "&lt;path&gt;.bak" as part of the same operation, and a plain
    /// <c>File.Move</c> when there is not. Both are same-volume renames, so the target is
    /// never opened for writing in place and a reader can never observe a short file.</item>
    /// </list>
    ///
    /// <b>Why not copy-then-delete.</b> <c>File.Copy(tmp, path, overwrite: true)</c> opens the
    /// target with <see cref="FileMode.Create"/> and streams into it, so the live
    /// <c>House{NN}.iff</c> was truncated and refilled byte by byte - for a lot file with two
    /// embedded PNG thumbnails, for hundreds of milliseconds. The data was never permanently lost
    /// (the .bak was intact), but a reader in that window saw a truncated save, which is the
    /// exact failure this class exists to prevent. <c>File.Replace</c> is the primitive for it.
    ///
    /// <para>
    /// <c>File.Replace</c> is not available on every platform <c>File.Move</c> is (it needs
    /// backup-file support from the filesystem), so it is wrapped: a
    /// <see cref="PlatformNotSupportedException"/> falls back to the copy-then-delete the code
    /// used before, and the class then promises the real, weaker guarantee - the previous
    /// contents are always in .bak, but the target may be briefly short during the commit.
    /// </para>
    ///
    /// An interrupted save can always be recovered by hand from .tmp or .bak, and the copy
    /// fallback still leaves both behind.
    /// </summary>
    public static class AtomicFileCommit
    {
        /// <summary>Suffix of the staged, not-yet-committed copy.</summary>
        private const string TmpSuffix = ".tmp";

        /// <summary>Suffix of the previous good copy kept beside the target.</summary>
        private const string BakSuffix = ".bak";

        /// <summary>
        /// The commit primitive, as a seam.
        ///
        /// <para>
        /// Production code never assigns this. It exists so
        /// <c>AtomicFileCommitTests</c> can make the commit step itself fail - the one failure
        /// a test cannot provoke from outside, because everything that can be provoked from
        /// outside (an unwritable folder, a directory in the way, a locked handle) fails earlier,
        /// in staging or in the backup. Swapping in a throwing delegate is the only way to assert
        /// what the class promises about a failure at exactly that step: the target keeps its
        /// previous bytes, and the complete .tmp survives next to it.
        /// </para>
        /// </summary>
        internal static Action<string, string, string> Replace = FileReplace;

        /// <summary>
        /// Writes <paramref name="bytes"/> to <paramref name="path"/> using the stage /
        /// backup / commit sequence documented on the class.
        /// </summary>
        /// <param name="path">Full destination path. Its parent directory is created if missing.
        /// Must be rooted.</param>
        /// <param name="bytes">The exact payload to commit. A zero-length array is a legal
        /// payload and is written and committed like any other.</param>
        /// <returns>
        /// The path of the "&lt;path&gt;.bak" copy of the previous contents when the target
        /// already existed, otherwise <c>null</c> (there was nothing to back up).
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> or
        /// <paramref name="bytes"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="path"/> is relative.</exception>
        /// <exception cref="IOException">Any failure while staging or committing. The original
        /// file - if there was one - is left intact.</exception>
        public static string Write(string path, byte[] bytes)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (!Path.IsPathRooted(path))
            {
                //Not a warning: a relative save path would put a player's house file in whatever
                //directory the process happens to be running in, which Program.cs deliberately
                //sets to the install folder. Every call site builds its path from a user
                //directory, so reaching this is a programming error rather than a bad install.
                throw new ArgumentException(
                    "AtomicFileCommit needs a full path; '" + path + "' would resolve against the "
                    + "process working directory.", nameof(path));
            }

            var tmpPath = path + TmpSuffix;
            var bakPath = path + BakSuffix;

            //1. the caller must not have to reason about whether the folder is there yet
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            //2. stage the new data beside the target, in full, before touching anything else.
            //   Flush(true) rather than Flush(): the OS page cache is not the disk, and a
            //   power cut after a "successful" commit would otherwise lose a save the player was
            //   told had been made.
            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            //3. commit. if this throws we are still holding a complete .tmp and the intact
            //   original - nothing is lost, and nothing was ever half-written either.
            string backupPath = null;
            if (File.Exists(path))
            {
                //File.Replace writes the backup as part of the same rename, so there is no
                //window in which the old contents exist nowhere.
                Replace(tmpPath, path, bakPath);
                backupPath = bakPath;
            }
            else
            {
                //Nothing to back up and nothing to replace: a rename is still the right way to
                //put the file in place, so a reader never sees a partially-created target.
                File.Move(tmpPath, path);
            }

            return File.Exists(backupPath) ? backupPath : null;
        }

        /// <summary>
        /// <c>File.Replace</c> with a documented fallback for filesystems that cannot do it.
        ///
        /// <para>
        /// <c>File.Replace</c> needs the destination to exist (the caller only reaches this when
        /// it does) and needs the filesystem to support a backup file. Where that is missing the
        /// platform reports it rather than corrupting anything, and the copy-then-delete this
        /// replaced is the documented weaker fallback: the previous contents are still copied to
        /// the .bak first, so the data is never lost, but the target is briefly short.
        /// </para>
        /// </summary>
        private static void FileReplace(string tmpPath, string path, string bakPath)
        {
            try
            {
                File.Replace(tmpPath, path, bakPath, true);
            }
            catch (PlatformNotSupportedException)
            {
                CopyThenDelete(tmpPath, path, bakPath);
            }
            catch (IOException) when (!File.Exists(bakPath))
            {
                //Same story, reported as an IO error by some providers: a filesystem that cannot
                //write the backup. Only fall back when the backup genuinely did not get written -
                //any other IOException is a real failure and must not be papered over with a
                //second, non-atomic write.
                CopyThenDelete(tmpPath, path, bakPath);
            }
        }

        /// <summary>
        /// The pre-<c>File.Replace</c> fallback, kept as a function rather than deleted so the
        /// weaker guarantee it offers is stated in one place. The .bak is written first, so the
        /// previous contents survive a copy that fails half way.
        /// </summary>
        private static void CopyThenDelete(string tmpPath, string path, string bakPath)
        {
            File.Copy(path, bakPath, true);
            File.Copy(tmpPath, path, true);
            File.Delete(tmpPath);
        }
    }
}
