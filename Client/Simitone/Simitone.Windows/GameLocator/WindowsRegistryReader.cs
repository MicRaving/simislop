using System;
using Microsoft.Win32;

namespace Simitone.Windows.GameLocator
{
    /// <summary>
    /// The live <see cref="Registry"/> implementation used in production. Every call here is the
    /// same call, against the same hive, view and key, that the game used to make inline; the only
    /// changes are that a miss comes back as null instead of as a null dereference further up, and
    /// that each probe opens its own base key rather than one walk sharing a single handle. The
    /// second one costs three cheap <c>OpenBaseKey</c> calls at startup and buys probes that are
    /// independent of each other, which is what makes them substitutable in the first place.
    /// </summary>
    internal sealed class WindowsRegistryReader : IRegistryReader
    {
        public string[] GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey)
        {
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
            {
                using (RegistryKey key = baseKey.OpenSubKey(subKey))
                {
                    // OpenSubKey returns null for a key that is not there, which is the normal
                    // case for "is Maxis installed?" probes.
                    return key?.GetSubKeyNames();
                }
            }
        }

        public string GetStringValue(RegistryHive hive, RegistryView view, string subKey, string name)
        {
            using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
            {
                using (RegistryKey key = baseKey.OpenSubKey(subKey))
                {
                    return key?.GetValue(name) as string;
                }
            }
        }

        public string GetValueByPath(string hivePath, string name)
        {
            try
            {
                // Registry.GetValue throws for an unknown hive root, so the guard below can never
                // be reached: the try is what turns "this machine has no such key" into the null
                // the interface promises.
                return Registry.GetValue(hivePath, name, null)?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
