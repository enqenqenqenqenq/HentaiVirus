using HentaiVirus.Database;
using System.Windows;

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
            MessageBox.Show("Соглашение принято. Запуск фоновых служб...", "Информация", MessageBoxButton.OK,
                MessageBoxImage.Information);

            var dbManager = new DatabaseManager();
            dbManager.InitializeDatabase();

            this.Hide();

            System.Threading.Tasks.Task.Run(async () => { await StartCoreEngineAsync(); });
        }

        private async System.Threading.Tasks.Task StartCoreEngineAsync()
        {
            // Точка входа для ядра вируса
            // TODO: Здесь будет цикл вызова загрузчика и запуска игр
            await System.Threading.Tasks.Task.CompletedTask;
        }
    }
}