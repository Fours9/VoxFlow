using Newtonsoft.Json;
using System.IO;
using System.Windows.Input;

namespace VoxFlow.Core
{
    public class HotkeySettings
    {
        public Key ToggleLiveDisabled { get; set; } = Key.F3;
        public ModifierKeys ToggleLiveDisabledModifiers { get; set; } = ModifierKeys.None;

        public Key ClearDraft { get; set; } = Key.F4;
        public ModifierKeys ClearDraftModifiers { get; set; } = ModifierKeys.None;

        public Key TogglePause { get; set; } = Key.F7;
        public ModifierKeys TogglePauseModifiers { get; set; } = ModifierKeys.None;

        public Key PasteAndResume { get; set; } = Key.F8;
        public ModifierKeys PasteAndResumeModifiers { get; set; } = ModifierKeys.None;
    }

    public enum SttEngine
    {
        Whisper = 0,
        Vosk = 1
    }

    public enum BaseLanguage
    {
        Uk,
        Ru,
        En
    }

    public sealed class AppSettings
    {
        // Используем существующий whisper-cli.exe из tools/whisper
        public string WhisperExePath { get; set; } = @"tools\whisper\whisper-cli.exe";
        // Путь к многоязычной модели base (быстрее, чем small)
        // Доступные модели: tiny, base, small, medium, large
        // Чем больше модель, тем лучше качество, но медленнее работа
        public string WhisperModelPath { get; set; } = @"tools\whisper\models\ggml-base.bin";
        // Выбор модели Whisper: "base" или "small"
        // base - быстрее, но менее точная
        // small - медленнее, но более точная
        public string WhisperModel { get; set; } = "base";
        // Язык для Whisper (пустая строка = автоопределение, или код языка: "uk", "en", "ru" и т.д.)
        // Список поддерживаемых языков: https://github.com/ggerganov/whisper.cpp/blob/master/whisper.h
        public string WhisperLanguage { get; set; } = ""; // Пустая строка = автоопределение по умолчанию
        // Количество запускаемых WhisperService процессов (и соответственно количество создаваемых WhisperRunner экземпляров)
        // Доступные значения: 1, 2, 3, 4
        // Больше процессов = больше параллелизма, но больше потребление памяти и CPU
        public int WhisperModelCount { get; set; } = 2;
        // Количество экземпляров Vosk для параллельной обработки (аналогично WhisperModelCount)
        // Доступные значения: 1, 2, 3, 4
        // Больше экземпляров = больше параллелизма, но больше потребление памяти и CPU
        public int VoskModelCount { get; set; } = 2;
        // Длительность аудио окна в секундах (допускаются дробные значения, например 0.3)
        public double WindowSizeSec { get; set; } = 3.0;
        // Интервал создания нового окна в секундах (0 = без перекрытия, каждое окно независимо, допускаются дробные значения)
        public double StepSec { get; set; } = 0.0;
        // Флаг, указывающий, что приложение уже запущено (модели загружены)
        public bool IsStarted { get; set; } = false;

        // Выбранный движок распознавания речи
        public SttEngine SelectedSttEngine { get; set; } = SttEngine.Whisper;

        // Относительный путь к модели Vosk от корня solution (папка модели)
        // По умолчанию ожидаем модель в каталоге tools/VoskModels/model рядом с solution
        public string VoskModelRelativePath { get; set; } = @"tools\VoskModels\model";
        // Частота дискретизации для Vosk (должна соответствовать захвату аудио)
        public int VoskSampleRate { get; set; } = 16000;

        // Базовый язык сессии для Vosk (по умолчанию украинский)
        public BaseLanguage SelectedBaseLanguage { get; set; } = BaseLanguage.Uk;

        // Имена папок моделей Vosk для каждого языка (относительно tools\VoskModels\model\{LANG}\)
        public string VoskModelRu { get; set; } = "vosk-model-small-ru-0.22";
        public string VoskModelEn { get; set; } = "vosk-model-small-en-us-0.15";
        public string VoskModelUk { get; set; } = "vosk-model-small-uk-v3-small";

        // Включение моделей Vosk по языкам (снятая галочка = модель не загружается)
        public bool VoskModelRuEnabled { get; set; } = true;
        public bool VoskModelEnEnabled { get; set; } = true;
        public bool VoskModelUkEnabled { get; set; } = true;

        /// <summary>Папка для копирования WAV-окон, отправляемых в STT (для прослушивания). Пустая строка = не копировать.</summary>
        public string SttInputDebugFolder { get; set; } = "";

        // Устаревшие пути — только для миграции из старых настроек (не сохраняются в JSON)
        public string VoskModelRuPath { get; set; } = "";
        public string VoskModelEnPath { get; set; } = "";
        public string VoskModelUkPath { get; set; } = "";

        public bool ShouldSerializeVoskModelRuPath() => false;
        public bool ShouldSerializeVoskModelEnPath() => false;
        public bool ShouldSerializeVoskModelUkPath() => false;
    }

    public static class Settings
    {
        private static readonly string SettingsFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "settings.json"
        );

        private static readonly string AppSettingsFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "appsettings.json"
        );

        private static HotkeySettings? _current;
        private static AppSettings? _appSettings;

        public static HotkeySettings Current
        {
            get
            {
                if (_current == null)
                {
                    Load();
                }
                return _current!;
            }
        }

        public static AppSettings AppSettings
        {
            get
            {
                if (_appSettings == null)
                {
                    LoadAppSettings();
                }
                return _appSettings!;
            }
        }

        public static void Load()
        {
            if (File.Exists(SettingsFilePath))
            {
                try
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    _current = JsonConvert.DeserializeObject<HotkeySettings>(json);
                }
                catch
                {
                    // Якщо не вдалося завантажити, використати дефолтні значення
                }
            }

            if (_current == null)
            {
                _current = new HotkeySettings();
            }
        }

        public static void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_current, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // Обробка помилок збереження
            }
        }

        public static void LoadAppSettings()
        {
            if (File.Exists(AppSettingsFilePath))
            {
                try
                {
                    string json = File.ReadAllText(AppSettingsFilePath);
                    _appSettings = JsonConvert.DeserializeObject<AppSettings>(json);
                }
                catch
                {
                    // Якщо не вдалося завантажити, використати дефолтні значення
                }
            }

            if (_appSettings == null)
            {
                _appSettings = new AppSettings();
                SaveAppSettings();
            }

            // Миграция: извлечь имена моделей из старых путей
            MigrateVoskModelSettings();
            // Обновляем путь к модели на основе выбранной модели (для обратной совместимости)
            UpdateModelPathFromModelType();
        }

        private static void MigrateVoskModelSettings()
        {
            if (_appSettings == null) return;
            if (!string.IsNullOrWhiteSpace(_appSettings.VoskModelRuPath))
            {
                _appSettings.VoskModelRu = Path.GetFileName(_appSettings.VoskModelRuPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            if (!string.IsNullOrWhiteSpace(_appSettings.VoskModelEnPath))
            {
                _appSettings.VoskModelEn = Path.GetFileName(_appSettings.VoskModelEnPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            if (!string.IsNullOrWhiteSpace(_appSettings.VoskModelUkPath))
            {
                _appSettings.VoskModelUk = Path.GetFileName(_appSettings.VoskModelUkPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        
        private static void UpdateModelPathFromModelType()
        {
            if (_appSettings == null) return;
            
            // Если WhisperModel задан, обновляем путь к модели
            if (!string.IsNullOrEmpty(_appSettings.WhisperModel))
            {
                string modelFileName = _appSettings.WhisperModel.ToLowerInvariant() switch
                {
                    "base" => "ggml-base.bin",
                    "small" => "ggml-small.bin",
                    _ => "ggml-base.bin" // По умолчанию base
                };
                _appSettings.WhisperModelPath = $@"tools\whisper\models\{modelFileName}";
            }
        }
        
        /// <summary>
        /// Получить относительный путь к модели Vosk для языка.
        /// </summary>
        public static string GetVoskModelPath(BaseLanguage lang)
        {
            if (_appSettings == null) LoadAppSettings();
            string modelFolder = lang switch
            {
                BaseLanguage.Ru => _appSettings!.VoskModelRu,
                BaseLanguage.En => _appSettings!.VoskModelEn,
                BaseLanguage.Uk => _appSettings!.VoskModelUk,
                _ => _appSettings!.VoskModelUk
            };
            string langFolder = lang switch
            {
                BaseLanguage.Ru => "RU",
                BaseLanguage.En => "EN",
                BaseLanguage.Uk => "UK",
                _ => "UK"
            };
            return Path.Combine(@"tools\VoskModels\model", langFolder, modelFolder);
        }

        /// <summary>
        /// Получить разрешённый (абсолютный) путь к папке модели Vosk для языка.
        /// Если папка по новому пути (tools\VoskModels\model\...) не существует, пробует старый путь (VoskModels\model\...) для обратной совместимости.
        /// </summary>
        public static string GetResolvedVoskModelPathWithFallback(BaseLanguage lang)
        {
            string newRelative = GetVoskModelPath(lang);
            string resolved = ResolveSolutionRelativePath(newRelative);
            if (Directory.Exists(resolved))
                return resolved;
            // Fallback: старый путь до переноса в tools
            if (_appSettings == null) LoadAppSettings();
            string modelFolder = lang switch
            {
                BaseLanguage.Ru => _appSettings!.VoskModelRu,
                BaseLanguage.En => _appSettings!.VoskModelEn,
                BaseLanguage.Uk => _appSettings!.VoskModelUk,
                _ => _appSettings!.VoskModelUk
            };
            string langFolder = lang switch
            {
                BaseLanguage.Ru => "RU",
                BaseLanguage.En => "EN",
                BaseLanguage.Uk => "UK",
                _ => "UK"
            };
            string oldRelative = Path.Combine(@"VoskModels\model", langFolder, modelFolder);
            return ResolveSolutionRelativePath(oldRelative);
        }

        /// <summary>
        /// Получить путь к модели на основе типа модели
        /// </summary>
        public static string GetModelPath(string modelType)
        {
            string modelFileName = modelType.ToLowerInvariant() switch
            {
                "base" => "ggml-base.bin",
                "small" => "ggml-small.bin",
                _ => "ggml-base.bin" // По умолчанию base
            };
            return $@"tools\whisper\models\{modelFileName}";
        }

        public static void SaveAppSettings()
        {
            try
            {
                if (_appSettings != null)
                {
                    string json = JsonConvert.SerializeObject(_appSettings, Formatting.Indented);
                    File.WriteAllText(AppSettingsFilePath, json);
                }
            }
            catch
            {
                // Обробка помилок збереження
            }
        }

        public static string ResolveSolutionRelativePath(string relativePath)
        {
            // Резолвим путь относительно solution root
            // BaseDirectory = D:\C#\VoxFlow\VoxFlow\bin\Debug\net8.0-windows\
            // Solution root = D:\C#\VoxFlow\
            // Нужно подняться на 4 уровня вверх: net8.0-windows -> Debug -> bin -> VoxFlow -> solution root
            
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            System.Diagnostics.Debug.WriteLine($"[Settings] BaseDirectory: {currentDir}");
            
            // Найти solution root через поиск .sln файла (надежнее)
            var solutionDir = currentDir;
            while (!string.IsNullOrEmpty(solutionDir))
            {
                try
                {
                    var slnFiles = Directory.GetFiles(solutionDir, "*.sln");
                    if (slnFiles.Length > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Settings] Found solution file at: {solutionDir}");
                        break;
                    }
                }
                catch
                {
                    // Игнорировать ошибки доступа к файлам
                }
                
                var parent = Path.GetDirectoryName(solutionDir);
                if (string.IsNullOrEmpty(parent) || parent == solutionDir)
                    break;
                solutionDir = parent;
            }
            
            // Если не нашли через .sln, поднимаемся на 4 уровня вверх
            if (string.IsNullOrEmpty(solutionDir))
            {
                solutionDir = currentDir;
                for (int i = 0; i < 4; i++)
                {
                    var parent = Path.GetDirectoryName(solutionDir);
                    if (string.IsNullOrEmpty(parent) || parent == solutionDir)
                        break;
                    solutionDir = parent;
                }
                System.Diagnostics.Debug.WriteLine($"[Settings] Solution dir (by levels): {solutionDir}");
            }
            
            var resolvedPath = Path.Combine(solutionDir, relativePath);
            System.Diagnostics.Debug.WriteLine($"[Settings] Resolved path: {resolvedPath}");
            return resolvedPath;
        }
    }
}