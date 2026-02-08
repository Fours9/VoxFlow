using System;
using System.IO;
using System.Threading;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    public class WindowBuffer : IDisposable
    {
        private double WindowSizeSec => Settings.AppSettings.WindowSizeSec > 0 ? Settings.AppSettings.WindowSizeSec : 3.0; // Длина отрывка аудио в секундах (допускаются дробные значения)
        private double StepSec => Settings.AppSettings.StepSec >= 0 ? Settings.AppSettings.StepSec : 0.0; // Интервал создания нового окна в секундах (0 = без перекрытия, каждое окно независимо, допускаются дробные значения)
        private const int SampleRate = 16000;
        private const int BytesPerSample = 2; // 16-bit = 2 bytes
        private const int Channels = 1; // mono

        private byte[] _windowBuffer = null!; // буфер текущего окна (заполняется из кольца + дописывается)
        private int _windowPosition;
        private bool _isCollectingWindow; // идёт накопление окна (старт по речи+lookback или по цепочке)
        private bool _inChainMode; // true = следующее окно сразу после конца текущего; false = ждём OnSpeechDetected + lookback
        private double _windowStartAbsSec;
        private Timer? _windowTimer;
        private string _tempDir;
        private readonly object _saveLock = new object(); // Защита от одновременных вызовов SaveWindowAndShift
        private double _lastSpeechTime; // Время последнего обнаружения речи
        private bool _hasSpeechInWindow; // Флаг наличия речи в текущем окне
        private const double MinWindowDurationSec = 0.5; // Минимальная длительность окна для преждевременного завершения
        
        // Константы для детектора границ слов
        private const double WordBoundarySilenceThreshold = 0.007; // RMS threshold (как у VadDetector, чуть ниже для тихих слогов)
        private const double WordBoundaryPauseDurationSec = 0.05; // Длительность паузы между словами (0.05 сек = 50мс, более надежно)
        private const double WordBoundaryCheckDurationSec = 0.3; // Анализируем последние 0.3 сек буфера (увеличено для лучшего поиска)
        private const double MaxExtensionDurationSec = 0.5; // Максимальное расширение окна при поиске паузы
        private const double MaxExtensionRatio = 1.5; // Максимальное расширение относительно размера окна (например, 0.3 сек * 1.5 = 0.45 сек)
        private const double PreBufferSec = 0.4; // Lookback при старте окна по речи (речь − PreBufferSec)
        private int RingCapacityBytes => (int)((3.0 * WindowSizeSec + MaxExtensionDurationSec) * SampleRate * BytesPerSample * Channels);
        private double BytesPerSec => SampleRate * BytesPerSample * Channels;

        // Кольцевой буфер 3× длины окна — постоянно пишем, не режем при сохранении окон
        private byte[] _ringBuffer = null!;
        private int _ringWritePos;
        private double _ringStartTime; // время потока, соответствующее самому старому байту в кольце
        private int _ringFilledBytes;
        
        // Поля для расширения окна при поиске границы слова
        private double _windowExtensionStartTime; // Время начала расширения окна
        private bool _isExtendingWindow; // Флаг расширения окна для поиска паузы
        private double _currentStreamTime; // Текущее время потока для проверки расширения
        private PauseController? _pauseController; // Контроллер паузы

        // Временная запись полного аудиопотока в ту же папку, что и окна (для проверки шумов в источнике)
        private const bool EnableRawStreamRecording = true; // выставить false, когда проверка не нужна
        private FileStream? _rawStreamFile;
        private int _rawStreamBytesWritten;

        public event Action<string, double>? OnWindowReady;

        public void SetPauseController(PauseController pauseController)
        {
            _pauseController = pauseController;
        }

        public WindowBuffer()
        {
            InitializeBuffer();
            _windowPosition = 0;
            _windowStartAbsSec = 0;
            _tempDir = Path.GetTempPath();
            _lastSpeechTime = 0;
            _hasSpeechInWindow = false;
            _isExtendingWindow = false;
            _windowExtensionStartTime = 0;
            _currentStreamTime = 0;
            _isCollectingWindow = false;
            _inChainMode = false;
        }

        private void InitializeBuffer()
        {
            int bufferSize = (int)((WindowSizeSec + MaxExtensionDurationSec) * SampleRate * BytesPerSample * Channels);
            _windowBuffer = new byte[bufferSize];
            _ringBuffer = new byte[RingCapacityBytes];
            _ringWritePos = 0;
            _ringFilledBytes = 0;
            _ringStartTime = 0;
        }

        public void AddAudioData(byte[] data, double streamAbsTimeSec)
        {
            // Проверка паузы - не обрабатывать аудио при паузе
            if (_pauseController?.GlobalPaused == true)
            {
                return;
            }

            _currentStreamTime = streamAbsTimeSec;

            // Временная запись полного потока в WAV (тот же каталог, что и окна)
            if (EnableRawStreamRecording && _rawStreamFile != null && data.Length > 0)
            {
                try
                {
                    _rawStreamFile.Write(data, 0, data.Length);
                    _rawStreamBytesWritten += data.Length;
                    _rawStreamFile.Flush(true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] Raw stream write error: {ex.Message}");
                }
            }

            WriteToRing(data);

            if (!_isCollectingWindow)
                return;

            int bytesToCopy = Math.Min(data.Length, _windowBuffer.Length - _windowPosition);
            Array.Copy(data, 0, _windowBuffer, _windowPosition, bytesToCopy);
            _windowPosition += bytesToCopy;

            // Если окно расширяется для поиска границы слова, проверяем периодически
            if (_isExtendingWindow)
            {
                lock (_saveLock)
                {
                    double extensionDuration = streamAbsTimeSec - _windowExtensionStartTime;
                    // Адаптивный лимит расширения
                    double maxExtension = Math.Min(MaxExtensionDurationSec, WindowSizeSec * MaxExtensionRatio);
                    
                    if (extensionDuration >= maxExtension)
                    {
                        // Достигнут лимит расширения - сохранить окно как есть
                        System.Diagnostics.Debug.WriteLine($"[WindowBuffer] AddAudioData: max extension duration reached ({extensionDuration:F3}s, limit: {maxExtension:F3}s), saving window as is");
                        _isExtendingWindow = false;
                        SaveWindowAndShift();
                        return;
                    }
                    
                    // Проверить границу слова при расширении
                    int wordBoundaryPos = FindWordBoundary();
                    if (wordBoundaryPos >= 0 && wordBoundaryPos < _windowPosition)
                    {
                        // Найдена пауза - сохранить окно с обрезкой
                        System.Diagnostics.Debug.WriteLine($"[WindowBuffer] AddAudioData: word boundary found during extension at position {wordBoundaryPos}");
                        _isExtendingWindow = false;
                        SaveWindowAndShiftWithBoundary(wordBoundaryPos);
                        return;
                    }
                }
            }

            // Якщо буфер повний, зберегти вікно та зсунути (только если была речь)
            // Проверяем заполнение буфера только если не идет расширение (расширение обрабатывается выше)
            if (_windowPosition >= _windowBuffer.Length && !_isExtendingWindow)
            {
                if (_hasSpeechInWindow)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] AddAudioData: buffer full, speech detected, saving window");
                    SaveWindowAndShift();
                }
                else
                {
                    // Буфер полон без речи — всё равно сохраняем окно (не пропускаем сегмент; STT может вернуть пусто)
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] AddAudioData: buffer full but no speech, saving window anyway (no skip), windowStart={_windowStartAbsSec:F3}s");
                    lock (_saveLock)
                    {
                        SaveWindowAndShift();
                    }
                }
            }
            else if (_windowPosition >= _windowBuffer.Length && _isExtendingWindow)
            {
                // Буфер заполнен во время расширения - принудительно сохранить окно
                System.Diagnostics.Debug.WriteLine($"[WindowBuffer] AddAudioData: buffer full during extension, forcing save");
                lock (_saveLock)
                {
                    _isExtendingWindow = false;
                    SaveWindowAndShift();
                }
            }
        }

        /// <summary>
        /// Вызывается при обнаружении речи - обновляет время последней речи; при режиме «ждём речи» стартует окно с lookback.
        /// </summary>
        public void OnSpeechDetected(double streamAbsTimeSec)
        {
            _lastSpeechTime = streamAbsTimeSec;

            // Режим «ждём речи»: старт окна с lookback (речь − PreBufferSec)
            if (!_isCollectingWindow && !_inChainMode)
            {
                double windowStart = streamAbsTimeSec - PreBufferSec;
                // Не уходить в отрицательные секунды и не запрашивать данные раньше начала кольца
                if (windowStart < 0) windowStart = 0;
                if (_ringFilledBytes > 0 && windowStart < _ringStartTime) windowStart = _ringStartTime;
                double ringEndTime = _ringStartTime + _ringFilledBytes / BytesPerSec;
                System.Diagnostics.Debug.WriteLine($"[WindowBuffer] OnSpeechDetected START: speech={streamAbsTimeSec:F3}s windowStart={windowStart:F3}s ring=[{_ringStartTime:F3}, {ringEndTime:F3}]");
                int copied = CopyFromRingToBuffer(_windowBuffer, 0, windowStart, streamAbsTimeSec);
                if (copied >= 0)
                {
                    _windowStartAbsSec = windowStart;
                    _windowPosition = copied;
                    _isCollectingWindow = true;
                    _hasSpeechInWindow = true;
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] OnSpeechDetected START done: windowStart={_windowStartAbsSec:F3}s _windowPosition={_windowPosition} bytes");
                }
                return;
            }

            if (!_hasSpeechInWindow)
            {
                _hasSpeechInWindow = true;
                System.Diagnostics.Debug.WriteLine($"[WindowBuffer] OnSpeechDetected: speech detected at {streamAbsTimeSec:F3}s, _hasSpeechInWindow set to true");
            }
        }

        /// <summary>
        /// Вызывается при обнаружении тишины - проверяет, можно ли преждевременно завершить окно.
        /// </summary>
        public void OnSilenceDetected(double streamAbsTimeSec)
        {
            // Проверка паузы - не обрабатывать тишину при паузе
            if (_pauseController?.GlobalPaused == true)
            {
                return;
            }

            lock (_saveLock)
            {
                // Проверяем, есть ли данные в буфере и была ли речь в окне
                if (_windowPosition == 0 || !_hasSpeechInWindow)
                    return;

                // Вычисляем длительность текущего окна
                double currentWindowDuration = streamAbsTimeSec - _windowStartAbsSec;
                
                // Проверяем минимальную длительность окна
                if (currentWindowDuration < MinWindowDurationSec)
                    return;

                // Проверяем, что прошло достаточно времени с последней речи (тишина >= 1 секунда)
                double silenceDuration = streamAbsTimeSec - _lastSpeechTime;
                if (silenceDuration >= 1.0)
                {
                    // Преждевременно завершаем окно
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] Early window completion: silence detected, windowDuration={currentWindowDuration:F3}s, silenceDuration={silenceDuration:F3}s");
                    
                    var windowEndAbsSec = _windowStartAbsSec + ((double)_windowPosition / (SampleRate * BytesPerSample * Channels));
                    var windowDuration = windowEndAbsSec - _windowStartAbsSec;
                    int saveBytes = _windowPosition >= 2 ? _windowPosition & ~1 : _windowPosition;
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] SaveWindowAndShift: windowStart={_windowStartAbsSec:F3}s, windowEnd={windowEndAbsSec:F3}s, duration={windowDuration:F3}s, bufferSize={_windowPosition} bytes");

                    string wavPath = Path.Combine(_tempDir, $"window_{Guid.NewGuid()}.wav");
                    var saveStartTime = DateTime.UtcNow;
                    SaveBufferToWav(wavPath, saveBytes);
                    var saveEndTime = DateTime.UtcNow;
                    var saveDuration = (saveEndTime - saveStartTime).TotalMilliseconds;
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] WAV file saved: {wavPath} (took {saveDuration:F1}ms)");

                    OnWindowReady?.Invoke(wavPath, _windowStartAbsSec);
                    
                    // Завершение по длительной тишине → выходим из цепочки, следующее окно только по OnSpeechDetected
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] OnSilenceDetected -> EXIT CHAIN: silenceDuration={silenceDuration:F3}s, next window waits for speech");
                    _inChainMode = false;
                    _isCollectingWindow = false;
                    _hasSpeechInWindow = false;
                    _windowPosition = 0;
                    _windowStartAbsSec = streamAbsTimeSec;
                }
            }
        }

        public void Start(double initialStreamTime)
        {
            InitializeBuffer();
            _windowStartAbsSec = initialStreamTime;
            _windowPosition = 0;
            _lastSpeechTime = initialStreamTime;
            _hasSpeechInWindow = false;
            _isExtendingWindow = false;
            _windowExtensionStartTime = 0;
            _currentStreamTime = initialStreamTime;
            _ringStartTime = initialStreamTime;
            _isCollectingWindow = false;
            _inChainMode = false;

            // Временная запись полного потока (тот же каталог, что и окна)
            if (EnableRawStreamRecording)
            {
                try
                {
                    string rawPath = Path.Combine(_tempDir, $"raw_stream_{DateTime.UtcNow:yyyyMMdd_HHmmss}.wav");
                    _rawStreamFile = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    _rawStreamBytesWritten = 0;
                    WriteWavHeader(_rawStreamFile, 0); // размер данных пока 0, обновим в Stop()
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] Raw stream recording started: {rawPath}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] Raw stream start error: {ex.Message}");
                    _rawStreamFile = null;
                }
            }

            // Таймер для генерації вікон
            // Если StepSec = 0, создаем окна каждые WindowSizeSec секунд (без перекрытия)
            // Если StepSec > 0, создаем окна каждые StepSec секунд (с перекрытием)
            double timerInterval = StepSec > 0 ? StepSec : WindowSizeSec;
            _windowTimer = new Timer(TimerCallback, null, TimeSpan.FromSeconds(timerInterval), TimeSpan.FromSeconds(timerInterval));
        }

        public void Stop()
        {
            _windowTimer?.Dispose();
            _windowTimer = null;

            // Закрыть и дописать заголовок WAV для полного потока
            if (_rawStreamFile != null)
            {
                try
                {
                    _rawStreamFile.Seek(4, SeekOrigin.Begin);
                    byte[] size4 = BitConverter.GetBytes(36 + _rawStreamBytesWritten);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(size4);
                    _rawStreamFile.Write(size4, 0, 4);
                    _rawStreamFile.Seek(40, SeekOrigin.Begin);
                    byte[] dataSize4 = BitConverter.GetBytes(_rawStreamBytesWritten);
                    if (!BitConverter.IsLittleEndian) Array.Reverse(dataSize4);
                    _rawStreamFile.Write(dataSize4, 0, 4);
                    string path = _rawStreamFile.Name;
                    _rawStreamFile.Dispose();
                    _rawStreamFile = null;
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] Raw stream recording stopped: {path}, bytes={_rawStreamBytesWritten}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] Raw stream stop error: {ex.Message}");
                    try { _rawStreamFile?.Dispose(); } catch { }
                    _rawStreamFile = null;
                }
            }
        }

        private void TimerCallback(object? state)
        {
            // Проверка паузы - не обрабатывать таймер при паузе
            if (_pauseController?.GlobalPaused == true)
            {
                return;
            }

            lock (_saveLock)
            {
                // Таймер только проверяет лимит длительности текущего окна; окно не стартует по таймеру
                if (_isCollectingWindow && _windowPosition > 0 && _hasSpeechInWindow)
                {
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] TimerCallback: timer triggered, saving window (duration limit check)");
                    SaveWindowAndShift();
                }
            }
        }

        private void SaveWindowAndShift()
        {
            // Проверка паузы - не сохранять окна при паузе
            if (_pauseController?.GlobalPaused == true)
            {
                return;
            }

            // Защита от одновременных вызовов
            lock (_saveLock)
            {
                // Проверить, что буфер не пустой (мог быть обработан между проверкой и блокировкой)
                if (_windowPosition == 0)
                    return;

                // Проверка границы слова
                int wordBoundaryPos = FindWordBoundary();
                int actualBufferPosition = _windowPosition;
                
                if (wordBoundaryPos >= 0 && wordBoundaryPos < _windowPosition)
                {
                    // Найдена пауза - обрезать буфер до начала паузы
                    actualBufferPosition = wordBoundaryPos;
                    System.Diagnostics.Debug.WriteLine($"[WindowBuffer] Word boundary found at position {wordBoundaryPos}, trimming buffer from {_windowPosition} to {actualBufferPosition}");
                }
                else if (!_isExtendingWindow)
                {
                    // Пауза не найдена, но расширение еще не началось
                    // Проверить, можем ли расширить окно
                    double currentWindowDuration = ((double)_windowPosition / (SampleRate * BytesPerSample * Channels));
                    // Адаптивное расширение: минимум из абсолютного лимита и относительного (размер окна * коэффициент)
                    double maxExtension = Math.Min(MaxExtensionDurationSec, WindowSizeSec * MaxExtensionRatio);
                    double maxWindowDuration = WindowSizeSec + maxExtension;
                    
                    if (currentWindowDuration < maxWindowDuration)
                    {
                        // Установить флаг расширения и продолжить накапливать
                        _isExtendingWindow = true;
                        _windowExtensionStartTime = _currentStreamTime;
                        System.Diagnostics.Debug.WriteLine($"[WindowBuffer] No word boundary found, extending window (current duration: {currentWindowDuration:F3}s, max: {maxWindowDuration:F3}s)");
                        return; // Не сохранять окно пока
                    }
                }
                else
                {
                    // Расширение уже идет - проверить лимит
                    double extensionDuration = _currentStreamTime - _windowExtensionStartTime;
                    // Адаптивный лимит расширения
                    double maxExtension = Math.Min(MaxExtensionDurationSec, WindowSizeSec * MaxExtensionRatio);
                    
                    if (extensionDuration >= maxExtension)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WindowBuffer] Max extension duration reached ({extensionDuration:F3}s, limit: {maxExtension:F3}s), saving window as is");
                        _isExtendingWindow = false;
                    }
                    else
                    {
                        // Продолжить расширение
                        return;
                    }
                }

                // Сохранить окно до actualBufferPosition
                SaveWindowAndShiftWithBoundary(actualBufferPosition);
            }
        }

        /// <summary>
        /// Сохраняет окно с указанной границей и обрабатывает данные после границы.
        /// </summary>
        private void SaveWindowAndShiftWithBoundary(int actualBufferPosition)
        {
            // 16-bit PCM: сохраняем только чётное число байт (целые сэмплы), иначе возможны шипение/артефакты
            if (actualBufferPosition >= 2) actualBufferPosition &= ~1;

            var windowEndAbsSec = _windowStartAbsSec + ((double)actualBufferPosition / (SampleRate * BytesPerSample * Channels));
            var windowDuration = windowEndAbsSec - _windowStartAbsSec;
            System.Diagnostics.Debug.WriteLine($"[WindowBuffer] SaveWindowWithBoundary: windowStart={_windowStartAbsSec:F3}s windowEnd={windowEndAbsSec:F3}s duration={windowDuration:F3}s bytes={actualBufferPosition}/{_windowPosition} _currentStreamTime={_currentStreamTime:F3}s");

            string wavPath = Path.Combine(_tempDir, $"window_{Guid.NewGuid()}.wav");
            var saveStartTime = DateTime.UtcNow;
            SaveBufferToWav(wavPath, actualBufferPosition);
            var saveEndTime = DateTime.UtcNow;
            var saveDuration = (saveEndTime - saveStartTime).TotalMilliseconds;
            System.Diagnostics.Debug.WriteLine($"[WindowBuffer] WAV file saved: {wavPath} (took {saveDuration:F1}ms)");

            OnWindowReady?.Invoke(wavPath, _windowStartAbsSec);

            // Цепочка: следующее окно сразу после конца текущего — заполняем из кольца
            System.Diagnostics.Debug.WriteLine($"[WindowBuffer] SaveWindowWithBoundary -> CHAIN: windowEndAbsSec={windowEndAbsSec:F3}s _currentStreamTime={_currentStreamTime:F3}s");
            _inChainMode = true;
            _hasSpeechInWindow = false;
            _isExtendingWindow = false;
            StartNextWindowInChain(windowEndAbsSec);
        }

        /// <summary>Старт следующего окна в цепочке: копирование из кольца от windowEndAbsSec до _currentStreamTime.</summary>
        private void StartNextWindowInChain(double windowEndAbsSec)
        {
            double ringEndTime = _ringStartTime + _ringFilledBytes / BytesPerSec;
            System.Diagnostics.Debug.WriteLine($"[WindowBuffer] StartNextWindowInChain: windowEnd={windowEndAbsSec:F3}s _currentStreamTime={_currentStreamTime:F3}s ring=[{_ringStartTime:F3}, {ringEndTime:F3}]");
            int copied = CopyFromRingToBuffer(_windowBuffer, 0, windowEndAbsSec, _currentStreamTime);
            // Если в кольце нет данных для этого диапазона (уже перезаписано или ещё не записано), стартуем пустое окно «сейчас»
            if (copied == 0)
            {
                _windowStartAbsSec = _currentStreamTime;
                Array.Clear(_windowBuffer, 0, _windowBuffer.Length); // исключить попадание старых данных в следующее сохранение
                System.Diagnostics.Debug.WriteLine($"[WindowBuffer] StartNextWindowInChain: no data in ring for request -> empty window from _currentStreamTime={_windowStartAbsSec:F3}s");
            }
            else
                _windowStartAbsSec = windowEndAbsSec;
            _windowPosition = copied;
            _isCollectingWindow = true;
            System.Diagnostics.Debug.WriteLine($"[WindowBuffer] StartNextWindowInChain: done -> windowStart={_windowStartAbsSec:F3}s _windowPosition={_windowPosition} bytes (={_windowPosition / BytesPerSec:F3}s)");
        }

        /// <summary>
        /// Проверяет наличие паузы между словами в последних ~300мс буфера (или во всем буфере для маленьких окон).
        /// Возвращает позицию начала паузы (в байтах от начала буфера) или -1, если пауза не найдена.
        /// </summary>
        private int FindWordBoundary()
        {
            if (_windowPosition == 0) return -1;
            
            // Вычислить количество байт для анализа (последние 0.3 сек или весь буфер для маленьких окон)
            double currentWindowDuration = (double)_windowPosition / (SampleRate * BytesPerSample * Channels);
            double checkDurationSec = Math.Min(WordBoundaryCheckDurationSec, currentWindowDuration);
            int checkBytes = (int)(checkDurationSec * SampleRate * BytesPerSample * Channels);
            int startPos = Math.Max(0, _windowPosition - checkBytes);
            
            // Анализировать буфер с шагом ~10мс (160 samples при 16kHz)
            int stepBytes = SampleRate / 100 * BytesPerSample * Channels; // ~10мс
            int pauseRequiredSamples = (int)(WordBoundaryPauseDurationSec * SampleRate);
            
            // Искать непрерывную тишину >= 0.05 сек (50мс) для более надежного определения границ слов
            int silenceStartPos = -1;
            int consecutiveSilenceSamples = 0;
            int chunksChecked = 0;
            double maxRMS = 0.0;
            double minRMS = double.MaxValue;
            
            for (int pos = startPos; pos < _windowPosition; pos += stepBytes)
            {
                int chunkSize = Math.Min(stepBytes, _windowPosition - pos);
                double rms = CalculateRMS(_windowBuffer, pos, chunkSize);
                chunksChecked++;
                
                if (rms > maxRMS) maxRMS = rms;
                if (rms < minRMS) minRMS = rms;
                
                if (rms < WordBoundarySilenceThreshold)
                {
                    if (silenceStartPos == -1)
                        silenceStartPos = pos;
                    consecutiveSilenceSamples += chunkSize / (BytesPerSample * Channels);
                    
                    if (consecutiveSilenceSamples >= pauseRequiredSamples)
                    {
                        // Найдена пауза >= 0.05 сек (50мс) - достаточно для границы между словами
                        double silenceStartSec = (double)silenceStartPos / (SampleRate * BytesPerSample * Channels);
                        double silenceDurationSec = (double)consecutiveSilenceSamples / SampleRate;
                        System.Diagnostics.Debug.WriteLine($"[WindowBuffer] FindWordBoundary: pause found at position {silenceStartPos} ({silenceStartSec:F3}s from start), duration={silenceDurationSec:F3}s, checked {chunksChecked} chunks, RMS range=[{minRMS:F6}, {maxRMS:F6}]");
                        return silenceStartPos;
                    }
                }
                else
                {
                    // Речь продолжается - сбросить счетчик тишины
                    silenceStartPos = -1;
                    consecutiveSilenceSamples = 0;
                }
            }
            
            // Пауза не найдена
            System.Diagnostics.Debug.WriteLine($"[WindowBuffer] FindWordBoundary: no pause found in last {checkDurationSec:F3}s (checked {chunksChecked} chunks, RMS range=[{minRMS:F6}, {maxRMS:F6}])");
            return -1;
        }

        /// <summary>
        /// Вычисляет RMS для части буфера.
        /// </summary>
        private double CalculateRMS(byte[] buffer, int offset, int length)
        {
            if (length < 2) return 0.0;
            
            long sumSquares = 0;
            int sampleCount = length / 2; // 16-bit = 2 bytes per sample
            
            for (int i = offset; i < offset + length - 1; i += 2)
            {
                short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                sumSquares += (long)sample * sample;
            }
            
            if (sampleCount == 0) return 0.0;
            
            double meanSquare = (double)sumSquares / sampleCount;
            return Math.Sqrt(meanSquare) / 32768.0; // Normalize to [-1, 1]
        }

        /// <summary>Пишет кусок аудио в кольцевой буфер (3× окна). Не очищает при сохранении окон.</summary>
        private void WriteToRing(byte[] data)
        {
            if (data.Length == 0) return;
            int cap = RingCapacityBytes;
            double oldStart = _ringStartTime;
            if (_ringFilledBytes == 0)
                _ringStartTime = _currentStreamTime - data.Length / BytesPerSec;
            else if (_ringFilledBytes >= cap)
                _ringStartTime += Math.Min(data.Length, cap) / BytesPerSec;
            for (int i = 0; i < data.Length; i++)
            {
                _ringBuffer[_ringWritePos] = data[i];
                _ringWritePos = (_ringWritePos + 1) % cap;
            }
            _ringFilledBytes = Math.Min(cap, _ringFilledBytes + data.Length);
            // Логируем только при сдвиге начала кольца (переполнение) или раз в ~5 сек по данным
            if (oldStart != _ringStartTime)
                System.Diagnostics.Debug.WriteLine($"[WindowBuffer] WriteToRing: ring start moved {oldStart:F3} -> {_ringStartTime:F3}s filled={_ringFilledBytes} cap={cap} streamTime={_currentStreamTime:F3}s");
        }

        /// <summary>Логический байтовый сдвиг в кольце для времени t (от _ringStartTime). Возвращает -1 если вне кольца. На границе (time = ring end) возвращает _ringFilledBytes, чтобы CopyFromRing скопировал 0 байт без "not in ring".</summary>
        private int GetRingLogicalOffsetForTime(double timeSec)
        {
            double secOffset = timeSec - _ringStartTime;
            if (secOffset < 0) return -1;
            int byteOffset = (int)(secOffset * BytesPerSec);
            if (byteOffset > _ringFilledBytes) return -1;
            // Граница: request start = ring end — считаем «в кольце», вернём _ringFilledBytes → в CopyFromRing получится пустой диапазон, 0 байт
            if (byteOffset == _ringFilledBytes) return _ringFilledBytes;
            return byteOffset;
        }

        /// <summary>Копирует из кольца отрезок [fromTimeSec, toTimeSec] в destBuffer начиная с destOffset. Возвращает число скопированных байт.</summary>
        private int CopyFromRingToBuffer(byte[] destBuffer, int destOffset, double fromTimeSec, double toTimeSec)
        {
            int cap = RingCapacityBytes;
            double ringEndTime = _ringStartTime + _ringFilledBytes / BytesPerSec;
            if (_ringFilledBytes == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowBuffer] CopyFromRing: ring empty, ringStart={_ringStartTime:F3}s, request [{fromTimeSec:F3}, {toTimeSec:F3}] -> 0 bytes");
                return 0;
            }
            int startLogical = GetRingLogicalOffsetForTime(fromTimeSec);
            // Не подставлять начало кольца, если запрошенное время вне кольца — иначе в окно попадёт старый аудио (шипение/дубли).
            if (startLogical < 0)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowBuffer] CopyFromRing: request start {fromTimeSec:F3}s not in ring=[{_ringStartTime:F3}, {ringEndTime:F3}] -> 0 bytes (no clamp)");
                return 0;
            }
            int endLogical = (int)((toTimeSec - _ringStartTime) * BytesPerSec);
            if (endLogical >= _ringFilledBytes) endLogical = _ringFilledBytes - 1;
            if (endLogical < startLogical)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowBuffer] CopyFromRing: empty range ring=[{_ringStartTime:F3}, {ringEndTime:F3}] req=[{fromTimeSec:F3}, {toTimeSec:F3}] -> 0 bytes");
                return 0;
            }
            int length = endLogical - startLogical + 1;
            if (destOffset + length > destBuffer.Length) length = destBuffer.Length - destOffset;
            if (length <= 0) return 0;
            // 16-bit PCM: длина должна быть чётной (целое число сэмплов), иначе последний байт даёт артефакты/шипение
            if ((length & 1) != 0) length--;
            if (length <= 0) return 0;
            for (int i = 0; i < length; i++)
            {
                int logical = startLogical + i;
                int physical = _ringFilledBytes >= cap
                    ? (_ringWritePos + logical) % cap
                    : logical;
                destBuffer[destOffset + i] = _ringBuffer[physical];
            }
            double copiedFrom = _ringStartTime + startLogical / BytesPerSec;
            double copiedTo = _ringStartTime + (startLogical + length - 1) / BytesPerSec;
            System.Diagnostics.Debug.WriteLine($"[WindowBuffer] CopyFromRing: ring=[{_ringStartTime:F3}, {ringEndTime:F3}] req=[{fromTimeSec:F3}, {toTimeSec:F3}] -> copied {length} bytes (time [{copiedFrom:F3}, {copiedTo:F3}])");
            return length;
        }

        /// <summary>Пишет только 44-байтный WAV-заголовок (16 kHz, mono, 16-bit). Размер данных задаётся dataLength.</summary>
        private static void WriteWavHeader(Stream stream, int dataLength)
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write("RIFF".ToCharArray());
                writer.Write(36 + dataLength);
                writer.Write("WAVE".ToCharArray());
                writer.Write("fmt ".ToCharArray());
                writer.Write(16);
                writer.Write((ushort)1);
                writer.Write((ushort)Channels);
                writer.Write(SampleRate);
                writer.Write(SampleRate * BytesPerSample * Channels);
                writer.Write((ushort)(BytesPerSample * Channels));
                writer.Write((ushort)(BytesPerSample * 8));
                writer.Write("data".ToCharArray());
                writer.Write(dataLength);
            }
        }

        private void SaveBufferToWav(string path, int dataLength)
        {
            using (var fileStream = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(fileStream))
            {
                // WAV header
                writer.Write("RIFF".ToCharArray());
                writer.Write(36 + dataLength); // ChunkSize
                writer.Write("WAVE".ToCharArray());
                writer.Write("fmt ".ToCharArray());
                writer.Write(16); // Subchunk1Size
                writer.Write((ushort)1); // AudioFormat (PCM)
                writer.Write((ushort)Channels);
                writer.Write(SampleRate);
                writer.Write(SampleRate * BytesPerSample * Channels); // ByteRate
                writer.Write((ushort)(BytesPerSample * Channels)); // BlockAlign
                writer.Write((ushort)(BytesPerSample * 8)); // BitsPerSample
                writer.Write("data".ToCharArray());
                writer.Write(dataLength); // Subchunk2Size
                writer.Write(_windowBuffer, 0, dataLength); // Data
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}