// Read-only Explorer discovery. Shell COM stays on a dedicated STA; UI Automation
// stays on a bounded, persistent MTA and only plain snapshots cross the boundary.
// No Explorer injection or foreign memory.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PastaGBIcons
{
    public sealed class ExplorerCaptureResult
    {
        public List<ExplorerSurface> Surfaces = new List<ExplorerSurface>();
        public string Status = "Nenhuma janela do Explorador visível.";
        public long CaptureMilliseconds;
    }

    public sealed class ExplorerSurface
    {
        public IntPtr RootHandle;
        public IntPtr ShellViewHandle;
        public string Key;
        public string Fingerprint;
        public string Location;
        public string HostClass;
        public uint ViewMode;
        public bool SupportsInlineLabels;
        public bool MetadataPending;
        public bool MetadataCached;
        public long StructureRevision;
        public long GeometryRevision;
        public DateTime CapturedUtc;
        public long CaptureMilliseconds;
        public DesktopSnapshot Snapshot = new DesktopSnapshot();
        // Both dictionaries are keyed by the actual full filesystem path. Empty
        // means the provider did not expose sufficiently precise name geometry.
        public Dictionary<string, Rectangle> NameBounds = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Rectangle> NameCellBounds = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Rectangle> SizeCellBounds = new Dictionary<string, Rectangle>(StringComparer.OrdinalIgnoreCase);
    }

    public static class ExplorerProbe
    {
        private const int MaxWindows = 128;
        private const int MaxShellItems = 100000;
        private const int MaxAccessibleItems = 10000;
        private const int MetadataSliceMilliseconds = 180;
        private const int MetadataRefreshSeconds = 30;
        private const int MetadataLifetimeSeconds = 120;
        private const long PerIndexMemoryLimit = 48L * 1024L * 1024L;
        private static readonly MetadataMemoryBudget IndexMemory = new MetadataMemoryBudget(96L * 1024L * 1024L);
        private const uint SfgaoFolder = 0x20000000, SfgaoFileSystem = 0x40000000;
        private const uint SfgaoLink = 0x00010000, SfgaoStream = 0x00400000;
        private const uint SigdnFileSystemPath = 0x80058000;
        private const uint SigdnDesktopAbsoluteParsing = 0x80028000;
        private static readonly Guid ShellWindowsClass = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly Guid TopLevelBrowserService = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid ShellBrowserId = new Guid("000214E2-0000-0000-C000-000000000046");
        private static readonly Guid ShellFolderId = new Guid("000214E6-0000-0000-C000-000000000046");
        private static readonly Guid ShellItemId = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        private static readonly Guid EnumIdListId = new Guid("000214F2-0000-0000-C000-000000000046");
        private static readonly Guid SummaryInformation = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC");
        [ThreadStatic] private static Dictionary<string, MetadataCache> metadataCaches;
        [ThreadStatic] private static int nextWindow;
        [ThreadStatic] private static Dictionary<int, IntPtr> knownWindowRoots;
        private static readonly BoundedMtaWorker<GeometryRequest, GeometryPacket> GeometryWorker =
            new BoundedMtaWorker<GeometryRequest, GeometryPacket>("UIA do Explorador", CaptureGeometry);

        public static string AutomationDiagnostic { get { return GeometryWorker.Diagnostic; } }
        public static string MetadataDiagnostic
        { get { return "Índices estimados: " + (IndexMemory.Used / 1048576d).ToString("0.0", CultureInfo.InvariantCulture) +
            " / " + (IndexMemory.Limit / 1048576) + " MiB; inclui índices ativos e em construção."; } }

        private sealed class ShellEntry
        {
            public string Name;
            public byte[] ChildId;
            public readonly FolderResolutionState Resolution = new FolderResolutionState();
            public string Path { get { return Resolution.Path; } }
        }
        private sealed class MetadataIndex : IDisposable
        {
            public IEnumIdListNative Enumerator;
            public readonly Dictionary<string, List<ShellEntry>> Names = new Dictionary<string, List<ShellEntry>>(StringComparer.OrdinalIgnoreCase);
            public int Count;
            public long Bytes;
            public bool Complete;
            public DateTime CompletedUtc;
            public void EndEnumeration() { Release(Enumerator); Enumerator = null; }
            public void Dispose()
            {
                EndEnumeration();
                Names.Clear();
                if (Bytes != 0) { IndexMemory.Release(Bytes); Bytes = 0; }
            }
        }
        private sealed class MetadataCache : IDisposable
        {
            public string ParentIdFingerprint;
            public long Revision;
            public int ExpectedCount;
            public DateTime LastSeenUtc;
            public DateTime ResidentSinceUtc = DateTime.UtcNow;
            public MetadataIndex Active, Building;
            public void Dispose()
            {
                if (Active != null) Active.Dispose();
                if (Building != null && Building != Active) Building.Dispose();
                Active = null; Building = null;
            }
        }
        private sealed class ItemGeometry
        {
            public string Name;
            public Rectangle Bounds, ItemBounds, NameBounds, NameCellBounds, SizeCellBounds;
            public bool IsSelected, IsEditing;
            public bool SafeToLabel = true;
            public uint? TextColor;
        }
        private sealed class Pane
        {
            public AutomationElement List;
            public IntPtr Host;
            public Rectangle ListBounds, Viewport;
            public AutomationElement NameHeader, SizeHeader;
            public Rectangle NameHeaderBounds, SizeHeaderBounds;
            public List<Rectangle> Chrome = new List<Rectangle>();
        }
        private sealed class GeometryRequest
        {
            public IntPtr View, Root;
            public uint ViewMode;
            public string NameColumn, SizeColumn;
            public HashSet<string> RelevantNames;
        }
        private sealed class GeometryPacket
        {
            public IntPtr Host;
            public Rectangle ListBounds, Viewport;
            public int Dpi;
            public uint TextColor;
            public List<Rectangle> Occupied;
            public List<ItemGeometry> Items;
        }

        public static ExplorerCaptureResult Capture()
        { return Capture(null, null, null); }

        public static ExplorerCaptureResult Capture(Func<IntPtr, long> readStructureRevision)
        { return Capture(readStructureRevision, null, null); }

        public static ExplorerCaptureResult Capture(Func<IntPtr, long> readStructureRevision, Action<ExplorerSurface> publishSurface)
        { return Capture(readStructureRevision, null, publishSurface); }

        // All delegates run on this STA worker. A structural revision must change
        // for item create/delete/rename/reorder; focus, selection and scroll alone
        // must not invalidate the name index. Geometry has its separate epoch.
        public static ExplorerCaptureResult Capture(Func<IntPtr, long> readStructureRevision,
            Func<IntPtr, long> readGeometryRevision, Action<ExplorerSurface> publishSurface)
        { return Capture(readStructureRevision, readGeometryRevision, publishSurface, null); }

        // observeIdentity is a separate notification, never a renderable frame.
        // It lets the caller retire a previous folder even while the new folder's
        // accessibility provider is still creating its content pane.
        public static ExplorerCaptureResult Capture(Func<IntPtr, long> readStructureRevision,
            Func<IntPtr, long> readGeometryRevision, Action<ExplorerSurface> publishSurface,
            Action<ExplorerSurface> observeIdentity)
        {
            ExplorerCaptureResult result = new ExplorerCaptureResult();
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                result.Status = "A leitura do Explorador precisa do Windows.";
                return result;
            }
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                result.Status = "A leitura do Explorador precisa de um worker STA.";
                return result;
            }
            IntPtr previousDpi = EnterPhysicalCoordinates();
            Stopwatch captureClock = Stopwatch.StartNew();
            object windowsObject = null;
            int failures = 0;
            string lastFailure = null;
            HashSet<string> published = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                PruneMetadataCaches();
                windowsObject = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, true));
                IShellWindowsDispatch windows = (IShellWindowsDispatch)windowsObject;
                int count = windows.Count;
                if (count < 0 || count > MaxWindows)
                    throw new InvalidOperationException("A coleção de janelas do Explorador excedeu o limite de leitura.");
                int first = count == 0 ? 0 : nextWindow % count;
                nextWindow = count == 0 ? 0 : (first + 1) % count;
                int priority = -1;
                IntPtr foreground = GetForegroundWindow();
                if (knownWindowRoots == null) knownWindowRoots = new Dictionary<int, IntPtr>();
                foreach (KeyValuePair<int, IntPtr> known in knownWindowRoots)
                    if (known.Key < count && known.Value == foreground) { priority = known.Key; break; }
                // Use known native roots only as a scheduling hint. A changed
                // Shell collection is still fully revalidated below. Foreground
                // is visited first and all other slots rotate without starvation.
                foreach (int slot in VisualSnapshotPolicy.WindowVisitOrder(count, first, priority))
                {
                    int index = slot;
                    object browserDispatch = null;
                    try
                    {
                        browserDispatch = windows.Item(index);
                        if (browserDispatch == null) continue;
                        ExplorerSurface surface = ReadSurface(browserDispatch, readStructureRevision, readGeometryRevision,
                            delegate(ExplorerSurface identity) {
                                knownWindowRoots[index] = identity.RootHandle;
                                if (observeIdentity != null) observeIdentity(identity);
                            });
                        if (surface != null && published.Add(surface.Key))
                        {
                            result.Surfaces.Add(surface);
                            if (publishSurface != null) publishSurface(surface);
                        }
                    }
                    catch (Exception exception)
                    {
                        failures++;
                        lastFailure = BriefError(exception);
                    }
                    finally { Release(browserDispatch); }
                }
                if (result.Surfaces.Count != 0)
                    result.Status = result.Surfaces.Count.ToString(CultureInfo.InvariantCulture) +
                        " janela(s)/aba(s) ativa(s) do Explorador identificada(s).";
                if (failures != 0)
                    result.Status += " Atualizando " + failures.ToString(CultureInfo.InvariantCulture) +
                        " visualização(ões): " + lastFailure;
            }
            catch (Exception exception)
            {
                result.Surfaces.Clear();
                result.Status = "Não foi possível consultar o Explorador: " + BriefError(exception);
            }
            finally
            {
                Release(windowsObject);
                RestoreDpiContext(previousDpi);
                result.CaptureMilliseconds = captureClock.ElapsedMilliseconds;
            }
            return result;
        }

        private static ExplorerSurface ReadSurface(object browserDispatch, Func<IntPtr, long> readStructureRevision,
            Func<IntPtr, long> readGeometryRevision, Action<ExplorerSurface> observeIdentity)
        {
            object browserObject = null, viewObject = null, folderObject = null;
            IntPtr parentId = IntPtr.Zero;
            Stopwatch clock = Stopwatch.StartNew();
            string cacheKey = null;
            MetadataIndex uncachedIndex = null;
            try
            {
                IComServiceProvider serviceProvider = browserDispatch as IComServiceProvider;
                if (serviceProvider == null) return null;
                Guid service = TopLevelBrowserService, browserId = ShellBrowserId;
                if (serviceProvider.QueryService(ref service, ref browserId, out browserObject) < 0 || browserObject == null) return null;
                IShellBrowserNative browser = (IShellBrowserNative)browserObject;
                if (browser.QueryActiveShellView(out viewObject) < 0 || viewObject == null) return null;
                IntPtr viewWindow;
                Check(((IShellViewWindow)viewObject).GetWindow(out viewWindow));
                if (viewWindow == IntPtr.Zero || !IsWindowVisible(viewWindow)) return null;
                IntPtr root = GetAncestor(viewWindow, 2); // GA_ROOT
                if (!IsExplorerRoot(root) || !BelongsToRoot(viewWindow, root)) return null;
                if (ClassName(viewWindow) != "SHELLDLL_DefView") return null;
                long structureRevision = ReadRevision(readStructureRevision, root);
                long geometryRevision = ReadRevision(readGeometryRevision, root);

                IFolderViewNative view = (IFolderViewNative)viewObject;
                uint viewMode;
                Check(view.GetCurrentViewMode(out viewMode));
                Guid folderId = ShellFolderId;
                Check(view.GetFolder(ref folderId, out folderObject));
                IPersistFolder2Native persistentFolder = folderObject as IPersistFolder2Native;
                if (persistentFolder == null) return null;
                Check(persistentFolder.GetCurFolder(out parentId));
                if (parentId == IntPtr.Zero) return null;
                // A virtual parent (Home, This PC, a library or search) can expose
                // real child folders. Each child still needs its own filesystem
                // identity; no path is assembled from a displayed folder name.
                string location = ReadAbsoluteName(parentId, SigdnFileSystemPath);
                string parsingIdentity = ReadAbsoluteName(parentId, SigdnDesktopAbsoluteParsing);
                string parentFingerprint = Fingerprint(parentId);
                if (String.IsNullOrEmpty(location)) location = ReadAbsoluteName(parentId, 0) ?? parsingIdentity ?? "Pasta virtual";
                string surfaceFingerprint = String.IsNullOrEmpty(parsingIdentity) ? parentFingerprint : Fingerprint(parsingIdentity);
                if (observeIdentity != null)
                {
                    // A distinct DTO is published and never mutated afterwards.
                    // The observer can register root/view event filtering now,
                    // before the first incremental metadata slice starts.
                    observeIdentity(new ExplorerSurface {
                        RootHandle = root, ShellViewHandle = viewWindow, Fingerprint = surfaceFingerprint,
                        Location = location, HostClass = String.Empty, ViewMode = viewMode,
                        StructureRevision = structureRevision, GeometryRevision = geometryRevision,
                        Key = root.ToInt64().ToString("X", CultureInfo.InvariantCulture) + ":" +
                            viewWindow.ToInt64().ToString("X", CultureInfo.InvariantCulture) + ":0:" + surfaceFingerprint,
                        CapturedUtc = DateTime.UtcNow
                    });
                    // Registration itself may advance epochs. These revisions
                    // bound all metadata/UIA work after observer registration.
                    structureRevision = ReadRevision(readStructureRevision, root);
                    geometryRevision = ReadRevision(readGeometryRevision, root);
                }

                ExplorerSurface surface = new ExplorerSurface {
                    RootHandle = root, ShellViewHandle = viewWindow,
                    ViewMode = viewMode, SupportsInlineLabels = viewMode == 1 || viewMode == 5,
                    Location = location, Fingerprint = surfaceFingerprint,
                    StructureRevision = structureRevision, GeometryRevision = geometryRevision
                };
                DesktopSnapshot snapshot = surface.Snapshot;

                int shellItemCount;
                Check(view.ItemCount(2, out shellItemCount));
                if (shellItemCount < 0 || shellItemCount > MaxShellItems)
                    throw new InvalidOperationException("A pasta excedeu o limite de 100 mil itens desta integração visual.");
                cacheKey = root.ToInt64().ToString("X", CultureInfo.InvariantCulture) + ":" +
                    viewWindow.ToInt64().ToString("X", CultureInfo.InvariantCulture) + ":" + surface.Fingerprint;
                MetadataIndex index;
                int indexedItems;
                if (readStructureRevision != null)
                {
                    index = ReadMetadataIndex(view, (IShellFolderNative)folderObject, cacheKey, parentFingerprint,
                        structureRevision, shellItemCount, out indexedItems, out surface.MetadataCached);
                }
                else
                {
                    // The compatibility overload lacks change notifications, so
                    // it does not reuse an identity map between separate captures.
                    uncachedIndex = NewMetadataIndex(view, shellItemCount);
                    while (!uncachedIndex.Complete)
                    {
                        AdvanceMetadataIndex(uncachedIndex, (IShellFolderNative)folderObject, shellItemCount, 1500);
                        if (!uncachedIndex.Complete) CheckReadBudget(clock);
                    }
                    index = uncachedIndex;
                    indexedItems = index.Count;
                }
                surface.MetadataPending = index == null;
                HashSet<string> relevantNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (index != null)
                    foreach (KeyValuePair<string, List<ShellEntry>> name in index.Names)
                        if (name.Value.Count == 1 && (!name.Value[0].Resolution.Resolved ||
                            !String.IsNullOrEmpty(name.Value[0].Path))) relevantNames.Add(name.Key);
                GeometryRequest request = new GeometryRequest {
                    View = viewWindow, Root = root, ViewMode = viewMode, RelevantNames = relevantNames
                };
                if (viewMode == 4)
                {
                    IColumnManagerNative columns = viewObject as IColumnManagerNative;
                    if (columns != null)
                    {
                        // Native Shell property keys stay on this STA. Only the
                        // proven captions cross to the accessibility MTA.
                        request.SizeColumn = ReadColumnName(columns, 12);
                        request.NameColumn = ReadColumnName(columns, 10);
                    }
                }
                GeometryPacket pane;
                string geometryError;
                if (!GeometryWorker.TryExecute(request, 2200, out pane, out geometryError) || pane == null)
                    throw new InvalidOperationException(geometryError ?? "O painel não disponibilizou uma posição segura.");
                surface.HostClass = ClassName(pane.Host);
                surface.Key = root.ToInt64().ToString("X", CultureInfo.InvariantCulture) + ":" +
                    viewWindow.ToInt64().ToString("X", CultureInfo.InvariantCulture) + ":" +
                    pane.Host.ToInt64().ToString("X", CultureInfo.InvariantCulture) + ":" + surface.Fingerprint;
                snapshot.HostHandle = pane.Host;
                snapshot.HostScreenBounds = pane.Viewport;
                snapshot.Dpi = pane.Dpi;
                snapshot.TextColor = pane.TextColor;
                snapshot.OccupiedBounds = pane.Occupied;
                List<ItemGeometry> geometries = pane.Items;
                Dictionary<string, int> accessibleNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ItemGeometry item in geometries)
                {
                    int count;
                    accessibleNames.TryGetValue(item.Name, out count);
                    accessibleNames[item.Name] = count + 1;
                }
                int unmatched = 0;
                bool nativeTextColor = false;
                foreach (ItemGeometry item in geometries)
                {
                    if (!item.SafeToLabel) { unmatched++; continue; }
                    List<ShellEntry> matchingEntries;
                    if (index == null || !index.Names.TryGetValue(item.Name, out matchingEntries) || matchingEntries.Count != 1 ||
                        accessibleNames[item.Name] != 1)
                    { unmatched++; continue; }
                    ShellEntry entry = matchingEntries[0];
                    try { ResolvePhysicalFolder(entry, (IShellFolderNative)folderObject, index); }
                    catch { RemoveMetadataCache(cacheKey); throw; }
                    if (String.IsNullOrEmpty(entry.Path)) continue;
                    if (!item.NameBounds.IsEmpty && item.NameBounds.Bottom > item.ItemBounds.Top && item.NameBounds.Bottom <= item.ItemBounds.Bottom)
                        item.Bounds = Rectangle.FromLTRB(item.ItemBounds.Left, item.ItemBounds.Top, item.ItemBounds.Right, item.NameBounds.Bottom);
                    snapshot.Folders.Add(new DesktopFolderIcon {
                        Path = entry.Path, Name = entry.Name, Bounds = item.Bounds, ItemBounds = item.ItemBounds,
                        IsSelected = item.IsSelected, IsEditing = item.IsEditing
                    });
                    if (!item.NameBounds.IsEmpty) surface.NameBounds[entry.Path] = item.NameBounds;
                    if (!item.NameCellBounds.IsEmpty) surface.NameCellBounds[entry.Path] = item.NameCellBounds;
                    if (!item.SizeCellBounds.IsEmpty) surface.SizeCellBounds[entry.Path] = item.SizeCellBounds;
                    if (!nativeTextColor && !item.IsSelected && item.TextColor.HasValue)
                    { snapshot.TextColor = item.TextColor.Value; nativeTextColor = true; }
                }

                // A reused view HWND does not imply the same folder. Requery the
                // active Shell view and parent PIDL after BOTH Shell/UIA reads.
                // This prevents an old path being paired with a new tab/folder
                // containing identically named items during navigation.
                int currentCount;
                bool sameStructure = ReadRevision(readStructureRevision, root) == structureRevision &&
                    view.ItemCount(2, out currentCount) >= 0 && currentCount == shellItemCount;
                bool sameFolder = StillCurrent(browser, root, viewWindow, parentId, pane);
                if (!sameStructure || !sameFolder)
                { RemoveMetadataCache(cacheKey); return null; }
                if (ReadRevision(readGeometryRevision, root) != geometryRevision) return null;
                List<VisualRowAnchor> cells = new List<VisualRowAnchor>();
                foreach (KeyValuePair<string, Rectangle> pair in surface.NameBounds)
                    cells.Add(new VisualRowAnchor { Name = "\0name:" + pair.Key, Bounds = pair.Value });
                foreach (KeyValuePair<string, Rectangle> pair in surface.NameCellBounds)
                    cells.Add(new VisualRowAnchor { Name = "\0namecell:" + pair.Key, Bounds = pair.Value });
                foreach (KeyValuePair<string, Rectangle> pair in surface.SizeCellBounds)
                    cells.Add(new VisualRowAnchor { Name = "\0sizecell:" + pair.Key, Bounds = pair.Value });
                snapshot.ComputeGeometryStamp(cells);
                snapshot.Visible = true;
                snapshot.Status = surface.MetadataPending ?
                    "Identificando itens: " + indexedItems.ToString(CultureInfo.InvariantCulture) + " de " + shellItemCount.ToString(CultureInfo.InvariantCulture) + "." : surface.SupportsInlineLabels ?
                    "Pastas visíveis identificadas no Explorador." :
                    "Visualização " + ViewModeName(viewMode) + " identificada; etiquetas abaixo do nome disponíveis no modo de ícones.";
                if (unmatched != 0 && !surface.MetadataPending)
                    snapshot.Status += " " + unmatched.ToString(CultureInfo.InvariantCulture) + " item(ns) sem correspondência única.";
                surface.CapturedUtc = DateTime.UtcNow;
                surface.CaptureMilliseconds = clock.ElapsedMilliseconds;
                return surface;
            }
            catch
            {
                // A failed COM enumeration is not a trustworthy partial index.
                // Geometry failures leave a completed, unchanged index reusable.
                MetadataCache failedCache;
                if (cacheKey != null && metadataCaches != null && metadataCaches.TryGetValue(cacheKey, out failedCache) &&
                    failedCache.Building != null && failedCache.Building.Enumerator == null && !failedCache.Building.Complete)
                    RemoveMetadataCache(cacheKey);
                throw;
            }
            finally
            {
                if (uncachedIndex != null) uncachedIndex.Dispose();
                if (parentId != IntPtr.Zero) Marshal.FreeCoTaskMem(parentId);
                Release(folderObject);
                Release(viewObject);
                Release(browserObject);
            }
        }

        private static bool StillCurrent(IShellBrowserNative browser, IntPtr root, IntPtr viewWindow, IntPtr parentId, GeometryPacket pane)
        {
            object currentViewObject = null, currentFolderObject = null;
            IntPtr currentId = IntPtr.Zero;
            try
            {
                if (!IsExplorerRootVisible(root) || !IsWindowVisible(pane.Host) ||
                    !BelongsToRoot(pane.Host, root) || !IsChildOrSelf(viewWindow, pane.Host)) return false;
                Check(browser.QueryActiveShellView(out currentViewObject));
                IntPtr currentWindow;
                Check(((IShellViewWindow)currentViewObject).GetWindow(out currentWindow));
                if (currentWindow != viewWindow) return false;
                Guid folderId = ShellFolderId;
                Check(((IFolderViewNative)currentViewObject).GetFolder(ref folderId, out currentFolderObject));
                IPersistFolder2Native currentFolder = currentFolderObject as IPersistFolder2Native;
                if (currentFolder == null || currentFolder.GetCurFolder(out currentId) < 0 || currentId == IntPtr.Zero ||
                    !ILIsEqual(parentId, currentId)) return false;
                Rectangle currentClient = PhysicalClientBounds(pane.Host);
                return currentClient.Contains(pane.Viewport);
            }
            finally
            {
                if (currentId != IntPtr.Zero) Marshal.FreeCoTaskMem(currentId);
                Release(currentFolderObject);
                Release(currentViewObject);
            }
        }

        private static GeometryPacket CaptureGeometry(GeometryRequest request)
        {
            // No HWNDs are created by this worker. All UIA elements and patterns
            // below are created and consumed within the same persistent MTA.
            IntPtr previous = EnterPhysicalCoordinates();
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                Pane pane = FindContentPane(request.View, request.Root);
                if (pane == null)
                    throw new InvalidOperationException("O painel não expôs uma lista/grade com limites seguros.");
                if (request.ViewMode == 4)
                    ReadColumnHeaders(pane, request.SizeColumn, request.NameColumn, request.View);
                List<Rectangle> occupied = new List<Rectangle>(pane.Chrome);
                List<VisualRowAnchor> anchors;
                List<ItemGeometry> items = ReadGeometry(pane, occupied, request.RelevantNames,
                                                       request.ViewMode == 4, clock, out anchors);
                // A second small bulk snapshot proves the row geometry remained
                // stable. WinEvent epochs remain an independent final guard.
                List<VisualRowAnchor> current = ReadGeometryAnchors(pane, clock);
                Rectangle currentList;
                if (!VisualSnapshotPolicy.SameRows(anchors, current) ||
                    pane.List.Current.IsOffscreen ||
                    !TryRectangle(pane.List.Current.BoundingRectangle, out currentList) ||
                    currentList != pane.ListBounds || !IsWindowVisible(pane.Host) ||
                    !BelongsToRoot(pane.Host, request.Root) ||
                    !PhysicalClientBounds(pane.Host).Contains(pane.Viewport))
                    throw new InvalidOperationException("A visualização mudou durante a leitura; atualizando as posições.");
                return new GeometryPacket { Host = pane.Host, ListBounds = pane.ListBounds, Viewport = pane.Viewport,
                    Dpi = ReadDpi(pane.Host), TextColor = ReadFallbackTextColor(pane.Host),
                    Items = items, Occupied = occupied };
            }
            finally { RestoreDpiContext(previous); }
        }

        private static Pane FindContentPane(IntPtr shellView, IntPtr root)
        {
            AutomationElement view = AutomationElement.FromHandle(shellView);
            if (view == null) return null;
            List<AutomationElement> candidates = new List<AutomationElement>();
            if (view.Current.ControlType == ControlType.List || view.Current.ControlType == ControlType.DataGrid) candidates.Add(view);
            AutomationElementCollection lists = view.FindAll(TreeScope.Descendants,
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid)));
            if (lists.Count > 16) return null;
            foreach (AutomationElement list in lists) candidates.Add(list);
            Pane found = null;
            foreach (AutomationElement list in candidates)
            {
                Rectangle listBounds;
                if (list.Current.IsOffscreen || !TryRectangle(list.Current.BoundingRectangle, out listBounds)) continue;
                IntPtr host = NativeAncestor(list, shellView);
                if (!ValidHost(host, shellView, root)) continue;
                Rectangle viewport = Rectangle.Intersect(listBounds, PhysicalClientBounds(host));
                if (viewport.Width < 16 || viewport.Height < 16) continue;
                if (found != null)
                {
                    // Some providers wrap the Items View in both a List and a
                    // DataGrid. Keep their outer container so grouped rows are
                    // not lost. Distinct peer content panes remain ambiguous.
                    if (found.Host == host && found.ListBounds == listBounds) continue;
                    if (IsAutomationAncestor(found.List, list)) continue;
                    if (IsAutomationAncestor(list, found.List))
                    { found = new Pane { List = list, Host = host, ListBounds = listBounds, Viewport = viewport }; continue; }
                    return null;
                }
                found = new Pane { List = list, Host = host, ListBounds = listBounds, Viewport = viewport };
            }
            if (found == null) return null;
            AutomationElementCollection chrome = view.FindAll(TreeScope.Descendants,
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Header),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ScrollBar)));
            foreach (AutomationElement element in chrome)
            {
                Rectangle bounds;
                if (element.Current.IsOffscreen || !TryRectangle(element.Current.BoundingRectangle, out bounds) ||
                    !bounds.IntersectsWith(found.Viewport)) continue;
                found.Chrome.Add(bounds);
                // Crop only explicit chrome spanning a complete viewport edge.
                // Partial chrome remains an obstacle without guessing a boundary.
                Rectangle v = found.Viewport;
                Rectangle overlap = Rectangle.Intersect(v, bounds);
                if (element.Current.ControlType == ControlType.Header && bounds.Top <= v.Top + 1 && overlap.Width >= v.Width - 1)
                    found.Viewport = Rectangle.FromLTRB(v.Left, Math.Min(v.Bottom, bounds.Bottom), v.Right, v.Bottom);
                else if (element.Current.ControlType == ControlType.ScrollBar)
                {
                    if (bounds.Right >= v.Right - 1 && overlap.Height >= v.Height - 1 && bounds.Width < bounds.Height)
                        found.Viewport = Rectangle.FromLTRB(v.Left, v.Top, Math.Max(v.Left, bounds.Left), v.Bottom);
                    else if (bounds.Bottom >= v.Bottom - 1 && overlap.Width >= v.Width - 1 && bounds.Height < bounds.Width)
                        found.Viewport = Rectangle.FromLTRB(v.Left, v.Top, v.Right, Math.Max(v.Top, bounds.Top));
                }
            }
            return found.Viewport.Width > 0 && found.Viewport.Height > 0 ? found : null;
        }

        private static IntPtr NativeAncestor(AutomationElement element, IntPtr shellView)
        {
            AutomationElement cursor = element;
            for (int depth = 0; cursor != null && depth < 32; depth++)
            {
                int native = cursor.Current.NativeWindowHandle;
                if (native != 0)
                {
                    IntPtr handle = new IntPtr(native);
                    if (!IsChildOrSelf(shellView, handle)) return IntPtr.Zero;
                    string name = ClassName(handle);
                    if (name == "DirectUIHWND" || name == "SysListView32" || name == "SHELLDLL_DefView") return handle;
                }
                cursor = TreeWalker.ControlViewWalker.GetParent(cursor);
            }
            return IntPtr.Zero;
        }

        private static bool IsAutomationAncestor(AutomationElement parent, AutomationElement child)
        {
            AutomationElement cursor = child;
            for (int depth = 0; cursor != null && depth < 24; depth++)
            {
                if (Automation.Compare(cursor, parent)) return true;
                cursor = TreeWalker.ControlViewWalker.GetParent(cursor);
            }
            return false;
        }

        private static bool IsNestedRow(AutomationElement element, AutomationElement pane)
        {
            AutomationElement cursor = TreeWalker.ControlViewWalker.GetParent(element);
            for (int depth = 0; cursor != null && depth < 16; depth++)
            {
                if (Automation.Compare(cursor, pane)) return false;
                ControlType type = cursor.Current.ControlType;
                if (type == ControlType.ListItem || type == ControlType.DataItem) return true;
                cursor = TreeWalker.ControlViewWalker.GetParent(cursor);
            }
            return true;
        }

        private static AutomationElementCollection ReadGeometryElements(Pane pane)
        {
            AutomationElementCollection items;
            CacheRequest cache = new CacheRequest();
            cache.TreeScope = TreeScope.Element;
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            cache.Add(SelectionItemPattern.IsSelectedProperty);
            using (cache.Activate())
                items = pane.List.FindAll(TreeScope.Descendants,
                    new AndCondition(new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataItem)),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            if (items.Count > MaxAccessibleItems)
                throw new InvalidOperationException("A visualização contém itens demais para uma leitura segura.");
            return items;
        }

        private static bool IsVisibleRow(AutomationElement item, Pane pane, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            return !item.Cached.IsOffscreen && TryRectangle(item.Cached.BoundingRectangle, out bounds) &&
                bounds.IntersectsWith(pane.Viewport) &&
                !(item.Cached.ControlType == ControlType.DataItem && IsNestedRow(item, pane.List));
        }

        private static List<ItemGeometry> ReadGeometry(Pane pane, List<Rectangle> occupied,
            HashSet<string> relevantNames, bool details, Stopwatch clock, out List<VisualRowAnchor> anchors)
        {
            AutomationElementCollection items = ReadGeometryElements(pane);
            anchors = new List<VisualRowAnchor>();
            List<ItemGeometry> result = new List<ItemGeometry>();
            foreach (AutomationElement item in items)
            {
                CheckReadBudget(clock);
                Rectangle bounds;
                if (!IsVisibleRow(item, pane, out bounds)) continue;
                string name = item.Cached.Name ?? String.Empty;
                object selected = item.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true);
                ItemGeometry geometry = new ItemGeometry { Name = name, Bounds = bounds, ItemBounds = bounds,
                    IsSelected = selected is bool && (bool)selected };
                anchors.Add(new VisualRowAnchor { Name = name, Bounds = bounds });
                if (relevantNames != null && relevantNames.Contains(name))
                {
                    try
                    {
                        ReadProvenNameGeometry(item, geometry);
                        if (details) ReadProvenColumnGeometry(item, geometry, pane);
                    }
                    catch (ElementNotAvailableException) { geometry.SafeToLabel = false; }
                    catch (InvalidOperationException) { geometry.SafeToLabel = false; }
                    catch (COMException) { geometry.SafeToLabel = false; }
                }
                result.Add(geometry);
                occupied.Add(bounds);
            }
            // Nonfocused Edit elements can be ordinary name fields. Only an
            // actual focused rename input invalidates the associated label.
            foreach (Rectangle editBounds in ReadFocusedEditors(pane, clock))
            {
                anchors.Add(new VisualRowAnchor { Name = String.Empty, Bounds = editBounds, Editor = true });
                occupied.Add(editBounds);
                bool associated = false;
                foreach (ItemGeometry item in result)
                {
                    if (!item.ItemBounds.IntersectsWith(editBounds)) continue;
                    item.IsEditing = true;
                    associated = true;
                }
                if (!associated) foreach (ItemGeometry item in result) item.IsEditing = true;
            }
            if (pane.NameHeader != null)
                anchors.Add(new VisualRowAnchor { Name = "\0header:name", Bounds = pane.NameHeaderBounds });
            if (pane.SizeHeader != null)
                anchors.Add(new VisualRowAnchor { Name = "\0header:size", Bounds = pane.SizeHeaderBounds });
            return result;
        }

        private static List<VisualRowAnchor> ReadGeometryAnchors(Pane pane, Stopwatch clock)
        {
            List<VisualRowAnchor> anchors = new List<VisualRowAnchor>();
            foreach (AutomationElement item in ReadGeometryElements(pane))
            {
                CheckReadBudget(clock);
                Rectangle bounds;
                if (IsVisibleRow(item, pane, out bounds))
                    anchors.Add(new VisualRowAnchor { Name = item.Cached.Name ?? String.Empty, Bounds = bounds });
            }
            foreach (Rectangle bounds in ReadFocusedEditors(pane, clock))
                anchors.Add(new VisualRowAnchor { Name = String.Empty, Bounds = bounds, Editor = true });
            AppendCurrentHeaderAnchor(anchors, pane.NameHeader, "\0header:name");
            AppendCurrentHeaderAnchor(anchors, pane.SizeHeader, "\0header:size");
            return anchors;
        }

        private static void AppendCurrentHeaderAnchor(List<VisualRowAnchor> anchors, AutomationElement header, string name)
        {
            Rectangle bounds;
            if (header != null && !header.Current.IsOffscreen && TryRectangle(header.Current.BoundingRectangle, out bounds))
                anchors.Add(new VisualRowAnchor { Name = name, Bounds = bounds });
        }

        private static List<Rectangle> ReadFocusedEditors(Pane pane, Stopwatch clock)
        {
            AutomationElementCollection editors;
            CacheRequest cache = new CacheRequest();
            cache.TreeScope = TreeScope.Element;
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.HasKeyboardFocusProperty);
            using (cache.Activate())
                editors = pane.List.FindAll(TreeScope.Descendants,
                    new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false)));
            if (editors.Count > MaxAccessibleItems)
                throw new InvalidOperationException("A visualização contém editores demais para uma leitura segura.");
            List<Rectangle> result = new List<Rectangle>();
            foreach (AutomationElement editor in editors)
            {
                CheckReadBudget(clock);
                Rectangle bounds;
                if (editor.Cached.HasKeyboardFocus && !editor.Cached.IsOffscreen &&
                    TryRectangle(editor.Cached.BoundingRectangle, out bounds) && bounds.IntersectsWith(pane.Viewport))
                    result.Add(bounds);
            }
            return result;
        }

        private static void ReadProvenNameGeometry(AutomationElement item, ItemGeometry geometry)
        {
            if (String.IsNullOrEmpty(geometry.Name)) return;
            // TextPattern on the item, when present, gives the text rectangles
            // directly. It is optional and never used to estimate missing bounds.
            Rectangle textBounds;
            uint? color;
            if (TryTextPattern(item, geometry.Name, geometry.ItemBounds, out textBounds, out color))
            { geometry.NameBounds = textBounds; geometry.TextColor = color; }
            AutomationElementCollection names;
            CacheRequest cache = new CacheRequest();
            cache.TreeScope = TreeScope.Element;
            cache.TreeFilter = Automation.RawViewCondition;
            cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.ControlTypeProperty);
            using (cache.Activate())
                names = item.FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
            AutomationElement proven = null;
            Rectangle provenBounds = Rectangle.Empty;
            foreach (AutomationElement name in names)
            {
                Rectangle bounds;
                if (name.Cached.IsOffscreen || !TryRectangle(name.Cached.BoundingRectangle, out bounds) ||
                    !geometry.ItemBounds.Contains(bounds)) continue;
                string actualName = name.Cached.Name;
                if (!String.Equals(actualName, geometry.Name, StringComparison.OrdinalIgnoreCase))
                {
                    object valueObject;
                    if (name.Cached.ControlType != ControlType.Edit || !name.TryGetCurrentPattern(ValuePattern.Pattern, out valueObject)) continue;
                    ValuePattern value = valueObject as ValuePattern;
                    if (value == null || !String.Equals(value.Current.Value, geometry.Name, StringComparison.OrdinalIgnoreCase)) continue;
                }
                if (proven != null && bounds != provenBounds)
                {
                    // Repeated identical text in distinct subitems is ambiguous.
                    proven = null;
                    provenBounds = Rectangle.Empty;
                    break;
                }
                proven = name;
                provenBounds = bounds;
            }
            if (proven == null) return;
            if (TryTextPattern(proven, geometry.Name, geometry.ItemBounds, out textBounds, out color))
            { geometry.NameBounds = textBounds; if (color.HasValue) geometry.TextColor = color; }
            else if (geometry.NameBounds.IsEmpty) geometry.NameBounds = provenBounds;

            // Publish a name cell only when UIA explicitly exposes a GridItem
            // container for this proven text. Do not infer a column from width.
            AutomationElement cell = proven;
            for (int depth = 0; cell != null && depth < 8 && !Automation.Compare(cell, item); depth++)
            {
                object gridObject;
                Rectangle cellBounds;
                if (cell.TryGetCurrentPattern(GridItemPattern.Pattern, out gridObject) &&
                    TryRectangle(cell.Current.BoundingRectangle, out cellBounds) &&
                    geometry.ItemBounds.Contains(cellBounds) && cellBounds.Contains(provenBounds))
                {
                    GridItemPattern grid = gridObject as GridItemPattern;
                    if (grid != null && grid.Current.Column >= 0 && grid.Current.ColumnSpan == 1)
                    { geometry.NameCellBounds = cellBounds; break; }
                }
                cell = TreeWalker.ControlViewWalker.GetParent(cell);
            }
        }

        private static bool TryTextPattern(AutomationElement element, string name, Rectangle itemBounds,
            out Rectangle bounds, out uint? color)
        {
            bounds = Rectangle.Empty;
            color = null;
            object patternObject;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out patternObject)) return false;
            TextPattern pattern = patternObject as TextPattern;
            if (pattern == null) return false;
            System.Windows.Automation.Text.TextPatternRange range = pattern.DocumentRange;
            string actualText = range.GetText(Math.Min(32768, name.Length * 3 + 8));
            if (actualText == null || !String.Equals(actualText.Replace("\r", String.Empty).Replace("\n", String.Empty),
                name, StringComparison.OrdinalIgnoreCase)) return false;
            System.Windows.Rect[] rectangles = range.GetBoundingRectangles();
            if (rectangles == null || rectangles.Length == 0) return false;
            foreach (System.Windows.Rect rectangle in rectangles)
            {
                Rectangle segment;
                if (!TryRectangle(rectangle, out segment) || !itemBounds.Contains(segment)) return false;
                bounds = bounds.IsEmpty ? segment : Rectangle.Union(bounds, segment);
            }
            object foreground = range.GetAttributeValue(TextPattern.ForegroundColorAttribute);
            if (foreground is int) color = unchecked((uint)(int)foreground) & 0xFFFFFF;
            return !bounds.IsEmpty;
        }

        private static void ReadColumnHeaders(Pane pane, string sizeName, string nameName, IntPtr viewWindow)
        {
            // These names were obtained from PROPERTYKEY on the Shell STA.
            if (String.IsNullOrEmpty(sizeName) && String.IsNullOrEmpty(nameName)) return;
            AutomationElement view = AutomationElement.FromHandle(viewWindow);
            AutomationElementCollection headers = view.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.HeaderItem));
            bool repeatedSize = false, repeatedName = false;
            foreach (AutomationElement header in headers)
            {
                Rectangle bounds;
                if (header.Current.IsOffscreen || !TryRectangle(header.Current.BoundingRectangle, out bounds) ||
                    bounds.Right <= pane.Viewport.Left || bounds.Left >= pane.Viewport.Right) continue;
                string caption = header.Current.Name;
                if (!String.IsNullOrEmpty(sizeName) && String.Equals(caption, sizeName, StringComparison.Ordinal))
                {
                    if (pane.SizeHeader != null) repeatedSize = true;
                    pane.SizeHeader = header;
                    pane.SizeHeaderBounds = bounds;
                }
                if (!String.IsNullOrEmpty(nameName) && String.Equals(caption, nameName, StringComparison.Ordinal))
                {
                    if (pane.NameHeader != null) repeatedName = true;
                    pane.NameHeader = header;
                    pane.NameHeaderBounds = bounds;
                }
            }
            if (repeatedSize) pane.SizeHeader = null;
            if (repeatedName) pane.NameHeader = null;
        }

        private static string ReadColumnName(IColumnManagerNative columns, uint propertyId)
        {
            PropertyKey key = new PropertyKey { FormatId = SummaryInformation, PropertyId = propertyId };
            ColumnInformation info = new ColumnInformation { Size = (uint)Marshal.SizeOf(typeof(ColumnInformation)), Mask = 0x18 };
            return columns.GetColumnInfo(ref key, ref info) >= 0 && (info.State & 1) != 0 ? info.Name : null;
        }

        private static void ReadProvenColumnGeometry(AutomationElement item, ItemGeometry geometry, Pane pane)
        {
            if (pane.SizeHeader == null && pane.NameHeader == null) return;
            AutomationElementCollection cells = item.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsTableItemPatternAvailableProperty, true));
            if (cells.Count > 64) return;
            Rectangle sizeCell = Rectangle.Empty, nameCell = Rectangle.Empty;
            bool sizeAmbiguous = false, nameAmbiguous = false;
            foreach (AutomationElement cell in cells)
            {
                Rectangle bounds;
                object tableObject;
                if (cell.Current.IsOffscreen || !TryRectangle(cell.Current.BoundingRectangle, out bounds) ||
                    !geometry.ItemBounds.Contains(bounds) || !cell.TryGetCurrentPattern(TableItemPattern.Pattern, out tableObject)) continue;
                TableItemPattern table = tableObject as TableItemPattern;
                if (table == null || table.Current.ColumnSpan != 1 || table.Current.RowSpan != 1) continue;
                AutomationElement[] headers = table.Current.GetColumnHeaderItems();
                if (headers == null || headers.Length != 1) continue;
                if (pane.SizeHeader != null && Automation.Compare(headers[0], pane.SizeHeader) && IsExplicitlyEmptyCell(cell))
                {
                    if (!sizeCell.IsEmpty && sizeCell != bounds) sizeAmbiguous = true;
                    sizeCell = bounds;
                }
                if (pane.NameHeader != null && Automation.Compare(headers[0], pane.NameHeader) &&
                    !geometry.NameBounds.IsEmpty && bounds.Contains(geometry.NameBounds))
                {
                    if (!nameCell.IsEmpty && nameCell != bounds) nameAmbiguous = true;
                    nameCell = bounds;
                }
            }
            if (!sizeAmbiguous && !sizeCell.IsEmpty &&
                (geometry.NameBounds.IsEmpty || !geometry.NameBounds.IntersectsWith(sizeCell))) geometry.SizeCellBounds = sizeCell;
            if (!nameAmbiguous && !nameCell.IsEmpty) geometry.NameCellBounds = nameCell;
        }

        private static bool IsExplicitlyEmptyCell(AutomationElement cell)
        {
            // A real, empty native cell is required. Absence of a Size cell or
            // guessing its rectangle from a column width is not evidence.
            if (!String.IsNullOrWhiteSpace(cell.Current.Name)) return false;
            object valueObject;
            if (cell.TryGetCurrentPattern(ValuePattern.Pattern, out valueObject))
            {
                ValuePattern value = valueObject as ValuePattern;
                if (value == null || !String.IsNullOrWhiteSpace(value.Current.Value)) return false;
            }
            object textObject;
            if (cell.TryGetCurrentPattern(TextPattern.Pattern, out textObject))
            {
                TextPattern text = textObject as TextPattern;
                if (text == null || !String.IsNullOrWhiteSpace(text.DocumentRange.GetText(64))) return false;
            }
            AutomationElementCollection content = cell.FindAll(TreeScope.Descendants,
                new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image)));
            foreach (AutomationElement element in content)
            {
                if (element.Current.IsOffscreen) continue;
                if (element.Current.ControlType == ControlType.Image || !String.IsNullOrWhiteSpace(element.Current.Name)) return false;
                object value;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out value) &&
                    !String.IsNullOrWhiteSpace(((ValuePattern)value).Current.Value)) return false;
            }
            return true;
        }

        private static long ReadRevision(Func<IntPtr, long> reader, IntPtr root)
        { return reader == null ? 0 : reader(root); }

        private static MetadataIndex ReadMetadataIndex(IFolderViewNative view, IShellFolderNative folder,
            string key, string parentFingerprint, long revision, int count, out int indexedItems, out bool reused)
        {
            if (metadataCaches == null) metadataCaches = new Dictionary<string, MetadataCache>(StringComparer.Ordinal);
            MetadataCache cache;
            DateTime now = DateTime.UtcNow;
            if (!metadataCaches.TryGetValue(key, out cache) || cache.ParentIdFingerprint != parentFingerprint ||
                cache.Revision != revision || cache.ExpectedCount != count)
            {
                RemoveMetadataCache(key);
                cache = new MetadataCache { ParentIdFingerprint = parentFingerprint, Revision = revision, ExpectedCount = count };
                metadataCaches.Add(key, cache);
            }
            cache.LastSeenUtc = now;
            reused = cache.Active != null && cache.Active.Complete &&
                (now - cache.Active.CompletedUtc).TotalSeconds < MetadataLifetimeSeconds;
            if (cache.Building == null && (cache.Active == null ||
                (now - cache.Active.CompletedUtc).TotalSeconds >= MetadataRefreshSeconds))
                cache.Building = NewMetadataIndex(view, count);
            if (cache.Building != null)
            {
                AdvanceMetadataIndex(cache.Building, folder, count, MetadataSliceMilliseconds);
                if (cache.Building.Complete)
                {
                    if (cache.Active != null) cache.Active.Dispose();
                    cache.Active = cache.Building;
                    cache.Building = null;
                }
            }
            indexedItems = cache.Building == null ? (cache.Active == null ? 0 : cache.Active.Count) : cache.Building.Count;
            if (cache.Active == null || !cache.Active.Complete ||
                (DateTime.UtcNow - cache.Active.CompletedUtc).TotalSeconds >= MetadataLifetimeSeconds) return null;
            return cache.Active;
        }

        private static MetadataIndex NewMetadataIndex(IFolderViewNative view, int expectedCount)
        {
            // An empty view legitimately has no enumerator in some providers.
            // Publish its empty surface so a previous folder's labels disappear.
            if (expectedCount == 0) return new MetadataIndex { Complete = true, CompletedUtc = DateTime.UtcNow };
            object enumerator = null;
            try
            {
                Guid interfaceId = EnumIdListId;
                Check(view.Items(2, ref interfaceId, out enumerator)); // SVGIO_ALLVIEW, IEnumIDList
                IEnumIdListNative typed = enumerator as IEnumIdListNative;
                if (typed == null) throw new InvalidOperationException("Esta visualização não fornece enumeração dos itens do Shell.");
                MetadataIndex index = new MetadataIndex { Enumerator = typed };
                enumerator = null; // Ownership remains on this STA until completion/invalidation.
                return index;
            }
            finally { Release(enumerator); }
        }

        private static void AdvanceMetadataIndex(MetadataIndex index, IShellFolderNative folder, int expectedCount, int milliseconds)
        {
            if (index.Complete) return;
            const int batchSize = 32;
            Stopwatch clock = Stopwatch.StartNew();
            // Native STRRET is 264 bytes in x86 and 272 in x64; a pointer-shaped
            // buffer preserves native union alignment without managed overlaps.
            IntPtr returnedName = Marshal.AllocCoTaskMem(IntPtr.Size == 8 ? 272 : 264);
            try
            {
                do
                {
                    IntPtr[] children = new IntPtr[batchSize];
                    uint fetched;
                    int hr = index.Enumerator.Next(batchSize, children, out fetched);
                    // FAILED: the COM contract says NONE of the array entries are
                    // valid. Only a successful Next establishes ownership to free.
                    Check(hr);
                    if (fetched > batchSize) throw new InvalidOperationException("O Shell devolveu uma enumeração inconsistente.");
                    try
                    {
                        for (int i = 0; i < (int)fetched; i++)
                        {
                            IntPtr child = children[i];
                            if (child == IntPtr.Zero) throw new InvalidOperationException("Um item mudou durante a identificação.");
                            string name = ReadFolderDisplayName(folder, child, returnedName);
                            if (String.IsNullOrEmpty(name)) throw new InvalidOperationException("Um item do Explorador não expôs seu nome.");
                            byte[] childId = CopyIdList(child);
                            if (index.Count >= MaxShellItems)
                                throw new InvalidOperationException("O índice desta visualização excedeu o limite de itens.");
                            ReserveMetadata(index, MetadataMemoryBudget.EstimateEntry(name.Length, childId.Length));
                            List<ShellEntry> names;
                            if (!index.Names.TryGetValue(name, out names))
                            { names = new List<ShellEntry>(); index.Names.Add(name, names); }
                            names.Add(new ShellEntry { Name = name, ChildId = childId });
                            index.Count++;
                        }
                    }
                    finally
                    {
                        for (int i = 0; i < (int)fetched; i++)
                            if (children[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(children[i]);
                    }
                    if (hr == 1 || fetched == 0)
                    {
                        if (index.Count != expectedCount)
                            throw new InvalidOperationException("A lista de arquivos mudou durante a identificação.");
                        index.Complete = true;
                        index.CompletedUtc = DateTime.UtcNow;
                        index.EndEnumeration();
                        break;
                    }
                }
                while (clock.ElapsedMilliseconds < milliseconds);
            }
            catch { index.Dispose(); throw; }
            finally { Marshal.FreeCoTaskMem(returnedName); }
        }

        private static string ReadFolderDisplayName(IShellFolderNative folder, IntPtr child, IntPtr returnedName)
        {
            IntPtr text = IntPtr.Zero;
            // uType / union storage are initialized, so the native method never
            // observes uninitialized input even in a nonstandard provider.
            Marshal.WriteInt32(returnedName, 0);
            Marshal.WriteIntPtr(returnedName, IntPtr.Size == 8 ? 8 : 4, IntPtr.Zero);
            Check(folder.GetDisplayNameOf(child, 0, returnedName)); // SHGDN_NORMAL
            try
            {
                Check(StrRetToStrW(returnedName, child, out text));
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text);
            }
            finally { if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text); }
        }

        private static void ResolvePhysicalFolder(ShellEntry entry, IShellFolderNative folder, MetadataIndex index)
        {
            if (!entry.Resolution.TryBegin(DateTime.UtcNow)) return;
            IntPtr child = Marshal.AllocCoTaskMem(entry.ChildId.Length);
            IShellItemNative item = null;
            try
            {
                Marshal.Copy(entry.ChildId, 0, child, entry.ChildId.Length);
                Guid itemId = ShellItemId;
                // A temporarily unavailable item must not discard the valid
                // mapping of every other folder in this window. This entry
                // remains pending and can be retried after a short interval.
                if (SHCreateItemWithParent(IntPtr.Zero, folder, child, ref itemId, out item) < 0 || item == null) return;
                // Confirm that the saved child PIDL still exposes the same name;
                // invalidation epochs and the final parent check guard its scope.
                string name = ReadItemName(item, 0);
                if (String.IsNullOrEmpty(name)) return;
                if (!String.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Um nome mudou durante a leitura da pasta.");
                uint attributes;
                if (item.GetAttributes(SfgaoFolder | SfgaoFileSystem | SfgaoLink | SfgaoStream, out attributes) < 0) return;
                if ((attributes & (SfgaoFolder | SfgaoFileSystem)) != (SfgaoFolder | SfgaoFileSystem) ||
                    (attributes & (SfgaoLink | SfgaoStream)) != 0)
                {
                    entry.Resolution.ExcludeNonFolder();
                    return;
                }
                string path = ReadItemName(item, SigdnFileSystemPath);
                if (String.IsNullOrEmpty(path)) return;
                try
                {
                    if (Path.IsPathRooted(path))
                    {
                        ReserveMetadata(index, 32L + path.Length * 2L);
                        entry.Resolution.AcceptFolder(path);
                    }
                }
                catch (ArgumentException) { /* A malformed provider path stays pending too. */ }
            }
            finally { Release(item); Marshal.FreeCoTaskMem(child); }
        }

        private static void ReserveMetadata(MetadataIndex index, long bytes)
        {
            if (bytes > PerIndexMemoryLimit - index.Bytes)
                throw new InvalidOperationException("O índice desta visualização atingiu o limite estimado de 48 MiB.");
            while (!IndexMemory.TryReserve(bytes))
            {
                // Completed views get a short residence lease; unfinished views
                // get longer to finish their incremental enumeration. Under real
                // pressure the oldest eligible resident leaves, so new windows
                // can make progress without an unbounded sum of per-view limits.
                string victim = null;
                DateTime oldest = DateTime.MaxValue;
                DateTime now = DateTime.UtcNow;
                if (metadataCaches != null)
                    foreach (KeyValuePair<string, MetadataCache> pair in metadataCaches)
                    {
                        MetadataCache candidate = pair.Value;
                        if (candidate.Active == index || candidate.Building == index) continue;
                        long owned = (candidate.Active == null ? 0 : candidate.Active.Bytes) +
                            (candidate.Building == null ? 0 : candidate.Building.Bytes);
                        if (owned == 0) continue;
                        double lease = candidate.Active == null && candidate.Building != null ? 20d : 5d;
                        if ((now - candidate.ResidentSinceUtc).TotalSeconds < lease &&
                            (now - candidate.LastSeenUtc).TotalSeconds < 5d) continue;
                        if (candidate.LastSeenUtc < oldest)
                        { oldest = candidate.LastSeenUtc; victim = pair.Key; }
                    }
                if (victim == null)
                    throw new InvalidOperationException("O orçamento global dos índices está ocupado; aguardando uma nova janela de leitura.");
                RemoveMetadataCache(victim);
            }
            index.Bytes += bytes;
        }

        private static void RemoveMetadataCache(string key)
        {
            MetadataCache cache;
            if (key == null || metadataCaches == null || !metadataCaches.TryGetValue(key, out cache)) return;
            metadataCaches.Remove(key);
            cache.Dispose();
        }

        private static void PruneMetadataCaches()
        {
            if (metadataCaches == null) return;
            DateTime now = DateTime.UtcNow;
            List<string> expired = new List<string>();
            foreach (KeyValuePair<string, MetadataCache> pair in metadataCaches)
                if ((now - pair.Value.LastSeenUtc).TotalSeconds > 30) expired.Add(pair.Key);
            foreach (string key in expired) RemoveMetadataCache(key);
            // Preserve enough indices for every window this probe accepts. A
            // smaller cap repeatedly evicts the first active window's unfinished
            // index on each capture, so that window can never finish indexing.
            // Old folder/tab identities still leave first through LastSeenUtc.
            while (metadataCaches.Count > MaxWindows)
            {
                string oldestKey = null;
                DateTime oldest = DateTime.MaxValue;
                foreach (KeyValuePair<string, MetadataCache> pair in metadataCaches)
                    if (pair.Value.LastSeenUtc < oldest) { oldest = pair.Value.LastSeenUtc; oldestKey = pair.Key; }
                if (oldestKey == null) break;
                RemoveMetadataCache(oldestKey);
            }
        }

        // Call from the same discovery STA when that worker exits. COM enumerator
        // ownership never crosses onto the renderer or its callbacks.
        public static void ReleaseThreadCache()
        {
            knownWindowRoots = null;
            if (metadataCaches == null) return;
            foreach (MetadataCache cache in metadataCaches.Values) cache.Dispose();
            metadataCaches.Clear();
            metadataCaches = null;
        }

        private static byte[] CopyIdList(IntPtr idList)
        {
            uint size = ILGetSize(idList);
            if (size < 2 || size > 1024 * 1024) throw new InvalidOperationException("Identidade de item inválida.");
            byte[] value = new byte[(int)size];
            Marshal.Copy(idList, value, 0, value.Length);
            return value;
        }

        private static string ReadItemName(IShellItemNative item, uint kind)
        {
            IntPtr name = IntPtr.Zero;
            try { return item.GetDisplayName(kind, out name) >= 0 && name != IntPtr.Zero ? Marshal.PtrToStringUni(name) : null; }
            finally { if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name); }
        }
        private static string ReadAbsoluteName(IntPtr itemId, uint kind)
        {
            IntPtr name = IntPtr.Zero;
            try { return SHGetNameFromIDList(itemId, kind, out name) >= 0 && name != IntPtr.Zero ? Marshal.PtrToStringUni(name) : null; }
            finally { if (name != IntPtr.Zero) Marshal.FreeCoTaskMem(name); }
        }
        private static string Fingerprint(IntPtr parentId)
        {
            byte[] value = CopyIdList(parentId); // Our own COM-allocated PIDL, never remote memory.
            using (SHA256 hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", String.Empty);
        }
        private static string Fingerprint(string parsingIdentity)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(parsingIdentity))).Replace("-", String.Empty);
        }

        private static bool IsExplorerRoot(IntPtr root)
        {
            if (!IsExplorerRootVisible(root)) return false;
            uint process;
            GetWindowThreadProcessId(root, out process);
            if (process == 0) return false;
            IntPtr handle = OpenProcess(0x1000, false, process); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero) return false;
            try
            {
                StringBuilder image = new StringBuilder(32768);
                uint length = (uint)image.Capacity;
                if (!QueryFullProcessImageName(handle, 0, image, ref length)) return false;
                string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                return String.Equals(image.ToString(), expected, StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseHandle(handle); }
        }
        private static bool IsExplorerRootVisible(IntPtr root)
        {
            if (root == IntPtr.Zero || !IsWindow(root) || !IsWindowVisible(root) || IsIconic(root)) return false;
            string name = ClassName(root);
            if (name != "CabinetWClass" && name != "ExploreWClass") return false;
            try { int cloaked; if (DwmGetWindowAttribute(root, 14, out cloaked, 4) == 0 && cloaked != 0) return false; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            return true;
        }
        private static bool ValidHost(IntPtr host, IntPtr shellView, IntPtr root)
        {
            if (host == IntPtr.Zero || !IsWindowVisible(host) || !BelongsToRoot(host, root) || !IsChildOrSelf(shellView, host)) return false;
            string name = ClassName(host);
            return name == "DirectUIHWND" || name == "SysListView32" || name == "SHELLDLL_DefView";
        }
        private static bool BelongsToRoot(IntPtr host, IntPtr root)
        {
            if (GetAncestor(host, 2) != root) return false;
            uint hostProcess, rootProcess;
            GetWindowThreadProcessId(host, out hostProcess);
            GetWindowThreadProcessId(root, out rootProcess);
            return hostProcess != 0 && hostProcess == rootProcess;
        }
        private static bool IsChildOrSelf(IntPtr parent, IntPtr child) { return parent == child || IsChild(parent, child); }
        private static string ClassName(IntPtr window)
        {
            StringBuilder name = new StringBuilder(256);
            return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : String.Empty;
        }
        private static Rectangle PhysicalClientBounds(IntPtr window)
        {
            NativeRect bounds;
            if (!GetClientRect(window, out bounds)) return Rectangle.Empty;
            NativePoint first = new NativePoint { X = bounds.Left, Y = bounds.Top };
            NativePoint last = new NativePoint { X = bounds.Right, Y = bounds.Bottom };
            if (!ClientToScreen(window, ref first) || !ClientToScreen(window, ref last)) return Rectangle.Empty;
            return Rectangle.FromLTRB(first.X, first.Y, last.X, last.Y);
        }
        private static bool TryRectangle(System.Windows.Rect value, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (value.IsEmpty || Double.IsNaN(value.X) || Double.IsNaN(value.Y) || Double.IsNaN(value.Width) || Double.IsNaN(value.Height) ||
                Double.IsInfinity(value.X) || Double.IsInfinity(value.Y) || Double.IsInfinity(value.Width) || Double.IsInfinity(value.Height) ||
                value.Width <= 0 || value.Height <= 0 || Math.Abs(value.X) > 1000000 || Math.Abs(value.Y) > 1000000 ||
                value.Width > 1000000 || value.Height > 1000000) return false;
            bounds = Rectangle.FromLTRB((int)Math.Floor(value.Left), (int)Math.Floor(value.Top), (int)Math.Ceiling(value.Right), (int)Math.Ceiling(value.Bottom));
            return bounds.Width > 0 && bounds.Height > 0;
        }
        private static int ReadDpi(IntPtr window)
        {
            try { uint dpi = GetDpiForWindow(window); if (dpi >= 48 && dpi <= 960) return (int)dpi; }
            catch (EntryPointNotFoundException) { }
            return 96;
        }
        private static uint ReadFallbackTextColor(IntPtr window)
        {
            if (ClassName(window) == "SysListView32")
            {
                UIntPtr result;
                if (SendMessageTimeout(window, 0x1023, IntPtr.Zero, IntPtr.Zero, 3, 150, out result) != IntPtr.Zero)
                    return unchecked((uint)result.ToUInt64());
            }
            uint systemColor = GetSysColor(8); // COLOR_WINDOWTEXT
            if (SystemInformation.HighContrast) return systemColor;
            try
            {
                // Preference fallback only; a proven UIA foreground color wins.
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object light = key == null ? null : key.GetValue("AppsUseLightTheme");
                    if (light is int && (int)light == 0) return 0xFFFFFF;
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            return systemColor;
        }
        private static IntPtr EnterPhysicalCoordinates()
        {
            try { IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); return previous != IntPtr.Zero ? previous : SetThreadDpiAwarenessContext(new IntPtr(-3)); }
            catch (EntryPointNotFoundException) { return IntPtr.Zero; }
        }
        private static void RestoreDpiContext(IntPtr previous)
        {
            if (previous == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(previous); }
            catch (EntryPointNotFoundException) { }
        }
        private static string ViewModeName(uint mode)
        {
            switch (mode) { case 1: return "ícones"; case 2: return "ícones pequenos"; case 3: return "lista"; case 4: return "detalhes";
                case 5: return "miniaturas"; case 6: return "blocos"; case 7: return "película"; case 8: return "conteúdo"; default: return mode.ToString(CultureInfo.InvariantCulture); }
        }
        private static string BriefError(Exception exception)
        {
            if (exception is COMException) return "O Explorer atualizou ou interrompeu a leitura (" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture) + ").";
            if (exception is ElementNotAvailableException) return "A visualização mudou durante a leitura.";
            return exception.Message;
        }
        private static void CheckReadBudget(Stopwatch clock)
        {
            // This bounds cooperative work; a single stalled COM/UIA call cannot
            // be interrupted here. The caller's snapshot TTL protects rendering.
            if (clock.ElapsedMilliseconds > 1500)
                throw new InvalidOperationException("A consulta desta pasta demorou mais que 1,5 s (" +
                    clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms); aguardando o Explorador responder.");
        }
        private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
        private static void Release(object value)
        {
            if (value == null || !Marshal.IsComObject(value)) return;
            try { Marshal.ReleaseComObject(value); } catch (InvalidComObjectException) { }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid FormatId; public uint PropertyId; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ColumnInformation
        {
            public uint Size, Mask, State, Width, DefaultWidth, IdealWidth;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Name;
        }
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, uint size);
        [DllImport("shell32.dll")] private static extern int SHCreateItemWithParent(IntPtr parentId, [MarshalAs(UnmanagedType.Interface)] IShellFolderNative folder,
            IntPtr childId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItemNative item);
        [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr itemId, uint kind, out IntPtr name);
        [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr itemId);
        [DllImport("shell32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ILIsEqual(IntPtr first, IntPtr second);
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrRetToStrW(IntPtr returnedName, IntPtr childId, out IntPtr value);

        [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        private interface IShellWindowsDispatch
        {
            int Count { get; }
            [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
        }
        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IComServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object value);
        }
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
        }
        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolderNative
        {
            [PreserveSig] int ParseDisplayName(IntPtr window, IntPtr context, [MarshalAs(UnmanagedType.LPWStr)] string name,
                IntPtr charactersParsed, out IntPtr child, IntPtr attributes);
            [PreserveSig] int EnumObjects(IntPtr window, uint flags, out IntPtr enumerator);
            [PreserveSig] int BindToObject(IntPtr child, IntPtr context, ref Guid interfaceId, out IntPtr value);
            [PreserveSig] int BindToStorage(IntPtr child, IntPtr context, ref Guid interfaceId, out IntPtr value);
            [PreserveSig] int CompareIDs(IntPtr flags, IntPtr first, IntPtr second);
            [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid interfaceId, out IntPtr view);
            [PreserveSig] int GetAttributesOf(uint count, IntPtr children, ref uint attributes);
            [PreserveSig] int GetUIObjectOf(IntPtr owner, uint count, IntPtr children, ref Guid interfaceId, IntPtr reserved, out IntPtr value);
            [PreserveSig] int GetDisplayNameOf(IntPtr child, uint flags, IntPtr returnedName);
        }
        [ComImport, Guid("000214F2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumIdListNative
        {
            [PreserveSig] int Next(uint requested, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] children, out uint fetched);
            [PreserveSig] int Skip(uint count);
            [PreserveSig] int Reset();
            [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumIdListNative clone);
        }
        [ComImport, Guid("D8EC27BB-3F3B-4042-B10A-4ACFD924D453"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IColumnManagerNative
        {
            [PreserveSig] int SetColumnInfo(ref PropertyKey key, ref ColumnInformation info);
            [PreserveSig] int GetColumnInfo(ref PropertyKey key, ref ColumnInformation info);
        }
        [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFolder2Native
        {
            [PreserveSig] int GetClassID(out Guid classId);
            [PreserveSig] int Initialize(IntPtr parentId);
            [PreserveSig] int GetCurFolder(out IntPtr parentId);
        }
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemNative
        {
            [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr value);
            [PreserveSig] int GetParent([MarshalAs(UnmanagedType.Interface)] out IShellItemNative parent);
            [PreserveSig] int GetDisplayName(uint kind, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attributes);
            [PreserveSig] int Compare(IShellItemNative other, uint hint, out int order);
        }
    }
}
