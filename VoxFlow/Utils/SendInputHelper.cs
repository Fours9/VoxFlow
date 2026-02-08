using System;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace VoxFlow.Utils
{
    public static class SendInputHelper
    {
        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        public static void Paste()
        {
            // Натиснути Ctrl+V
            INPUT[] inputs = new INPUT[4];

            // Ctrl down
            inputs[0] = CreateKeyInput(0x11, false); // VK_CONTROL
            // V down
            inputs[1] = CreateKeyInput(0x56, false); // V key
            // V up
            inputs[2] = CreateKeyInput(0x56, true);
            // Ctrl up
            inputs[3] = CreateKeyInput(0x11, true);

            SendInput(4, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT CreateKeyInput(ushort vk, bool keyUp)
        {
            INPUT input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            return input;
        }
    }
}