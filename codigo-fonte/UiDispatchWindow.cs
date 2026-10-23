using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace PastaGBIcons
{
    // One private, hidden HWND. Background workers only PostMessage a
    // coalesced wake; all drawing, timers and shutdown stay on the UI thread.
    internal sealed class UiDispatchWindow : NativeWindow, IDisposable
    {
        private const int WakeMessage = 0x8000 + 271;
        private readonly Action callback;
        private readonly Action<bool> environmentChanged;
        private readonly object gate = new object();
        private int pending;
        private bool disposed;

        internal UiDispatchWindow(Action callback, Action<bool> environmentChanged)
        {
            this.callback = callback;
            this.environmentChanged = environmentChanged;
            CreateParams parameters = new CreateParams();
            parameters.Caption = "PastaGB.Dispatch";
            // Hidden top-level window also receives display/theme/settings
            // broadcasts; it is never shown or activated.
            parameters.ExStyle = 0x08000000 | 0x00000080;
            CreateHandle(parameters);
        }

        internal void Signal()
        {
            lock (gate)
            {
                if (disposed || pending != 0) return;
                pending = 1;
                if (!PostMessage(Handle, WakeMessage, IntPtr.Zero, IntPtr.Zero)) pending = 0;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WakeMessage)
            {
                lock (gate) { pending = 0; if (disposed) return; }
                callback();
                return;
            }
            // WM_SETTINGCHANGE / DISPLAYCHANGE / THEMECHANGED / POWERBROADCAST.
            if (!disposed && environmentChanged != null)
            {
                if (message.Msg == 0x001A || message.Msg == 0x007E || message.Msg == 0x031A) environmentChanged(false);
                // Battery/AC status broadcasts do not invalidate a Shell index.
                // Resume from suspend does require a new media/content check.
                if (message.Msg == 0x0218 && (message.WParam.ToInt64() == 0x12 ||
                    message.WParam.ToInt64() == 0x7 || message.WParam.ToInt64() == 0x6)) environmentChanged(true);
            }
            base.WndProc(ref message);
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; pending = 0; }
            DestroyHandle();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    }
}
