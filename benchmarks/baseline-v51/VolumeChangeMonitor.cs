using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PastaGBIcons
{
    // Hidden top-level receiver: HWND_MESSAGE does not receive broadcasts.
    // No disk access or device removal operation takes place in this callback.
    internal sealed class VolumeChangeMonitor : NativeWindow, IDisposable
    {
        private bool disposed;
        private readonly VolumeRevisions revisions;
        internal event Action<uint> Changed;
        internal VolumeChangeMonitor(VolumeRevisions versions)
        {
            revisions = versions;
            CreateHandle(new CreateParams { Caption = "Pasta GB — volumes",
                ExStyle = 0x08000080, Width = 0, Height = 0 });
        }
        protected override void WndProc(ref Message message)
        {
            const int WM_DEVICECHANGE = 0x0219;
            if (!disposed && message.Msg == WM_DEVICECHANGE && message.LParam != IntPtr.Zero)
            {
                int reason = message.WParam.ToInt32();
                if (reason == 0x8000 || reason == 0x8003 || reason == 0x8004)
                {
                    try
                    {
                        int size = Marshal.ReadInt32(message.LParam, 0);
                        int kind = Marshal.ReadInt32(message.LParam, 4);
                        if (size >= 18 && size <= 4096 && kind == 2) // DBT_DEVTYP_VOLUME
                        {
                            uint mask = unchecked((uint)Marshal.ReadInt32(message.LParam, 12)) & 0x03FFFFFFu;
                            if (mask != 0)
                            {
                                revisions.Changed(mask);
                                Action<uint> handler = Changed;
                                if (handler != null) handler(mask);
                            }
                        }
                    }
                    catch { } // no managed exceptions across the native callback
                }
            }
            base.WndProc(ref message);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Changed = null;
            DestroyHandle();
        }
    }
}
