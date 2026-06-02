using System;
using System.Collections.Generic;
using System.IO;
using HentaiVirus.Core;
using Microsoft.Data.Sqlite;

namespace HentaiVirus.Database
{
    public class DatabaseManager
    {
        private readonly string _databasePath = AppPaths.DatabasePath;

        public string ConnectionString { get; }

        public DatabaseManager()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath
            };

            ConnectionString = builder.ToString();
        }

        public bool DatabaseExists => File.Exists(_databasePath);

        public SqliteConnection CreateConnection()
        {
            return new SqliteConnection(ConnectionString);
        }

        public void InitializeDatabase()
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);

            using var connection = CreateConnection();
            connection.Open();

            const string createTableQuery = @"
                CREATE TABLE IF NOT EXISTS Games (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DownloadUrl TEXT NOT NULL,
                    TargetDirectory TEXT NOT NULL,
                    ExePath TEXT NOT NULL,
                    IsDownloaded INTEGER DEFAULT 0
                );";

            using var command = new SqliteCommand(createTableQuery, connection);
            command.ExecuteNonQuery();
        }

        public string GenerateHiddenPath(string gameName)
        {
            string safeName = SanitizePathSegment(gameName);
            string contentPath = Path.Combine(AppPaths.ContentDirectory, safeName);
            Directory.CreateDirectory(contentPath);

            return contentPath;
        }

        public void PurgeEverything()
        {
            if (!DatabaseExists)
            {
                DeleteContentRoot();
                return;
            }

            try
            {
                using var connection = CreateConnection();
                connection.Open();

                if (GamesTableExists(connection))
                {
                    using var cmd = new SqliteCommand("DELETE FROM Games", connection);
                    cmd.ExecuteNonQuery();
                }
                else
                {
                    AppLogger.Log("Games table was missing during purge. Database file will be removed.");
                }
            }
            catch (SqliteException ex)
            {
                AppLogger.Log(ex, "Failed to read database during purge. Database file will be removed.");
            }

            DeleteContentRoot();

            SqliteConnection.ClearAllPools();

            try
            {
                if (File.Exists(_databasePath))
                {
                    File.Delete(_databasePath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "Failed to delete database");
            }
        }

        private static string SanitizePathSegment(string value)
        {
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalidChar, '_');
            }

            return string.IsNullOrWhiteSpace(value) ? "Content" : value;
        }

        private static void DeleteAppDirectory(string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return;
            }

            if (!IsPathInsideAppData(targetDirectory))
            {
                AppLogger.Log($"Skipped deletion outside application directory: {targetDirectory}");
                return;
            }

            try
            {
                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, true);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, $"Failed to delete directory {targetDirectory}");
            }
        }

        private static bool IsPathInsideAppData(string path)
        {
            try
            {
                string appRoot = Path.GetFullPath(AppPaths.AppDataDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                string fullPath = Path.GetFullPath(path);

                return fullPath.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, $"Failed to validate path {path}");
                return false;
            }
        }

        private static bool GamesTableExists(SqliteConnection connection)
        {
            using var command = new SqliteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Games'",
                connection);
            long count = (long)command.ExecuteScalar()!;

            return count > 0;
        }

        private static void DeleteContentRoot()
        {
            DeleteAppDirectory(AppPaths.ContentDirectory);
        }
    }
}
