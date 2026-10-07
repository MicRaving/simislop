using Microsoft.Win32;

namespace Simitone.Windows.GameLocator
{
    /// <summary>
    /// The registry reads that game discovery needs, behind an interface.
    ///
    /// <para>
    /// <see cref="Microsoft.Win32.Registry"/> is a set of static calls against the live
    /// Windows registry. There is no way to seed a fake one, so any behaviour that depends on what
    /// those calls return - most importantly "nothing is installed, what does Simitone do?" -
    /// could only ever be exercised on a machine that happened to have a Sims install. Wrapping
    /// them keeps the Windows implementation byte-for-byte the same set of key paths and views it
    /// always was, while letting the surrounding policy be driven with canned answers.
    /// </para>
    ///
    /// <para>
    /// Implementations must report a missing key or a missing value as <c>null</c> rather than
    /// throwing: probing the registry for an install that may not be there is normal operation,
    /// and a <see cref="System.Security.SecurityException"/> or
    /// <see cref="System.UnauthorizedAccessException"/> from a locked-down or partially virtualised
    /// registry view would otherwise abort discovery at startup.
    /// </para>
    /// </summary>
    internal interface IRegistryReader
    {
        /// <summary>
        /// Lists the immediate child key names of <paramref name="subKey"/>.
        /// </summary>
        /// <returns>The child key names, or null if the key does not exist or cannot be read.</returns>
        string[] GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey);

        /// <summary>
        /// Reads a string value out of a key.
        /// </summary>
        /// <returns>The value, or null if the key or value does not exist, or is not a string.</returns>
        string GetStringValue(RegistryHive hive, RegistryView view, string subKey, string name);

        /// <summary>
        /// Reads a string value addressed by a full hive path such as
        /// <c>HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Valve\Steam</c>.
        /// </summary>
        /// <returns>The value, or null if the key or value does not exist, or is not a string.</returns>
        string GetValueByPath(string hivePath, string name);
    }
}
