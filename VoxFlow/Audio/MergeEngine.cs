using System;
using System.Collections.Generic;
using System.Linq;
using VoxFlow.Core;

namespace VoxFlow.Audio
{
    public class MergeEngine
    {
        public List<HistorySegment> MergeSegments(
            List<TextSegment> textSegments,
            List<SpeakerSegment> speakerSegments,
            Dictionary<string, int> labelToStableId,
            double windowStartAbsSec)
        {
            var historySegments = new List<HistorySegment>();

            foreach (var textSeg in textSegments)
            {
                double textStartAbs = windowStartAbsSec + textSeg.startSec;
                double textEndAbs = windowStartAbsSec + textSeg.endSec;

                // Знайти SpeakerSegment з максимальним overlap
                SpeakerSegment? bestSpeakerSeg = null;
                double maxOverlap = 0;

                foreach (var speakerSeg in speakerSegments)
                {
                    double speakerStartAbs = windowStartAbsSec + speakerSeg.startSec;
                    double speakerEndAbs = windowStartAbsSec + speakerSeg.endSec;

                    double overlapStart = Math.Max(textStartAbs, speakerStartAbs);
                    double overlapEnd = Math.Min(textEndAbs, speakerEndAbs);

                    if (overlapEnd > overlapStart)
                    {
                        double overlap = overlapEnd - overlapStart;
                        if (overlap > maxOverlap)
                        {
                            maxOverlap = overlap;
                            bestSpeakerSeg = speakerSeg;
                        }
                    }
                }

                // Визначити speakerId
                int speakerId;
                if (bestSpeakerSeg != null && labelToStableId.ContainsKey(bestSpeakerSeg.label))
                {
                    speakerId = labelToStableId[bestSpeakerSeg.label];
                }
                else
                {
                    // Немає overlap - призначити "unknown" (speakerId = 6)
                    speakerId = 6;
                }

                historySegments.Add(new HistorySegment
                {
                    ts = DateTime.Now,
                    speakerId = speakerId,
                    text = textSeg.text,
                    startSecAbs = textStartAbs,
                    endSecAbs = textEndAbs
                });
            }

            return historySegments;
        }
    }
}