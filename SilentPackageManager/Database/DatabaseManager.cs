using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SilentPackageManager.Core;
using Microsoft.Data.Sqlite;

namespace SilentPackageManager.Database
{
    public class DatabaseManager
    {
        private readonly string _databasePath = AppPaths.DatabasePath;

        public string ConnectionString { get; }

        public DatabaseManager()
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = _databasePath };
            ConnectionString = builder.ToString();
        }

        public bool DatabaseExists => File.Exists(_databasePath);

        public SqliteConnection CreateConnection() => new SqliteConnection(ConnectionString);

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
                );
                CREATE INDEX IF NOT EXISTS IDX_Games_IsDownloaded ON Games(IsDownloaded);";

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
                    var cmd = new SqliteCommand("SELECT TargetDirectory, ExePath FROM Games WHERE IsDownloaded = 1", connection);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string targetDir = reader.GetString(0);
                        string exePath = reader.GetString(1);

                        if (!string.IsNullOrWhiteSpace(exePath))
                        {
                            string processName = Path.GetFileNameWithoutExtension(exePath);
                            var runningProcesses = Process.GetProcessesByName(processName);
                            foreach (var p in runningProcesses)
                            {
                                try 
                                { 
                                    p.Kill(); 
                                    p.WaitForExit(3000); 
                                } 
                                catch (Exception ex) 
                                { 
                                    AppLogger.Log(ex, $"Failed to kill process {processName}"); 
                                }
                                finally
                                {
                                    p.Dispose();
                                }
                            }
                        }

                        DeleteAppDirectory(targetDir);
                    }

                    using var deleteCmd = new SqliteCommand("DELETE FROM Games", connection);
                    deleteCmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "Failed to read database during purge.");
            }

            DeleteContentRoot();
            SqliteConnection.ClearAllPools();

            try
            {
                if (File.Exists(_databasePath)) File.Delete(_databasePath);
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "Failed to delete database file.");
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
            if (string.IsNullOrWhiteSpace(targetDirectory)) return;

            if (!IsPathInsideAppData(targetDirectory)) return;

            try
            {
                if (Directory.Exists(targetDirectory)) Directory.Delete(targetDirectory, true);
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
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(appRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool GamesTableExists(SqliteConnection connection)
        {
            using var command = new SqliteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Games'", connection);
            return (long)command.ExecuteScalar()! > 0;
        }

        private static void DeleteContentRoot() => DeleteAppDirectory(AppPaths.ContentDirectory);
    }
}