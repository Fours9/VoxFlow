using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    public class DiarizerRunner
    {
        private readonly string _pythonPath;
        private readonly string _diarizeScriptPath;

        public DiarizerRunner(string? pythonPath = null, string? diarizeScriptPath = null)
        {
            // Шлях до Python: diarizer_service\.venv\Scripts\python.exe (відносно solution root)
            // Используем тот же метод поиска solution root, что и в Settings
            var solutionDir = Core.Settings.ResolveSolutionRelativePath("");
            var diarizerServicePath = Path.Combine(solutionDir, "diarizer_service");

            _pythonPath = pythonPath ?? Path.Combine(diarizerServicePath, ".venv", "Scripts", "python.exe");
            _diarizeScriptPath = diarizeScriptPath ?? Path.Combine(diarizerServicePath, "diarize.py");
        }

        public async Task<List<SpeakerSegment>> DiarizeAsync(string wavPath, int maxSpeakers = 6)
        {
            var segments = new List<SpeakerSegment>();

            try
            {
                if (!File.Exists(_pythonPath) || !File.Exists(_diarizeScriptPath))
                {
                    // Graceful degradation - якщо Python/venv не знайдено
                    return segments;
                }

                string jsonPath = Path.GetTempFileName() + ".json";

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = _pythonPath,
                    Arguments = $"\"{_diarizeScriptPath}\" --in \"{wavPath}\" --out \"{jsonPath}\" --max_speakers {maxSpeakers}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processStartInfo);
                if (process == null) return segments;

                await process.WaitForExitAsync();

                // Парсинг JSON виводу
                if (File.Exists(jsonPath))
                {
                    segments = ParseDiarizationJson(jsonPath);
                }
            }
            catch
            {
                // Обробка помилок
            }

            return segments;
        }

        private List<SpeakerSegment> ParseDiarizationJson(string jsonPath)
        {
            var segments = new List<SpeakerSegment>();

            try
            {
                string json = File.ReadAllText(jsonPath);
                var jsonObj = JObject.Parse(json);

                // Формат: { "speakerSegments": [{"start": 0.0, "end": 1.5, "label": "0"}, ...] }
                var segmentsArray = jsonObj["speakerSegments"] as JArray;

                if (segmentsArray != null)
                {
                    foreach (var segmentToken in segmentsArray)
                    {
                        var segment = segmentToken as JObject;
                        if (segment == null) continue;

                        var speakerSegment = new SpeakerSegment
                        {
                            startSec = segment["start"]?.Value<double>() ?? 0.0,
                            endSec = segment["end"]?.Value<double>() ?? 0.0,
                            label = segment["label"]?.Value<string>() ?? "0"
                        };

                        segments.Add(speakerSegment);
                    }
                }
            }
            catch
            {
                // Обробка помилок парсингу
            }

            return segments;
        }
    }
}