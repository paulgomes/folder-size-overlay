using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PastaGBIcons
{
    /// <summary>
    /// One read-only, click-through size label attached to a Shell content viewport.
    /// Construct, update, hide and dispose on the same message-pumping UI thread.
    /// screenBounds must be PHYSICAL screen pixels, as returned by UI Automation.
    /// No file system, Shell COM, UI Automation, or synchronous worker calls occur here.
    ///
    /// The EXE manifest must declare supportedOS
    /// {8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a} (Windows 10/11).
    /// Windows 10 version 1607 or later is required for thread DPI contexts.
    /// </summary>
    public sealed class LayeredLabel : IDisposable
    {
        private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
        private LabelWindow window;
        private IntPtr parent;
        private IntPtr explorerRoot;
        private uint explorerProcessId;
        private Rectangle lastClientBounds;
        private Rectangle lastClipBounds;
        private string lastText;
        private uint lastColor;
        private int lastDpi;
        private Size lastPhysicalSize;
        private bool hasBounds;
        private bool hasClipBounds;
        private bool hasWindowRegion;
        private bool contentDirty = true;
        private bool shown;
        private bool disposed;
        private bool compactBadge;
        private bool alignRight;
        private LabelTheme theme;
        private float textScale = 1f;
        private bool lastHighContrast;
        private int lastSystemText, lastSystemBackground;

        public LabelTheme Theme
        {
            get { return theme; }
            set
            {
                CheckThread();
                if (disposed) throw new ObjectDisposedException("LayeredLabel");
                if (value != LabelTheme.Automatic && value != LabelTheme.Light && value != LabelTheme.Dark)
                    value = LabelTheme.Automatic;
                if (theme == value) return;
                theme = value;
                contentDirty = true;
            }
        }

        public float TextScale
        {
            get { return textScale; }
            set
            {
                CheckThread();
                if (disposed) throw new ObjectDisposedException("LayeredLabel");
                value = LabelLayout.NormalizeTextScale(value);
                if (textScale == value) return;
                textScale = value;
                contentDirty = true;
            }
        }

        public bool IsAlive
        {
            get { return !disposed && window != null && window.Alive; }
        }

        // A live HWND may be intentionally hidden during navigation/renaming.
        public bool IsShown
        {
            get
            {
                return shown && IsAlive && Native.IsWindowVisible(window.Handle) &&
                    (explorerRoot == IntPtr.Zero || !Native.IsIconic(explorerRoot));
            }
        }

        /// <summary>Use only for a layout-confirmed badge above an icon's name.</summary>
        public bool CompactBadge
        {
            get { return compactBadge; }
            set
            {
                CheckThread();
                if (disposed) throw new ObjectDisposedException("LayeredLabel");
                if (compactBadge == value) return;
                compactBadge = value;
                contentDirty = true;
            }
        }

        /// <summary>Right-align a size in a proven empty System.Size cell.</summary>
        public bool AlignRight
        {
            get { return alignRight; }
            set
            {
                CheckThread();
                if (disposed) throw new ObjectDisposedException("LayeredLabel");
                if (alignRight == value) return;
                alignRight = value;
                contentDirty = true;
            }
        }

        public void Update(IntPtr parentHandle, Rectangle screenBounds, string text,
                           uint colorRef, int dpi)
        {
            // Preserve the desktop-only entry point and its existing behavior.
            UpdateCore(parentHandle, screenBounds, text, colorRef, dpi, null, IntPtr.Zero);
        }

        /// <summary>
        /// Updates a label within a real File Explorer content viewport.
        /// Both rectangles are PHYSICAL screen pixels. expectedRootHandle must be
        /// the CabinetWClass/ExploreWClass window that owns parentHandle.
        /// The coordinator must hide labels when its view/navigation epoch changes;
        /// the same content HWND can be reused for a different folder or tab.
        /// </summary>
        public void Update(IntPtr parentHandle, Rectangle screenBounds, string text,
                           uint colorRef, int dpi, Rectangle screenClipBounds,
                           IntPtr expectedRootHandle)
        {
            if (expectedRootHandle == IntPtr.Zero)
                throw new ArgumentException("Informe a janela do Explorador que contém a pasta.",
                                            "expectedRootHandle");
            UpdateCore(parentHandle, screenBounds, text, colorRef, dpi,
                       screenClipBounds, expectedRootHandle);
        }

        private void UpdateCore(IntPtr parentHandle, Rectangle screenBounds, string text,
                                uint colorRef, int dpi, Rectangle? screenClipBounds,
                                IntPtr expectedRootHandle)
        {
            CheckThread();
            if (disposed) throw new ObjectDisposedException("LayeredLabel");
            bool isExplorer = expectedRootHandle != IntPtr.Zero;
            if (parentHandle == IntPtr.Zero || !Native.IsWindow(parentHandle))
            {
                DestroyOwnedWindow();
                return;
            }
            if (isExplorer && (!Native.IsWindow(expectedRootHandle) ||
                !Native.IsWindowVisible(expectedRootHandle) || Native.IsIconic(expectedRootHandle)))
            {
                Hide();
                return;
            }
            if (screenBounds.Width <= 0 || screenBounds.Height <= 0 ||
                String.IsNullOrWhiteSpace(text) || !Native.IsWindowVisible(parentHandle))
            {
                Hide();
                return;
            }
            if (screenClipBounds.HasValue && (screenClipBounds.Value.Width <= 0 ||
                                              screenClipBounds.Value.Height <= 0))
            {
                Hide();
                return;
            }
            // Refuse accidental full-screen bitmaps and unbounded caller content.
            if (screenBounds.Width > 4096 || screenBounds.Height > 512 || text.Length > 128)
            {
                Hide();
                throw new ArgumentOutOfRangeException("screenBounds",
                    "A etiqueta deve ocupar apenas a faixa pequena abaixo do nome da pasta.");
            }
            dpi = Math.Max(72, Math.Min(768, dpi));

            try
            {
                // Cross-process child HWNDs must match their parent's DPI context.
                // No temporary top-level window or SetParent operation is used.
                using (new ParentDpiScope(parentHandle))
                {
                    Rectangle client = PhysicalScreenToClient(parentHandle, screenBounds);
                    if (client.Width <= 0 || client.Height <= 0)
                    {
                        Hide();
                        return;
                    }
                    Rectangle clip = new Rectangle(Point.Empty, client.Size);
                    if (screenClipBounds.HasValue)
                    {
                        Rectangle clipClient = PhysicalScreenToClient(parentHandle,
                                                                      screenClipBounds.Value);
                        Native.Rect nativeClient;
                        if (!Native.GetClientRect(parentHandle, out nativeClient))
                            throw Native.Failure("Ler os limites da área de arquivos");
                        Rectangle actualClient = Rectangle.FromLTRB(nativeClient.Left,
                            nativeClient.Top, nativeClient.Right, nativeClient.Bottom);
                        Rectangle visible = Rectangle.Intersect(client,
                            Rectangle.Intersect(clipClient, actualClient));
                        if (visible.Width <= 0 || visible.Height <= 0)
                        {
                            Hide();
                            return;
                        }
                        // Keep the complete label's alignment. A native region
                        // clips it instead of shrinking and recentering its text.
                        clip = new Rectangle(visible.X - client.X, visible.Y - client.Y,
                                             visible.Width, visible.Height);
                    }
                    if (parent != parentHandle || explorerRoot != expectedRootHandle || !IsAlive)
                    {
                        DestroyOwnedWindow();
                        if (isExplorer)
                            explorerProcessId = ValidateExplorerHost(parentHandle, expectedRootHandle);
                        else
                            ValidateDesktopHost(parentHandle);
                        parent = parentHandle;
                        explorerRoot = expectedRootHandle;
                        window = new LabelWindow(this);
                        window.Create(parentHandle, client);
                        hasBounds = false;
                        contentDirty = true;
                    }
                    else if (isExplorer)
                    {
                        // A stale or recycled host must not migrate to another window.
                        uint currentProcess;
                        Native.GetWindowThreadProcessId(parentHandle, out currentProcess);
                        if (Native.GetAncestor(parentHandle, Native.GA_ROOT) != expectedRootHandle ||
                            currentProcess != explorerProcessId)
                            throw new InvalidOperationException(
                                "A janela de conteúdo do Explorador mudou; atualize a visualização.");
                    }

                    bool sizeChanged = !hasBounds || lastClientBounds.Size != client.Size;
                    bool clipChanged = screenClipBounds.HasValue &&
                        (!hasClipBounds || lastClipBounds != clip || sizeChanged);
                    // Layered surfaces retain their pixels while they move.
                    // Do not create a hidden frame for ordinary position jitter.
                    // When a partial clip changes, restrict to the common region
                    // during the move; neither old nor new placement can escape
                    // its viewport. Only a disjoint transition needs full hiding.
                    if (isExplorer && shown && hasBounds && hasClipBounds && clipChanged &&
                        (hasWindowRegion || clip != new Rectangle(Point.Empty, client.Size)))
                    {
                        Rectangle common = Rectangle.Intersect(lastClipBounds, clip);
                        if (common.Width <= 0 || common.Height <= 0) Hide();
                        else SetClipRegion(common);
                    }
                    if (!hasBounds || lastClientBounds != client)
                    {
                        if (!Native.SetWindowPos(window.Handle, IntPtr.Zero,
                                client.X, client.Y, client.Width, client.Height,
                                Native.SWP_NOACTIVATE | Native.SWP_NOZORDER |
                                Native.SWP_NOSENDCHANGING))
                            throw Native.Failure("Posicionar a etiqueta da pasta");
                        lastClientBounds = client;
                        hasBounds = true;
                    }

                    bool highContrast = SystemInformation.HighContrast;
                    Color systemText = SystemColors.WindowText;
                    Color systemBackground = SystemColors.Window;
                    if (contentDirty || sizeChanged || lastText != text ||
                        lastColor != colorRef || lastDpi != dpi ||
                        lastPhysicalSize != screenBounds.Size || lastHighContrast != highContrast ||
                        (highContrast && (lastSystemText != systemText.ToArgb() ||
                                          lastSystemBackground != systemBackground.ToArgb())))
                    {
                        float logicalScale = (float)client.Height / screenBounds.Height;
                        DrawBitmap(window.Handle, client.Size, text, colorRef, dpi,
                                   logicalScale, compactBadge, alignRight, theme, textScale,
                                   highContrast, systemText, systemBackground);
                        // Keep diagnostic/native accessibility text synchronized
                        // with the drawn value. This is our own child HWND only.
                        if (contentDirty || lastText != text) Native.SetWindowText(window.Handle, "Pasta GB: " + text);
                        lastText = text;
                        lastColor = colorRef;
                        lastDpi = dpi;
                        lastPhysicalSize = screenBounds.Size;
                        lastHighContrast = highContrast;
                        lastSystemText = systemText.ToArgb();
                        lastSystemBackground = systemBackground.ToArgb();
                        contentDirty = false;
                    }
                    if (clipChanged) ApplyClip(clip, client.Size);

                    // The Shell can hide a child HWND without destroying it.
                    // Reconcile native visibility instead of trusting only our
                    // last requested state; the host was checked above.
                    if (!shown || !Native.IsWindowVisible(window.Handle))
                    {
                        if (!Native.SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, 0, 0,
                                Native.SWP_NOACTIVATE | Native.SWP_NOZORDER |
                                Native.SWP_NOMOVE | Native.SWP_NOSIZE |
                                Native.SWP_SHOWWINDOW))
                            throw Native.Failure("Exibir a etiqueta da pasta");
                        shown = true;
                    }
                }
            }
            catch
            {
                // Never leave stale text visible if a native operation failed.
                Hide();
                throw;
            }
        }

        public void Hide()
        {
            CheckThread();
            bool mayNeedHide = shown;
            // Native calls may reenter message processing. Publish the hidden
            // state first and avoid repeating SW_HIDE for an already hidden HWND.
            shown = false;
            if (IsAlive && (mayNeedHide || Native.IsWindowVisible(window.Handle)))
                Native.ShowWindow(window.Handle, Native.SW_HIDE);
        }

        public void Dispose()
        {
            CheckThread();
            if (disposed) return;
            DestroyOwnedWindow();
            disposed = true;
            GC.SuppressFinalize(this);
        }

        private void CheckThread()
        {
            if (ownerThread != Thread.CurrentThread.ManagedThreadId)
                throw new InvalidOperationException(
                    "Atualize as etiquetas na mesma thread de interface que as criou.");
        }

        private void DestroyOwnedWindow()
        {
            LabelWindow old = window;
            window = null;
            parent = IntPtr.Zero;
            explorerRoot = IntPtr.Zero;
            explorerProcessId = 0;
            shown = false;
            hasBounds = false;
            hasClipBounds = false;
            hasWindowRegion = false;
            contentDirty = true;
            if (old != null) old.Destroy();
        }

        private void OnWindowDestroyed()
        {
            shown = false;
            hasBounds = false;
            hasClipBounds = false;
            hasWindowRegion = false;
            contentDirty = true;
        }

        private void ApplyClip(Rectangle clip, Size fullSize)
        {
            Rectangle full = new Rectangle(Point.Empty, fullSize);
            if (clip == full)
            {
                if (hasWindowRegion && Native.SetWindowRgn(window.Handle, IntPtr.Zero, true) == 0)
                    throw Native.Failure("Remover o recorte anterior da etiqueta");
                hasWindowRegion = false;
            }
            else
            {
                SetClipRegion(clip);
            }
            lastClipBounds = clip;
            hasClipBounds = true;
        }

        private void SetClipRegion(Rectangle clip)
        {
            IntPtr region = Native.CreateRectRgn(clip.Left, clip.Top, clip.Right, clip.Bottom);
            if (region == IntPtr.Zero) throw Native.Failure("Criar o recorte da etiqueta");
            if (Native.SetWindowRgn(window.Handle, region, true) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Native.DeleteObject(region);
                throw new Win32Exception(error, "Não foi possível recortar a etiqueta na área de arquivos.");
            }
            // On success Windows owns HRGN and releases it when replaced/destroyed.
            hasWindowRegion = true;
            lastClipBounds = clip;
            hasClipBounds = true;
        }

        private static uint ValidateExplorerHost(IntPtr handle, IntPtr expectedRoot)
        {
            string hostClass = Native.ClassName(handle);
            if (hostClass != "SysListView32" && hostClass != "DirectUIHWND" &&
                hostClass != "SHELLDLL_DefView")
                throw new InvalidOperationException("A área de arquivos do Explorador não é compatível.");

            IntPtr actualRoot = Native.GetAncestor(handle, Native.GA_ROOT);
            string rootClass = Native.ClassName(actualRoot);
            if (actualRoot != expectedRoot ||
                (rootClass != "CabinetWClass" && rootClass != "ExploreWClass"))
                throw new InvalidOperationException("A área de arquivos não pertence à janela esperada do Explorador.");

            bool hasShellView = hostClass == "SHELLDLL_DefView";
            IntPtr ancestor = Native.GetParent(handle);
            for (int depth = 0; ancestor != IntPtr.Zero && depth < 16; depth++)
            {
                if (Native.ClassName(ancestor) == "SHELLDLL_DefView") hasShellView = true;
                if (ancestor == actualRoot) break;
                ancestor = Native.GetParent(ancestor);
            }
            uint hostProcess, rootProcess;
            Native.GetWindowThreadProcessId(handle, out hostProcess);
            Native.GetWindowThreadProcessId(actualRoot, out rootProcess);
            if (!hasShellView || hostProcess == 0 || hostProcess != rootProcess ||
                !IsExplorerProcess(rootProcess))
                throw new InvalidOperationException("Não foi possível confirmar o processo e a visualização do Explorador.");
            return rootProcess;
        }

        private static bool IsExplorerProcess(uint processId)
        {
            // A separate Explorer process is supported. Only executable metadata
            // is queried; no file contents or foreign process memory are read.
            IntPtr process = Native.OpenProcess(0x1000, false, processId);
            if (process == IntPtr.Zero) return false; // PROCESS_QUERY_LIMITED_INFORMATION
            try
            {
                StringBuilder executable = new StringBuilder(32768);
                uint length = (uint)executable.Capacity;
                if (!Native.QueryFullProcessImageName(process, 0, executable, ref length)) return false;
                StringBuilder windowsDirectory = new StringBuilder(32768);
                uint directoryLength = Native.GetWindowsDirectory(windowsDirectory,
                                                                  (uint)windowsDirectory.Capacity);
                if (directoryLength == 0 || directoryLength >= windowsDirectory.Capacity) return false;
                string expected = windowsDirectory.ToString().TrimEnd('\\') + "\\explorer.exe";
                return String.Equals(executable.ToString(), expected, StringComparison.OrdinalIgnoreCase);
            }
            finally { Native.CloseHandle(process); }
        }

        private static void ValidateDesktopHost(IntPtr handle)
        {
            if (!String.Equals(Native.ClassName(handle), "SysListView32",
                               StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "O Windows não apresentou uma lista de ícones compatível na área de trabalho.");

            // Explorer folder windows can also contain SHELLDLL_DefView.
            // Require both the view and a desktop-specific ancestor.
            bool hasShellView = false;
            bool hasDesktopHost = false;
            IntPtr ancestor = Native.GetParent(handle);
            for (int depth = 0; ancestor != IntPtr.Zero && depth < 8; depth++)
            {
                string className = Native.ClassName(ancestor);
                if (String.Equals(className, "SHELLDLL_DefView", StringComparison.Ordinal))
                    hasShellView = true;
                if (String.Equals(className, "Progman", StringComparison.Ordinal) ||
                    String.Equals(className, "WorkerW", StringComparison.Ordinal))
                    hasDesktopHost = true;
                ancestor = Native.GetParent(ancestor);
            }
            if (hasShellView && hasDesktopHost) return;
            throw new InvalidOperationException(
                "A lista de ícones não pertence a uma visualização Shell compatível.");
        }

        private static Rectangle PhysicalScreenToClient(IntPtr host, Rectangle bounds)
        {
            Native.Point first = new Native.Point(bounds.Left, bounds.Top);
            Native.Point last = new Native.Point(bounds.Right, bounds.Bottom);
            // UIA is always physical. Convert to the host's logical screen units
            // before ScreenToClient, which runs in the host's DPI context.
            if (!Native.PhysicalToLogicalPointForPerMonitorDPI(host, ref first) ||
                !Native.PhysicalToLogicalPointForPerMonitorDPI(host, ref last) ||
                !Native.ScreenToClient(host, ref first) ||
                !Native.ScreenToClient(host, ref last))
                throw Native.Failure("Converter a posição da etiqueta");
            return Rectangle.FromLTRB(first.X, first.Y, last.X, last.Y);
        }

        private static void DrawBitmap(IntPtr handle, Size size, string text,
                                       uint colorRef, int dpi, float logicalScale,
                                       bool badge, bool rightAligned, LabelTheme theme, float textScale,
                                       bool highContrast, Color systemText, Color systemBackground)
        {
            LabelPalette palette = LabelPalette.Create(theme, colorRef, highContrast,
                                                       systemText, systemBackground);
            float scale = dpi / 96f * logicalScale;
            float padding = Math.Max(0.5f, (badge ? 2f : 1f) * scale);
            float em = Math.Min((badge ? 11f : 12f) * scale * textScale,
                                Math.Max(5f, size.Height - 2f * padding));

            // PArgb is explicitly premultiplied, as required by AC_SRC_ALPHA.
            // ClearType is unsuitable on a transparent surface; use grayscale AA.
            using (Bitmap bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                using (SolidBrush inkBrush = new SolidBrush(palette.Ink))
                using (SolidBrush shadowBrush = new SolidBrush(palette.Shade))
                using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.None;
                    graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    if (badge || palette.SolidBackground)
                    {
                        // A neutral chip keeps sizes legible over folder artwork.
                        // The layout guarantees that native names stay uncovered.
                        graphics.SmoothingMode = SmoothingMode.AntiAlias;
                        RectangleF chip = new RectangleF(0.5f, 0.5f,
                            Math.Max(1, size.Width - 1f), Math.Max(1, size.Height - 1f));
                        using (GraphicsPath path = RoundedRectangle(chip, Math.Max(2f, 4f * scale)))
                        using (SolidBrush fill = new SolidBrush(palette.Fill))
                        using (Pen border = new Pen(palette.Border, Math.Max(0.5f, scale * 0.6f)))
                        {
                            graphics.FillPath(fill, path);
                            graphics.DrawPath(border, path);
                        }
                    }
                    format.Alignment = rightAligned && !badge ? StringAlignment.Far : StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                    format.Trimming = StringTrimming.EllipsisCharacter;
                    RectangleF area = new RectangleF(padding, 0,
                        Math.Max(1, size.Width - 2 * padding), size.Height - padding);
                    RectangleF shadow = area;
                    shadow.Offset(0, Math.Max(0.5f, 0.65f * scale));
                    // Fit moderately longer state captions before trimming. The
                    // layout has already protected native text/editor bounds;
                    // font fitting must never make the label exceed that space.
                    using (Font font = FitFont(graphics, text, em, Math.Max(5f, em * 0.72f), area, format))
                    {
                        if (!highContrast) graphics.DrawString(text, font, shadowBrush, shadow, format);
                        graphics.DrawString(text, font, inkBrush, area, format);
                    }
                }
                // Dispose Graphics before locking the bitmap's backing memory.
                PushPremultipliedBitmap(handle, bitmap);
            }
        }

        private static Font FitFont(Graphics graphics, string text, float desired, float minimum,
                                    RectangleF area, StringFormat format)
        {
            float em = desired;
            while (true)
            {
                Font font = new Font("Segoe UI", em, FontStyle.Regular, GraphicsUnit.Pixel);
                if (em <= minimum || graphics.MeasureString(text, font, Int32.MaxValue, format).Width <= area.Width)
                    return font;
                font.Dispose();
                em = Math.Max(minimum, em - 0.5f);
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = Math.Max(1f, Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height)));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void PushPremultipliedBitmap(IntPtr handle, Bitmap bitmap)
        {
            IntPtr dc = IntPtr.Zero;
            IntPtr dib = IntPtr.Zero;
            IntPtr previousBitmap = IntPtr.Zero;
            try
            {
                dc = Native.CreateCompatibleDC(IntPtr.Zero);
                if (dc == IntPtr.Zero) throw Native.Failure("Criar a superfície da etiqueta");
                Native.BitmapInfo info = new Native.BitmapInfo();
                info.Header.Size = (uint)Marshal.SizeOf(typeof(Native.BitmapInfoHeader));
                info.Header.Width = bitmap.Width;
                info.Header.Height = -bitmap.Height; // A top-down 32-bit DIB.
                info.Header.Planes = 1;
                info.Header.BitCount = 32;
                IntPtr bits;
                dib = Native.CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                    throw Native.Failure("Criar o bitmap transparente da etiqueta");
                previousBitmap = Native.SelectObject(dc, dib);
                if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
                    throw Native.Failure("Preparar o bitmap da etiqueta");

                Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly,
                                                  PixelFormat.Format32bppPArgb);
                try
                {
                    int rowBytes = checked(bitmap.Width * 4);
                    byte[] row = new byte[rowBytes];
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes);
                        Marshal.Copy(row, 0, IntPtr.Add(bits, y * rowBytes), rowBytes);
                    }
                }
                finally { bitmap.UnlockBits(data); }

                Native.Size nativeSize = new Native.Size(bitmap.Width, bitmap.Height);
                Native.Point source = new Native.Point(0, 0);
                Native.Blend blend = new Native.Blend();
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1; // AC_SRC_ALPHA, with AC_SRC_OVER == 0.
                // A null destination point leaves the already-positioned child
                // exactly where SetWindowPos placed it in parent client units.
                if (!Native.UpdateLayeredWindow(handle, IntPtr.Zero, IntPtr.Zero,
                        ref nativeSize, dc, ref source, 0, ref blend, 2))
                    throw Native.Failure(
                        "Desenhar a etiqueta (é necessário Windows 10/11 e manifesto compatível)");
            }
            finally
            {
                if (previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1) && dc != IntPtr.Zero)
                    Native.SelectObject(dc, previousBitmap);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                if (dc != IntPtr.Zero) Native.DeleteDC(dc);
            }
        }

        private sealed class LabelWindow : NativeWindow
        {
            private readonly LayeredLabel owner;
            private bool alive;
            private IntPtr expectedParent;
            private uint ownerNativeThread;
            internal LabelWindow(LayeredLabel owner) { this.owner = owner; }
            internal bool Alive
            { get { return alive && IsOwnedNativeWindow() && Native.GetParent(Handle) == expectedParent; } }

            private bool IsOwnedNativeWindow()
            {
                if (Handle == IntPtr.Zero || !Native.IsWindow(Handle)) return false;
                uint process;
                uint nativeThread = Native.GetWindowThreadProcessId(Handle, out process);
                return process == Native.GetCurrentProcessId() && nativeThread == ownerNativeThread;
            }

            internal void Create(IntPtr parent, Rectangle bounds)
            {
                expectedParent = parent;
                ownerNativeThread = Native.GetCurrentThreadId();
                CreateParams parameters = new CreateParams();
                parameters.Caption = "Pasta GB: tamanho";
                parameters.ClassStyle = 0;
                parameters.Style = Native.WS_CHILD | Native.WS_DISABLED;
                parameters.ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT |
                                     Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW |
                                     Native.WS_EX_NOPARENTNOTIFY;
                parameters.Parent = parent;
                parameters.X = bounds.X;
                parameters.Y = bounds.Y;
                parameters.Width = bounds.Width;
                parameters.Height = bounds.Height;
                CreateHandle(parameters);
                alive = Handle != IntPtr.Zero && Native.IsWindow(Handle);
                if (!alive) throw Native.Failure("Criar a etiqueta da pasta");
            }

            internal void Destroy()
            {
                if (Handle != IntPtr.Zero)
                {
                    if (IsOwnedNativeWindow()) DestroyHandle();
                    else ReleaseHandle();
                }
                alive = false;
            }

            protected override void WndProc(ref Message message)
            {
                switch (message.Msg)
                {
                    case 0x0084: // WM_NCHITTEST; layered transparency also spans threads.
                        message.Result = new IntPtr(-1); // HTTRANSPARENT
                        return;
                    case 0x0021: // WM_MOUSEACTIVATE
                        message.Result = new IntPtr(3); // MA_NOACTIVATE
                        return;
                    case 0x0014: // WM_ERASEBKGND: keep the ARGB surface transparent.
                        message.Result = new IntPtr(1);
                        return;
                    case 0x0002: // WM_DESTROY, including destruction by Explorer.
                    case 0x0082: // WM_NCDESTROY
                        alive = false;
                        owner.OnWindowDestroyed();
                        break;
                    case 0x001A: // WM_SETTINGCHANGE
                    case 0x001D: // WM_FONTCHANGE
                    case 0x007E: // WM_DISPLAYCHANGE
                    case 0x031A: // WM_THEMECHANGED
                    case 0x02E0: // WM_DPICHANGED
                    case 0x02E3: // WM_DPICHANGED_AFTERPARENT
                        owner.contentDirty = true;
                        break;
                }
                base.WndProc(ref message);
            }
        }

        private sealed class ParentDpiScope : IDisposable
        {
            private IntPtr previous;
            internal ParentDpiScope(IntPtr parent)
            {
                try
                {
                    IntPtr context = Native.GetWindowDpiAwarenessContext(parent);
                    if (context == IntPtr.Zero)
                        throw new InvalidOperationException("A visualização da pasta foi recriada pelo Windows.");
                    previous = Native.SetThreadDpiAwarenessContext(context);
                    if (previous == IntPtr.Zero)
                        throw new InvalidOperationException("Não foi possível acompanhar a escala de tela da pasta.");
                }
                catch (EntryPointNotFoundException ex)
                {
                    throw new PlatformNotSupportedException(
                        "As etiquetas requerem Windows 10 versão 1607 ou mais recente, ou Windows 11.", ex);
                }
            }
            public void Dispose()
            {
                if (previous != IntPtr.Zero)
                {
                    Native.SetThreadDpiAwarenessContext(previous);
                    previous = IntPtr.Zero;
                }
            }
        }

        private static class Native
        {
            internal const int WS_CHILD = 0x40000000, WS_DISABLED = 0x08000000;
            internal const int WS_EX_LAYERED = 0x00080000, WS_EX_TRANSPARENT = 0x00000020;
            internal const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
            internal const int WS_EX_NOPARENTNOTIFY = 0x00000004;
            internal const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004;
            internal const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOSENDCHANGING = 0x0400;
            internal const uint GA_ROOT = 2;
            internal const int SW_HIDE = 0;
            [StructLayout(LayoutKind.Sequential)]
            internal struct Point { internal int X, Y; internal Point(int x, int y) { X = x; Y = y; } }
            [StructLayout(LayoutKind.Sequential)]
            internal struct Size { internal int Width, Height; internal Size(int w, int h) { Width = w; Height = h; } }
            [StructLayout(LayoutKind.Sequential)]
            internal struct Rect { internal int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            internal struct Blend { internal byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
            [StructLayout(LayoutKind.Sequential)]
            internal struct BitmapInfoHeader
            {
                internal uint Size;
                internal int Width, Height;
                internal ushort Planes, BitCount;
                internal uint Compression, SizeImage;
                internal int XPelsPerMeter, YPelsPerMeter;
                internal uint ClrUsed, ClrImportant;
            }
            [StructLayout(LayoutKind.Sequential)]
            internal struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Color; }

            internal static Win32Exception Failure(string operation)
            {
                int error = Marshal.GetLastWin32Error();
                return new Win32Exception(error, operation + " falhou." +
                    (error == 0 ? String.Empty : " Código " + error + "."));
            }
            internal static string ClassName(IntPtr handle)
            {
                StringBuilder text = new StringBuilder(128);
                return GetClassName(handle, text, text.Capacity) != 0 ? text.ToString() : String.Empty;
            }

            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsWindow(IntPtr handle);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowText(IntPtr handle, string text);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsWindowVisible(IntPtr handle);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsIconic(IntPtr handle);
            [DllImport("user32.dll")]
            internal static extern IntPtr GetParent(IntPtr handle);
            [DllImport("user32.dll")]
            internal static extern IntPtr GetAncestor(IntPtr handle, uint flags);
            [DllImport("user32.dll")]
            internal static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetClientRect(IntPtr handle, out Rect rectangle);
            [DllImport("user32.dll", SetLastError = true)]
            internal static extern int SetWindowRgn(IntPtr handle, IntPtr region,
                                                   [MarshalAs(UnmanagedType.Bool)] bool redraw);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr handle, StringBuilder name, int length);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y,
                                                     int width, int height, uint flags);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ShowWindow(IntPtr handle, int command);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ScreenToClient(IntPtr handle, ref Point point);
            [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr handle, ref Point point);
            [DllImport("user32.dll")]
            internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr handle);
            [DllImport("user32.dll")]
            internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
            [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool UpdateLayeredWindow(IntPtr handle, IntPtr destinationDC,
                IntPtr destinationPoint, ref Size size, IntPtr sourceDC, ref Point sourcePoint,
                uint colorKey, ref Blend blend, uint flags);
            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
                                                          out IntPtr bits, IntPtr section, uint offset);
            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
            [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeleteObject(IntPtr value);
            [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool DeleteDC(IntPtr dc);
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr OpenProcess(uint access,
                [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
                                                                  StringBuilder path, ref uint length);
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            internal static extern uint GetWindowsDirectory(StringBuilder buffer, uint size);
            [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseHandle(IntPtr handle);
            [DllImport("kernel32.dll")]
            internal static extern uint GetCurrentProcessId();
            [DllImport("kernel32.dll")]
            internal static extern uint GetCurrentThreadId();
        }
    }
}
