using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WhisperService;

class Program
{
    private static IntPtr _whisperContext = IntPtr.Zero;
    private static string? _modelPath;

    static async Task Main(string[] args)
    {
        // ВАЖНО: Устанавливаем кодировку UTF-8 без BOM для stdin/stdout/stderr
        // Это предотвращает проблему с BOM (я╗┐) в начале строк при чтении путей к файлам
        Console.InputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        
        // ВАЖНО: Выводим в самом начале, чтобы убедиться, что stderr работает
        Console.Error.WriteLine("[WhisperService] ========== PROGRAM START ==========");
        Console.Error.Flush();
        
        // Парсинг аргументов командной строки
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: WhisperService.exe <model-path> [language]");
            Environment.Exit(1);
            return;
        }

        _modelPath = args[0];
        string? language = args.Length > 1 && !string.IsNullOrEmpty(args[1]) ? args[1] : null;
        
        try
        {

            Console.Error.WriteLine($"[WhisperService] Model path: {_modelPath}");
            Console.Error.WriteLine($"[WhisperService] Language: {language ?? "null"}");
            Console.Error.Flush();

            // Проверка существования файла модели
            if (!File.Exists(_modelPath))
            {
                Console.Error.WriteLine($"ERROR: Whisper model not found: {_modelPath}");
                Environment.Exit(1);
                return;
            }

            Console.Error.WriteLine("[WhisperService] Model file exists, proceeding to load...");
            Console.Error.Flush();

            // Загрузка модели один раз при старте
            Console.Error.WriteLine($"[WhisperService] ========== MODEL LOADING START ==========");
            Console.Error.WriteLine($"[WhisperService] Loading model from: {_modelPath}");
            Console.Error.Flush();
            
            // Проверяем, что файл существует
            if (!File.Exists(_modelPath))
            {
                Console.Error.WriteLine($"ERROR: Model file does not exist: {_modelPath}");
                Environment.Exit(1);
                return;
            }
            
            var modelFileInfo = new FileInfo(_modelPath);
            long modelSizeBytes = modelFileInfo.Length;
            Console.Error.WriteLine($"[WhisperService] Model file exists, size: {modelSizeBytes} bytes");
            Console.Error.Flush();
            
            // Проверка минимального размера модели (модели Whisper обычно от 75 МБ для tiny до нескольких ГБ для large)
            // Если файл меньше 1 МБ, это явно не валидная модель
            const long MIN_MODEL_SIZE_BYTES = 1024 * 1024; // 1 МБ
            if (modelSizeBytes < MIN_MODEL_SIZE_BYTES)
            {
                Console.Error.WriteLine($"ERROR: Model file is too small ({modelSizeBytes} bytes). Expected at least {MIN_MODEL_SIZE_BYTES} bytes.");
                Console.Error.WriteLine($"ERROR: The model file may be corrupted, incomplete, or not downloaded properly.");
                Console.Error.WriteLine($"ERROR: Please download a valid Whisper model from: https://github.com/ggerganov/whisper.cpp/tree/master/models");
                Console.Error.Flush();
                Environment.Exit(1);
                return;
            }
            
            // ВАЖНО: whisper_init_from_file помечена как deprecated и внутри вызывает whisper_context_default_params()
            // которая возвращает структуру по значению. Это может вызывать проблемы с маршалингом на x64.
            // Используем ТОЛЬКО whisper_init_from_file_with_params с ручной инициализацией структуры
            
            Console.Error.WriteLine("[WhisperService] Using whisper_init_from_file_with_params (avoiding deprecated whisper_init_from_file)...");
            Console.Error.Flush();
            
            // Создаем структуру параметров вручную
            Console.Error.WriteLine("[WhisperService] Creating WhisperContextParams structure...");
            Console.Error.Flush();
            
            WhisperNative.WhisperAheads aheads = new WhisperNative.WhisperAheads
            {
                n_heads = UIntPtr.Zero,
                heads = IntPtr.Zero
            };
            Console.Error.WriteLine($"[WhisperService] WhisperAheads created: n_heads={aheads.n_heads}, heads={aheads.heads}");
            Console.Error.Flush();
            
            WhisperNative.WhisperContextParams contextParams = new WhisperNative.WhisperContextParams
            {
                use_gpu = false,
                flash_attn = false,
                gpu_device = 0,
                dtw_token_timestamps = false,
                dtw_aheads_preset = WhisperNative.WhisperAlignmentHeadsPreset.WHISPER_AHEADS_NONE,
                dtw_n_top = -1,
                dtw_aheads = aheads,
                dtw_mem_size = new UIntPtr(1024 * 1024 * 128)
            };
            Console.Error.WriteLine($"[WhisperService] WhisperContextParams created:");
            Console.Error.WriteLine($"  use_gpu={contextParams.use_gpu}, flash_attn={contextParams.flash_attn}, gpu_device={contextParams.gpu_device}");
            Console.Error.WriteLine($"  dtw_token_timestamps={contextParams.dtw_token_timestamps}, dtw_aheads_preset={contextParams.dtw_aheads_preset}");
            Console.Error.WriteLine($"  dtw_n_top={contextParams.dtw_n_top}, dtw_mem_size={contextParams.dtw_mem_size}");
            Console.Error.Flush();
            
            // Проверяем размер структуры
            try
            {
                int structSize = Marshal.SizeOf(typeof(WhisperNative.WhisperContextParams));
                Console.Error.WriteLine($"[WhisperService] WhisperContextParams Marshal.SizeOf: {structSize} bytes");
                
                int aheadsSize = Marshal.SizeOf(typeof(WhisperNative.WhisperAheads));
                Console.Error.WriteLine($"[WhisperService] WhisperAheads Marshal.SizeOf: {aheadsSize} bytes");
                Console.Error.Flush();
            }
            catch (Exception sizeEx)
            {
                Console.Error.WriteLine($"[WhisperService] WARNING: Failed to get struct size: {sizeEx.Message}");
                Console.Error.Flush();
            }
            
            // Вызываем функцию с параметрами
            Console.Error.WriteLine("[WhisperService] About to call whisper_init_from_file_with_params...");
            Console.Error.Flush();
            
            try
            {
                _whisperContext = WhisperNative.whisper_init_from_file_with_params(_modelPath, contextParams);
                Console.Error.WriteLine($"[WhisperService] SUCCESS: whisper_init_from_file_with_params returned: {_whisperContext} (Zero={_whisperContext == IntPtr.Zero})");
                Console.Error.Flush();
            }
            catch (AccessViolationException avex2)
            {
                Console.Error.WriteLine($"[WhisperService] AccessViolationException in whisper_init_from_file_with_params: {avex2.Message}");
                Console.Error.WriteLine($"[WhisperService] This indicates a marshalling problem with the WhisperContextParams structure");
                Console.Error.WriteLine($"[WhisperService] StackTrace: {avex2.StackTrace}");
                Console.Error.Flush();
                throw;
            }
            catch (Exception ex2)
            {
                Console.Error.WriteLine($"[WhisperService] Exception in whisper_init_from_file_with_params: {ex2.GetType().Name}: {ex2.Message}");
                Console.Error.WriteLine($"[WhisperService] StackTrace: {ex2.StackTrace}");
                Console.Error.Flush();
                throw;
            }

            if (_whisperContext == IntPtr.Zero)
            {
                Console.Error.WriteLine($"ERROR: Failed to load Whisper model from {_modelPath}");
                Console.Error.WriteLine($"ERROR: whisper_init_from_file_with_params returned NULL");
                Environment.Exit(1);
                return;
            }

            Console.Error.WriteLine($"[WhisperService] Model loaded successfully: {_modelPath}");
        }
        catch (DllNotFoundException dllEx)
        {
            Console.Error.WriteLine($"ERROR: DLL not found: {dllEx.Message}");
            Console.Error.WriteLine($"ERROR: Make sure whisper.dll is in the WorkingDirectory");
            Environment.Exit(1);
            return;
        }
        catch (EntryPointNotFoundException entryEx)
        {
            Console.Error.WriteLine($"ERROR: Entry point not found: {entryEx.Message}");
            Console.Error.WriteLine($"ERROR: Function may not exist in whisper.dll");
            Environment.Exit(1);
            return;
        }
        catch (AccessViolationException avEx)
        {
            Console.Error.WriteLine($"ERROR: Access violation - likely structure mismatch: {avEx.Message}");
            Console.Error.WriteLine($"ERROR: StackTrace: {avEx.StackTrace}");
            Environment.Exit(1);
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to initialize Whisper: {ex.Message}");
            Console.Error.WriteLine($"ERROR: Exception type: {ex.GetType().Name}");
            Console.Error.WriteLine($"ERROR: StackTrace: {ex.StackTrace}");
            Environment.Exit(1);
            return;
        }

        // Заголовок для stdout (чтобы клиент знал, что сервис готов)
        Console.Out.WriteLine("READY");
        Console.Out.Flush();

        // Основной цикл: читаем пути к WAV файлам из stdin
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            line = line.Trim();
            
            // Удаляем BOM (U+FEFF) из начала строки, если он присутствует
            // BOM может появиться, если StreamWriter добавил его в первую строку
            if (line.Length > 0 && line[0] == '\uFEFF')
            {
                line = line.Substring(1);
            }

            // Пустая строка - игнорируем
            if (string.IsNullOrEmpty(line))
                continue;

            // Команда "EXIT" - завершаем работу
            if (line.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            // Обрабатываем WAV файл
            await ProcessWavFileAsync(line, language);
        }

        // Освобождаем ресурсы
        if (_whisperContext != IntPtr.Zero)
        {
            WhisperNative.whisper_free(_whisperContext);
            _whisperContext = IntPtr.Zero;
            Console.Error.WriteLine("[WhisperService] Model unloaded");
        }
    }

    private static Task ProcessWavFileAsync(string wavPath, string? language)
    {
        IntPtr languagePtr = IntPtr.Zero;
        try
        {
            // Проверка существования WAV файла
            if (!File.Exists(wavPath))
            {
                var errorJson = JsonSerializer.Serialize(new { error = $"WAV file not found: {wavPath}" });
                Console.Out.WriteLine(errorJson);
                Console.Out.Flush();
                return Task.CompletedTask;
            }

            // Читаем WAV файл и конвертируем в PCM float32 16kHz mono
            float[] audioSamples;
            try
            {
                audioSamples = WavReader.ReadWavToFloat32(wavPath);
            }
            catch (Exception ex)
            {
                var errorJson = JsonSerializer.Serialize(new { error = $"Failed to read WAV file: {ex.Message}" });
                Console.Out.WriteLine(errorJson);
                Console.Out.Flush();
                return Task.CompletedTask;
            }

            // Настройка параметров для whisper_full
            var fullParams = WhisperNative.whisper_full_default_params(WhisperNative.WhisperSamplingStrategy.WHISPER_SAMPLING_GREEDY);

            // Устанавливаем язык: если передан - используем его, иначе - автоопределение
            if (!string.IsNullOrEmpty(language))
            {
                // Преобразуем строку языка в указатель на char*
                // Важно: память будет освобождена после использования (в finally блоке)
                byte[] languageBytes = System.Text.Encoding.ASCII.GetBytes(language);
                languagePtr = Marshal.AllocHGlobal(languageBytes.Length + 1);
                Marshal.Copy(languageBytes, 0, languagePtr, languageBytes.Length);
                Marshal.WriteByte(languagePtr, languageBytes.Length, 0); // null terminator
                
                Console.Error.WriteLine($"[WhisperService] Setting language manually to: {language} (no auto-detection)");
            }
            else
            {
                // languagePtr остается IntPtr.Zero для автоопределения языка
                Console.Error.WriteLine("[WhisperService] Language not specified, using auto-detection");
            }
            Console.Error.Flush();

            // Настройки для транскрипции
            fullParams.n_threads = Math.Max(1, Environment.ProcessorCount / 2); // Используем половину доступных потоков
            fullParams.translate = false;
            fullParams.no_context = true;
            fullParams.no_timestamps = false;
            fullParams.single_segment = false;
            fullParams.print_special = false;
            fullParams.print_progress = false;
            fullParams.print_realtime = false;
            fullParams.print_timestamps = false;
            // ВАЖНО: Отключаем строгую фильтрацию для отладки
            fullParams.suppress_blank = false; // Не подавлять пустые токены - могут содержать полезную информацию
            fullParams.suppress_nst = false;   // Не подавлять no-speech tokens - позволить им пройти
            fullParams.temperature = 0.0f; // Жадный декодинг
            fullParams.max_initial_ts = 1.0f;
            fullParams.length_penalty = -1.0f;
            fullParams.temperature_inc = 0.2f;
            fullParams.entropy_thold = -1.0f; // Отключаем порог энтропии (было 2.4f) - -1.0 отключает фильтрацию
            fullParams.logprob_thold = -1.0f; // Отключаем порог logprob
            fullParams.no_speech_thold = 0.1f; // Еще более чувствительный порог (было 0.2f)
            // ИСПРАВЛЕНО: Снижен с 0.6 до 0.1 для максимально чувствительного определения речи
            
            // ВАЖНО: Логируем параметры для отладки
            Console.Error.WriteLine($"[WhisperService] whisper_full params: n_threads={fullParams.n_threads}, no_speech_thold={fullParams.no_speech_thold}, entropy_thold={fullParams.entropy_thold}");
            Console.Error.Flush();

            // Очищаем указатели (callbacks, grammar, etc.) - устанавливаем в null
            fullParams.suppress_regex = IntPtr.Zero;
            fullParams.initial_prompt = IntPtr.Zero;
            fullParams.prompt_tokens = IntPtr.Zero;
            // Устанавливаем язык вручную, если указан
            fullParams.language = languagePtr;
            fullParams.detect_language = languagePtr == IntPtr.Zero; // Автоопределение только если язык не задан
            fullParams.new_segment_callback = IntPtr.Zero;
            fullParams.new_segment_callback_user_data = IntPtr.Zero;
            fullParams.progress_callback = IntPtr.Zero;
            fullParams.progress_callback_user_data = IntPtr.Zero;
            fullParams.encoder_begin_callback = IntPtr.Zero;
            fullParams.encoder_begin_callback_user_data = IntPtr.Zero;
            fullParams.abort_callback = IntPtr.Zero;
            fullParams.abort_callback_user_data = IntPtr.Zero;
            fullParams.logits_filter_callback = IntPtr.Zero;
            fullParams.logits_filter_callback_user_data = IntPtr.Zero;
            fullParams.grammar_rules = IntPtr.Zero;
            fullParams.n_grammar_rules = UIntPtr.Zero;
            fullParams.i_start_rule = UIntPtr.Zero;
            fullParams.vad = false;
            fullParams.vad_model_path = IntPtr.Zero;

               // Вызываем whisper_full для обработки аудио
               Console.Error.WriteLine($"[WhisperService] Calling whisper_full with {audioSamples.Length} samples");
               Console.Error.Flush();
               
               var whisperStartTime = DateTime.UtcNow;
               int result = WhisperNative.whisper_full(_whisperContext, ref fullParams, audioSamples, audioSamples.Length);
               var whisperEndTime = DateTime.UtcNow;
               var whisperDuration = (whisperEndTime - whisperStartTime).TotalMilliseconds;

               Console.Error.WriteLine($"[WhisperService] whisper_full returned: {result}");
               Console.Error.WriteLine($"[WhisperService] whisper_full execution time: {whisperDuration:F1}ms");
               Console.Error.Flush();

            if (result != 0)
            {
                var errorJson = JsonSerializer.Serialize(new { error = $"Whisper processing failed with code {result}" });
                Console.Out.WriteLine(errorJson);
                Console.Out.Flush();
                return Task.CompletedTask;
            }

            // Получаем количество сегментов
            int nSegments = WhisperNative.whisper_full_n_segments(_whisperContext);
            Console.Error.WriteLine($"[WhisperService] whisper_full_n_segments returned: {nSegments}");
            Console.Error.Flush();

            // Формируем JSON результат в формате, совместимом с whisper-cli.exe
            var segments = new System.Collections.Generic.List<object>();

            for (int i = 0; i < nSegments; i++)
            {
                // Получаем текст сегмента
                IntPtr textPtr = WhisperNative.whisper_full_get_segment_text(_whisperContext, i);
                string? text = WhisperNative.PtrToStringAnsi(textPtr);

                // Получаем временные метки (в миллисекундах)
                long t0 = WhisperNative.whisper_full_get_segment_t0(_whisperContext, i);
                long t1 = WhisperNative.whisper_full_get_segment_t1(_whisperContext, i);
                
                // Логируем каждый сегмент до фильтрации
                Console.Error.WriteLine($"[WhisperService] Segment {i}: text='{text}', t0={t0}, t1={t1}");
                Console.Error.Flush();
                
                if (string.IsNullOrEmpty(text))
                {
                    Console.Error.WriteLine($"[WhisperService] Skipping segment {i}: empty text");
                    Console.Error.Flush();
                    continue;
                }

                // Конвертируем в секунды
                double startSec = t0 / 1000.0;
                double endSec = t1 / 1000.0;

                // Формируем объект сегмента в формате whisper.cpp JSON
                segments.Add(new
                {
                    offsets = new
                    {
                        from = t0,
                        to = t1
                    },
                    start = startSec,
                    end = endSec,
                    text = text.Trim()
                });
            }

            // Формируем финальный JSON
            var resultJson = new
            {
                segments = segments
            };

            string jsonOutput = JsonSerializer.Serialize(resultJson, new JsonSerializerOptions
            {
                WriteIndented = false, // Компактный формат для stdout
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // Разрешаем Unicode символы без экранирования
            });

            // Логируем JSON для отладки (в stderr, чтобы не мешать парсингу в stdout)
            Console.Error.WriteLine($"[WhisperService] Sending JSON with {segments.Count} segments (nSegments was {nSegments})");
            Console.Error.WriteLine($"[WhisperService] JSON preview: {jsonOutput.Substring(0, Math.Min(200, jsonOutput.Length))}...");
            Console.Error.Flush();

            Console.Out.WriteLine(jsonOutput);
            Console.Out.Flush();

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            var errorJson = JsonSerializer.Serialize(new { error = $"Exception: {ex.Message}" });
            Console.Out.WriteLine(errorJson);
            Console.Out.Flush();
            return Task.CompletedTask;
        }
        finally
        {
            // Освобождаем память, выделенную для языка
            if (languagePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(languagePtr);
                languagePtr = IntPtr.Zero;
            }
        }
    }
}