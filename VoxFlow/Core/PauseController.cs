namespace VoxFlow.Core
{
    public enum PauseReason
    {
        None,
        Manual,
        AutoSilence
    }

    public class PauseController
    {
        private bool _globalPaused = false;
        private PauseReason _pauseReason = PauseReason.None;

        public bool GlobalPaused
        {
            get => _globalPaused;
            private set
            {
                if (_globalPaused != value)
                {
                    _globalPaused = value;
                    PauseStateChanged?.Invoke();
                }
            }
        }

        public PauseReason PauseReason
        {
            get => _pauseReason;
            private set
            {
                if (_pauseReason != value)
                {
                    _pauseReason = value;
                    PauseStateChanged?.Invoke();
                }
            }
        }

        public event Action? PauseStateChanged;

        public void SetManualPause(bool on)
        {
            if (on)
            {
                GlobalPaused = true;
                PauseReason = PauseReason.Manual;
            }
            else
            {
                // Manual pause вимкнений - якщо було Manual, очистити
                if (PauseReason == PauseReason.Manual)
                {
                    GlobalPaused = false;
                    PauseReason = PauseReason.None;
                }
            }
        }

        public void ApplyAutoSilencePause()
        {
            // AutoSilence pause тільки якщо не Manual
            if (PauseReason != PauseReason.Manual)
            {
                GlobalPaused = true;
                PauseReason = PauseReason.AutoSilence;
            }
        }

        public void ApplySpeechResume()
        {
            // Resume тільки якщо було AutoSilence
            if (PauseReason == PauseReason.AutoSilence)
            {
                GlobalPaused = false;
                PauseReason = PauseReason.None;
            }
        }
    }
}