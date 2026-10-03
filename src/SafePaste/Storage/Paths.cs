using System;
using System.IO;

namespace SafePaste.Storage
{
    /// <summary>Где лежат настройки и база. Каталог подменяется только тестами.</summary>
    public static class Paths
    {
        private static string directory;

        public static string DataDirectory
        {
            get
            {
                if (directory == null)
                {
                    directory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafePaste");
                }
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                return directory;
            }
            set { directory = value; }
        }

        public static string DatabaseFile
        {
            get { return Path.Combine(DataDirectory, "database.dat"); }
        }

        public static string LabelsFile
        {
            get { return Path.Combine(DataDirectory, "labels.dat"); }
        }

        public static string SettingsFile
        {
            get { return Path.Combine(DataDirectory, "settings.json"); }
        }

        public static string BridgeSettingsFile
        {
            get { return Path.Combine(DataDirectory, "bridge.json"); }
        }
    }
}
