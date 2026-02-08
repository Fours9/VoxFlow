using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using VoxFlow.Core;

namespace VoxFlow.UI
{
    public class Hotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private IntPtr _windowHandle;
        private int _hotkeyIdCounter = 9000;

        private int _toggleLiveDisabledId;
        private int _clearDraftId;
        private int _togglePauseId;
        private int _pasteAndResumeId;

        public event Action? ToggleLiveDisabled;
        public event Action? ClearDraft;
        public event Action? TogglePause;
        public event Action? PasteAndResume;

        public bool RegisterHotkeys(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
            var settings = Settings.Current;

            _toggleLiveDisabledId = RegisterHotkey(settings.ToggleLiveDisabled, settings.ToggleLiveDisabledModifiers);
            _clearDraftId = RegisterHotkey(settings.ClearDraft, settings.ClearDraftModifiers);
            _togglePauseId = RegisterHotkey(settings.TogglePause, settings.TogglePauseModifiers);
            _pasteAndResumeId = RegisterHotkey(settings.PasteAndResume, settings.PasteAndResumeModifiers);

            return true;
        }

        private int RegisterHotkey(Key key, ModifierKeys modifiers)
        {
            int id = _hotkeyIdCounter++;
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            uint mod = ConvertModifiers(modifiers);

            RegisterHotKey(_windowHandle, id, mod, vk);
            return id;
        }

        private uint ConvertModifiers(ModifierKeys modifiers)
        {
            uint result = 0;
            if (modifiers.HasFlag(ModifierKeys.Alt)) result |= MOD_ALT;
            if (modifiers.HasFlag(ModifierKeys.Control)) result |= MOD_CONTROL;
            if (modifiers.HasFlag(ModifierKeys.Shift)) result |= MOD_SHIFT;
            if (modifiers.HasFlag(ModifierKeys.Windows)) result |= MOD_WIN;
            return result;
        }

        public bool ProcessHotkey(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                handled = true;

                if (id == _toggleLiveDisabledId)
                {
                    ToggleLiveDisabled?.Invoke();
                }
                else if (id == _clearDraftId)
                {
                    ClearDraft?.Invoke();
                }
                else if (id == _togglePauseId)
                {
                    TogglePause?.Invoke();
                }
                else if (id == _pasteAndResumeId)
                {
                    PasteAndResume?.Invoke();
                }

                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_windowHandle != IntPtr.Zero)
            {
                UnregisterHotKey(_windowHandle, _toggleLiveDisabledId);
                UnregisterHotKey(_windowHandle, _clearDraftId);
                UnregisterHotKey(_windowHandle, _togglePauseId);
                UnregisterHotKey(_windowHandle, _pasteAndResumeId);
            }
        }
    }
}