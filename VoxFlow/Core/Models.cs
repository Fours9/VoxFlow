namespace VoxFlow.Core
{
    public class TextSegment
    {
        public double startSec { get; set; }
        public double endSec { get; set; }
        public string text { get; set; } = string.Empty;
    }

    public class SpeakerSegment
    {
        public double startSec { get; set; }
        public double endSec { get; set; }
        public string label { get; set; } = string.Empty;
    }

    public class HistorySegment
    {
        public DateTime ts { get; set; }
        public int speakerId { get; set; }
        public string text { get; set; } = string.Empty;
        public double startSecAbs { get; set; }
        public double endSecAbs { get; set; }
    }

    public enum ActiveEditor
    {
        Draft,
        Live
    }
}