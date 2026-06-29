using System;
using System.IO;

namespace SilentPackageManager.Core
{
    public static class AppPaths
    {
        public const string ApplicationFolderName = "SilentPackageManager";

        public static string AppDataDirectory
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string directory = Path.Combine(appData, ApplicationFolderName);
                Directory.CreateDirectory(directory);
                return directory;
            }
        }

        public static string DatabasePath => Path.Combine(AppDataDirectory, "games.db");

        public static string LogPath => Path.Combine(AppDataDirectory, "sys_log.txt");

        public static string ContentDirectory
        {
            get
            {
                string directory = Path.Combine(AppDataDirectory, "Content");
                Directory.CreateDirectory(directory);
                return directory;
            }
        }
    }
}
