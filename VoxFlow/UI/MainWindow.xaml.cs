using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using VoxFlow.Audio;
using VoxFlow.Core;
using VoxFlow.Utils;

namespace VoxFlow.UI
{
    public partial class MainWindow : Window
    {
        private AudioPipeline? _audioPipeline;
        private HistoryController? _historyController;
        private PauseController? _pauseController;
        private EditorState? _editorState;
        private Hotkeys? _hotkeys;
        private HwndSource? _hwndSource;
        private int _lastHistorySegmentCount = 0;

        public MainWindow()
        {
            InitializeComponent();
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            // 1. Создать EditorState и подписаться на события
            _editorState = new EditorState();
            _editorState.SharedTextChanged += EditorState_SharedTextChanged;
            _editorState.ActiveEditorChanged += EditorState_ActiveEditorChanged;
            _editorState.LiveDisabledChanged += EditorState_LiveDisabledChanged;
            
            // 2. Создать HistoryController и подписаться на события
            _historyController = new HistoryController();
            _historyController.HistoryChanged += HistoryController_HistoryChanged;
            
            // 3. Создать PauseController и подписаться на события
            _pauseController = new PauseController();
            _pauseController.PauseStateChanged += PauseController_PauseStateChanged;
            
            // 4. Получить настройки
            var appSettings = Settings.AppSettings;
            string modelType = appSettings.WhisperModel ?? "base";
            string language = appSettings.WhisperLanguage ?? "";
            int modelCount = appSettings.WhisperModelCount > 0 ? appSettings.WhisperModelCount : 2;

            // 5. Создать движки STT в зависимости от выбранного движка
            var sttEngines = new List<ISttEngine>();

            if (appSettings.SelectedSttEngine == SttEngine.Whisper)
            {
                // Разрешить пути для Whisper
                string whisperServicePath = Settings.ResolveSolutionRelativePath(@"tools\whisper\WhisperService\bin\Debug\net8.0\WhisperService.exe");
                string modelPath = Settings.ResolveSolutionRelativePath(Settings.GetModelPath(modelType));

                System.Diagnostics.Debug.WriteLine($"[MainWindow] Initializing Whisper with model: {modelType} at path: {modelPath}, language: {language}");

                for (int i = 0; i < modelCount; i++)
                {
                    var runner = new WhisperRunner(whisperServicePath, modelPath, language);
                    sttEngines.Add(runner);
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Created WhisperRunner{i + 1} of {modelCount}");
                }
            }
            else
            {
                // Vosk: используем многоязычный движок (только включённые модели RU/EN/UK). Путь с fallback на старую папку VoskModels, если в tools ещё нет моделей.
                string? ruModelPath = appSettings.VoskModelRuEnabled
                    ? Settings.GetResolvedVoskModelPathWithFallback(BaseLanguage.Ru)
                    : null;
                string? enModelPath = appSettings.VoskModelEnEnabled
                    ? Settings.GetResolvedVoskModelPathWithFallback(BaseLanguage.En)
                    : null;
                string? ukModelPath = appSettings.VoskModelUkEnabled
                    ? Settings.GetResolvedVoskModelPathWithFallback(BaseLanguage.Uk)
                    : null;
                int sampleRate = appSettings.VoskSampleRate > 0 ? appSettings.VoskSampleRate : 16000;
                int voskModelCount = appSettings.VoskModelCount > 0 ? appSettings.VoskModelCount : 2;

                System.Diagnostics.Debug.WriteLine($"[MainWindow] Initializing MultiLang Vosk with RU='{ruModelPath ?? "(disabled)"}', EN='{enModelPath ?? "(disabled)"}', UK='{ukModelPath ?? "(disabled)"}', base={appSettings.SelectedBaseLanguage}, sampleRate: {sampleRate}, modelCount: {voskModelCount}");

                // Создаем несколько экземпляров Vosk для параллельной обработки (аналогично Whisper)
                for (int i = 0; i < voskModelCount; i++)
                {
                    var voskStt = new MultiLangVoskStt(
                        ruModelPath,
                        enModelPath,
                        ukModelPath,
                        sampleRate,
                        appSettings.SelectedBaseLanguage);
                    sttEngines.Add(voskStt);
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Created MultiLangVoskStt{i + 1} of {voskModelCount}");
                }
            }
            
            // 7. Создать вспомогательные компоненты
            var audioCapture = new AudioCapture();
            var windowBuffer = new WindowBuffer();
            var vadDetector = new VadDetector();
            vadDetector.SetPauseController(_pauseController);
            var diarizerRunner = new DiarizerRunner();
            var mergeEngine = new MergeEngine();
            var speakerTracker = new SpeakerTracker();
            
            // 8. Создать AudioPipeline
            _audioPipeline = new AudioPipeline(
                audioCapture,
                windowBuffer,
                vadDetector,
                sttEngines,
                diarizerRunner,
                mergeEngine,
                speakerTracker,
                _historyController
            );
            
            // 8.1. Передать PauseController в WindowBuffer
            windowBuffer.SetPauseController(_pauseController);
            
            // 9. Запустить захват аудио
            _audioPipeline.Start();

            // 9.1. Подписаться на изменение очередей для обновления счётчиков в UI
            _audioPipeline.QueueStatsChanged += OnQueueStatsChanged;

            // 10. Инициализировать горячие клавиши
            InitializeHotkeys();
            
            // 11. Обновить UI
            UpdateHistoryDisplay();
            UpdateStatus();
            UpdateQueueStatsDisplay();
            
            // 12. Убедиться, что индикатор паузы инициализирован после загрузки окна
            Loaded += (s, e) => UpdatePauseIndicator();
        }

        private void OnQueueStatsChanged()
        {
            Dispatcher.BeginInvoke(() => UpdateQueueStatsDisplay());
        }

        private void InitializeHotkeys()
        {
            _hotkeys = new Hotkeys();
            _hotkeys.ToggleLiveDisabled += Hotkeys_ToggleLiveDisabled;
            _hotkeys.ClearDraft += Hotkeys_ClearDraft;
            _hotkeys.TogglePause += Hotkeys_TogglePause;
            _hotkeys.PasteAndResume += Hotkeys_PasteAndResume;

            // Регистрируем горячие клавиши после загрузки окна
            Loaded += MainWindow_Loaded;
            SourceInitialized += MainWindow_SourceInitialized;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] MainWindow_Loaded: window loaded, thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}, isUIThread: {Dispatcher.CheckAccess()}");
            // Окно загружено, можно регистрировать горячие клавиши
            // Инициализировать индикатор паузы
            UpdatePauseIndicator();
            
            // Проверить, что все элементы UI доступны
            System.Diagnostics.Debug.WriteLine($"[MainWindow] MainWindow_Loaded: HistoryRichTextBox={HistoryRichTextBox != null}, StatusTextBlock={StatusTextBlock != null}, PauseIndicator found={this.FindName("PauseIndicator") != null}");
        }

        private void MainWindow_SourceInitialized(object sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);

            if (_hotkeys != null)
            {
                _hotkeys.RegisterHotkeys(hwnd);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_hotkeys != null)
            {
                _hotkeys.ProcessHotkey(hwnd, msg, wParam, lParam, ref handled);
            }
            return IntPtr.Zero;
        }

        private void Hotkeys_ToggleLiveDisabled()
        {
            if (_editorState != null)
            {
                _editorState.LiveDisabled = !_editorState.LiveDisabled;
            }
        }

        private void Hotkeys_ClearDraft()
        {
            Dispatcher.Invoke(() =>
            {
                DraftTextBox.Text = string.Empty;
                if (_editorState != null)
                {
                    _editorState.SharedEditorText = string.Empty;
                }
            });
        }

        private void Hotkeys_TogglePause()
        {
            if (_pauseController != null)
            {
                _pauseController.SetManualPause(!_pauseController.GlobalPaused);
            }
        }

        private void Hotkeys_PasteAndResume()
        {
            Dispatcher.Invoke(() =>
            {
                // Получить текст из активного редактора
                string textToPaste = string.Empty;
                if (_editorState != null)
                {
                    if (_editorState.ActiveEditor == ActiveEditor.Draft)
                    {
                        textToPaste = DraftTextBox.Text;
                    }
                    else if (_editorState.ActiveEditor == ActiveEditor.Live)
                    {
                        if (_editorState.LiveDisabled)
                        {
                            textToPaste = _editorState.LiveDetachedText;
                        }
                        else
                        {
                            textToPaste = LiveTextBox.Text;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(textToPaste))
                {
                    // Скопировать в буфер обмена
                    ClipboardHelper.SetTextSafe(textToPaste);
                    
                    // Вставить через SendInput
                    SendInputHelper.Paste();
                    
                    // Снять паузу
                    if (_pauseController != null)
                    {
                        _pauseController.SetManualPause(false);
                    }
                }
            });
        }

        private void EditorState_SharedTextChanged()
        {
            Dispatcher.Invoke(() =>
            {
                if (_editorState != null)
                {
                    // Обновить активный редактор
                    if (_editorState.ActiveEditor == ActiveEditor.Draft)
                    {
                        if (DraftTextBox.Text != _editorState.SharedEditorText)
                        {
                            DraftTextBox.Text = _editorState.SharedEditorText;
                        }
                    }
                    else if (_editorState.ActiveEditor == ActiveEditor.Live && !_editorState.LiveDisabled)
                    {
                        if (LiveTextBox.Text != _editorState.SharedEditorText)
                        {
                            LiveTextBox.Text = _editorState.SharedEditorText;
                        }
                    }
                }
            });
        }

        private void EditorState_ActiveEditorChanged()
        {
            Dispatcher.Invoke(() =>
            {
                UpdateEditorFocus();
            });
        }

        private void EditorState_LiveDisabledChanged()
        {
            Dispatcher.Invoke(() =>
            {
                if (_editorState != null)
                {
                    if (_editorState.LiveDisabled)
                    {
                        // Live отключен - очистить LiveDetachedText
                        LiveTextBox.Text = string.Empty;
                    }
                }
                UpdateStatus();
            });
        }

        private void PauseController_PauseStateChanged()
        {
            Dispatcher.Invoke(() =>
            {
                UpdateStatus();
            });
        }
        
        private void HistoryController_HistoryChanged(object? sender, EventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: CALLED, thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");
            System.Diagnostics.Debug.Flush();
            
            var isUIThread = System.Windows.Application.Current?.Dispatcher?.CheckAccess() ?? false;
            System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: isUIThread={isUIThread}");
            System.Diagnostics.Debug.Flush();
            
            if (isUIThread)
            {
                // Уже в UI потоке - обновляем напрямую
                System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: updating directly in UI thread");
                System.Diagnostics.Debug.Flush();
                UpdateHistoryDisplay();
                UpdateDraftAndLiveFromHistory();
                System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: update completed");
                System.Diagnostics.Debug.Flush();
            }
            else
            {
                // Не в UI потоке - используем BeginInvoke для асинхронного обновления
                System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: scheduling BeginInvoke");
                System.Diagnostics.Debug.Flush();
                if (System.Windows.Application.Current?.Dispatcher != null)
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.Normal,
                        new System.Action(() =>
                        {
                            System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: BeginInvoke callback executing, thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");
                            System.Diagnostics.Debug.Flush();
                            try
                            {
                                UpdateHistoryDisplay();
                                UpdateDraftAndLiveFromHistory();
                                System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: BeginInvoke callback completed");
                                System.Diagnostics.Debug.Flush();
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: EXCEPTION in BeginInvoke callback - {ex.Message}\n{ex.StackTrace}");
                                System.Diagnostics.Debug.Flush();
                            }
                        }));
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: BeginInvoke scheduled");
                    System.Diagnostics.Debug.Flush();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] HistoryController_HistoryChanged: ERROR - Dispatcher is null!");
                    System.Diagnostics.Debug.Flush();
                }
            }
        }

        private void UpdateDraftAndLiveFromHistory()
        {
            if (_historyController == null || _editorState == null) return;

            var allSegments = _historyController.AllSegments;
            
            // Получить только новые сегменты
            if (allSegments.Count > _lastHistorySegmentCount)
            {
                var newSegments = allSegments.Skip(_lastHistorySegmentCount).ToList();
                _lastHistorySegmentCount = allSegments.Count;

                // Объединить текст новых сегментов
                string newText = string.Join(" ", newSegments.Select(s => s.text));

                if (!string.IsNullOrEmpty(newText))
                {
                    // Обновить Draft или Live в зависимости от активного редактора
                    if (_editorState.ActiveEditor == ActiveEditor.Draft)
                    {
                        // Добавить к существующему тексту Draft
                        string currentDraft = DraftTextBox.Text;
                        string updatedDraft = string.IsNullOrEmpty(currentDraft) ? newText : currentDraft + " " + newText;
                        DraftTextBox.Text = updatedDraft;
                        _editorState.SharedEditorText = updatedDraft;
                    }
                    else if (_editorState.ActiveEditor == ActiveEditor.Live)
                    {
                        if (_editorState.LiveDisabled)
                        {
                            // Live отключен - добавить к LiveDetachedText
                            string currentDetached = _editorState.LiveDetachedText;
                            string updatedDetached = string.IsNullOrEmpty(currentDetached) ? newText : currentDetached + " " + newText;
                            _editorState.LiveDetachedText = updatedDetached;
                            LiveTextBox.Text = updatedDetached;
                        }
                        else
                        {
                            // Live включен - добавить к SharedEditorText
                            string currentLive = LiveTextBox.Text;
                            string updatedLive = string.IsNullOrEmpty(currentLive) ? newText : currentLive + " " + newText;
                            LiveTextBox.Text = updatedLive;
                            _editorState.SharedEditorText = updatedLive;
                        }
                    }
                }
            }
        }

        public async Task WarmUpModels()
        {
            if (_audioPipeline != null)
            {
                await _audioPipeline.WarmUpModels();
            }
        }

        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Keyboard.ClearFocus();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow();
            settingsWindow.ShowDialog();
        }

        private void OpenSttInputFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folder = Settings.AppSettings.SttInputDebugFolder?.Trim();
            if (string.IsNullOrEmpty(folder))
            {
                MessageBox.Show(
                    "Укажите папку в настройках (Settings): «Папка для сохранения окон STT».\nТогда сюда будут копироваться WAV-файлы для прослушивания.",
                    "Папка не указана",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            if (!Directory.Exists(folder))
            {
                MessageBox.Show(
                    "Папка не существует. Укажите существующую папку в настройках или создайте её.",
                    "Папка не найдена",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            try
            {
                Process.Start("explorer.exe", folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть папку: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SpeakerToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggleButton && toggleButton.Tag is string tagStr && int.TryParse(tagStr, out int speakerId))
            {
                _historyController?.SetSpeakerEnabled(speakerId, true);
                UpdateHistoryDisplay();
            }
        }

        private void SpeakerToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggleButton && toggleButton.Tag is string tagStr && int.TryParse(tagStr, out int speakerId))
            {
                _historyController?.SetSpeakerEnabled(speakerId, false);
                UpdateHistoryDisplay();
            }
        }

        private void HistoryRichTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // При клике на историю очистить фокус
            Keyboard.ClearFocus();
        }

        private void HistoryRichTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            // Можно добавить функциональность копирования выделенного текста
        }

        private void EditorTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (_editorState != null)
            {
                if (sender == DraftTextBox)
                {
                    _editorState.ActiveEditor = ActiveEditor.Draft;
                }
                else if (sender == LiveTextBox)
                {
                    _editorState.ActiveEditor = ActiveEditor.Live;
                }
            }
        }

        private void DraftTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_editorState != null && _editorState.ActiveEditor == ActiveEditor.Draft)
            {
                _editorState.SharedEditorText = DraftTextBox.Text;
            }
        }

        private void LiveTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_editorState != null && _editorState.ActiveEditor == ActiveEditor.Live)
            {
                if (_editorState.LiveDisabled)
                {
                    _editorState.LiveDetachedText = LiveTextBox.Text;
                }
                else
                {
                    _editorState.SharedEditorText = LiveTextBox.Text;
                }
            }
        }

        private void UpdateHistoryDisplay()
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: CALLED, thread={System.Threading.Thread.CurrentThread.ManagedThreadId}");
            System.Diagnostics.Debug.Flush();
            
            // Убедиться, что мы в UI потоке
            var isUIThread = System.Windows.Application.Current?.Dispatcher?.CheckAccess() ?? false;
            System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: isUIThread={isUIThread}");
            System.Diagnostics.Debug.Flush();
            
            if (!isUIThread)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: not in UI thread, invoking Dispatcher.Invoke");
                System.Diagnostics.Debug.Flush();
                System.Windows.Application.Current?.Dispatcher?.Invoke(() => UpdateHistoryDisplay());
                return;
            }
            
            if (_historyController != null && HistoryRichTextBox != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: updating history, segment count={_historyController.AllSegments.Count}");
                    System.Diagnostics.Debug.Flush();
                    
                    // Создать новый документ в UI потоке
                    System.Windows.Documents.FlowDocument doc = new System.Windows.Documents.FlowDocument();
                    System.Windows.Documents.Paragraph para = new System.Windows.Documents.Paragraph();

                    // Получить видимые сегменты
                    var visibleSegments = _historyController.AllSegments
                        .Where(s => s.speakerId >= 1 && s.speakerId <= 6 && _historyController.GetSpeakerEnabled(s.speakerId));
                    
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: visible segments count={visibleSegments.Count()}");
                    System.Diagnostics.Debug.Flush();

                    foreach (var segment in visibleSegments)
                    {
                        System.Windows.Documents.Run run = new System.Windows.Documents.Run(segment.text + " ");
                        
                        // Установить цвет в зависимости от speakerId
                        System.Windows.Media.Brush color = segment.speakerId switch
                        {
                            1 => System.Windows.Media.Brushes.Blue,
                            2 => System.Windows.Media.Brushes.Red,
                            3 => System.Windows.Media.Brushes.Purple,
                            4 => System.Windows.Media.Brushes.Yellow,
                            5 => System.Windows.Media.Brushes.Orange,
                            6 => System.Windows.Media.Brushes.Green,
                            _ => System.Windows.Media.Brushes.Black
                        };
                        
                        run.Foreground = color;
                        run.Tag = segment.speakerId;
                        para.Inlines.Add(run);
                    }

                    doc.Blocks.Add(para);
                    doc.FlowDirection = System.Windows.FlowDirection.LeftToRight;
                    
                    // Обновить содержимое существующего документа
                    var currentDoc = HistoryRichTextBox.Document;
                    if (currentDoc == null)
                    {
                        HistoryRichTextBox.Document = doc;
                    }
                    else
                    {
                        try
                        {
                            currentDoc.Blocks.Clear();
                            currentDoc.Blocks.Add(para);
                            currentDoc.FlowDirection = System.Windows.FlowDirection.LeftToRight;
                        }
                        catch (Exception ex)
                        {
                            // Если не удалось обновить, заменить документ
                            System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: ERROR updating document: {ex.Message}, replacing document");
                            if (HistoryRichTextBox != null)
                            {
                                HistoryRichTextBox.Document = null;
                                System.Threading.Thread.Sleep(10);
                                HistoryRichTextBox.Document = doc;
                            }
                        }
                    }
                    
                    // Прокрутить вниз
                    HistoryRichTextBox.ScrollToEnd();
                    
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: history updated successfully, para.Inlines.Count={para.Inlines.Count}");
                    System.Diagnostics.Debug.Flush();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: EXCEPTION - {ex.Message}\n{ex.StackTrace}");
                    System.Diagnostics.Debug.Flush();
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateHistoryDisplay: WARNING - _historyController={_historyController != null}, HistoryRichTextBox={HistoryRichTextBox != null}");
                System.Diagnostics.Debug.Flush();
            }
        }

        private void UpdateStatus()
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateStatus: called, thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}");
            
            string status = "Готов";
            
            if (_pauseController != null)
            {
                if (_pauseController.GlobalPaused)
                {
                    status = _pauseController.PauseReason switch
                    {
                        PauseReason.Manual => "Пауза (вручную)",
                        PauseReason.AutoSilence => "Пауза (тишина)",
                        _ => "Пауза"
                    };
                }
            }
            
            if (_editorState != null)
            {
                if (_editorState.LiveDisabled)
                {
                    status += " | Live отключен";
                }
            }
            
            if (StatusTextBlock != null)
            {
                StatusTextBlock.Text = status;
                System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateStatus: status text set to '{status}'");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdateStatus: WARNING - StatusTextBlock is null!");
            }
            
            UpdatePauseIndicator();
        }

        private void UpdatePauseIndicator()
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: called, thread: {System.Threading.Thread.CurrentThread.ManagedThreadId}, isUIThread: {Dispatcher.CheckAccess()}");
            
            // Убедиться, что мы в UI потоке
            if (!Dispatcher.CheckAccess())
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: not in UI thread, invoking Dispatcher.Invoke");
                Dispatcher.Invoke(() => UpdatePauseIndicator());
                return;
            }
            
            if (_pauseController != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: _pauseController.GlobalPaused={_pauseController.GlobalPaused}");
                    
                    // Попробовать найти элемент по имени
                    var indicator = this.FindName("PauseIndicator") as System.Windows.Shapes.Ellipse;
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: FindName result: {indicator != null}");
                    
                    if (indicator == null)
                    {
                        // Если не найден через FindName, попробовать через визуальное дерево
                        indicator = LogicalTreeHelper.FindLogicalNode(this, "PauseIndicator") as System.Windows.Shapes.Ellipse;
                        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: LogicalTreeHelper result: {indicator != null}");
                    }
                    
                    if (indicator == null)
                    {
                        // Попробовать найти через визуальное дерево
                        var stackPanel = LogicalTreeHelper.FindLogicalNode(this, "StatusBar") as System.Windows.Controls.StackPanel;
                        if (stackPanel != null)
                        {
                            foreach (var child in LogicalTreeHelper.GetChildren(stackPanel))
                            {
                                if (child is System.Windows.Shapes.Ellipse ellipse && ellipse.Name == "PauseIndicator")
                                {
                                    indicator = ellipse;
                                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: found via visual tree");
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (indicator != null)
                    {
                        indicator.Fill = _pauseController.GlobalPaused 
                            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red) 
                            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green);
                        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: indicator updated successfully, paused={_pauseController.GlobalPaused}, color={indicator.Fill}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: ERROR - PauseIndicator not found! Trying to enumerate children...");
                        // Попробовать найти все Ellipse элементы
                        var allEllipses = new List<System.Windows.Shapes.Ellipse>();
                        FindVisualChildren<System.Windows.Shapes.Ellipse>(this, allEllipses);
                        System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: found {allEllipses.Count} Ellipse elements in visual tree");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: ERROR - {ex.Message}\n{ex.StackTrace}");
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] UpdatePauseIndicator: WARNING - _pauseController is null!");
            }
        }
        
        private static void FindVisualChildren<T>(DependencyObject depObj, List<T> children) where T : DependencyObject
        {
            if (depObj != null)
            {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
                {
                    DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
                    if (child != null && child is T t)
                    {
                        children.Add(t);
                    }
                    FindVisualChildren(child, children);
                }
            }
        }

        private void UpdateEditorFocus()
        {
            if (_editorState != null)
            {
                if (_editorState.ActiveEditor == ActiveEditor.Draft)
                {
                    // Обновить текст Draft из EditorState
                    if (DraftTextBox.Text != _editorState.SharedEditorText)
                    {
                        DraftTextBox.Text = _editorState.SharedEditorText;
                    }
                }
                else if (_editorState.ActiveEditor == ActiveEditor.Live)
                {
                    // Обновить текст Live из EditorState
                    if (_editorState.LiveDisabled)
                    {
                        if (LiveTextBox.Text != _editorState.LiveDetachedText)
                        {
                            LiveTextBox.Text = _editorState.LiveDetachedText;
                        }
                    }
                    else
                    {
                        if (LiveTextBox.Text != _editorState.SharedEditorText)
                        {
                            LiveTextBox.Text = _editorState.SharedEditorText;
                        }
                    }
                }
            }
        }

        private void UpdateQueueStatsDisplay()
        {
            if (QueueStatsTextBlock == null) return;
            if (_audioPipeline == null)
            {
                QueueStatsTextBlock.Text = "";
                return;
            }
            var stats = _audioPipeline.GetQueueStats();
            var parts = new List<string> { $"Оч: {stats.IncomingCount}/{stats.MaxIncomingQueueSize}" };
            for (int i = 0; i < stats.Runners.Count; i++)
            {
                var r = stats.Runners[i];
                parts.Add($"{i + 1}: {r.QueueCount}{(r.IsProcessing ? " ●" : "")}");
            }
            if (stats.ReorderBufferCount > 0)
                parts.Add($"Буфер: {stats.ReorderBufferCount}");
            QueueStatsTextBlock.Text = string.Join(" | ", parts);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_audioPipeline != null)
                _audioPipeline.QueueStatsChanged -= OnQueueStatsChanged;
            _hotkeys?.Dispose();
            _hwndSource?.RemoveHook(WndProc);
            _audioPipeline?.Dispose();
            base.OnClosed(e);
        }
    }
}
