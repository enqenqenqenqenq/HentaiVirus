using System;
using System.Collections.Generic;
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
                Title = "HentaiVirus - Очистка";
                InstructionText.Text = "Демо-данные уже существуют. Нажмите кнопку ниже, чтобы удалить локальные данные приложения.";
                AcceptButton.Content = "Удалить демо-данные";
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

                MessageBox.Show("Очистка успешно завершена. Локальные данные приложения удалены.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
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
            MessageBox.Show("Симуляция запущена. Приложение будет периодически обрабатывать демо-задачи и писать лог.", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);

            _dbManager.InitializeDatabase();
            SeedDatabaseWithLinks();

            InstructionText.Text = "Симуляция работает. Каждую минуту будет обрабатываться следующая демо-задача.";
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

            if (count != 0)
            {
                return;
            }

            string[] taskNames =
            {
                "DemoTask_1",
                "DemoTask_2",
                "DemoTask_3"
            };

            foreach (string name in taskNames)
            {
                using var insertCmd = new SqliteCommand(
                    "INSERT INTO Games (DownloadUrl, TargetDirectory, ExePath) VALUES (@name, '', '')", connection);
                insertCmd.Parameters.AddWithValue("@name", name);
                insertCmd.ExecuteNonQuery();
            }
        }

        private async Task StartCoreEngineAsync(CancellationToken cancellationToken)
        {
            int nextGameIndex = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _dbManager.InitializeDatabase();
                    List<int> taskIds = GetTaskIds();
                    if (taskIds.Count == 0)
                    {
                        AppLogger.Log("No demo tasks are available.");
                    }
                    else
                    {
                        int taskId = taskIds[nextGameIndex % taskIds.Count];
                        nextGameIndex++;
                        MarkTaskAsProcessed(taskId);
                        AppLogger.Log($"Processed demo task {taskId}.");
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

        private List<int> GetTaskIds()
        {
            var ids = new List<int>();

            using var connection = _dbManager.CreateConnection();
            connection.Open();
            using var selectCmd = new SqliteCommand("SELECT Id FROM Games ORDER BY Id", connection);

            using var reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt32(0));
            }

            return ids;
        }

        private void MarkTaskAsProcessed(int id)
        {
            using var connection = _dbManager.CreateConnection();
            connection.Open();
            using var updateCmd = new SqliteCommand(
                "UPDATE Games SET IsDownloaded = 1 WHERE Id = @id",
                connection);

            updateCmd.Parameters.AddWithValue("@id", id);
            updateCmd.ExecuteNonQuery();
        }

        protected override void OnClosed(EventArgs e)
        {
            _engineCancellation?.Cancel();
            _engineCancellation?.Dispose();
            base.OnClosed(e);
        }
    }
}
