using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using VoxFlow.Core;

#pragma warning disable CS0266 // Явные приведения типов добавлены, но IDE может показывать ошибки из-за кэша

namespace VoxFlow.UI
{
    public partial class SettingsWindow : Window
    {
        private HotkeySettings _settings;
        private HotkeySettings _originalSettings;
        private AppSettings _appSettings;
        private bool _inputInMilliseconds = false; // Флаг режима ввода: false = секунды, true = миллисекунды
        
        public event EventHandler? StartRequested;

        public SettingsWindow()
        {
            InitializeComponent();
            _settings = new HotkeySettings
            {
                ToggleLiveDisabled = Settings.Current.ToggleLiveDisabled,
                ToggleLiveDisabledModifiers = Settings.Current.ToggleLiveDisabledModifiers,
                ClearDraft = Settings.Current.ClearDraft,
                ClearDraftModifiers = Settings.Current.ClearDraftModifiers,
                TogglePause = Settings.Current.TogglePause,
                TogglePauseModifiers = Settings.Current.TogglePauseModifiers,
                PasteAndResume = Settings.Current.PasteAndResume,
                PasteAndResumeModifiers = Settings.Current.PasteAndResumeModifiers
            };
            _originalSettings = _settings;
            
            _appSettings = new AppSettings
            {
                WhisperModel = Settings.AppSettings.WhisperModel ?? "base",
                WhisperLanguage = Settings.AppSettings.WhisperLanguage ?? "", // Пустая строка = автоопределение
                WhisperModelCount = Settings.AppSettings.WhisperModelCount > 0 ? Settings.AppSettings.WhisperModelCount : 2, // По умолчанию 2
                VoskModelCount = Settings.AppSettings.VoskModelCount > 0 ? Settings.AppSettings.VoskModelCount : 2, // По умолчанию 2
                WindowSizeSec = Settings.AppSettings.WindowSizeSec > 0.0 ? (double)Settings.AppSettings.WindowSizeSec : 3.0, // По умолчанию 3 секунды
                StepSec = Settings.AppSettings.StepSec >= 0.0 ? (double)Settings.AppSettings.StepSec : 0.0, // По умолчанию 0 секунд
                SelectedSttEngine = Settings.AppSettings.SelectedSttEngine,
                VoskModelRelativePath = Settings.AppSettings.VoskModelRelativePath,
                VoskSampleRate = Settings.AppSettings.VoskSampleRate,
                SelectedBaseLanguage = Settings.AppSettings.SelectedBaseLanguage,
                VoskModelRu = Settings.AppSettings.VoskModelRu ?? "vosk-model-small-ru-0.22",
                VoskModelEn = Settings.AppSettings.VoskModelEn ?? "vosk-model-small-en-us-0.15",
                VoskModelUk = Settings.AppSettings.VoskModelUk ?? "vosk-model-small-uk-v3-small",
                VoskModelRuEnabled = Settings.AppSettings.VoskModelRuEnabled,
                VoskModelEnEnabled = Settings.AppSettings.VoskModelEnEnabled,
                VoskModelUkEnabled = Settings.AppSettings.VoskModelUkEnabled,
                SttInputDebugFolder = Settings.AppSettings.SttInputDebugFolder ?? ""
            };

            InitializeComboBoxes();
            InitializeInputMode();
            InitializeTextBoxes();
            SttInputDebugFolderTextBox.Text = _appSettings.SttInputDebugFolder ?? "";
            InitializeVoskCheckboxes();
            UpdateSttEngineVisibility();
            
            // Кнопка OK скрыта по умолчанию
            OkButton.Visibility = Visibility.Collapsed;
            
            // Если приложение уже запущено, заблокировать элементы
            if (Settings.AppSettings.IsStarted)
            {
                WhisperModelCombo.IsEnabled = false;
                WhisperLanguageCombo.IsEnabled = false;
                WhisperModelCountCombo.IsEnabled = false;
                VoskModelRuCheck.IsEnabled = false;
                VoskModelEnCheck.IsEnabled = false;
                VoskModelUkCheck.IsEnabled = false;
                VoskModelRuCombo.IsEnabled = false;
                VoskModelEnCombo.IsEnabled = false;
                VoskModelUkCombo.IsEnabled = false;
                VoskModelCountCombo.IsEnabled = false;
                WindowSizeSecTextBox.IsEnabled = false;
                StepSecTextBox.IsEnabled = false;
                InputInMillisecondsCheck.IsEnabled = false;
                StartButton.IsEnabled = false;
                StartButton.Visibility = Visibility.Collapsed;
                // Показать кнопку OK, если приложение уже запущено
                OkButton.Visibility = Visibility.Visible;
            }
        }

        private void InitializeComboBoxes()
        {
            // Установить выбранный движок STT
            foreach (System.Windows.Controls.ComboBoxItem item in SttEngineCombo.Items)
            {
                if (item.Tag?.ToString() == _appSettings.SelectedSttEngine.ToString())
                {
                    SttEngineCombo.SelectedItem = item;
                    break;
                }
            }
            if (SttEngineCombo.SelectedItem == null)
            {
                SttEngineCombo.SelectedIndex = 0; // Whisper по умолчанию
            }

            // Установить базовый язык Vosk
            foreach (System.Windows.Controls.ComboBoxItem item in VoskBaseLanguageCombo.Items)
            {
                if (item.Tag?.ToString() == _appSettings.SelectedBaseLanguage.ToString())
                {
                    VoskBaseLanguageCombo.SelectedItem = item;
                    break;
                }
            }
            if (VoskBaseLanguageCombo.SelectedItem == null)
            {
                VoskBaseLanguageCombo.SelectedIndex = 0; // Uk по умолчанию
            }

            // Установить выбранные модели Vosk по языкам
            SelectVoskModelInCombo(VoskModelRuCombo, _appSettings.VoskModelRu);
            SelectVoskModelInCombo(VoskModelEnCombo, _appSettings.VoskModelEn);
            SelectVoskModelInCombo(VoskModelUkCombo, _appSettings.VoskModelUk);

            // Установить выбранную модель Whisper
            foreach (System.Windows.Controls.ComboBoxItem item in WhisperModelCombo.Items)
            {
                if (item.Tag?.ToString() == _appSettings.WhisperModel)
                {
                    WhisperModelCombo.SelectedItem = item;
                    break;
                }
            }
            // Если ничего не выбрано, выбрать первый элемент (base)
            if (WhisperModelCombo.SelectedItem == null)
            {
                WhisperModelCombo.SelectedIndex = 0;
            }
            
            // Установить выбранный язык Whisper
            foreach (System.Windows.Controls.ComboBoxItem item in WhisperLanguageCombo.Items)
            {
                if (item.Tag?.ToString() == (_appSettings.WhisperLanguage ?? ""))
                {
                    WhisperLanguageCombo.SelectedItem = item;
                    break;
                }
            }
            // Если ничего не выбрано, выбрать первый элемент (auto)
            if (WhisperLanguageCombo.SelectedItem == null)
            {
                WhisperLanguageCombo.SelectedIndex = 0;
            }
            
            // Установить выбранное количество моделей
            foreach (System.Windows.Controls.ComboBoxItem item in WhisperModelCountCombo.Items)
            {
                if (item.Tag?.ToString() == _appSettings.WhisperModelCount.ToString())
                {
                    WhisperModelCountCombo.SelectedItem = item;
                    break;
                }
            }
            // Если ничего не выбрано, выбрать элемент с Tag="2" (по умолчанию)
            if (WhisperModelCountCombo.SelectedItem == null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in WhisperModelCountCombo.Items)
                {
                    if (item.Tag?.ToString() == "2")
                    {
                        WhisperModelCountCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            
            // Установить выбранное количество экземпляров Vosk
            foreach (System.Windows.Controls.ComboBoxItem item in VoskModelCountCombo.Items)
            {
                if (item.Tag?.ToString() == _appSettings.VoskModelCount.ToString())
                {
                    VoskModelCountCombo.SelectedItem = item;
                    break;
                }
            }
            // Если ничего не выбрано, выбрать элемент с Tag="2" (по умолчанию)
            if (VoskModelCountCombo.SelectedItem == null)
            {
                foreach (System.Windows.Controls.ComboBoxItem item in VoskModelCountCombo.Items)
                {
                    if (item.Tag?.ToString() == "2")
                    {
                        VoskModelCountCombo.SelectedItem = item;
                        break;
                    }
                }
            }
            
            // Спрощена версія - можна розширити для вибору модифікаторів
            ToggleLiveDisabledCombo.ItemsSource = Enum.GetValues(typeof(Key)).Cast<Key>();
            ToggleLiveDisabledCombo.SelectedItem = _settings.ToggleLiveDisabled;

            ClearDraftCombo.ItemsSource = Enum.GetValues(typeof(Key)).Cast<Key>();
            ClearDraftCombo.SelectedItem = _settings.ClearDraft;

            TogglePauseCombo.ItemsSource = Enum.GetValues(typeof(Key)).Cast<Key>();
            TogglePauseCombo.SelectedItem = _settings.TogglePause;

            PasteAndResumeCombo.ItemsSource = Enum.GetValues(typeof(Key)).Cast<Key>();
            PasteAndResumeCombo.SelectedItem = _settings.PasteAndResume;
        }

        private void InitializeVoskCheckboxes()
        {
            VoskModelRuCheck.IsChecked = _appSettings.VoskModelRuEnabled;
            VoskModelEnCheck.IsChecked = _appSettings.VoskModelEnEnabled;
            VoskModelUkCheck.IsChecked = _appSettings.VoskModelUkEnabled;
            UpdateVoskComboEnabledState();
        }

        private void UpdateVoskComboEnabledState()
        {
            VoskModelRuCombo.IsEnabled = _appSettings.VoskModelRuEnabled;
            VoskModelEnCombo.IsEnabled = _appSettings.VoskModelEnEnabled;
            VoskModelUkCombo.IsEnabled = _appSettings.VoskModelUkEnabled;
        }

        private void UpdateSttEngineVisibility()
        {
            if (_appSettings.SelectedSttEngine == SttEngine.Vosk)
            {
                VoskSettingsPanel.Visibility = Visibility.Visible;
                WhisperSettingsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                VoskSettingsPanel.Visibility = Visibility.Collapsed;
                WhisperSettingsPanel.Visibility = Visibility.Visible;
            }
        }

        private void InitializeInputMode()
        {
            // По умолчанию режим секунд (галочка выключена)
            InputInMillisecondsCheck.IsChecked = false;
            _inputInMilliseconds = false;
            UpdateInputModeLabels();
        }

        private void UpdateInputModeLabels()
        {
            if (_inputInMilliseconds)
            {
                WindowSizeLabel.Text = "Длительность аудио (миллисекунды, например 300):";
                StepLabel.Text = "Интервал между аудио (миллисекунды, например 100):";
            }
            else
            {
                WindowSizeLabel.Text = "Длительность аудио (секунды, например 0.3):";
                StepLabel.Text = "Интервал между аудио (секунды, например 0.1):";
            }
        }

        private void InitializeTextBoxes()
        {
            UpdateTextBoxValues();
        }

        private void UpdateTextBoxValues()
        {
            if (_inputInMilliseconds)
            {
                // Отображаем в миллисекундах
                WindowSizeSecTextBox.Text = ((int)(_appSettings.WindowSizeSec * 1000)).ToString();
                StepSecTextBox.Text = ((int)(_appSettings.StepSec * 1000)).ToString();
            }
            else
            {
                // Отображаем в секундах с дробной частью
                WindowSizeSecTextBox.Text = _appSettings.WindowSizeSec.ToString("0.###", CultureInfo.InvariantCulture);
                StepSecTextBox.Text = _appSettings.StepSec.ToString("0.###", CultureInfo.InvariantCulture);
            }
        }

        private static void SelectVoskModelInCombo(System.Windows.Controls.ComboBox combo, string modelFolder)
        {
            foreach (System.Windows.Controls.ComboBoxItem item in combo.Items)
            {
                if (item.Tag?.ToString() == modelFolder)
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private void VoskBaseLanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VoskBaseLanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                if (selectedItem.Tag is string langStr &&
                    Enum.TryParse<BaseLanguage>(langStr, out var baseLang))
                {
                    _appSettings.SelectedBaseLanguage = baseLang;
                }
            }
        }

        private void VoskModelRuCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VoskModelRuCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                selectedItem.Tag is string modelFolder)
            {
                _appSettings.VoskModelRu = modelFolder;
            }
        }

        private void VoskModelEnCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VoskModelEnCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                selectedItem.Tag is string modelFolder)
            {
                _appSettings.VoskModelEn = modelFolder;
            }
        }

        private void VoskModelUkCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VoskModelUkCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem &&
                selectedItem.Tag is string modelFolder)
            {
                _appSettings.VoskModelUk = modelFolder;
            }
        }

        private void SttEngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SttEngineCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                if (selectedItem.Tag is string engineStr &&
                    Enum.TryParse<SttEngine>(engineStr, out var engine))
                {
                    _appSettings.SelectedSttEngine = engine;
                    UpdateSttEngineVisibility();
                }
            }
        }

        private void VoskModelCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (VoskModelRuCheck.IsChecked.HasValue)
                _appSettings.VoskModelRuEnabled = VoskModelRuCheck.IsChecked.Value;
            if (VoskModelEnCheck.IsChecked.HasValue)
                _appSettings.VoskModelEnEnabled = VoskModelEnCheck.IsChecked.Value;
            if (VoskModelUkCheck.IsChecked.HasValue)
                _appSettings.VoskModelUkEnabled = VoskModelUkCheck.IsChecked.Value;
            UpdateVoskComboEnabledState();
        }
        
        private void WhisperModelCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (WhisperModelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                if (selectedItem.Tag is string modelType)
                {
                    _appSettings.WhisperModel = modelType;
                }
            }
        }

        private void WhisperLanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (WhisperLanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                if (selectedItem.Tag is string languageCode)
                {
                    _appSettings.WhisperLanguage = languageCode;
                }
            }
        }

        private void WhisperModelCountCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (WhisperModelCountCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                if (selectedItem.Tag is string countStr && int.TryParse(countStr, out int count))
                {
                    _appSettings.WhisperModelCount = count;
                }
            }
        }

        private void VoskModelCountCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (VoskModelCountCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                if (selectedItem.Tag is string countStr && int.TryParse(countStr, out int count))
                {
                    _appSettings.VoskModelCount = count;
                }
            }
        }

        private void InputModeCheck_Changed(object sender, RoutedEventArgs e)
        {
            bool newMode = InputInMillisecondsCheck.IsChecked == true;
            
            // Если режим изменился, конвертируем текущие значения
            if (newMode != _inputInMilliseconds)
            {
                // Сохраняем текущие значения из TextBox перед переключением
                ParseAndSaveCurrentValues();
                
                // Переключаем режим
                _inputInMilliseconds = newMode;
                
                // Обновляем метки
                UpdateInputModeLabels();
                
                // Обновляем значения в TextBox с конвертацией
                UpdateTextBoxValues();
            }
        }

        private void ParseAndSaveCurrentValues()
        {
            // Парсим текущие значения из TextBox и сохраняем в _appSettings
            if (double.TryParse(WindowSizeSecTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double windowValue))
            {
                if (_inputInMilliseconds)
                {
                    // Если сейчас режим миллисекунд, конвертируем в секунды
                    _appSettings.WindowSizeSec = (double)(windowValue / 1000.0);
                }
                else
                {
                    // Если режим секунд, сохраняем как есть
                    _appSettings.WindowSizeSec = (double)windowValue;
                }
            }

            if (double.TryParse(StepSecTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double stepValue))
            {
                if (_inputInMilliseconds)
                {
                    // Если сейчас режим миллисекунд, конвертируем в секунды
                    _appSettings.StepSec = (double)(stepValue / 1000.0);
                }
                else
                {
                    // Если режим секунд, сохраняем как есть
                    _appSettings.StepSec = (double)stepValue;
                }
            }
        }

        private void WindowSizeSecTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (double.TryParse(WindowSizeSecTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double windowValue))
            {
                if (_inputInMilliseconds)
                {
                    // Ввод в миллисекундах, конвертируем в секунды
                    if (windowValue > 0)
                    {
                        _appSettings.WindowSizeSec = (double)(windowValue / 1000.0);
                    }
                }
                else
                {
                    // Ввод в секундах
                    if (windowValue > 0)
                    {
                        _appSettings.WindowSizeSec = (double)windowValue;
                    }
                }
            }
        }

        private void StepSecTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (double.TryParse(StepSecTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double stepValue))
            {
                if (_inputInMilliseconds)
                {
                    // Ввод в миллисекундах, конвертируем в секунды
                    if (stepValue >= 0)
                    {
                        _appSettings.StepSec = (double)(stepValue / 1000.0);
                    }
                }
                else
                {
                    // Ввод в секундах
                    if (stepValue >= 0)
                    {
                        _appSettings.StepSec = (double)stepValue;
                    }
                }
            }
        }

        private void HotkeyCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Оновити налаштування при зміні
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            _settings = new HotkeySettings();
            InitializeComboBoxes();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            Close();
        }
        
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            // Заблокировать элементы интерфейса перед запуском
            WhisperModelCombo.IsEnabled = false;
            WhisperLanguageCombo.IsEnabled = false;
            WhisperModelCountCombo.IsEnabled = false;
            VoskModelRuCheck.IsEnabled = false;
            VoskModelEnCheck.IsEnabled = false;
            VoskModelUkCheck.IsEnabled = false;
            VoskModelRuCombo.IsEnabled = false;
            VoskModelEnCombo.IsEnabled = false;
            VoskModelUkCombo.IsEnabled = false;
            VoskModelCountCombo.IsEnabled = false;
            WindowSizeSecTextBox.IsEnabled = false;
            StepSecTextBox.IsEnabled = false;
            
            // Скрыть и отключить кнопку "Запустить"
            StartButton.IsEnabled = false;
            StartButton.Visibility = Visibility.Collapsed;
            
            // Показать кнопку "OK" после нажатия на "Запустить"
            OkButton.Visibility = Visibility.Visible;
            
            // Принудительно обновить UI немедленно
            this.UpdateLayout();
            this.InvalidateVisual();
            
            // Обработать все pending UI updates через Dispatcher
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            
            // Сохранить настройки перед запуском и установить флаг запуска
            SaveSettings();
            Settings.AppSettings.IsStarted = true;
            Settings.SaveAppSettings();
            
            // Вызвать событие для запуска главного окна (асинхронно, чтобы UI успел обновиться)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StartRequested?.Invoke(this, EventArgs.Empty);
            }), System.Windows.Threading.DispatcherPriority.Background);
            
            // Закрыть окно после того, как все изменения применились
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Close();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        
        private void SaveSettings()
        {
            // Зберегти налаштування хоткеев
            if (ToggleLiveDisabledCombo.SelectedItem is Key key1)
            {
                Settings.Current.ToggleLiveDisabled = key1;
            }
            if (ClearDraftCombo.SelectedItem is Key key2)
            {
                Settings.Current.ClearDraft = key2;
            }
            if (TogglePauseCombo.SelectedItem is Key key3)
            {
                Settings.Current.TogglePause = key3;
            }
            if (PasteAndResumeCombo.SelectedItem is Key key4)
            {
                Settings.Current.PasteAndResume = key4;
            }

            Settings.Save();

            // Сохранить выбранный движок STT
            Settings.AppSettings.SelectedSttEngine = _appSettings.SelectedSttEngine;

            // Сохранить базовый язык Vosk
            Settings.AppSettings.SelectedBaseLanguage = _appSettings.SelectedBaseLanguage;

            // Сохранить выбранные модели Vosk по языкам
            Settings.AppSettings.VoskModelRu = _appSettings.VoskModelRu ?? "vosk-model-small-ru-0.22";
            Settings.AppSettings.VoskModelEn = _appSettings.VoskModelEn ?? "vosk-model-small-en-us-0.15";
            Settings.AppSettings.VoskModelUk = _appSettings.VoskModelUk ?? "vosk-model-small-uk-v3-small";
            Settings.AppSettings.VoskModelRuEnabled = _appSettings.VoskModelRuEnabled;
            Settings.AppSettings.VoskModelEnEnabled = _appSettings.VoskModelEnEnabled;
            Settings.AppSettings.VoskModelUkEnabled = _appSettings.VoskModelUkEnabled;
            Settings.AppSettings.SttInputDebugFolder = SttInputDebugFolderTextBox.Text?.Trim() ?? "";
            
            // Сохранить выбранное количество экземпляров Vosk
            if (VoskModelCountCombo.SelectedItem is System.Windows.Controls.ComboBoxItem voskCountItem)
            {
                if (voskCountItem.Tag is string voskCountStr && int.TryParse(voskCountStr, out int voskCount))
                {
                    Settings.AppSettings.VoskModelCount = voskCount;
                }
            }
            else
            {
                // Если ничего не выбрано, используем сохраненное значение
                Settings.AppSettings.VoskModelCount = _appSettings.VoskModelCount;
            }

            // Сохранить настройки модели Whisper
            // Обновляем выбранную модель и путь к ней
            if (WhisperModelCombo.SelectedItem is System.Windows.Controls.ComboBoxItem selectedItem)
            {
                if (selectedItem.Tag is string modelType)
                {
                    Settings.AppSettings.WhisperModel = modelType;
                    Settings.AppSettings.WhisperModelPath = Settings.GetModelPath(modelType);
                }
            }
            else
            {
                // Если ничего не выбрано, используем сохраненное значение
                Settings.AppSettings.WhisperModel = _appSettings.WhisperModel;
                Settings.AppSettings.WhisperModelPath = Settings.GetModelPath(_appSettings.WhisperModel);
            }
            
            // Сохранить выбранный язык
            if (WhisperLanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem languageItem)
            {
                if (languageItem.Tag is string languageCode)
                {
                    Settings.AppSettings.WhisperLanguage = languageCode;
                }
            }
            else
            {
                // Если ничего не выбрано, используем сохраненное значение
                Settings.AppSettings.WhisperLanguage = _appSettings.WhisperLanguage ?? "";
            }
            
            // Сохранить выбранное количество моделей
            if (WhisperModelCountCombo.SelectedItem is System.Windows.Controls.ComboBoxItem countItem)
            {
                if (countItem.Tag is string countStr && int.TryParse(countStr, out int count))
                {
                    Settings.AppSettings.WhisperModelCount = count;
                }
            }
            else
            {
                // Если ничего не выбрано, используем сохраненное значение
                Settings.AppSettings.WhisperModelCount = _appSettings.WhisperModelCount;
            }
            
            // Сохранить длительность аудио окна
            ParseAndSaveCurrentValues();
            
            // Сохраняем значения из _appSettings (уже в секундах)
            if (_appSettings.WindowSizeSec > 0)
            {
                Settings.AppSettings.WindowSizeSec = (double)_appSettings.WindowSizeSec;
            }
            else
            {
                Settings.AppSettings.WindowSizeSec = (double)3.0; // Дефолтное значение
            }
            
            // Сохранить интервал между аудио окнами
            if (_appSettings.StepSec >= 0)
            {
                Settings.AppSettings.StepSec = (double)_appSettings.StepSec;
            }
            else
            {
                Settings.AppSettings.StepSec = (double)0.0; // Дефолтное значение
            }
            
            Settings.SaveAppSettings();
        }
    }
}