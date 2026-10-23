// Read-only desktop inspection. Call Capture only from a dedicated STA worker.
// Shell COM supplies identity; a persistent bounded MTA supplies physical UIA bounds.
// No Explorer injection, cross-process memory, folder rename or Shell mutation.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace PastaGBIcons
{
    public static class DesktopProbe
    {
        private const uint SvgioAllView = 2;
        private const uint SfgaoFolder = 0x20000000;
        private const uint SfgaoFileSystem = 0x40000000;
        private const uint SfgaoLink = 0x00010000;
        private const uint SfgaoStream = 0x00400000;
        private const uint SigdnFileSystemPath = 0x80058000;
        private const int MaxDesktopItems = 5000;
        private static readonly Guid ShellWindowsClass = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly Guid TopLevelBrowserService = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid ShellBrowserInterface = new Guid("000214E2-0000-0000-C000-000000000046");
        private static readonly Guid ShellFolderInterface = new Guid("000214E6-0000-0000-C000-000000000046");
        private static readonly Guid ShellItemInterface = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        private static readonly BoundedMtaWorker<GeometryRequest, GeometryPacket> GeometryWorker =
            new BoundedMtaWorker<GeometryRequest, GeometryPacket>("UIA da área de trabalho", CaptureGeometry);
        public static string AutomationDiagnostic { get { return GeometryWorker.Diagnostic; } }

        private sealed class GeometryRequest
        {
            public IntPtr Host;
            public Rectangle Bounds;
            public HashSet<string> FolderNames;
        }
        private sealed class GeometryPacket
        {
            public List<AccessibleItem> Items;
            public List<Rectangle> Occupied;
        }

        private sealed class ShellEntry
        {
            public string Name;
            public string Path;
            public bool HasPosition;
            public Point Position;
        }

        private sealed class AccessibleItem
        {
            public string Name;
            public Rectangle Bounds;
            public Rectangle HitBounds;
            public bool Selected;
            public bool Editing;
        }

        public static DesktopSnapshot Capture()
        {
            DesktopSnapshot result = new DesktopSnapshot();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                result.Status = "Esta integração precisa da área de trabalho do Windows.";
                return result;
            }
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                result.Status = "A leitura da área de trabalho precisa de um worker STA.";
                return result;
            }

            object windows = null, dispatch = null, browserObject = null;
            object viewObject = null, folderObject = null;
            IntPtr previousDpiContext = EnterPhysicalCoordinates();
            try
            {
                windows = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, true));
                // FindWindowSW is dispatched by name using its automation contract,
                // avoiding a handwritten dual-interface vtable. This declaration is
                // also used by Microsoft's Node.js Tools SystemUtilities.cs.
                // VARIANT CSIDL_DESKTOP = 0; reserved root = VT_EMPTY; SWC_DESKTOP = 8.
                object location = 0, locationRoot = null;
                int desktopWindow;
                dispatch = ((IShellWindowsDispatch)windows).FindWindowSW(ref location, ref locationRoot, 8, out desktopWindow, 1);
                if (dispatch == null)
                    throw new InvalidOperationException("O Windows Explorer ainda não disponibilizou a área de trabalho.");

                Guid service = TopLevelBrowserService, browserId = ShellBrowserInterface;
                Check(((IComServiceProvider)dispatch).QueryService(ref service, ref browserId, out browserObject));
                Check(((IShellBrowserNative)browserObject).QueryActiveShellView(out viewObject));
                IntPtr shellViewWindow;
                Check(((IShellViewWindow)viewObject).GetWindow(out shellViewWindow));
                IntPtr host = FindDesktopListView(shellViewWindow);
                if (host == IntPtr.Zero)
                {
                    result.Status = "Esta versão da área de trabalho não expôs a lista de ícones compatível.";
                    return result;
                }

                result.HostHandle = host;
                result.HostScreenBounds = GetPhysicalClientBounds(host);
                result.Dpi = ReadDpi(host);
                result.TextColor = ReadTextColor(host);
                result.Visible = IsWindowVisible(host) && !result.HostScreenBounds.IsEmpty;
                if (!result.Visible)
                {
                    result.Status = "Os ícones da área de trabalho estão ocultos.";
                    return result;
                }

                IFolderViewNative folderView = (IFolderViewNative)viewObject;
                uint viewMode;
                Check(folderView.GetCurrentViewMode(out viewMode));
                // Keep the convention "label underneath the name" within icon views.
                if (viewMode != 1 && viewMode != 2 && viewMode != 5)
                {
                    result.Status = "Use a visualização por ícones na área de trabalho.";
                    return result;
                }
                Guid folderId = ShellFolderInterface;
                Check(folderView.GetFolder(ref folderId, out folderObject));
                List<ShellEntry> entries = ReadShellEntries(folderView, (IShellFolderNative)folderObject, host);
                HashSet<string> folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ShellEntry entry in entries)
                    if (!String.IsNullOrEmpty(entry.Path)) folderNames.Add(entry.Name);
                GeometryPacket geometry;
                string geometryError;
                if (!GeometryWorker.TryExecute(new GeometryRequest { Host = host, Bounds = result.HostScreenBounds,
                    FolderNames = folderNames },
                    2200, out geometry, out geometryError) || geometry == null)
                    throw new InvalidOperationException(geometryError ?? "A área de trabalho está atualizando os ícones.");
                List<AccessibleItem> accessible = geometry.Items;
                result.OccupiedBounds = geometry.Occupied;

                Dictionary<string, int> shellNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ShellEntry entry in entries)
                {
                    int count;
                    shellNames.TryGetValue(entry.Name, out count);
                    shellNames[entry.Name] = count + 1;
                }
                HashSet<AccessibleItem> used = new HashSet<AccessibleItem>();
                int folders = 0, unmatched = 0;
                foreach (ShellEntry entry in entries)
                {
                    if (String.IsNullOrEmpty(entry.Path)) continue;
                    folders++;
                    AccessibleItem match = FindMatch(entry, accessible, shellNames[entry.Name], result.Dpi);
                    if (match == null || used.Contains(match))
                    {
                        unmatched++;
                        continue;
                    }
                    used.Add(match);
                    result.Folders.Add(new DesktopFolderIcon {
                        Path = entry.Path, Name = entry.Name, Bounds = match.Bounds, ItemBounds = match.HitBounds,
                        IsSelected = match.Selected, IsEditing = match.Editing
                    });
                }

                // Discard a scan if the host moved, disappeared or hid while COM/UIA
                // were queried. A later snapshot can safely reattach the drawing layer.
                if (!IsWindow(host) || !IsWindowVisible(host) ||
                    GetPhysicalClientBounds(host) != result.HostScreenBounds)
                {
                    result.Visible = false;
                    result.Folders.Clear();
                    result.OccupiedBounds.Clear();
                    result.Status = "A área de trabalho mudou; atualizando as posições.";
                    return result;
                }
                result.ComputeGeometryStamp(null);
                result.Status = folders == 0 ? "Nenhuma pasta local visível na área de trabalho." :
                    unmatched == 0 ? "Ícones da área de trabalho identificados." :
                    String.Format(CultureInfo.GetCultureInfo("pt-BR"),
                        "{0} pasta(s) identificada(s); {1} sem posição segura para exibir.", result.Folders.Count, unmatched);
                return result;
            }
            catch (Exception exception)
            {
                // Failure is deliberately silent visually: stale or guessed labels are
                // worse than no label. The tray/status interface may show this message.
                result.Folders.Clear();
                result.OccupiedBounds.Clear();
                result.Visible = false;
                Exception cause = exception;
                result.Status = cause is COMException ?
                    "O Explorer não respondeu à leitura dos ícones (" + cause.HResult.ToString("X8", CultureInfo.InvariantCulture) + ")." :
                    cause is ElementNotAvailableException ? "A área de trabalho está sendo atualizada." :
                    "Não foi possível identificar os ícones com segurança: " + cause.Message;
                return result;
            }
            finally
            {
                // Casts share their source RCW. Release only the actual COM return
                // objects, once per acquisition, and never FinalRelease shared RCWs.
                Release(folderObject);
                Release(viewObject);
                Release(browserObject);
                Release(dispatch);
                Release(windows);
                RestoreDpiContext(previousDpiContext);
            }
        }

        private static List<ShellEntry> ReadShellEntries(IFolderViewNative view, IShellFolderNative folder, IntPtr host)
        {
            int count;
            Check(view.ItemCount(SvgioAllView, out count));
            if (count < 0 || count > MaxDesktopItems)
                throw new InvalidOperationException("A lista de ícones está indisponível ou excedeu o limite de leitura.");
            List<ShellEntry> entries = new List<ShellEntry>();
            for (int index = 0; index < count; index++)
            {
                IntPtr child = IntPtr.Zero;
                IShellItemNative item = null;
                try
                {
                    if (view.Item(index, out child) < 0 || child == IntPtr.Zero) continue;
                    Guid itemId = ShellItemInterface;
                    if (SHCreateItemWithParent(IntPtr.Zero, folder, child, ref itemId, out item) < 0 || item == null) continue;
                    string name = ReadDisplayName(item, 0);
                    if (String.IsNullOrEmpty(name)) continue;
                    ShellEntry entry = new ShellEntry { Name = name };
                    uint attributes;
                    if (item.GetAttributes(SfgaoFolder | SfgaoFileSystem | SfgaoLink | SfgaoStream, out attributes) >= 0 &&
                        (attributes & (SfgaoFolder | SfgaoFileSystem)) == (SfgaoFolder | SfgaoFileSystem) &&
                        (attributes & (SfgaoLink | SfgaoStream)) == 0)
                    {
                        // SFGAO_STREAM excludes archives exposed as shell folders;
                        // SFGAO_LINK excludes shortcuts. Do not resolve network links
                        // or perform recursive filesystem work on this STA worker.
                        entry.Path = ReadDisplayName(item, SigdnFileSystemPath);
                        // Positions disambiguate folder labels only. Keep every
                        // item's name for duplicate detection, but avoid this
                        // Shell call and coordinate conversion for files/shortcuts.
                        if (!String.IsNullOrEmpty(entry.Path))
                        {
                            NativePoint position;
                            if (view.GetItemPosition(child, out position) >= 0 && ClientToScreen(host, ref position))
                            {
                                entry.HasPosition = true;
                                entry.Position = new Point(position.X, position.Y);
                            }
                        }
                    }
                    entries.Add(entry);
                }
                catch (COMException)
                {
                    // A desktop item can disappear during enumeration. UIA matching
                    // below still requires unambiguous identity before any publication.
                }
                finally
                {
                    Release(item);
                    if (child != IntPtr.Zero) Marshal.FreeCoTaskMem(child);
                }
            }
            return entries;
        }

        private static string ReadDisplayName(IShellItemNative item, uint kind)
        {
            IntPtr text = IntPtr.Zero;
            try
            {
                return item.GetDisplayName(kind, out text) >= 0 && text != IntPtr.Zero ? Marshal.PtrToStringUni(text) : null;
            }
            finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
        }

        private static GeometryPacket CaptureGeometry(GeometryRequest request)
        {
            IntPtr previous = EnterPhysicalCoordinates();
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                AutomationElement list = AutomationElement.FromHandle(request.Host);
                if (list == null) throw new InvalidOperationException("A lista de ícones não expôs informações de acessibilidade.");
                List<Rectangle> occupied = new List<Rectangle>();
                List<VisualRowAnchor> anchors;
                List<AccessibleItem> items = ReadAccessibleItems(list, request.Bounds, occupied, request.FolderNames,
                    clock, out anchors);
                List<VisualRowAnchor> current = ReadDesktopAnchors(list, request.Bounds, clock);
                if (!VisualSnapshotPolicy.SameRows(anchors, current) || !IsWindow(request.Host) ||
                    !IsWindowVisible(request.Host) || GetPhysicalClientBounds(request.Host) != request.Bounds)
                    throw new InvalidOperationException("A área de trabalho mudou durante a leitura das posições.");
                return new GeometryPacket { Items = items, Occupied = occupied };
            }
            finally
            {
                if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
            }
        }

        private static AutomationElementCollection ReadDesktopElements(AutomationElement list)
        {
            AutomationElementCollection elements;
            CacheRequest cache = new CacheRequest();
            cache.TreeScope = TreeScope.Element;
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(SelectionItemPattern.IsSelectedProperty);
            using (cache.Activate())
                elements = list.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            if (elements.Count > MaxDesktopItems)
                throw new InvalidOperationException("A lista de acessibilidade excedeu o limite de leitura.");
            return elements;
        }

        private static List<AccessibleItem> ReadAccessibleItems(AutomationElement list, Rectangle hostBounds,
            List<Rectangle> occupied, HashSet<string> folderNames, Stopwatch clock, out List<VisualRowAnchor> anchors)
        {
            anchors = new List<VisualRowAnchor>();
            List<AccessibleItem> result = new List<AccessibleItem>();
            foreach (AutomationElement element in ReadDesktopElements(list))
            {
                CheckGeometryBudget(clock);
                Rectangle bounds;
                if (element.Cached.IsOffscreen || !TryRectangle(element.Cached.BoundingRectangle, out bounds) ||
                    !hostBounds.IntersectsWith(bounds)) continue;
                object selection = element.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true);
                string name = element.Cached.Name ?? String.Empty;
                anchors.Add(new VisualRowAnchor { Name = name, Bounds = bounds });
                Rectangle tightened = bounds;
                bool unsafeGeometry = false;
                // Keep every item's full bounds as an obstacle/anchor, but only
                // ask the provider for fine text geometry when a real folder
                // of that name can receive a size label. Shortcuts and files
                // need no text-pattern or child-element round trips.
                try
                {
                    if (folderNames != null && folderNames.Contains(name))
                        tightened = TightenLabelBottom(element, name, bounds);
                }
                catch (ElementNotAvailableException) { unsafeGeometry = true; }
                catch (InvalidOperationException) { unsafeGeometry = true; }
                catch (COMException) { unsafeGeometry = true; }
                result.Add(new AccessibleItem {
                    Name = name, Bounds = tightened, HitBounds = bounds,
                    Selected = selection is bool && (bool)selection, Editing = unsafeGeometry
                });
                occupied.Add(bounds);
            }

            foreach (Rectangle editBounds in ReadDesktopEditors(list, hostBounds, clock))
            {
                anchors.Add(new VisualRowAnchor { Name = String.Empty, Bounds = editBounds, Editor = true });
                occupied.Add(editBounds);
                bool associated = false;
                foreach (AccessibleItem item in result)
                {
                    Rectangle near = item.HitBounds;
                    near.Inflate(4, 4);
                    if (!near.IntersectsWith(editBounds)) continue;
                    item.Editing = true;
                    associated = true;
                }
                if (!associated) foreach (AccessibleItem item in result) item.Editing = true;
            }
            return result;
        }

        private static List<VisualRowAnchor> ReadDesktopAnchors(AutomationElement list, Rectangle hostBounds, Stopwatch clock)
        {
            List<VisualRowAnchor> result = new List<VisualRowAnchor>();
            foreach (AutomationElement item in ReadDesktopElements(list))
            {
                CheckGeometryBudget(clock);
                Rectangle bounds;
                if (!item.Cached.IsOffscreen && TryRectangle(item.Cached.BoundingRectangle, out bounds) && hostBounds.IntersectsWith(bounds))
                    result.Add(new VisualRowAnchor { Name = item.Cached.Name ?? String.Empty, Bounds = bounds });
            }
            foreach (Rectangle bounds in ReadDesktopEditors(list, hostBounds, clock))
                result.Add(new VisualRowAnchor { Name = String.Empty, Bounds = bounds, Editor = true });
            return result;
        }

        private static List<Rectangle> ReadDesktopEditors(AutomationElement list, Rectangle hostBounds, Stopwatch clock)
        {
            AutomationElementCollection editors;
            CacheRequest cache = new CacheRequest();
            cache.TreeScope = TreeScope.Element;
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.HasKeyboardFocusProperty);
            using (cache.Activate())
                editors = list.FindAll(TreeScope.Descendants,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            if (editors.Count > MaxDesktopItems) throw new InvalidOperationException("Há editores demais na área de trabalho.");
            List<Rectangle> result = new List<Rectangle>();
            foreach (AutomationElement editor in editors)
            {
                CheckGeometryBudget(clock);
                Rectangle bounds;
                if (editor.Cached.HasKeyboardFocus && !editor.Cached.IsOffscreen &&
                    TryRectangle(editor.Cached.BoundingRectangle, out bounds) && hostBounds.IntersectsWith(bounds)) result.Add(bounds);
            }
            return result;
        }

        private static void CheckGeometryBudget(Stopwatch clock)
        {
            if (clock.ElapsedMilliseconds > 1500)
                throw new InvalidOperationException("A leitura dos ícones excedeu 1,5 s; aguardando a área de trabalho.");
        }

        private static Rectangle TightenLabelBottom(AutomationElement element, string name, Rectangle itemBounds)
        {
            // Some providers expose the name as a Text child; others expose only
            // ListItem. An exact name child lets us remove bottom padding. Without
            // it retain the conservative item bounds; never estimate font wrapping.
            if (String.IsNullOrEmpty(name)) return itemBounds;
            try
            {
                AutomationElementCollection texts;
                CacheRequest cache = new CacheRequest();
                cache.TreeScope = TreeScope.Element;
                cache.TreeFilter = Automation.RawViewCondition;
                cache.AutomationElementMode = AutomationElementMode.None;
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.NameProperty);
                cache.Add(AutomationElement.BoundingRectangleProperty);
                using (cache.Activate())
                    texts = element.FindAll(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                int bottom = Int32.MinValue;
                foreach (AutomationElement text in texts)
                {
                    Rectangle textBounds;
                    if (text.Cached.IsOffscreen || !String.Equals(text.Cached.Name, name, StringComparison.OrdinalIgnoreCase) ||
                        !TryRectangle(text.Cached.BoundingRectangle, out textBounds) || !itemBounds.Contains(textBounds)) continue;
                    bottom = Math.Max(bottom, textBounds.Bottom);
                }
                if (bottom > itemBounds.Top && bottom <= itemBounds.Bottom)
                    return Rectangle.FromLTRB(itemBounds.Left, itemBounds.Top, itemBounds.Right, bottom);

                // TextPattern is optional on ListItem. When it is exposed, accept
                // its geometry only for an exact full-name match. The standard
                // desktop provider may support neither Text children nor TextPattern.
                object patternObject;
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out patternObject))
                {
                    TextPattern pattern = patternObject as TextPattern;
                    if (pattern != null)
                    {
                        System.Windows.Automation.Text.TextPatternRange range = pattern.DocumentRange;
                        string rangeText = range.GetText(Math.Min(32768, name.Length * 3 + 8));
                        if (rangeText != null && String.Equals(rangeText.Replace("\r", String.Empty).Replace("\n", String.Empty),
                            name, StringComparison.OrdinalIgnoreCase))
                        {
                            System.Windows.Rect[] rectangles = range.GetBoundingRectangles();
                            bool valid = rectangles != null && rectangles.Length > 0;
                            if (valid)
                            {
                                for (int i = 0; i < rectangles.Length; i++)
                                {
                                    Rectangle textBounds;
                                    if (!TryRectangle(rectangles[i], out textBounds) ||
                                        !itemBounds.Contains(textBounds)) { valid = false; break; }
                                    bottom = Math.Max(bottom, textBounds.Bottom);
                                }
                            }
                            if (valid && bottom > itemBounds.Top && bottom <= itemBounds.Bottom)
                                return Rectangle.FromLTRB(itemBounds.Left, itemBounds.Top, itemBounds.Right, bottom);
                        }
                    }
                }
                return itemBounds;
            }
            catch (InvalidOperationException) { return itemBounds; }
            catch (NotSupportedException) { return itemBounds; }
        }

        private static AccessibleItem FindMatch(ShellEntry entry, List<AccessibleItem> items, int shellNameCount, int dpi)
        {
            AccessibleItem soleName = null, solePosition = null;
            int nameMatches = 0, positionMatches = 0;
            foreach (AccessibleItem candidate in items)
            {
                if (!String.Equals(entry.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)) continue;
                nameMatches++;
                soleName = candidate;
                if (!entry.HasPosition) continue;
                Rectangle near = candidate.HitBounds;
                int tolerance = Math.Max(3, dpi / 24);
                near.Inflate(tolerance, tolerance);
                if (near.Contains(entry.Position)) { positionMatches++; solePosition = candidate; }
            }
            // Position cross-checks prevent using a mixed snapshot after dragging.
            if (entry.HasPosition) return positionMatches == 1 ? solePosition : null;
            return nameMatches == 1 && shellNameCount == 1 ? soleName : null;
        }

        private static bool TryRectangle(System.Windows.Rect value, out Rectangle rectangle)
        {
            rectangle = Rectangle.Empty;
            if (value.IsEmpty || Double.IsNaN(value.X) || Double.IsNaN(value.Y) ||
                Double.IsNaN(value.Width) || Double.IsNaN(value.Height) ||
                Double.IsInfinity(value.X) || Double.IsInfinity(value.Y) ||
                Double.IsInfinity(value.Width) || Double.IsInfinity(value.Height) ||
                value.Width <= 0 || value.Height <= 0 || Math.Abs(value.X) > 1000000 ||
                Math.Abs(value.Y) > 1000000 || value.Width > 1000000 || value.Height > 1000000) return false;
            rectangle = Rectangle.FromLTRB((int)Math.Floor(value.Left), (int)Math.Floor(value.Top),
                (int)Math.Ceiling(value.Right), (int)Math.Ceiling(value.Bottom));
            return rectangle.Width > 0 && rectangle.Height > 0;
        }

        private static IntPtr FindDesktopListView(IntPtr shellView)
        {
            IntPtr direct = FindListView(shellView);
            if (IsDesktopHost(direct)) return direct;
            // Host class names are Explorer implementation details. This fallback
            // is restricted to the actual shell process and never creates a host.
            uint shellProcess;
            GetWindowThreadProcessId(GetShellWindow(), out shellProcess);
            if (shellProcess == 0) return IntPtr.Zero;
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr state) {
                uint process;
                GetWindowThreadProcessId(window, out process);
                if (process != shellProcess) return true;
                string windowClass = ClassName(window);
                if (windowClass != "Progman" && windowClass != "WorkerW") return true;
                IntPtr view = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view != IntPtr.Zero) found = FindListView(view);
                if (!IsDesktopHost(found)) found = IntPtr.Zero;
                return found == IntPtr.Zero;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsDesktopHost(IntPtr host)
        {
            if (host == IntPtr.Zero || !IsWindow(host) || ClassName(host) != "SysListView32" ||
                ClassName(GetParent(host)) != "SHELLDLL_DefView") return false;
            string topClass = ClassName(GetAncestor(host, 2)); // GA_ROOT
            if (topClass != "Progman" && topClass != "WorkerW") return false;
            uint process, shellProcess;
            GetWindowThreadProcessId(host, out process);
            GetWindowThreadProcessId(GetShellWindow(), out shellProcess);
            return process != 0 && process == shellProcess;
        }

        private static IntPtr FindListView(IntPtr parent)
        {
            if (parent == IntPtr.Zero || !IsWindow(parent)) return IntPtr.Zero;
            if (ClassName(parent) == "SysListView32") return parent;
            IntPtr result = FindWindowEx(parent, IntPtr.Zero, "SysListView32", null);
            if (result != IntPtr.Zero) return result;
            EnumChildWindows(parent, delegate(IntPtr child, IntPtr state) {
                if (ClassName(child) == "SysListView32") { result = child; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static string ClassName(IntPtr window)
        {
            StringBuilder value = new StringBuilder(256);
            return GetClassName(window, value, value.Capacity) > 0 ? value.ToString() : String.Empty;
        }

        private static Rectangle GetPhysicalClientBounds(IntPtr window)
        {
            NativeRect bounds;
            if (!GetClientRect(window, out bounds)) return Rectangle.Empty;
            NativePoint topLeft = new NativePoint { X = bounds.Left, Y = bounds.Top };
            NativePoint bottomRight = new NativePoint { X = bounds.Right, Y = bounds.Bottom };
            if (!ClientToScreen(window, ref topLeft) || !ClientToScreen(window, ref bottomRight)) return Rectangle.Empty;
            return Rectangle.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        }

        private static int ReadDpi(IntPtr window)
        {
            try { uint dpi = GetDpiForWindow(window); if (dpi >= 48 && dpi <= 960) return (int)dpi; }
            catch (EntryPointNotFoundException) { }
            return 96;
        }

        private static uint ReadTextColor(IntPtr window)
        {
            UIntPtr value;
            // LVM_GETTEXTCOLOR is a scalar-only query; no foreign memory is needed.
            IntPtr success = SendMessageTimeout(window, 0x1023, IntPtr.Zero, IntPtr.Zero, 3, 150, out value);
            return success == IntPtr.Zero ? 0xFFFFFFFF : unchecked((uint)value.ToUInt64());
        }

        private static IntPtr EnterPhysicalCoordinates()
        {
            try
            {
                IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
                return previous != IntPtr.Zero ? previous : SetThreadDpiAwarenessContext(new IntPtr(-3));
            }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
        }

        private static void RestoreDpiContext(IntPtr previous)
        {
            if (previous == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(previous); }
            catch (EntryPointNotFoundException) { }
        }

        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        private static void Release(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.ReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        private delegate bool EnumWindowCallback(IntPtr window, IntPtr state);

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("shell32.dll", PreserveSig = true)] private static extern int SHCreateItemWithParent(IntPtr parentIdList,
            [MarshalAs(UnmanagedType.Interface)] IShellFolderNative parentFolder, IntPtr childIdList,
            ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItemNative item);

        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IShellWindowsDispatch
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location,
                [MarshalAs(UnmanagedType.Struct)] ref object locationRoot,
                int shellWindowClass, out int window, int options);
        }

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IComServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object value);
        }

        // IShellBrowser derives from IOleWindow. All preceding slots are declared;
        // unused methods are never invoked. Native HRESULTs are preserved throughout.
        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowserNative
        {
            [PreserveSig] int GetWindow(out IntPtr window);
            [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
            [PreserveSig] int InsertMenusSB(IntPtr sharedMenu, IntPtr widths);
            [PreserveSig] int SetMenuSB(IntPtr sharedMenu, IntPtr oleMenu, IntPtr activeObject);
            [PreserveSig] int RemoveMenusSB(IntPtr sharedMenu);
            [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
            [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
            [PreserveSig] int TranslateAcceleratorSB(IntPtr message, ushort id);
            [PreserveSig] int BrowseObject(IntPtr itemIdList, uint flags);
            [PreserveSig] int GetViewStateStream(uint mode, out IntPtr stream);
            [PreserveSig] int GetControlWindow(uint id, out IntPtr window);
            [PreserveSig] int SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
            [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
        }

        [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellViewWindow
        {
            [PreserveSig] int GetWindow(out IntPtr window);
            [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
        }

        [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderViewNative
        {
            [PreserveSig] int GetCurrentViewMode(out uint mode);
            [PreserveSig] int SetCurrentViewMode(uint mode);
            [PreserveSig] int GetFolder(ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object folder);
            [PreserveSig] int Item(int index, out IntPtr childIdList);
            [PreserveSig] int ItemCount(uint flags, out int count);
            [PreserveSig] int Items(uint flags, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object items);
            [PreserveSig] int GetSelectionMarkedItem(out int index);
            [PreserveSig] int GetFocusedItem(out int index);
            [PreserveSig] int GetItemPosition(IntPtr childIdList, out NativePoint point);
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolderNative { }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemNative
        {
            [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr value);
            [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItemNative parent);
            [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItemNative other, uint hint, out int order);
        }
    }
}
