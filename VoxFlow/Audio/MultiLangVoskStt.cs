using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Vosk;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    /// <summary>
    /// Обёртка над несколькими Vosk Model (RU/EN/UK) с выбором базового языка сессии.
    /// Передайте null или пустую строку для отключённых языков — будет использован первый включённый.
    /// </summary>
    public class MultiLangVoskStt : ISttEngine
    {
        private readonly string? _ruModelPath;
        private readonly string? _enModelPath;
        private readonly string? _ukModelPath;
        private readonly int _sampleRate;
        private readonly BaseLanguage _baseLanguage;

        private Model? _ruModel;
        private Model? _enModel;
        private Model? _ukModel;

        public MultiLangVoskStt(
            string? ruModelPath,
            string? enModelPath,
            string? ukModelPath,
            int sampleRate,
            BaseLanguage baseLanguage)
        {
            _ruModelPath = string.IsNullOrWhiteSpace(ruModelPath) ? null : ruModelPath;
            _enModelPath = string.IsNullOrWhiteSpace(enModelPath) ? null : enModelPath;
            _ukModelPath = string.IsNullOrWhiteSpace(ukModelPath) ? null : ukModelPath;
            _sampleRate = sampleRate;
            _baseLanguage = baseLanguage;
        }

        public async Task WarmUp()
        {
            // Прогреваем только базовую модель, остальные при необходимости подгрузим позже
            await EnsureBaseModelLoadedAsync();
        }

        public async Task<List<TextSegment>> TranscribeAsync(string wavPath)
        {
            var segments = new List<TextSegment>();

            if (!File.Exists(wavPath))
            {
                Debug.WriteLine($"[MultiLangVoskStt] ERROR: WAV file not found at {wavPath}");
                return segments;
            }

            var model = await EnsureBaseModelLoadedAsync();
            if (model == null)
            {
                Debug.WriteLine("[MultiLangVoskStt] ERROR: Base model is not loaded");
                return segments;
            }

            try
            {
                using var rec = new VoskRecognizer(model, _sampleRate);
                rec.SetWords(true);

                using var source = File.OpenRead(wavPath);
                var buffer = new byte[4096];
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    rec.AcceptWaveform(buffer, bytesRead);
                }

                var finalResult = rec.FinalResult();
                segments = ParseVoskResult(finalResult);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiLangVoskStt] ERROR: {ex.Message}\n{ex.StackTrace}");
            }

            return segments;
        }

        private async Task<Model?> EnsureBaseModelLoadedAsync()
        {
            // Определяем фактический язык: если базовый отключён, берём первый включённый (UK → RU → EN)
            var effectiveLang = GetEffectiveBaseLanguage();
            string? path = GetPathForLanguage(effectiveLang);
            if (path == null)
            {
                Debug.WriteLine("[MultiLangVoskStt] ERROR: All Vosk models are disabled");
                return null;
            }

            var existing = GetModel(effectiveLang);
            if (existing != null)
                return existing;

            try
            {
                if (!Directory.Exists(path))
                {
                    Debug.WriteLine($"[MultiLangVoskStt] ERROR: Vosk model directory not found: {path}");
                    return null;
                }

                await Task.Run(() =>
                {
                    Debug.WriteLine($"[MultiLangVoskStt] Loading model '{effectiveLang}' from '{path}'");
                    SetModel(effectiveLang, new Model(path));
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiLangVoskStt] ERROR loading model '{effectiveLang}': {ex.Message}\n{ex.StackTrace}");
                SetModel(effectiveLang, null);
            }

            return GetModel(effectiveLang);
        }

        private BaseLanguage GetEffectiveBaseLanguage()
        {
            // Сначала пробуем выбранный базовый язык
            if (GetPathForLanguage(_baseLanguage) != null)
                return _baseLanguage;
            // Иначе берём первый включённый: UK → RU → EN
            if (_ukModelPath != null) return BaseLanguage.Uk;
            if (_ruModelPath != null) return BaseLanguage.Ru;
            if (_enModelPath != null) return BaseLanguage.En;
            return _baseLanguage; // Все отключены — вернём любой, путь будет null
        }

        private string? GetPathForLanguage(BaseLanguage lang)
        {
            return lang switch
            {
                BaseLanguage.Ru => _ruModelPath,
                BaseLanguage.En => _enModelPath,
                BaseLanguage.Uk => _ukModelPath,
                _ => _ukModelPath
            };
        }

        private Model? GetModel(BaseLanguage lang)
        {
            switch (lang)
            {
                case BaseLanguage.Ru:
                    return _ruModel;
                case BaseLanguage.En:
                    return _enModel;
                case BaseLanguage.Uk:
                default:
                    return _ukModel;
            }
        }

        private void SetModel(BaseLanguage lang, Model? model)
        {
            switch (lang)
            {
                case BaseLanguage.Ru:
                    _ruModel = model;
                    break;
                case BaseLanguage.En:
                    _enModel = model;
                    break;
                case BaseLanguage.Uk:
                default:
                    _ukModel = model;
                    break;
            }
        }

        private static List<TextSegment> ParseVoskResult(string json)
        {
            var segments = new List<TextSegment>();

            if (string.IsNullOrWhiteSpace(json))
                return segments;

            try
            {
                var obj = JObject.Parse(json);
                var resultArray = obj["result"] as JArray;
                if (resultArray == null)
                {
                    // Если нет подробных слов, используем просто text
                    var text = obj["text"]?.Value<string>() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        segments.Add(new TextSegment
                        {
                            startSec = 0,
                            endSec = 0,
                            text = text
                        });
                    }

                    return segments;
                }

                // Группируем слова в один сегмент (можно усложнить позже)
                double start = resultArray.First?["start"]?.Value<double>() ?? 0;
                double end = resultArray.Last?["end"]?.Value<double>() ?? 0;
                var textCombined = string.Join(" ",
                    resultArray.Select(w => w["word"]?.Value<string>() ?? string.Empty).Where(t => !string.IsNullOrWhiteSpace(t)));

                if (!string.IsNullOrWhiteSpace(textCombined))
                {
                    segments.Add(new TextSegment
                    {
                        startSec = start,
                        endSec = end,
                        text = textCombined
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MultiLangVoskStt] ERROR parsing JSON: {ex.Message}\n{ex.StackTrace}");
            }

            return segments;
        }

        public void Dispose()
        {
            _ruModel?.Dispose();
            _enModel?.Dispose();
            _ukModel?.Dispose();

            _ruModel = null;
            _enModel = null;
            _ukModel = null;
        }
    }
}

