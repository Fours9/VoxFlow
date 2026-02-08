using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Documents;
using System.Windows.Media;

namespace VoxFlow.Core
{
    public class HistoryController
    {
        private readonly List<HistorySegment> _allSegments = new();
        private readonly bool[] _enabledSpeakers = new bool[6] { true, true, true, true, true, true };
        private double _lastCommittedAbsSec = 0;
        private const double Epsilon = 0.05; // Для de-duplication

        // Кольорова палітра для speakerId 1-6
        private static readonly Brush[] SpeakerColors = new Brush[]
        {
            Brushes.Blue,      // speakerId 1
            Brushes.Red,       // speakerId 2
            Brushes.Purple,    // speakerId 3
            Brushes.Yellow,    // speakerId 4
            Brushes.Orange,    // speakerId 5
            Brushes.Green      // speakerId 6
        };

        public IReadOnlyList<HistorySegment> AllSegments => _allSegments.AsReadOnly();

        public bool GetSpeakerEnabled(int speakerId)
        {
            if (speakerId >= 1 && speakerId <= 6)
            {
                return _enabledSpeakers[speakerId - 1];
            }
            return true;
        }

        public void SetSpeakerEnabled(int speakerId, bool enabled)
        {
            if (speakerId >= 1 && speakerId <= 6)
            {
                _enabledSpeakers[speakerId - 1] = enabled;
            }
        }

        public event EventHandler? HistoryChanged;

        public void AppendSegment(HistorySegment segment)
        {
            // De-duplication: додавати тільки якщо endSecAbs > lastCommittedAbsSec + epsilon
            if (segment.endSecAbs > _lastCommittedAbsSec + Epsilon)
            {
                _allSegments.Add(segment);
                _lastCommittedAbsSec = Math.Max(_lastCommittedAbsSec, segment.endSecAbs);
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        // Метод для добавления нескольких сегментов сразу - вызывает событие только один раз
        public void AppendSegments(IEnumerable<HistorySegment> segments)
        {
            bool hasNewSegments = false;
            int addedCount = 0;
            int skippedCount = 0;
            
            foreach (var segment in segments)
            {
                // De-duplication: додавати тільки якщо endSecAbs > lastCommittedAbsSec + epsilon
                if (segment.endSecAbs > _lastCommittedAbsSec + Epsilon)
                {
                    _allSegments.Add(segment);
                    _lastCommittedAbsSec = Math.Max(_lastCommittedAbsSec, segment.endSecAbs);
                    hasNewSegments = true;
                    addedCount++;
                    Debug.WriteLine($"[HistoryController] Added segment: text='{segment.text}', endSecAbs={segment.endSecAbs:F3}, _lastCommittedAbsSec={_lastCommittedAbsSec:F3}");
                }
                else
                {
                    skippedCount++;
                    Debug.WriteLine($"[HistoryController] Skipped segment (duplicate): text='{segment.text}', endSecAbs={segment.endSecAbs:F3}, _lastCommittedAbsSec={_lastCommittedAbsSec:F3}, epsilon={Epsilon}");
                }
            }
            
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var threadName = System.Threading.Thread.CurrentThread.Name ?? "Unknown";
            Debug.WriteLine($"[HistoryController] AppendSegments: added={addedCount}, skipped={skippedCount}, hasNewSegments={hasNewSegments}, totalSegments={_allSegments.Count}, thread={threadId}, threadName={threadName}");
            Debug.WriteLine($"[HistoryController] AppendSegments: call stack: {Environment.StackTrace}");
            
            // Вызываем событие только один раз для всех новых сегментов
            if (hasNewSegments)
            {
                var subscribers = HistoryChanged?.GetInvocationList();
                Debug.WriteLine($"[HistoryController] Invoking HistoryChanged event, thread={threadId}, threadName={threadName}, subscribers={subscribers?.Length ?? 0}");
                if (subscribers != null)
                {
                    foreach (var subscriber in subscribers)
                    {
                        Debug.WriteLine($"[HistoryController] Subscriber: {subscriber.Method.DeclaringType?.FullName}.{subscriber.Method.Name}");
                    }
                }
                try
                {
                    Debug.WriteLine($"[HistoryController] About to invoke HistoryChanged event");
                    Debug.WriteLine($"[HistoryController] HistoryChanged is null: {HistoryChanged == null}");
                    if (HistoryChanged != null)
                    {
                        Debug.WriteLine($"[HistoryController] Invoking HistoryChanged event NOW");
                        // Вызываем событие синхронно, чтобы увидеть, что происходит
                        HistoryChanged.Invoke(this, EventArgs.Empty);
                        Debug.WriteLine($"[HistoryController] HistoryChanged event invoked successfully");
                    }
                    else
                    {
                        Debug.WriteLine($"[HistoryController] HistoryChanged event is null, skipping invocation");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HistoryController] EXCEPTION invoking HistoryChanged event: {ex.Message}\n{ex.StackTrace}");
                    Debug.WriteLine($"[HistoryController] Exception type: {ex.GetType().FullName}");
                    Debug.WriteLine($"[HistoryController] Inner exception: {ex.InnerException?.Message ?? "None"}");
                }
            }
            else
            {
                Debug.WriteLine("[HistoryController] No new segments, HistoryChanged event NOT invoked");
            }
        }

        public FlowDocument RenderHistory()
        {
            // Создаем новый документ - он будет создан в том потоке, где вызывается метод
            FlowDocument doc = new FlowDocument();
            Paragraph para = new Paragraph();

            // Фільтруємо сегменти за enabledSpeakers
            // НЕ сортируем - сегменты уже добавляются в хронологическом порядке
            var visibleSegments = _allSegments
                .Where(s => s.speakerId >= 1 && s.speakerId <= 6 && _enabledSpeakers[s.speakerId - 1]);

            foreach (var segment in visibleSegments)
            {
                Run run = new Run(segment.text + " ")
                {
                    Foreground = GetSpeakerColor(segment.speakerId),
                    Tag = segment.speakerId
                };
                para.Inlines.Add(run);
            }

            doc.Blocks.Add(para);
            
            // Установить свойства документа для правильной работы в UI
            doc.FlowDirection = System.Windows.FlowDirection.LeftToRight;
            
            return doc;
        }

        // Метод для получения новых сегментов для инкрементального обновления
        public IEnumerable<HistorySegment> GetNewSegments(int fromIndex)
        {
            if (fromIndex < 0)
            {
                Debug.WriteLine($"[HistoryController] GetNewSegments: fromIndex={fromIndex} is negative, returning empty");
                yield break;
            }
            
            if (fromIndex > _allSegments.Count)
            {
                Debug.WriteLine($"[HistoryController] GetNewSegments: fromIndex={fromIndex} > _allSegments.Count={_allSegments.Count}, returning empty");
                yield break;
            }

            int returnedCount = 0;
            for (int i = fromIndex; i < _allSegments.Count; i++)
            {
                var segment = _allSegments[i];
                // Фильтруем по enabledSpeakers
                if (segment.speakerId >= 1 && segment.speakerId <= 6 && _enabledSpeakers[segment.speakerId - 1])
                {
                    returnedCount++;
                    yield return segment;
                }
                else
                {
                    Debug.WriteLine($"[HistoryController] GetNewSegments: filtered out segment {i} (speakerId={segment.speakerId}, enabled={(segment.speakerId >= 1 && segment.speakerId <= 6 ? _enabledSpeakers[segment.speakerId - 1] : false)})");
                }
            }
            
            Debug.WriteLine($"[HistoryController] GetNewSegments: fromIndex={fromIndex}, totalSegments={_allSegments.Count}, returned={returnedCount}");
        }

        private Brush GetSpeakerColor(int speakerId)
        {
            if (speakerId >= 1 && speakerId <= 6)
            {
                return SpeakerColors[speakerId - 1];
            }
            return Brushes.Black; // Default color
        }
    }
}