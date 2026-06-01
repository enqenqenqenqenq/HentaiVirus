using HentaiVirus.Database;
using System.Windows;

using System.Windows;
using HentaiVirus.Database;

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

            // Инициализация БД
            var dbManager = new DatabaseManager();
            dbManager.InitializeDatabase();

            // TODO: Скрыть окно 
        }
    }
}