using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxFlow.Core
{
    /// <summary>
    /// Простейший постпроцессор текста: исправление часто искажаемых тех-терминов и английских слов.
    /// Делает минимальные безопасные замены по словарю.
    /// </summary>
    public static class TextPostProcessor
    {
        private static readonly Dictionary<string, string> _replacements =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Примеры тех-терминов / английских слов
                { "вай-фай", "Wi‑Fi" },
                { "вайфай", "Wi‑Fi" },
                { "ютуб", "YouTube" },
                { "ютюб", "YouTube" },
                { "гугл", "Google" },
                { "айфон", "iPhone" },
                { "виндовс", "Windows" },
                { "дота", "Dota" },
                { "ютьюб", "YouTube" },
            };

        /// <summary>
        /// Применить словарь исправлений к одному фрагменту текста.
        /// </summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                var w = words[i].Trim();
                if (w.Length == 0)
                    continue;

                // Без сложной токенизации: сравниваем слово целиком в нижнем регистре
                if (_replacements.TryGetValue(w, out var replacement))
                {
                    words[i] = replacement;
                }
            }

            return string.Join(" ", words);
        }

        /// <summary>
        /// Нормализует все сегменты (используется после STT).
        /// </summary>
        public static IReadOnlyList<TextSegment> NormalizeSegments(IEnumerable<TextSegment> segments)
        {
            var result = new List<TextSegment>();
            foreach (var seg in segments)
            {
                var normalized = Normalize(seg.text);
                result.Add(new TextSegment
                {
                    startSec = seg.startSec,
                    endSec = seg.endSec,
                    text = normalized
                });
            }

            return result;
        }
    }
}

