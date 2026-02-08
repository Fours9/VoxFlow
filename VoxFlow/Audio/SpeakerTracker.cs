using System;
using System.Collections.Generic;
using System.Linq;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    public class SpeakerTracker
    {
        private const int WindowSizeSec = 6;
        private const int StepSec = 2;
        private const int MaxSpeakers = 6;

        private List<SpeakerSegment> _lastWindowStableSegments = new();
        private double _lastWindowStartAbsSec = 0;

        public Dictionary<string, int> MapLabelsToStableIds(List<SpeakerSegment> currentSegments, double windowStartAbsSec)
        {
            var labelToStableId = new Dictionary<string, int>();
            var usedStableIds = new HashSet<int>();

            if (_lastWindowStableSegments.Count == 0 || windowStartAbsSec - _lastWindowStartAbsSec >= WindowSizeSec)
            {
                // Перше вікно або немає overlap - призначити нові IDs
                int nextId = 1;
                foreach (var segment in currentSegments)
                {
                    if (!labelToStableId.ContainsKey(segment.label))
                    {
                        if (nextId <= MaxSpeakers)
                        {
                            labelToStableId[segment.label] = nextId++;
                        }
                        else
                        {
                            labelToStableId[segment.label] = MaxSpeakers; // "Other" bucket
                        }
                    }
                }

                _lastWindowStableSegments = currentSegments.Select(s => new SpeakerSegment
                {
                    startSec = s.startSec,
                    endSec = s.endSec,
                    label = s.label
                }).ToList();
                _lastWindowStartAbsSec = windowStartAbsSec;
                return labelToStableId;
            }

            // Overlap region між вікнами
            double overlapStartAbs = windowStartAbsSec;
            double overlapEndAbs = _lastWindowStartAbsSec + WindowSizeSec;

            // Знайти segments в overlap region
            var currentOverlap = currentSegments.Where(s =>
                (windowStartAbsSec + s.startSec) < overlapEndAbs &&
                (windowStartAbsSec + s.endSec) > overlapStartAbs
            ).ToList();

            var previousOverlap = _lastWindowStableSegments.Where(s =>
                (_lastWindowStartAbsSec + s.startSec) < overlapEndAbs &&
                (_lastWindowStartAbsSec + s.endSec) > overlapStartAbs
            ).ToList();

            // Greedy matching: для кожного поточного label знайти найкращий match за overlap duration
            var currentLabels = currentSegments.Select(s => s.label).Distinct().ToList();
            
            foreach (var label in currentLabels)
            {
                var labelSegments = currentOverlap.Where(s => s.label == label).ToList();
                int bestStableId = 0;
                double bestOverlap = 0;

                for (int stableId = 1; stableId <= MaxSpeakers; stableId++)
                {
                    // Знайти segments з цією stableId в попередньому вікні
                    // (для MVP припускаємо, що label відповідає stableId в попередньому вікні)
                    double overlap = CalculateOverlapDuration(labelSegments, previousOverlap, 
                        windowStartAbsSec, _lastWindowStartAbsSec);

                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        bestStableId = stableId;
                    }
                }

                if (bestStableId > 0 && !usedStableIds.Contains(bestStableId))
                {
                    labelToStableId[label] = bestStableId;
                    usedStableIds.Add(bestStableId);
                }
                else
                {
                    // Призначити перший вільний ID
                    int freeId = Enumerable.Range(1, MaxSpeakers).FirstOrDefault(id => !usedStableIds.Contains(id));
                    if (freeId == 0) freeId = MaxSpeakers;
                    labelToStableId[label] = freeId;
                    usedStableIds.Add(freeId);
                }
            }

            _lastWindowStableSegments = currentSegments.Select(s => new SpeakerSegment
            {
                startSec = s.startSec,
                endSec = s.endSec,
                label = labelToStableId.ContainsKey(s.label) ? labelToStableId[s.label].ToString() : s.label
            }).ToList();
            _lastWindowStartAbsSec = windowStartAbsSec;

            return labelToStableId;
        }

        private double CalculateOverlapDuration(List<SpeakerSegment> current, List<SpeakerSegment> previous,
            double currentStartAbs, double previousStartAbs)
        {
            double totalOverlap = 0;

            foreach (var currSeg in current)
            {
                double currStart = currentStartAbs + currSeg.startSec;
                double currEnd = currentStartAbs + currSeg.endSec;

                foreach (var prevSeg in previous)
                {
                    double prevStart = previousStartAbs + prevSeg.startSec;
                    double prevEnd = previousStartAbs + prevSeg.endSec;

                    double overlapStart = Math.Max(currStart, prevStart);
                    double overlapEnd = Math.Min(currEnd, prevEnd);

                    if (overlapEnd > overlapStart)
                    {
                        totalOverlap += overlapEnd - overlapStart;
                    }
                }
            }

            return totalOverlap;
        }
    }
}