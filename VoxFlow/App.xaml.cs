using System.Configuration;
using System.Data;
using System.Windows;
using VoxFlow.Core;
using VoxFlow.UI;

namespace VoxFlow
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Загружаем настройки перед показом окна
            Settings.LoadAppSettings();
            
            // Сбрасываем флаг IsStarted при каждом запуске приложения
            // (это новый запуск, модели еще не запущены)
            Settings.AppSettings.IsStarted = false;
            Settings.SaveAppSettings();
            
            // Сначала показываем окно настроек
            var settingsWindow = new SettingsWindow();
            bool startRequested = false;
            
            settingsWindow.StartRequested += async (sender, args) =>
            {
                startRequested = true;
                
                // После нажатия "Запустить" открываем главное окно
                var mainWindow = new MainWindow();
                mainWindow.Show();
                
                // Предварительно запускаем модели при запуске только для Whisper
                if (Settings.AppSettings.SelectedSttEngine == SttEngine.Whisper)
                {
                    await mainWindow.WarmUpModels();
                }
            };
            
            // Если пользователь закрыл окно настроек без нажатия "Запустить", закрываем приложение
            settingsWindow.Closed += (sender, args) =>
            {
                if (!startRequested)
                {
                    // Пользователь закрыл окно без нажатия "Запустить" (например, через X)
                    Shutdown();
                }
            };
            
            settingsWindow.Show();
        }
    }
}
