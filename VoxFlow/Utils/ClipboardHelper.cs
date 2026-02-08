using System.Windows;
using System.Windows.Threading;

namespace VoxFlow.Utils
{
    public static class ClipboardHelper
    {
        public static void SetTextSafe(string text)
        {
            // STA-safe clipboard операція
            if (Application.Current.Dispatcher.CheckAccess())
            {
                Clipboard.SetText(text);
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Clipboard.SetText(text);
                });
            }
        }
    }
}