using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VoxFlow.Core;
using Vosk;

namespace VoxFlow.Audio
{
    public class VoskRunner : ISttEngine
    {
        private readonly string _modelPath;
        private readonly int _sampleRate;
        private Model? _model;

        public VoskRunner(string modelPath, int sampleRate)
        {
            _modelPath = modelPath;
            _sampleRate = sampleRate;
        }

        public async Task WarmUp()
        {
            await EnsureModelLoadedAsync();
        }

        public async Task<List<TextSegment>> TranscribeAsync(string wavPath)
        {
            var segments = new List<TextSegment>();

            if (!File.Exists(wavPath))
            {
                Debug.WriteLine($"[VoskRunner] ERROR: WAV file not found at {wavPath}");
                return segments;
            }

            await EnsureModelLoadedAsync();
            if (_model == null)
            {
                Debug.WriteLine("[VoskRunner] ERROR: Model is not loaded");
                return segments;
            }

            try
            {
                using var rec = new VoskRecognizer(_model, _sampleRate);
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
                Debug.WriteLine($"[VoskRunner] ERROR: {ex.Message}\n{ex.StackTrace}");
            }

            return segments;
        }

        private async Task EnsureModelLoadedAsync()
        {
            if (_model != null)
                return;

            try
            {
                // Путь к модели - это папка, поэтому проверяем директорию
                if (!Directory.Exists(_modelPath))
                {
                    Debug.WriteLine($"[VoskRunner] ERROR: Vosk model directory not found: {_modelPath}");
                    return;
                }

                await Task.Run(() =>
                {
                    Debug.WriteLine($"[VoskRunner] Loading model from '{_modelPath}'");
                    _model = new Model(_modelPath);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoskRunner] ERROR loading model: {ex.Message}\n{ex.StackTrace}");
                _model = null;
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
                Debug.WriteLine($"[VoskRunner] ERROR parsing JSON: {ex.Message}\n{ex.StackTrace}");
            }

            return segments;
        }

        public void Dispose()
        {
            _model?.Dispose();
            _model = null;
        }
    }
}

