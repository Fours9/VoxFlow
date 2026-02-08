using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    /// <summary>Снимок состояния очередей для отображения в UI.</summary>
    public sealed class QueueStats
    {
        public int IncomingCount { get; init; }
        public int MaxIncomingQueueSize { get; init; }
        public IReadOnlyList<RunnerQueueStats> Runners { get; init; } = Array.Empty<RunnerQueueStats>();
        public int ReorderBufferCount { get; init; }
    }

    /// <summary>Состояние очереди одного движка STT.</summary>
    public sealed class RunnerQueueStats
    {
        public int QueueCount { get; init; }
        public bool IsProcessing { get; init; }
    }

    public class AudioPipeline : IDisposable
    {
        private readonly AudioCapture _audioCapture;
        private readonly WindowBuffer _windowBuffer;
        private readonly VadDetector _vadDetector;
        private readonly List<ISttEngine> _sttEngines; // Список движков STT для параллельной обработки
        private readonly DiarizerRunner _diarizerRunner;
        private readonly MergeEngine _mergeEngine;
        private readonly SpeakerTracker _speakerTracker;
        private readonly HistoryController _historyController;

        // Очереди задач для каждого движка STT (runner)
        private class WindowTask
        {
            public string WavPath { get; set; } = string.Empty;
            public double StartAbsSec { get; set; }
            public long SequenceNumber { get; set; } // Порядковый номер для упорядочивания
        }

        private readonly List<Queue<WindowTask>> _runnerQueues;
        private readonly List<object> _runnerQueueLocks;
        private readonly List<bool> _runnerIsProcessing;
        private int _lastSelectedRunnerIndex = -1; // Для round-robin распределения

        // Общая очередь входящих окон
        private readonly Queue<WindowTask> _incomingWindows = new();
        private readonly object _incomingQueueLock = new object();
        private bool _isDispatching;

        private long _nextSequenceNumber = 0; // Счетчик для присвоения порядковых номеров окон
        private const int MaxQueueSize = 10; // Увеличено для предотвращения потери окон при медленной обработке (особенно для Vosk)

        // Буфер упорядочивания результатов
        private readonly Dictionary<long, List<HistorySegment>> _reorderBuffer = new(); // sequenceNumber -> segments
        private long _nextExpectedSequence = 0; // Следующий ожидаемый sequence number
        private readonly object _reorderBufferLock = new object();

        /// <summary>Вызывается при изменении состояния очередей (новое окно в очереди или сдвиг по очередям). Подписчик может обновить UI.</summary>
        public event Action? QueueStatsChanged;

        private void NotifyQueueStatsChanged() => QueueStatsChanged?.Invoke();
        
        // Флаг для отключения диаризации (временно для снижения нагрузки)
        // При значении false используется только STT (без диаризации), диаризация и merge отключены
        private const bool EnableDiarization = false;

        public AudioPipeline(
            AudioCapture audioCapture,
            WindowBuffer windowBuffer,
            VadDetector vadDetector,
            List<ISttEngine> sttEngines,
            DiarizerRunner diarizerRunner,
            MergeEngine mergeEngine,
            SpeakerTracker speakerTracker,
            HistoryController historyController)
        {
            if (sttEngines == null || sttEngines.Count == 0)
            {
                throw new ArgumentException("At least one STT engine is required", nameof(sttEngines));
            }

            _audioCapture = audioCapture;
            _windowBuffer = windowBuffer;
            _vadDetector = vadDetector;
            _sttEngines = new List<ISttEngine>(sttEngines);
            _diarizerRunner = diarizerRunner;
            _mergeEngine = mergeEngine;
            _speakerTracker = speakerTracker;
            _historyController = historyController;

            // Инициализируем очереди и флаги для каждого runner'а
            _runnerQueues = new List<Queue<WindowTask>>();
            _runnerQueueLocks = new List<object>();
            _runnerIsProcessing = new List<bool>();
            for (int i = 0; i < _sttEngines.Count; i++)
            {
                _runnerQueues.Add(new Queue<WindowTask>());
                _runnerQueueLocks.Add(new object());
                _runnerIsProcessing.Add(false);
            }

            // Підписка на події
            _audioCapture.OnAudioData += OnAudioData;
            _windowBuffer.OnWindowReady += OnWindowReady;
            _vadDetector.OnSpeechDetected += OnSpeechDetected;
            _vadDetector.OnSilenceDetected += OnSilenceDetected;
        }

        private void OnSpeechDetected()
        {
            _windowBuffer.OnSpeechDetected(_audioCapture.StreamAbsTimeSec);
        }

        private void OnSilenceDetected()
        {
            _windowBuffer.OnSilenceDetected(_audioCapture.StreamAbsTimeSec);
        }

        private void OnAudioData(byte[] data)
        {
            // Логировать первые несколько пакетов для диагностики
            if (_audioCapture.StreamAbsTimeSec < 1.0)
            {
                Debug.WriteLine($"[AudioPipeline] Audio data received: {data.Length} bytes at {_audioCapture.StreamAbsTimeSec:F3}s");
            }
            
            _vadDetector.ProcessAudioData(data);
            _windowBuffer.AddAudioData(data, _audioCapture.StreamAbsTimeSec);
        }

        private void OnWindowReady(string wavPath, double windowStartAbsSec)
        {
            var currentTime = _audioCapture.StreamAbsTimeSec;
            var delayFromWindowEnd = currentTime - windowStartAbsSec;
            Debug.WriteLine($"[AudioPipeline] WindowReady: {wavPath}, startAbs={windowStartAbsSec:F3}, currentStreamTime={currentTime:F3}, delayFromWindowStart={delayFromWindowEnd:F3}s");

            // Создаем задачу окна с порядковым номером
            WindowTask windowTask;
            lock (_incomingQueueLock)
            {
                // Перевірити чергу - якщо переповнена, відкинути найстаріше вікно
                if (_incomingWindows.Count >= MaxQueueSize)
                {
                    var oldest = _incomingWindows.Dequeue();
                    Debug.WriteLine($"[AudioPipeline] Incoming queue full, dropping oldest window: {oldest.WavPath}");
                    try
                    {
                        System.IO.File.Delete(oldest.WavPath);
                    }
                    catch { }
                }

                windowTask = new WindowTask
                {
                    WavPath = wavPath,
                    StartAbsSec = windowStartAbsSec,
                    SequenceNumber = _nextSequenceNumber++
                };

                _incomingWindows.Enqueue(windowTask);

                // Запускаем диспетчер, если он еще не работает
                if (!_isDispatching)
                {
                    _isDispatching = true;
                    _ = DispatchWindowsAsync();
                }
            }
            NotifyQueueStatsChanged();
        }

        /// <summary>
        /// Диспетчер: распределяет окна из общей очереди по очередям runner'ов.
        /// </summary>
        private async Task DispatchWindowsAsync()
        {
            while (true)
            {
                WindowTask? task = null;
                lock (_incomingQueueLock)
                {
                    if (_incomingWindows.Count > 0)
                    {
                        task = _incomingWindows.Dequeue();
                    }
                    else
                    {
                        // Очередь пуста - останавливаем диспетчер
                        _isDispatching = false;
                        NotifyQueueStatsChanged();
                        return;
                    }
                }
                NotifyQueueStatsChanged();

                int runnerIndex = SelectBestRunnerForTask();
                EnqueueToRunnerQueue(runnerIndex, task);

                // Небольшая уступка планировщику
                await Task.Yield();
            }
        }

        /// <summary>
        /// Выбирает лучший движок STT для новой задачи.
        /// Приоритет: 1) свободные движки с пустой очередью (round-robin), 2) движки с наименьшей длиной очереди.
        /// </summary>
        private int SelectBestRunnerForTask()
        {
            int selectedIndex = 0;
            int minQueueSize = int.MaxValue;

            // Сначала ищем полностью свободных runner'ов (без активной обработки и с пустой очередью)
            // Используем round-robin: начинаем поиск с последнего выбранного индекса + 1
            int startIndex = _lastSelectedRunnerIndex < 0 ? 0 : (_lastSelectedRunnerIndex + 1) % _sttEngines.Count;
            for (int offset = 0; offset < _sttEngines.Count; offset++)
            {
                int i = (startIndex + offset) % _sttEngines.Count;
                lock (_runnerQueueLocks[i])
                {
                    if (!_runnerIsProcessing[i] && _runnerQueues[i].Count == 0)
                    {
                        // Найден свободный runner - используем его
                        selectedIndex = i;
                    Debug.WriteLine($"[AudioPipeline] Selected idle STT engine {selectedIndex + 1} (queue: 0, round-robin from index {startIndex})");
                        _lastSelectedRunnerIndex = selectedIndex;
                        return selectedIndex;
                    }
                }
            }

            // Если полностью свободных нет, ищем runner с наименьшей очередью
            for (int i = 0; i < _sttEngines.Count; i++)
            {
                int queueSize;
                lock (_runnerQueueLocks[i])
                {
                    queueSize = _runnerQueues[i].Count;
                }

                if (queueSize < minQueueSize)
                {
                    minQueueSize = queueSize;
                    selectedIndex = i;
                }
            }

                    Debug.WriteLine($"[AudioPipeline] All runners busy or queued, selected STT engine {selectedIndex + 1} with smallest queue ({minQueueSize})");
            _lastSelectedRunnerIndex = selectedIndex;
            return selectedIndex;
        }

        /// <summary>
        /// Добавляет задачу в очередь конкретного runner'а и запускает обработку при необходимости.
        /// </summary>
        private void EnqueueToRunnerQueue(int runnerIndex, WindowTask task)
        {
            lock (_runnerQueueLocks[runnerIndex])
            {
                _runnerQueues[runnerIndex].Enqueue(task);

                if (!_runnerIsProcessing[runnerIndex])
                {
                    _runnerIsProcessing[runnerIndex] = true;
                    _ = ProcessRunnerQueueAsync(runnerIndex);
                }
            }
            NotifyQueueStatsChanged();
        }

        /// <summary>
        /// Обрабатывает очередь конкретного runner'а.
        /// </summary>
        private async Task ProcessRunnerQueueAsync(int runnerIndex)
        {
            var runner = _sttEngines[runnerIndex];

            while (true)
            {
                WindowTask? task = null;
                lock (_runnerQueueLocks[runnerIndex])
                {
                    if (_runnerQueues[runnerIndex].Count > 0)
                    {
                        task = _runnerQueues[runnerIndex].Dequeue();
                    }
                    else
                    {
                        _runnerIsProcessing[runnerIndex] = false;
                        NotifyQueueStatsChanged();
                        return;
                    }
                }
                NotifyQueueStatsChanged();

                try
                {
                    await ProcessWindowAsync(task, runner);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AudioPipeline] ERROR in ProcessRunnerQueueAsync for runner {runnerIndex + 1}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// Обрабатывает одно окно, возвращая сегменты истории и отправляя их в буфер упорядочивания.
        /// </summary>
        private async Task ProcessWindowAsync(WindowTask windowTask, ISttEngine sttEngine)
        {
            var windowStartTime = DateTime.UtcNow;
            try
            {
                CopyWavToDebugFolderIfEnabled(windowTask);
                Debug.WriteLine($"[AudioPipeline] Processing window: {windowTask.WavPath} (seq={windowTask.SequenceNumber}) at {windowStartTime:HH:mm:ss.fff}");

                var historySegments = new List<HistorySegment>();

                if (EnableDiarization)
                {
                    // Полная версия с диаризацией (временно отключена для снижения нагрузки)
                    // Паралельний запуск STT та Diarization
                    var textTask = sttEngine.TranscribeAsync(windowTask.WavPath);
                    var speakerTask = _diarizerRunner.DiarizeAsync(windowTask.WavPath);

                    await Task.WhenAll(textTask, speakerTask);

                    var textSegments = await textTask;
                    var speakerSegments = await speakerTask;

                    Debug.WriteLine($"[AudioPipeline] STT finished: {textSegments.Count} text segments");
                    if (textSegments.Count == 0)
                    {
                        Debug.WriteLine("[AudioPipeline] WARNING: STT returned 0 segments");
                    }

                    Debug.WriteLine($"[AudioPipeline] Diarizer finished: {speakerSegments.Count} speaker segments");

                    // Speaker tracking
                    var labelToStableId = _speakerTracker.MapLabelsToStableIds(speakerSegments, windowTask.StartAbsSec);

                    // Merge
                    historySegments = _mergeEngine.MergeSegments(
                        textSegments, speakerSegments, labelToStableId, windowTask.StartAbsSec);

                    Debug.WriteLine($"[AudioPipeline] Merged: {historySegments.Count} history segments");
                }
                else
                {
                    // Упрощенная версия без диаризации (только STT) — используется по умолчанию
                    var textSegments = await sttEngine.TranscribeAsync(windowTask.WavPath);

                    Debug.WriteLine($"[AudioPipeline] STT finished: {textSegments.Count} text segments");
                    if (textSegments.Count == 0)
                    {
                        Debug.WriteLine("[AudioPipeline] WARNING: STT returned 0 segments");
                    }

                    // Создать HistorySegments без диаризации (speakerId = 1 для всех)
                    historySegments = new List<HistorySegment>();
                    foreach (var textSeg in textSegments)
                    {
                        double textStartAbs = windowTask.StartAbsSec + textSeg.startSec;
                        double textEndAbs = windowTask.StartAbsSec + textSeg.endSec;

                        historySegments.Add(new HistorySegment
                        {
                            ts = DateTime.Now,
                            speakerId = 1, // Фиксированный speakerId при отключенной диаризации
                            text = textSeg.text,
                            startSecAbs = textStartAbs,
                            endSecAbs = textEndAbs
                        });
                    }

                    Debug.WriteLine($"[AudioPipeline] Created {historySegments.Count} history segments (without diarization)");
                }

                // Отправляем результаты в буфер упорядочивания (даже если список пуст, чтобы не блокировать последующие окна)
                AddToReorderBuffer(windowTask.SequenceNumber, historySegments);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioPipeline] ERROR: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                var windowEndTime = DateTime.UtcNow;
                var windowDuration = (windowEndTime - windowStartTime).TotalMilliseconds;
                var finalStreamTime = _audioCapture.StreamAbsTimeSec;
                var totalDelayFromWindowStart = finalStreamTime - windowTask.StartAbsSec;
                Debug.WriteLine($"[AudioPipeline] ProcessWindowAsync completed for seq={windowTask.SequenceNumber} in {windowDuration:F1}ms (started at {windowStartTime:HH:mm:ss.fff}, ended at {windowEndTime:HH:mm:ss.fff}), totalDelayFromWindowStart={totalDelayFromWindowStart:F3}s");
            }
        }

        /// <summary>
        /// Добавляет сегменты в буфер упорядочивания и пытается выдать результаты в HistoryController в правильном порядке.
        /// </summary>
        private void AddToReorderBuffer(long sequenceNumber, List<HistorySegment> segments)
        {
            lock (_reorderBufferLock)
            {
                Debug.WriteLine($"[AudioPipeline] AddToReorderBuffer: seq={sequenceNumber}, segments={segments.Count}, nextExpected={_nextExpectedSequence}");
                _reorderBuffer[sequenceNumber] = segments;
                FlushReorderBuffer();
            }
            NotifyQueueStatsChanged();
        }

        /// <summary>
        /// Пытается выгрузить из буфера все последовательные результаты, начиная с _nextExpectedSequence.
        /// </summary>
        private void FlushReorderBuffer()
        {
            while (_reorderBuffer.TryGetValue(_nextExpectedSequence, out var segments))
            {
                _reorderBuffer.Remove(_nextExpectedSequence);

                if (segments.Any())
                {
                    var appendStartTime = DateTime.UtcNow;
                    _historyController.AppendSegments(segments);
                    var appendEndTime = DateTime.UtcNow;
                    var appendDuration = (appendEndTime - appendStartTime).TotalMilliseconds;
                    Debug.WriteLine($"[AudioPipeline] FlushReorderBuffer: sent seq={_nextExpectedSequence} to HistoryController ({segments.Count} segments, took {appendDuration:F1}ms)");
                }
                else
                {
                    Debug.WriteLine($"[AudioPipeline] FlushReorderBuffer: skipped seq={_nextExpectedSequence} (empty segments)");
                }

                _nextExpectedSequence++;
            }
        }

        private void CopyWavToDebugFolderIfEnabled(WindowTask windowTask)
        {
            var folder = Settings.AppSettings.SttInputDebugFolder?.Trim();
            if (string.IsNullOrEmpty(folder) || !File.Exists(windowTask.WavPath))
                return;
            try
            {
                if (!Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
                string fileName = $"window_{windowTask.SequenceNumber:D4}_{windowTask.StartAbsSec:F2}s.wav";
                string destPath = Path.Combine(folder, fileName);
                File.Copy(windowTask.WavPath, destPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioPipeline] CopyWavToDebugFolder failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Возвращает текущее состояние очередей (общей и по каждому движку) для отображения в UI.
        /// Потокобезопасно.
        /// </summary>
        public QueueStats GetQueueStats()
        {
            int incomingCount;
            lock (_incomingQueueLock)
            {
                incomingCount = _incomingWindows.Count;
            }

            var runners = new List<RunnerQueueStats>(_sttEngines.Count);
            for (int i = 0; i < _runnerQueues.Count; i++)
            {
                lock (_runnerQueueLocks[i])
                {
                    runners.Add(new RunnerQueueStats
                    {
                        QueueCount = _runnerQueues[i].Count,
                        IsProcessing = _runnerIsProcessing[i]
                    });
                }
            }

            int reorderCount;
            lock (_reorderBufferLock)
            {
                reorderCount = _reorderBuffer.Count;
            }

            return new QueueStats
            {
                IncomingCount = incomingCount,
                MaxIncomingQueueSize = MaxQueueSize,
                Runners = runners,
                ReorderBufferCount = reorderCount
            };
        }

        public void Start()
        {
            _audioCapture.Start();
            _windowBuffer.Start(_audioCapture.StreamAbsTimeSec);
        }

        public void Stop()
        {
            _audioCapture.Stop();
            _windowBuffer.Stop();
        }

        /// <summary>
        /// Предварительно запускает все движки STT для подготовки к обработке.
        /// Вызывается при снятии с паузы, чтобы избежать задержки при первой транскрипции.
        /// </summary>
        public async Task WarmUpModels()
        {
            Debug.WriteLine($"[AudioPipeline] Warming up {_sttEngines.Count} STT engines...");
            
            // Запускаем все сервисы параллельно и ждем их готовности
            var warmupTasks = new List<Task>();
            for (int i = 0; i < _sttEngines.Count; i++)
            {
                int runnerIndex = i; // Захватываем индекс для замыкания
                var task = Task.Run(async () =>
                {
                    try
                    {
                        Debug.WriteLine($"[AudioPipeline] Starting STT engine {runnerIndex + 1} warmup...");
                        await _sttEngines[runnerIndex].WarmUp();
                        Debug.WriteLine($"[AudioPipeline] STT engine {runnerIndex + 1} warmed up successfully");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AudioPipeline] ERROR: Failed to warm up STT engine {runnerIndex + 1}: {ex.Message}");
                        Debug.WriteLine($"[AudioPipeline] ERROR: StackTrace: {ex.StackTrace}");
                    }
                });
                warmupTasks.Add(task);
            }
            
            // Ждем завершения всех задач с таймаутом
            try
            {
                var timeoutTask = Task.Delay(60000); // 60 секунд общий таймаут
                var warmupTask = Task.WhenAll(warmupTasks);
                
                var completedTask = await Task.WhenAny(warmupTask, timeoutTask);
                if (completedTask == timeoutTask)
                {
                    Debug.WriteLine($"[AudioPipeline] WARNING: Warmup timeout (60 seconds) exceeded!");
                }
                else
                {
                    await warmupTask; // Ждем завершения, чтобы обработать исключения
                    Debug.WriteLine($"[AudioPipeline] All {_sttEngines.Count} STT engines warmed up successfully");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AudioPipeline] ERROR during warmup: {ex.Message}");
                Debug.WriteLine($"[AudioPipeline] ERROR: StackTrace: {ex.StackTrace}");
            }
        }

        public void Dispose()
        {
            Stop();
            _audioCapture.Dispose();
            _windowBuffer.Dispose();
            _vadDetector.Dispose();

            // Освобождаем все движки STT
            foreach (var runner in _sttEngines)
            {
                runner.Dispose();
            }
        }
    }
}