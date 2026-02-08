namespace VoxFlow.Core
{
    public class EditorState
    {
        private string _sharedEditorText = string.Empty;
        private ActiveEditor _activeEditor = ActiveEditor.Draft;
        private bool _liveDisabled = true;
        private string _liveDetachedText = string.Empty;

        public string SharedEditorText
        {
            get => _sharedEditorText;
            set
            {
                if (_sharedEditorText != value)
                {
                    _sharedEditorText = value;
                    SharedTextChanged?.Invoke();
                }
            }
        }

        public ActiveEditor ActiveEditor
        {
            get => _activeEditor;
            set
            {
                if (_activeEditor != value)
                {
                    _activeEditor = value;
                    ActiveEditorChanged?.Invoke();
                }
            }
        }

        public bool LiveDisabled
        {
            get => _liveDisabled;
            set
            {
                if (_liveDisabled != value)
                {
                    _liveDisabled = value;
                    if (value)
                    {
                        // При відключенні Live очищаємо LiveDetachedText
                        LiveDetachedText = string.Empty;
                    }
                    LiveDisabledChanged?.Invoke();
                }
            }
        }

        public string LiveDetachedText
        {
            get => _liveDetachedText;
            set
            {
                if (_liveDetachedText != value)
                {
                    _liveDetachedText = value;
                }
            }
        }

        public event Action? SharedTextChanged;
        public event Action? ActiveEditorChanged;
        public event Action? LiveDisabledChanged;
    }
}