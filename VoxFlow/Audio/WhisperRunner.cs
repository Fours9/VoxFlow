using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    public class WhisperRunner : ISttEngine
    {
        private readonly string _whisperServicePath;
        private readonly string _whisperModelPath;
        private readonly string _whisperLanguage;
        
        private Process? _whisperServiceProcess;
        private StreamWriter? _processInputWriter;
        private StreamReader? _processOutputReader;
        private StreamReader? _processErrorReader;
        private readonly object _serviceLock = new object();
        private bool _serviceStarted = false;
        private Task? _readyTask; // Задача чтения "READY"
        private readonly SemaphoreSlim _requestSemaphore = new SemaphoreSlim(1, 1); // Последовательная обработка через один сервис
        
        // Кэшированный HashSet для быстрого поиска галлюцинаций (O(1) вместо O(n))
        private static readonly HashSet<string> CommonHallucinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "you", "yes", "no", "ok", "hi", "oh", "ah", "um", "uh", "er", "mm", "hm"
        };

        public WhisperRunner(string whisperServicePath, string whisperModelPath, string whisperLanguage = "")
        {
            _whisperServicePath = whisperServicePath;
            _whisperModelPath = whisperModelPath;
            _whisperLanguage = whisperLanguage ?? "";
        }

        /// <summary>
        /// Предварительно запускает WhisperService для подготовки к обработке.
        /// Вызывается при снятии с паузы, чтобы избежать задержки при первой транскрипции.
        /// </summary>
        public async Task WarmUp()
        {
            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp started (thread: {Thread.CurrentThread.ManagedThreadId})");
            await _requestSemaphore.WaitAsync();
            try
            {
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: calling EnsureWhisperServiceStarted (thread: {Thread.CurrentThread.ManagedThreadId})");
                EnsureWhisperServiceStarted();
                
                // Проверяем, что процесс запущен
                if (_whisperServiceProcess == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: ERROR - Process is null after EnsureWhisperServiceStarted!");
                    throw new Exception("WhisperService process is null");
                }
                
                // Ждем, когда сервис будет готов (READY)
                if (_readyTask != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: waiting for READY task (PID: {_whisperServiceProcess.Id})");
                    
                    // Используем таймаут для чтения READY (30 секунд)
                    var readyTaskWithTimeout = Task.WhenAny(_readyTask, Task.Delay(30000));
                    var completedTask = await readyTaskWithTimeout;
                    
                    if (completedTask == _readyTask)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: READY task completed (PID: {_whisperServiceProcess.Id})");
                        await _readyTask; // Дожидаемся завершения, чтобы обработать исключения
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: WARNING - READY task timeout (PID: {_whisperServiceProcess.Id})");
                        // Проверяем состояние процесса
                        if (_whisperServiceProcess.HasExited)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: ERROR - Process exited (exit code: {_whisperServiceProcess.ExitCode})");
                            _serviceStarted = false;
                            throw new Exception($"WhisperService process exited before READY (exit code: {_whisperServiceProcess.ExitCode})");
                        }
                    }
                    
                    // Дополнительная небольшая задержка, чтобы убедиться, что сервис полностью инициализирован
                    await Task.Delay(100);
                    
                    // Финальная проверка состояния процесса
                    if (_whisperServiceProcess.HasExited)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: ERROR - Process exited after READY (exit code: {_whisperServiceProcess.ExitCode})");
                        _serviceStarted = false;
                        throw new Exception($"WhisperService process exited after READY (exit code: {_whisperServiceProcess.ExitCode})");
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: completed successfully (PID: {_whisperServiceProcess.Id}, HasExited: {_whisperServiceProcess.HasExited})");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: WARNING - _readyTask is null!");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WarmUp: ERROR - {ex.Message}");
                throw;
            }
            finally
            {
                _requestSemaphore.Release();
            }
        }

        private void EnsureWhisperServiceStarted()
        {
            lock (_serviceLock)
            {
                if (_serviceStarted && _whisperServiceProcess != null && !_whisperServiceProcess.HasExited)
                    return;

                // Очищаем предыдущий процесс, если он существует
                if (_whisperServiceProcess != null && _whisperServiceProcess.HasExited)
                {
                    try
                    {
                        if (_processInputWriter != null)
                        {
                            _processInputWriter.Dispose();
                            _processInputWriter = null;
                        }
                        if (_processOutputReader != null)
                        {
                            _processOutputReader.Dispose();
                            _processOutputReader = null;
                        }
                        if (_processErrorReader != null)
                        {
                            _processErrorReader.Dispose();
                            _processErrorReader = null;
                        }
                        _whisperServiceProcess.Dispose();
                    }
                    catch { }
                    _whisperServiceProcess = null;
                }

                // Сбрасываем предыдущую задачу чтения READY при перезапуске
                _readyTask = null;

                // Запуск WhisperService
                // _whisperServicePath уже может быть абсолютным (если разрешен в MainWindow)
                // или относительным (нужно разрешить относительно solution root)
                string serviceExePath;
                if (Path.IsPathRooted(_whisperServicePath))
                {
                    serviceExePath = _whisperServicePath;
                }
                else
                {
                    var solutionRoot = Core.Settings.ResolveSolutionRelativePath("");
                    serviceExePath = Path.Combine(solutionRoot, _whisperServicePath);
                }
                
                if (!File.Exists(serviceExePath))
                {
                    throw new FileNotFoundException($"WhisperService executable not found: {serviceExePath}");
                }

                // _whisperModelPath также уже может быть разрешен в MainWindow
                string modelPath;
                if (Path.IsPathRooted(_whisperModelPath))
                {
                    modelPath = _whisperModelPath;
                }
                else
                {
                    modelPath = Core.Settings.ResolveSolutionRelativePath(_whisperModelPath);
                }
                
                if (!File.Exists(modelPath))
                {
                    throw new FileNotFoundException($"Whisper model not found: {modelPath}");
                }

                // Устанавливаем рабочую директорию в tools\whisper\ где находятся whisper.dll и другие зависимости
                string whisperDir = Path.GetDirectoryName(serviceExePath) ?? "";
                // Поднимаемся на 4 уровня вверх от WhisperService\bin\Debug\net8.0\ до tools\whisper\
                // Путь: tools\whisper\WhisperService\bin\Debug\net8.0\ -> tools\whisper\
                for (int i = 0; i < 4 && !string.IsNullOrEmpty(whisperDir); i++)
                {
                    var parent = Directory.GetParent(whisperDir);
                    whisperDir = parent?.FullName ?? whisperDir;
                }
                
                // Проверяем наличие whisper.dll в предполагаемой директории
                string? workingDir = null;
                if (Directory.Exists(whisperDir))
                {
                    string whisperDllPath = Path.Combine(whisperDir, "whisper.dll");
                    if (File.Exists(whisperDllPath))
                    {
                        workingDir = whisperDir;
                    }
                }
                
                // Если не нашли, используем директорию exe в качестве fallback
                if (workingDir == null)
                {
                    workingDir = Path.GetDirectoryName(serviceExePath) ?? "";
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WARNING: whisper.dll not found in {whisperDir}, using exe directory as WorkingDirectory");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Using WorkingDirectory: {workingDir}");
                }

                // Формируем аргументы: если язык не указан (пустая строка), не передаем его вообще
                string arguments = $"\"{modelPath}\"";
                if (!string.IsNullOrEmpty(_whisperLanguage))
                {
                    arguments += $" {_whisperLanguage}";
                }
                
                var startInfo = new ProcessStartInfo
                {
                    FileName = serviceExePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir
                };

                _whisperServiceProcess = Process.Start(startInfo);
                if (_whisperServiceProcess == null)
                {
                    throw new Exception("Failed to start WhisperService process");
                }

                // ВАЖНО: Используем UTF-8 БЕЗ BOM (encoderShouldEmitUTF8Identifier: false)
                // Это предотвращает добавление BOM в начало строк при записи в stdin
                var utf8NoBom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                _processInputWriter = new StreamWriter(_whisperServiceProcess.StandardInput.BaseStream, utf8NoBom) { AutoFlush = true };
                _processOutputReader = new StreamReader(_whisperServiceProcess.StandardOutput.BaseStream, utf8NoBom);
                _processErrorReader = new StreamReader(_whisperServiceProcess.StandardError.BaseStream, utf8NoBom);
                
                // Запускаем чтение stderr в фоне для диагностики
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string? errorLine;
                        while ((errorLine = await _processErrorReader.ReadLineAsync()) != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] stderr: {errorLine}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Error reading stderr: {ex.Message}");
                    }
                });

                // Читаем "READY" из stdout синхронно с таймаутом
                // Это предотвращает конфликт с последующими асинхронными чтениями
                _readyTask = Task.Run(async () =>
                {
                    try
                    {
                        string? readyLine = await _processOutputReader.ReadLineAsync();
                        
                        // Проверяем, не завершился ли процесс
                        if (_whisperServiceProcess?.HasExited == true)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: WhisperService process exited while reading READY (exit code: {_whisperServiceProcess.ExitCode})");
                            
                            // Попробуем прочитать stderr для диагностики
                            if (_whisperServiceProcess.StandardError != null)
                            {
                                try
                                {
                                    string? errorOutput = await _whisperServiceProcess.StandardError.ReadToEndAsync();
                                    if (!string.IsNullOrEmpty(errorOutput))
                                    {
                                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WhisperService stderr: {errorOutput}");
                                    }
                                }
                                catch { }
                            }
                            
                            _serviceStarted = false;
                            return;
                        }
                        
                        if (readyLine?.Trim() == "READY")
                        {
                            System.Diagnostics.Debug.WriteLine("[WhisperRunner] WhisperService ready");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WARNING: WhisperService did not send READY, got: {readyLine}");
                            // Если получили пустую строку или null, это может означать, что поток закрылся
                            if (string.IsNullOrEmpty(readyLine))
                            {
                                System.Diagnostics.Debug.WriteLine("[WhisperRunner] ERROR: Received empty line, process may have exited");
                                _serviceStarted = false;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR reading READY: {ex.Message}");
                        _serviceStarted = false;
                    }
                });

                _serviceStarted = true;
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WhisperService started (PID: {_whisperServiceProcess.Id})");
            }
        }

        public async Task<List<TextSegment>> TranscribeAsync(string wavPath)
        {
            var startTime = DateTime.UtcNow;
            var segments = new List<TextSegment>();

            if (!File.Exists(wavPath))
            {
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: WAV file not found at {wavPath}");
                return segments;
            }

            var wavFileInfo = new FileInfo(wavPath);
            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Transcribing WAV file: {wavPath} ({wavFileInfo.Length} bytes) at {startTime:HH:mm:ss.fff}");

            await _requestSemaphore.WaitAsync();
            try
            {
                EnsureWhisperServiceStarted();

                if (_processInputWriter == null || _processOutputReader == null || _whisperServiceProcess == null || _whisperServiceProcess.HasExited)
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: WhisperService process is not available");
                    return segments;
                }

                // Дожидаемся завершения чтения "READY", чтобы избежать конфликта потоков
                if (_readyTask != null)
                {
                    try
                    {
                        await _readyTask.WaitAsync(TimeSpan.FromSeconds(10));
                        _readyTask = null; // Сбрасываем после первого использования
                    }
                    catch (TimeoutException)
                    {
                        System.Diagnostics.Debug.WriteLine("[WhisperRunner] WARNING: Timeout waiting for READY signal");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] WARNING: Error waiting for READY: {ex.Message}");
                    }
                }

                // Проверяем, не завершился ли процесс после чтения READY
                if (_whisperServiceProcess == null || _whisperServiceProcess.HasExited)
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: WhisperService process exited (exit code: {_whisperServiceProcess?.ExitCode ?? -1})");
                    _serviceStarted = false; // Позволяем перезапустить при следующем вызове
                    return segments;
                }

                // Отправляем путь к WAV файлу в stdin
                try
                {
                    await _processInputWriter.WriteLineAsync(wavPath);
                    await _processInputWriter.FlushAsync();
                }
                catch (IOException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: Failed to write to WhisperService stdin: {ex.Message}");
                    _serviceStarted = false; // Позволяем перезапустить при следующем вызове
                    return segments;
                }

                // Читаем JSON ответ из stdout
                var readStartTime = DateTime.UtcNow;
                
                // Используем таймаут для чтения ответа (30 секунд должно быть достаточно для обработки)
                var readTask = _processOutputReader.ReadLineAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
                var completedTask = await Task.WhenAny(readTask, timeoutTask);
                
                string? jsonLine = null;
                var readEndTime = DateTime.UtcNow;
                var readDuration = (readEndTime - readStartTime).TotalMilliseconds;
                
                if (completedTask == readTask)
                {
                    jsonLine = await readTask;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: Timeout reading response from WhisperService (waited {readDuration:F1}ms)");
                    // Проверяем, не завершился ли процесс
                    if (_whisperServiceProcess?.HasExited == true)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: WhisperService process exited (exit code: {_whisperServiceProcess.ExitCode})");
                        _serviceStarted = false;
                    }
                    return segments;
                }
                
                if (string.IsNullOrEmpty(jsonLine))
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: Empty response from WhisperService (read took {readDuration:F1}ms)");
                    // Проверяем, не завершился ли процесс
                    if (_whisperServiceProcess?.HasExited == true)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: WhisperService process exited (exit code: {_whisperServiceProcess.ExitCode})");
                        _serviceStarted = false;
                        // Пытаемся прочитать stderr для диагностики
                        try
                        {
                            // stderr читается в фоне, просто логируем что процесс завершился
                        }
                        catch { }
                    }
                    return segments;
                }
                
                // ВАЖНО: Логируем сырой JSON для отладки
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Received JSON (first 500 chars): {jsonLine.Substring(0, Math.Min(500, jsonLine.Length))} (read took {readDuration:F1}ms)");

                // Парсим JSON ответ
                try
                {
                    var jsonObj = JObject.Parse(jsonLine);

                    // Проверка на ошибку
                    if (jsonObj["error"] != null)
                    {
                        string error = jsonObj["error"]?.Value<string>() ?? "Unknown error";
                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR from WhisperService: {error}");
                        throw new Exception($"WhisperService error: {error}");
                    }

                    // Парсим сегменты
                    segments = ParseWhisperJson(jsonObj);
                }
                catch (JsonException ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR parsing JSON: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] JSON content: {jsonLine}");
                    throw;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] StackTrace: {ex.StackTrace}");
            }
            finally
            {
                _requestSemaphore.Release();
            }

            var endTime = DateTime.UtcNow;
            var totalDuration = (endTime - startTime).TotalMilliseconds;
            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] TranscribeAsync completed: {segments.Count} segments in {totalDuration:F1}ms (started at {startTime:HH:mm:ss.fff}, ended at {endTime:HH:mm:ss.fff})");
            return segments;
        }

        private List<TextSegment> ParseWhisperJson(JObject jsonObj)
        {
            var segments = new List<TextSegment>();

            try
            {
                // WhisperService возвращает формат: { "segments": [...] }
                JArray? segmentsArray = jsonObj["segments"] as JArray;

                if (segmentsArray != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Found {segmentsArray.Count} items in segments array");

                    foreach (var segmentToken in segmentsArray)
                    {
                        var segment = segmentToken as JObject;
                        if (segment == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Skipping non-object segment: {segmentToken}");
                            continue;
                        }

                        // WhisperService формат: { "offsets": { "from": ms, "to": ms }, "start": sec, "end": sec, "text": "..." }
                        var offsets = segment["offsets"] as JObject;
                        double startSec = 0.0;
                        double endSec = 0.0;

                        if (offsets != null)
                        {
                            // Используем offsets (в миллисекундах)
                            startSec = (offsets["from"]?.Value<double>() ?? 0.0) / 1000.0;
                            endSec = (offsets["to"]?.Value<double>() ?? 0.0) / 1000.0;
                        }
                        else
                        {
                            // Fallback на start/end (в секундах)
                            startSec = segment["start"]?.Value<double>() ?? 0.0;
                            endSec = segment["end"]?.Value<double>() ?? 0.0;
                        }

                        string text = segment["text"]?.Value<string>()?.Trim() ?? string.Empty;

                        var textSegment = new TextSegment
                        {
                            startSec = startSec,
                            endSec = endSec,
                            text = text
                        };

                        System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Parsed segment: start={textSegment.startSec}, end={textSegment.endSec}, text='{textSegment.text}'");

                        // Фильтровать пустой текст и специальные метки
                        if (string.IsNullOrEmpty(textSegment.text))
                        {
                            continue;
                        }

                        // Очистить текст от артефактных символов (например, "в™Є" и подобных)
                        // Это могут быть проблемы кодировки или специальные символы от Whisper
                        string cleanedText = textSegment.text;
                        
                        // Удаляем известные артефактные символы/последовательности
                        // "в™Є" появляется из-за проблем с кодировкой некоторых символов
                        cleanedText = cleanedText.Replace("в™Є", "").Replace("в™", "").Replace("™Є", "");
                        
                        // Удаляем символы нот (♪ U+266A и ♫ U+266B)
                        cleanedText = cleanedText.Replace("♪", "").Replace("♫", "");
                        
                        // Удаляем невидимые/контрольные символы и артефакты кодировки
                        var sbClean = new StringBuilder(cleanedText.Length);
                        foreach (char c in cleanedText)
                        {
                            // Пропускаем только печатные символы и пробелы
                            // Исключаем контрольные символы, символы форматирования, и артефакты кодировки
                            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                                continue;
                            // Пропускаем некоторые проблемные символы, которые могут появляться как артефакты
                            // Например, символы форматирования Unicode (U+200B - U+200F, U+FEFF и т.д.)
                            if (c >= 0x200B && c <= 0x200F) // Zero-width spaces и directional marks
                                continue;
                            if (c == 0xFEFF) // Zero Width No-Break Space (BOM)
                                continue;
                            sbClean.Append(c);
                        }
                        cleanedText = sbClean.ToString().Trim();

                        // Обновляем текст сегмента очищенной версией
                        textSegment.text = cleanedText;

                        if (string.IsNullOrEmpty(textSegment.text))
                        {
                            continue;
                        }

                        // Фильтровать специальные метки Whisper
                        string textTrimmed = textSegment.text.Trim();
                        string textLower = textTrimmed.ToLowerInvariant();

                        // ИСПРАВЛЕНО: Фильтруем ВСЕ метки в формате [...] (квадратные скобки)
                        // Это включает [Music], [MUSIC], [bell dings], [BLANK_AUDIO] и т.д.
                        bool isBracketLabel = textTrimmed.StartsWith("[") && textTrimmed.EndsWith("]");

                        // ИСПРАВЛЕНО: Фильтруем ВСЕ метки в формате *...* (звездочки)
                        // Это включает *Gasps*, *Laughs*, *Sighs* и т.д.
                        bool isAsteriskLabel = textTrimmed.StartsWith("*") && textTrimmed.EndsWith("*");

                        // ИСПРАВЛЕНО: Фильтруем ВСЕ метки в формате (...) (круглые скобки)
                        // Это включает (upbeat music), (music), (speaking in foreign language) и любые другие
                        bool isParenthesisLabel = textTrimmed.StartsWith("(") && textTrimmed.EndsWith(")");

                        // Проверяем другие специальные метки (на случай, если они не в скобках)
                        bool isOtherSpecialLabel = textSegment.text.Contains("(speaking in foreign language)") ||
                                                  textSegment.text.Contains("[BLANK_AUDIO]");

                        // Фильтруем титры с редактором/корректором субтитров (постоянно повторяющиеся кредиты)
                        // Раньше фильтровали только, если были и "редактор субтитров" и "корректор".
                        // Теперь режем любые фразы, начинающиеся или содержащие "редактор субтитров" (даже без "корректор"),
                        // а также комбинации с "корректор субтитров" и похожие кредиты.
                        bool hasSubtitleEditor = textLower.Contains("редактор субтитров");
                        bool hasSubtitleCorrector = textLower.Contains("корректор субтитров") || textLower.Contains("корректор");
                        bool isCreditsLabel =
                            hasSubtitleEditor ||
                            hasSubtitleCorrector ||
                            (textLower.Contains("перевод субтитров") || textLower.Contains("переводчик субтитров"));

                        bool isSpecialLabel = isBracketLabel || isAsteriskLabel || isParenthesisLabel || isOtherSpecialLabel || isCreditsLabel;

                        if (isSpecialLabel)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Filtered special label: text='{textSegment.text}'");
                            continue;
                        }

                        // Фильтровать очень короткие сегменты (меньше 0.1 секунды)
                        // ИСПРАВЛЕНО: Снижен порог с 0.3 до 0.1 для пропуска более коротких сегментов
                        double segmentDuration = textSegment.endSec - textSegment.startSec;
                        if (segmentDuration < 0.1)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Filtered short segment: duration={segmentDuration:F2}s, text='{textSegment.text}'");
                            continue;
                        }

                        // Фильтровать очень короткие тексты (меньше 3 символов без учета пробелов и знаков препинания)
                        int letterCount = 0;
                        foreach (char c in textSegment.text)
                        {
                            if (char.IsLetterOrDigit(c))
                                letterCount++;
                        }
                        if (letterCount < 3)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Filtered short text: cleanLength={letterCount}, text='{textSegment.text}'");
                            continue;
                        }

                        // Разделить текст на слова
                        string[] splitWords = textSegment.text.Split(new[] { ' ', '\t', '\n', '\r', '-', '.', ',', '?', '!', ';', ':' }, StringSplitOptions.RemoveEmptyEntries);
                        var words = new List<string>(splitWords.Length);
                        foreach (var word in splitWords)
                        {
                            var sb = new StringBuilder(word.Length);
                            foreach (char c in word)
                            {
                                if (char.IsLetterOrDigit(c) || c == '\'')
                                    sb.Append(c);
                            }
                            string cleanedWord = sb.ToString();
                            if (!string.IsNullOrEmpty(cleanedWord))
                                words.Add(cleanedWord);
                        }
                        string[] wordsArray = words.ToArray();

                        // Фильтровать одиночные короткие слова
                        if (wordsArray.Length == 1 && wordsArray[0].Length < 4)
                        {
                            if (segmentDuration < 1.0)
                            {
                                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Filtered single short word in short segment: duration={segmentDuration:F2}s, text='{textSegment.text}'");
                                continue;
                            }
                            string wordLower = wordsArray[0].ToLowerInvariant();
                            if (CommonHallucinations.Contains(wordLower))
                            {
                                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Filtered common hallucination word: text='{textSegment.text}', duration={segmentDuration:F2}s");
                                continue;
                            }
                        }

                        // Фильтровать очень короткие сегменты с одиночными словами
                        if (segmentDuration < 0.5 && wordsArray.Length == 1 && wordsArray[0].Length <= 4)
                        {
                            System.Diagnostics.Debug.WriteLine($"[WhisperRunner] Filtered very short segment with single word: duration={segmentDuration:F2}s, text='{textSegment.text}'");
                            continue;
                        }

                        segments.Add(textSegment);
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[WhisperRunner] No 'segments' array found in JSON");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] ERROR parsing JSON: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[WhisperRunner] StackTrace: {ex.StackTrace}");
            }

            return segments;
        }

        public void Dispose()
        {
            lock (_serviceLock)
            {
                _readyTask = null; // Сбрасываем задачу чтения READY

                if (_processInputWriter != null)
                {
                    try
                    {
                        // Отправляем EXIT команду
                        _processInputWriter.WriteLineAsync("EXIT").Wait(TimeSpan.FromSeconds(2));
                    }
                    catch { }
                    
                    _processInputWriter.Dispose();
                    _processInputWriter = null;
                }

                if (_processOutputReader != null)
                {
                    _processOutputReader.Dispose();
                    _processOutputReader = null;
                }

                if (_processErrorReader != null)
                {
                    _processErrorReader.Dispose();
                    _processErrorReader = null;
                }

                if (_whisperServiceProcess != null)
                {
                    try
                    {
                        if (!_whisperServiceProcess.HasExited)
                        {
                            _whisperServiceProcess.Kill();
                            _whisperServiceProcess.WaitForExit(5000);
                        }
                    }
                    catch { }
                    
                    _whisperServiceProcess.Dispose();
                    _whisperServiceProcess = null;
                }

                _requestSemaphore.Dispose();
                _serviceStarted = false;
                System.Diagnostics.Debug.WriteLine("[WhisperRunner] WhisperService disposed");
            }
        }
    }
}