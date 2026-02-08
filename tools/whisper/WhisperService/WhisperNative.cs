using System;
using System.Runtime.InteropServices;

namespace WhisperService;

// P/Invoke bindings для whisper.dll

internal static class WhisperNative
{
    private const string DllName = "whisper.dll";

    // Enum для стратегии семплирования
    internal enum WhisperSamplingStrategy
    {
        WHISPER_SAMPLING_GREEDY = 0,
        WHISPER_SAMPLING_BEAM_SEARCH = 1
    }

    // Enum для alignment heads preset (из whisper.h строки 88-104)
    // ВАЖНО: в C++ enum обычно имеет размер int (4 байта)
    // Используем int вместо enum для гарантированного размера
    internal enum WhisperAlignmentHeadsPreset : int
    {
        WHISPER_AHEADS_NONE = 0,
        WHISPER_AHEADS_N_TOP_MOST = 1,
        WHISPER_AHEADS_CUSTOM = 2,
        WHISPER_AHEADS_TINY_EN = 3,
        WHISPER_AHEADS_TINY = 4,
        WHISPER_AHEADS_BASE_EN = 5,
        WHISPER_AHEADS_BASE = 6,
        WHISPER_AHEADS_SMALL_EN = 7,
        WHISPER_AHEADS_SMALL = 8,
        WHISPER_AHEADS_MEDIUM_EN = 9,
        WHISPER_AHEADS_MEDIUM = 10,
        WHISPER_AHEADS_LARGE_V1 = 11,
        WHISPER_AHEADS_LARGE_V2 = 12,
        WHISPER_AHEADS_LARGE_V3 = 13,
        WHISPER_AHEADS_LARGE_V3_TURBO = 14
    }

    // Структура whisper_ahead (из whisper.h строки 106-109)
    [StructLayout(LayoutKind.Sequential)]
    internal struct WhisperAhead
    {
        public int n_text_layer;
        public int n_head;
    }

    // Структура whisper_aheads (из whisper.h строки 111-114)
    // ВАЖНО: на x64 size_t и указатели выровнены на 8 байт
    [StructLayout(LayoutKind.Sequential)]
    internal struct WhisperAheads
    {
        public UIntPtr n_heads;              // size_t (8 bytes на x64)
        public IntPtr heads;                 // const whisper_ahead * (8 bytes на x64)
    }

    // Структура параметров контекста (из whisper.h строки 116-129)
    // Точное соответствие C++ структуре whisper_context_params
    // ВАЖНО: на x64 Windows структуры обычно выровнены на 8 байт по умолчанию
    // Используем Sequential с автоматическим выравниванием
    [StructLayout(LayoutKind.Sequential)]
    internal struct WhisperContextParams
    {
        [MarshalAs(UnmanagedType.I1)]
        public bool use_gpu;                              // bool (1 byte)

        [MarshalAs(UnmanagedType.I1)]
        public bool flash_attn;                           // bool (1 byte)

        public int gpu_device;                            // int (4 bytes, CUDA device)

        // [EXPERIMENTAL] Token-level timestamps with DTW
        [MarshalAs(UnmanagedType.I1)]
        public bool dtw_token_timestamps;                 // bool (1 byte)
        
        public WhisperAlignmentHeadsPreset dtw_aheads_preset; // enum = int (4 bytes)

        public int dtw_n_top;                             // int (4 bytes)
        
        public WhisperAheads dtw_aheads;                  // struct whisper_aheads (16 bytes на x64)

        public UIntPtr dtw_mem_size;                      // size_t (8 bytes на x64)
    }
    

    // Упрощенная структура параметров для whisper_full
    // Используем только необходимые поля
    [StructLayout(LayoutKind.Sequential)]
    internal struct WhisperFullParams
    {
        public WhisperSamplingStrategy strategy;
        public int n_threads;
        public int n_max_text_ctx;
        public int offset_ms;
        public int duration_ms;

        [MarshalAs(UnmanagedType.I1)]
        public bool translate;

        [MarshalAs(UnmanagedType.I1)]
        public bool no_context;

        [MarshalAs(UnmanagedType.I1)]
        public bool no_timestamps;

        [MarshalAs(UnmanagedType.I1)]
        public bool single_segment;

        [MarshalAs(UnmanagedType.I1)]
        public bool print_special;

        [MarshalAs(UnmanagedType.I1)]
        public bool print_progress;

        [MarshalAs(UnmanagedType.I1)]
        public bool print_realtime;

        [MarshalAs(UnmanagedType.I1)]
        public bool print_timestamps;

        [MarshalAs(UnmanagedType.I1)]
        public bool token_timestamps;

        public float thold_pt;
        public float thold_ptsum;
        public int max_len;

        [MarshalAs(UnmanagedType.I1)]
        public bool split_on_word;

        public int max_tokens;

        [MarshalAs(UnmanagedType.I1)]
        public bool debug_mode;

        public int audio_ctx;

        [MarshalAs(UnmanagedType.I1)]
        public bool tdrz_enable;

        public IntPtr suppress_regex; // const char*
        public IntPtr initial_prompt; // const char*

        [MarshalAs(UnmanagedType.I1)]
        public bool carry_initial_prompt;

        public IntPtr prompt_tokens; // const whisper_token*
        public int prompt_n_tokens;

        public IntPtr language; // const char*

        [MarshalAs(UnmanagedType.I1)]
        public bool detect_language;

        [MarshalAs(UnmanagedType.I1)]
        public bool suppress_blank;

        [MarshalAs(UnmanagedType.I1)]
        public bool suppress_nst;

        public float temperature;
        public float max_initial_ts;
        public float length_penalty;
        public float temperature_inc;
        public float entropy_thold;
        public float logprob_thold;
        public float no_speech_thold;

        // Вложенные структуры для greedy и beam_search
        [StructLayout(LayoutKind.Sequential)]
        public struct GreedyParams
        {
            public int best_of;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BeamSearchParams
        {
            public int beam_size;
            public float patience;
        }

        public GreedyParams greedy;
        public BeamSearchParams beam_search;

        // Callbacks - для упрощения устанавливаем в null
        public IntPtr new_segment_callback;
        public IntPtr new_segment_callback_user_data;
        public IntPtr progress_callback;
        public IntPtr progress_callback_user_data;
        public IntPtr encoder_begin_callback;
        public IntPtr encoder_begin_callback_user_data;
        public IntPtr abort_callback;
        public IntPtr abort_callback_user_data;
        public IntPtr logits_filter_callback;
        public IntPtr logits_filter_callback_user_data;

        // Grammar - для упрощения устанавливаем в null
        public IntPtr grammar_rules;
        public UIntPtr n_grammar_rules;
        public UIntPtr i_start_rule;
        public float grammar_penalty;

        // VAD - для упрощения отключаем
        [MarshalAs(UnmanagedType.I1)]
        public bool vad;

        public IntPtr vad_model_path;
        
        // VAD params - упрощенная структура
        [StructLayout(LayoutKind.Sequential)]
        public struct VadParams
        {
            public float threshold;
            public int min_speech_duration_ms;
            public int min_silence_duration_ms;
            public float max_speech_duration_s;
            public int speech_pad_ms;
            public float samples_overlap;
        }

        public VadParams vad_params;
    }

    // whisper_context - непрозрачный указатель (используется как IntPtr напрямую)

    // Инициализация контекста с параметрами по умолчанию
    // ВАЖНО: эта функция возвращает структуру по значению, что может вызывать проблемы с маршалингом
    // Используем whisper_context_default_params_by_ref вместо этого
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "whisper_context_default_params")]
    internal static extern WhisperContextParams whisper_context_default_params();
    
    // Альтернативная функция, которая возвращает указатель на структуру
    // ВАЖНО: вызывающий должен освободить память с помощью whisper_free_context_params
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "whisper_context_default_params_by_ref")]
    internal static extern IntPtr whisper_context_default_params_by_ref();
    
    // Освобождение параметров контекста
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "whisper_free_context_params")]
    internal static extern void whisper_free_context_params(IntPtr params_ptr);

    // Загрузка модели из файла БЕЗ параметров (простая версия)
    // Эта функция использует параметры по умолчанию внутри библиотеки
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "whisper_init_from_file")]
    internal static extern IntPtr whisper_init_from_file(
        [MarshalAs(UnmanagedType.LPStr)] string path_model);
    
    // Загрузка модели из файла с параметрами
    // ВАЖНО: функция принимает структуру по значению (by value), не по указателю
    // Это означает, что структура копируется на стек при вызове
    // Пробуем передать через указатель, если возможно
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "whisper_init_from_file_with_params")]
    internal static extern IntPtr whisper_init_from_file_with_params(
        [MarshalAs(UnmanagedType.LPStr)] string path_model,
        WhisperContextParams params_);
    
    // Альтернативная версия, которая принимает указатель на структуру
    // Это может быть более безопасным способом передачи структуры
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "whisper_init_from_file_with_params", SetLastError = false)]
    internal static extern IntPtr whisper_init_from_file_with_params_ptr(
        [MarshalAs(UnmanagedType.LPStr)] string path_model,
        IntPtr params_ptr);

    // Освобождение контекста
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void whisper_free(IntPtr ctx);

    // Получение параметров по умолчанию для whisper_full
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern WhisperFullParams whisper_full_default_params(WhisperSamplingStrategy strategy);

    // Основная функция обработки аудио
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int whisper_full(
        IntPtr ctx,
        ref WhisperFullParams params_,
        [MarshalAs(UnmanagedType.LPArray)] float[] samples,
        int n_samples);

    // Получение количества сегментов
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int whisper_full_n_segments(IntPtr ctx);

    // Получение текста сегмента
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern IntPtr whisper_full_get_segment_text(IntPtr ctx, int i_segment);

    // Получение времени начала сегмента (в миллисекундах)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long whisper_full_get_segment_t0(IntPtr ctx, int i_segment);

    // Получение времени конца сегмента (в миллисекундах)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long whisper_full_get_segment_t1(IntPtr ctx, int i_segment);

    // Получение ID языка (если указан язык в params.language)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int whisper_full_lang_id(IntPtr ctx);

    // Получение ID языка по строке (например, "ru" -> id)
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int whisper_lang_id([MarshalAs(UnmanagedType.LPStr)] string lang);

    // Вспомогательная функция для безопасного копирования строки из IntPtr
    // ВАЖНО: whisper возвращает UTF-8 строки, а не ANSI
    internal static string? PtrToStringAnsi(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return null;
        
        // В .NET 5+ есть Marshal.PtrToStringUTF8 для правильного декодирования UTF-8
        // Используем его, если доступен
        try
        {
            // Marshal.PtrToStringUTF8 доступен в .NET 5+ и правильно декодирует UTF-8
            return Marshal.PtrToStringUTF8(ptr);
        }
        catch
        {
            // Fallback для старых версий .NET: ручное декодирование UTF-8
            // Находим длину строки (до нулевого байта) безопасным способом
            var bytes = new System.Collections.Generic.List<byte>();
            int offset = 0;
            while (true)
            {
                byte b = Marshal.ReadByte(ptr, offset);
                if (b == 0)
                    break;
                bytes.Add(b);
                offset++;
                
                // Защита от бесконечного цикла (максимум 1MB)
                if (offset > 1024 * 1024)
                    break;
            }
            
            if (bytes.Count == 0)
                return string.Empty;
            
            // Декодируем байты как UTF-8
            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}