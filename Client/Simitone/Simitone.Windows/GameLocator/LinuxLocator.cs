using System;
using System.IO;

namespace Simitone.Windows.GameLocator
{
    public class LinuxLocator : ILocator
    {
        public string FindTheSims1()
        {
            string localDir = @"../The Sims/";
            if (File.Exists(Path.Combine(localDir, "GameData", "Behavior.iff"))) return localDir;

            return "game1/";
        }
    }
}
