using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using PastaGB;

[assembly: AssemblyTitle("Pasta GB Ícones")]
[assembly: AssemblyDescription("Tamanhos junto às pastas no desktop e no Explorador de Arquivos")]
[assembly: AssemblyProduct("Pasta GB Ícones")]
[assembly: AssemblyVersion("5.1.0.0")]
[assembly: AssemblyFileVersion("5.1.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace PastaGBIcons
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (ScanWorker.TryRun(args)) return;
            bool first;
            // Keep the v2 mutex so the desktop version cannot run over the new one.
            using (var mutex = new Mutex(true, "Local\\PastaGB.Icones.2", out first))
            {
                if (!first)
                {
                    MessageBox.Show("Uma versão do Pasta GB Ícones já está ativa. Use “Encerrar” no ícone perto do relógio e abra esta versão novamente.", "Pasta GB Ícones 5.1");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { using (var context = new IconContext()) Application.Run(context); }
                catch (Exception ex)
                {
                    MessageBox.Show("Não foi possível iniciar a exibição junto às pastas.\n\n" + ex.Message,
                        "Pasta GB Ícones", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }

    internal sealed class ViewState
    {
        internal DesktopSnapshot Desktop;
        internal DateTime CapturedUtc;
    }

    internal sealed class SurfaceState
    {
        internal string Key;
        internal DesktopSnapshot Snapshot;
        internal ExplorerSurface Explorer;
        internal long Revision;
    }

    internal sealed class LabelEntry
    {
        internal LayeredLabel Label;
        internal IntPtr Root;
        internal string Path;
        internal DateTime LastUsedUtc;
        internal SizeCaptionCache Caption;
    }

    internal sealed class ScanWork
    {
        internal string Path;
        internal ProcessScanSession Session;
        internal int Generation;
        internal int PolicyGeneration;
        internal long[] VolumeStamp;
        internal DateTime LastWantedUtc;
        internal SizeRecord Previous;
        internal bool CancelRequested;
        internal bool ProtectedFirstRead;
        internal bool ChangedDuringRead;
        internal long ChangeRevision;
    }

    internal sealed partial class IconContext : ApplicationContext
    {
        private readonly object stateGate = new object();
        private ViewState view;
        private readonly Dictionary<IntPtr, ExplorerSurface> explorerSurfaces = new Dictionary<IntPtr, ExplorerSurface>();
        private ExplorerCaptureResult lastExplorerCapture;
        private Dictionary<string, SizeRecord> sizes = new Dictionary<string, SizeRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LabelEntry> labels = new Dictionary<string, LabelEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly LayoutPlanCache layoutPlans = new LayoutPlanCache();
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer renderTimer;
        private readonly ToolStripMenuItem statusItem, pauseItem, startupItem, explorerItem;
        private readonly FolderEventTracker events;
        private readonly VolumeRevisions volumes = new VolumeRevisions();
        private readonly VolumeChangeMonitor volumeMonitor;
        private readonly FolderChangeTracker changes;
        private readonly List<ManagedWorker> workers = new List<ManagedWorker>();
        private readonly Queue<string> recentEvents = new Queue<string>();
        private readonly Dictionary<string, int> suppressionReasons = new Dictionary<string, int>();
        private AppPreferences preferences;
        private int policyGeneration;
        private string preferenceError;
        private volatile bool stopped, paused;
        private volatile bool explorerEnabled = true;
        private int refreshGeneration;
        private bool announced;
        private int displayedDesktop, displayedExplorer, suppressed;
        private string renderError, desktopError, explorerError, scannerError;
        private string scannerActivity = "Aguardando identificação das pastas.";
        private DateTime nextTrayUpdate = DateTime.MinValue;
        private readonly DateTime startedUtc = DateTime.UtcNow;
        private const int MaximumLabelObjects = 1024;

        internal IconContext()
        {
            preferences = AppPreferences.Load(AppPreferences.DefaultPath, out preferenceError);
            var menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("Procurando pastas no desktop e no Explorador…") { Enabled = false };
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Atualizar tamanhos agora", null, delegate { Interlocked.Increment(ref refreshGeneration); });
            explorerItem = new ToolStripMenuItem("Mostrar no Explorador de Arquivos") { CheckOnClick = true, Checked = true };
            explorerItem.CheckedChanged += delegate
            {
                explorerEnabled = explorerItem.Checked;
                if (!explorerEnabled)
                {
                    lock (stateGate) { explorerSurfaces.Clear(); }
                    HideExplorer(IntPtr.Zero);
                }
            };
            menu.Items.Add(explorerItem);
            pauseItem = new ToolStripMenuItem("Pausar exibição") { CheckOnClick = true };
            pauseItem.CheckedChanged += delegate { paused = pauseItem.Checked; if (paused) { HideAll(); layoutPlans.Clear(); } };
            menu.Items.Add(pauseItem);
            startupItem = new ToolStripMenuItem("Iniciar com o Windows") { CheckOnClick = false, Checked = ReadStartup() };
            startupItem.Click += delegate { ChangeStartup(); };
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Preferências…", null, delegate { ShowPreferences(); });
            menu.Items.Add("Como funciona · versão 5.1", null, delegate { ShowHelp(); });
            menu.Items.Add("Copiar diagnóstico", null, delegate { CopyDiagnostic(); });
            menu.Items.Add("Encerrar", null, delegate { ExitThread(); });
            tray = new NotifyIcon
            {
                Icon = SystemIcons.Information,
                Text = "Pasta GB Ícones 5.1 · iniciando",
                Visible = true,
                ContextMenuStrip = menu
            };
            tray.DoubleClick += delegate { ShowHelp(); };
            events = new FolderEventTracker();
            events.Changed += HideExplorer;
            volumeMonitor = new VolumeChangeMonitor(volumes);
            volumeMonitor.Changed += VolumesChanged;
            changes = new FolderChangeTracker();

            StartWorker(DiscoverDesktopLoop, "PastaGB.DesktopProbe", true, false);
            StartWorker(DiscoverExplorerLoop, "PastaGB.ExplorerProbe", true, false);
            StartWorker(ScanLoop, "PastaGB.SizeCoordinator", false, true);
            renderTimer = new System.Windows.Forms.Timer { Interval = 125 };
            renderTimer.Tick += delegate { Render(); };
            renderTimer.Start();
        }

        private void StartWorker(ThreadStart action, string name, bool sta, bool scanner)
        {
            workers.Add(new ManagedWorker(name, action, delegate(string message)
            {
                RecordEvent("Recuperação de " + name + ": " + message);
                if (scanner)
                {
                    lock (stateGate)
                    {
                        scannerError = message;
                        var recovering = new Dictionary<string, SizeRecord>(StringComparer.OrdinalIgnoreCase);
                        foreach (var pair in sizes)
                        {
                            SizeRecord value = pair.Value.Copy();
                            value.Phase = value.HasValue ? SizePhase.Stale : SizePhase.Unavailable;
                            value.Detail = "O cálculo será retomado após uma falha: " + message;
                            value.NextAttemptUtc = DateTime.UtcNow.AddSeconds(2);
                            recovering[pair.Key] = value;
                        }
                        sizes = recovering;
                    }
                }
            }, delegate { return stopped; }, sta));
        }

        private AppPreferences PreferencesSnapshot()
        { lock (stateGate) { return preferences; } }

        private void RecordEvent(string message)
        {
            lock (stateGate)
            {
                recentEvents.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " +
                    (message.Length > 600 ? message.Substring(0, 600) : message));
                while (recentEvents.Count > 100) recentEvents.Dequeue();
            }
        }

        private void ShowPreferences()
        {
            using (var dialog = new PreferencesDialog(PreferencesSnapshot()))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                AppPreferences next = dialog.Result;
                try { next.Save(AppPreferences.DefaultPath); }
                catch (Exception ex)
                { MessageBox.Show("Não foi possível salvar as preferências.\n\n" + ex.Message,
                    "Pasta GB", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                lock (stateGate)
                {
                    if (preferences.FollowDirectoryLinks != next.FollowDirectoryLinks ||
                        preferences.CrossVolumeLinks != next.CrossVolumeLinks)
                    {
                        policyGeneration++;
                        sizes = new Dictionary<string, SizeRecord>(StringComparer.OrdinalIgnoreCase);
                        Interlocked.Increment(ref refreshGeneration);
                    }
                    preferences = next; preferenceError = null;
                }
                RecordEvent("Preferências atualizadas.");
                Render();
            }
        }

        private void DiscoverDesktopLoop()
        {
            // Independent of Explorer: opening a slow folder cannot stall desktop labels.
            while (!stopped)
            {
                if (!paused)
                {
                    try
                    {
                        DesktopSnapshot snapshot = DesktopProbe.Capture();
                        lock (stateGate)
                        {
                            view = new ViewState { Desktop = snapshot, CapturedUtc = DateTime.UtcNow };
                            desktopError = snapshot.Status;
                        }
                    }
                    catch (Exception ex) { lock (stateGate) { view = null; desktopError = ex.Message; } }
                }
                Thread.Sleep(PreferencesSnapshot().DesktopInterval);
            }
        }

        private void DiscoverExplorerLoop()
        {
            try
            {
                while (!stopped)
                {
                    if (!paused && explorerEnabled)
                    {
                        try
                        {
                            // Publish each window immediately; a bounded adaptive
                            // drawing lease also tolerates longer capture rounds.
                            ExplorerCaptureResult capture = ExplorerProbe.Capture(
                                events.ReadStructureVersion, events.ReadGeometryVersion, PublishExplorerSurface,
                                ObserveExplorerIdentity);
                            lock (stateGate)
                            {
                                lastExplorerCapture = capture;
                                explorerError = capture.Status;
                                foreach (IntPtr root in explorerSurfaces.Keys.ToList())
                                    if (!IsWindow(root) || (DateTime.UtcNow - explorerSurfaces[root].CapturedUtc).TotalSeconds > 30)
                                        explorerSurfaces.Remove(root);
                            }
                        }
                        catch (Exception ex) { lock (stateGate) { explorerError = ex.Message; } }
                    }
                    Thread.Sleep(PreferencesSnapshot().ExplorerInterval);
                }
            }
            finally { ExplorerProbe.ReleaseThreadCache(); }
        }

        private void PublishExplorerSurface(ExplorerSurface surface)
        {
            if (surface == null || surface.Snapshot == null || stopped || !explorerEnabled) return;
            events.RegisterSurface(surface.RootHandle, surface.ShellViewHandle, surface.Snapshot.HostHandle);
            // Empty and still-indexing views replace the previous folder too.
            // Geometry revisions govern drawing, not the lifetime of a size read.
            lock (stateGate) { explorerSurfaces[surface.RootHandle] = surface; }
        }

        private void ObserveExplorerIdentity(ExplorerSurface identity)
        {
            if (identity == null || stopped || !explorerEnabled) return;
            events.RegisterSurface(identity.RootHandle, identity.ShellViewHandle, IntPtr.Zero);
            lock (stateGate)
            {
                ExplorerSurface current;
                if (!explorerSurfaces.TryGetValue(identity.RootHandle, out current) ||
                    current.ShellViewHandle != identity.ShellViewHandle || current.Fingerprint != identity.Fingerprint)
                    explorerSurfaces[identity.RootHandle] = identity;
            }
        }

        private void VolumesChanged(uint letters)
        {
            // The receiver has already incremented each affected media epoch.
            // Reject late reads as well as cached values for a reused drive letter.
            foreach (LabelEntry entry in labels.Values)
            {
                SizeRecord record;
                lock (stateGate) { sizes.TryGetValue(entry.Path, out record); }
                if (AffectedDrive(entry.Path, letters) || (record != null &&
                    record.Dependencies.Any(x => AffectedDrive(x, letters)))) entry.Label.Hide();
            }
            RecordEvent("Unidade conectada/removida; resultados dependentes serão revalidados.");
            var roots = new List<IntPtr>();
            lock (stateGate)
            {
                foreach (ExplorerSurface surface in explorerSurfaces.Values)
                    if (AffectedDrive(surface.Location, letters) || (surface.Snapshot != null &&
                        surface.Snapshot.Folders.Any(x => AffectedDrive(x.Path, letters))))
                        roots.Add(surface.RootHandle);
                foreach (IntPtr root in roots) explorerSurfaces.Remove(root);
            }
            foreach (IntPtr root in roots) events.Invalidate(root);
        }

        private static bool AffectedDrive(string path, uint letters)
        {
            int index = VolumeRevisions.DriveIndex(path);
            return index >= 0 && (letters & (1u << index)) != 0;
        }

        private List<SurfaceState> CurrentSurfaces(bool forDrawing)
        {
            ViewState desktop;
            List<ExplorerSurface> explorer;
            lock (stateGate) { desktop = view; explorer = explorerSurfaces.Values.ToList(); }
            DateTime now = DateTime.UtcNow;
            double drawingLease = ExplorerDrawingLeaseSeconds();
            var result = new List<SurfaceState>();
            if (desktop != null && desktop.Desktop != null && desktop.Desktop.Visible &&
                (now - desktop.CapturedUtc).TotalSeconds < (forDrawing ? 2 : 15))
                result.Add(new SurfaceState { Key = "desktop", Snapshot = desktop.Desktop });
            if (explorerEnabled)
            {
                foreach (var surface in explorer)
                {
                    if (surface.Snapshot == null || !surface.Snapshot.Visible ||
                        (now - surface.CapturedUtc).TotalSeconds > (forDrawing ? drawingLease : 15) ||
                        !IsWindow(surface.RootHandle) || !IsWindowVisible(surface.Snapshot.HostHandle) ||
                        IsIconic(surface.RootHandle)) continue;
                    if (forDrawing && events.ReadGeometryVersion(surface.RootHandle) != surface.GeometryRevision) continue;
                    result.Add(new SurfaceState
                    {
                        Key = "explorer:" + surface.Key, Snapshot = surface.Snapshot,
                        Explorer = surface, Revision = surface.GeometryRevision
                    });
                }
            }
            return result;
        }

        private double ExplorerDrawingLeaseSeconds()
        {
            // A fixed timeout shorter than one complete pass creates periodic
            // blank intervals. Native geometry events and confirmed identity
            // changes still invalidate immediately, irrespective of this lease.
            lock (stateGate)
                return lastExplorerCapture == null ? 2.5 :
                    Math.Max(2.5, Math.Min(8, lastExplorerCapture.CaptureMilliseconds / 1000d + 1));
        }

        private static bool CanPlaceForMode(SurfaceState surface, string path)
        {
            if (surface.Explorer == null) return true;
            uint mode = surface.Explorer.ViewMode;
            if (mode == 1 || mode == 5) return true;
            if (mode == 4 && surface.Explorer.SizeCellBounds.ContainsKey(path)) return true;
            return (mode == 2 || mode == 3 || mode == 4) &&
                surface.Explorer.NameBounds.ContainsKey(path) && surface.Explorer.NameCellBounds.ContainsKey(path);
        }

        private void PublishSizes(Dictionary<string, SizeRecord> source, int expectedPolicy)
        {
            // State objects are replaced, never changed after publication.
            // A late round must not restore totals computed with old link settings.
            lock (stateGate)
                if (policyGeneration == expectedPolicy && !stopped)
                    sizes = new Dictionary<string, SizeRecord>(source, StringComparer.OrdinalIgnoreCase);
        }

        private bool SurfaceCurrent(SurfaceState surface)
        {
            if (surface.Explorer == null)
            {
                lock (stateGate)
                    return view != null && view.Desktop != null && view.Desktop.Visible &&
                        view.Desktop.HostHandle == surface.Snapshot.HostHandle &&
                        !String.IsNullOrEmpty(surface.Snapshot.GeometryStamp) &&
                        view.Desktop.GeometryStamp == surface.Snapshot.GeometryStamp;
            }
            if (!explorerEnabled || events.ReadGeometryVersion(surface.Explorer.RootHandle) != surface.Revision)
                return false;
            lock (stateGate)
            {
                ExplorerSurface current;
                return explorerSurfaces.TryGetValue(surface.Explorer.RootHandle, out current) &&
                    current.Key == surface.Explorer.Key && current.Snapshot != null && current.Snapshot.Visible &&
                    current.Snapshot.HostHandle == surface.Snapshot.HostHandle &&
                    !String.IsNullOrEmpty(surface.Snapshot.GeometryStamp) &&
                    current.Snapshot.GeometryStamp == surface.Snapshot.GeometryStamp;
            }
        }

        private void Render()
        {
            if (stopped) return;
            if (paused)
            {
                HideAll(); displayedDesktop = displayedExplorer = suppressed = 0; UpdateTray(); return;
            }
            Dictionary<string, SizeRecord> values;
            AppPreferences appearance;
            int appearancePolicy;
            lock (stateGate) { values = sizes; appearance = preferences; appearancePolicy = policyGeneration; }
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            displayedDesktop = displayedExplorer = suppressed = 0;
            suppressionReasons.Clear();
            renderError = null;
            DateTime now = DateTime.UtcNow;
            IntPtr foreground = GetForegroundWindow();
            foreach (var surface in CurrentSurfaces(true).OrderBy(x =>
                x.Explorer != null && x.Explorer.RootHandle == foreground ? 0 : 1))
            {
                var snapshot = surface.Snapshot;
                if (!SurfaceCurrent(surface)) continue;
                LabelPlacement[] plan = layoutPlans.GetOrCreate(surface.Key, snapshot.GeometryStamp,
                    surface.Explorer == null ? 0 : surface.Explorer.ViewMode, appearance.TextScale,
                    snapshot.Folders, now, delegate(DesktopFolderIcon folder, IList<Rectangle> used)
                    { return PlanPlacement(surface, folder, used, appearance.TextScale); });
                for (int itemIndex = 0; itemIndex < snapshot.Folders.Count; itemIndex++)
                {
                    if (!SurfaceCurrent(surface)) break;
                    DesktopFolderIcon item = snapshot.Folders[itemIndex];
                    LabelPlacement placement = plan[itemIndex];
                    if (!placement.Placed) { Suppress(placement.SuppressionReason ?? "espaço insuficiente ou conflito entre células"); continue; }
                    Rectangle bounds = placement.Bounds;
                    bool compact = placement.Compact, alignRight = placement.AlignRight;
                    SizeRecord value;
                    if (!values.TryGetValue(item.Path, out value) || !MediaCurrent(value)) value = null;
                    bool shortCaption = compact || bounds.Width * 96d / Math.Max(72, snapshot.Dpi) /
                        appearance.TextScale < 150;
                    string key = placement.LabelKey;
                    LabelEntry entry;
                    if (!labels.TryGetValue(key, out entry))
                    {
                        if (labels.Count >= MaximumLabelObjects)
                        {
                            // Earlier surfaces have priority. Labels from a later
                            // surface may yield even if still visible from last frame.
                            string idle = labels.Where(x => !live.Contains(x.Key))
                                .OrderBy(x => x.Value.Root == foreground ? 1 : 0)
                                .ThenBy(x => x.Value.Label.IsShown ? 1 : 0)
                                .ThenBy(x => x.Value.LastUsedUtc).Select(x => x.Key).FirstOrDefault();
                            if (idle != null) { labels[idle].Label.Dispose(); labels.Remove(idle); }
                            else { Suppress("limite de etiquetas simultâneas"); continue; }
                        }
                        entry = new LabelEntry { Label = new LayeredLabel(),
                            Root = surface.Explorer == null ? IntPtr.Zero : surface.Explorer.RootHandle, Path = item.Path };
                        labels.Add(key, entry);
                    }
                    try
                    {
                        string caption = entry.Caption.Get(value, appearance.Units, shortCaption);
                        entry.Label.CompactBadge = compact;
                        entry.Label.AlignRight = alignRight;
                        entry.Label.Theme = (LabelTheme)(int)appearance.Theme;
                        entry.Label.TextScale = appearance.TextScale;
                        if (surface.Explorer == null)
                            entry.Label.Update(snapshot.HostHandle, bounds, caption, snapshot.TextColor, snapshot.Dpi);
                        else
                            entry.Label.Update(snapshot.HostHandle, bounds, caption, snapshot.TextColor, snapshot.Dpi,
                                snapshot.HostScreenBounds, surface.Explorer.RootHandle);
                        if (!SurfaceCurrent(surface) || (value != null && !MediaCurrent(value)))
                        { entry.Label.Hide(); break; }
                        lock (stateGate)
                            if (policyGeneration != appearancePolicy) { entry.Label.Hide(); break; }
                        entry.LastUsedUtc = now;
                        live.Add(key);
                        if (entry.Label.IsShown)
                        {
                            if (surface.Explorer == null) displayedDesktop++;
                            else displayedExplorer++;
                        }
                    }
                    catch (Exception ex) { entry.Label.Hide(); renderError = ex.Message; Suppress("falha de desenho"); }
                }
                if (!SurfaceCurrent(surface))
                {
                    if (surface.Explorer != null) HideExplorer(surface.Explorer.RootHandle);
                    else foreach (LabelEntry label in labels.Values) if (label.Root == IntPtr.Zero) label.Label.Hide();
                }
            }
            layoutPlans.Trim(now);
            foreach (string key in labels.Keys.ToList())
                if (!live.Contains(key))
                {
                    LabelEntry entry = labels[key];
                    entry.Label.Hide();
                    // Keep the native object across short UIA/navigation gaps;
                    // hidden geometry is never redrawn until a fresh valid view.
                    if ((now - entry.LastUsedUtc).TotalSeconds > 30 ||
                        (entry.Root != IntPtr.Zero && !IsWindow(entry.Root)))
                    { entry.Label.Dispose(); labels.Remove(key); }
                }
            UpdateTray();
        }

        private void Suppress(string reason)
        {
            suppressed++;
            int count; suppressionReasons.TryGetValue(reason, out count);
            suppressionReasons[reason] = count + 1;
        }

        private static LabelPlacement PlanPlacement(SurfaceState surface, DesktopFolderIcon item,
            IList<Rectangle> used, float textScale)
        {
            var result = new LabelPlacement();
            if (item.IsEditing) result.SuppressionReason = "renomeação em andamento";
            else if (String.IsNullOrEmpty(item.Path)) result.SuppressionReason = "identidade não confirmada";
            else if (!CanPlaceForMode(surface, item.Path)) result.SuppressionReason = "modo sem célula identificada";
            else if (!TryLayout(surface, item, used, textScale, out result.Bounds, out result.Compact, out result.AlignRight))
                result.SuppressionReason = "espaço insuficiente ou conflito entre células";
            return result;
        }

        private static bool TryLayout(SurfaceState surface, DesktopFolderIcon item, IList<Rectangle> used,
            float textScale, out Rectangle bounds, out bool compact, out bool alignRight)
        {
            compact = alignRight = false;
            bounds = Rectangle.Empty;
            DesktopSnapshot snapshot = surface.Snapshot;
            ExplorerSurface explorer = surface.Explorer;
            if (explorer == null)
                return LabelLayout.TryPlace(item.Bounds, item.ItemBounds, snapshot.OccupiedBounds, used,
                    snapshot.HostScreenBounds, snapshot.Dpi, textScale, out bounds);
            Rectangle name, cell;
            explorer.NameBounds.TryGetValue(item.Path, out name);
            if (explorer.ViewMode == 1 || explorer.ViewMode == 5)
            {
                if (LabelLayout.TryPlace(item.Bounds, item.ItemBounds, snapshot.OccupiedBounds, used,
                    snapshot.HostScreenBounds, snapshot.Dpi, textScale, out bounds)) return true;
                compact = LabelLayout.TryPlaceIconBadge(name, item.ItemBounds, snapshot.OccupiedBounds, used,
                    snapshot.HostScreenBounds, snapshot.Dpi, textScale, out bounds);
                return compact;
            }
            if (explorer.ViewMode == 4 && explorer.SizeCellBounds.TryGetValue(item.Path, out cell) &&
                LabelLayout.TryPlaceInSizeCell(name, item.ItemBounds, cell, snapshot.OccupiedBounds, used,
                    snapshot.HostScreenBounds, snapshot.Dpi, textScale, out bounds))
            { alignRight = true; return true; }
            return (explorer.ViewMode == 2 || explorer.ViewMode == 3 || explorer.ViewMode == 4) &&
                explorer.NameCellBounds.TryGetValue(item.Path, out cell) &&
                LabelLayout.TryPlaceInline(name, item.ItemBounds, cell, snapshot.OccupiedBounds, used,
                    snapshot.HostScreenBounds, snapshot.Dpi, textScale, out bounds);
        }

        private void HideAll()
        {
            foreach (var entry in labels.Values) entry.Label.Hide();
        }

        private void HideExplorer(IntPtr root)
        {
            if (stopped) return;
            // Native callbacks can reenter a draw call. Do not mutate the dictionary.
            foreach (var entry in labels.Values)
                if (entry.Root != IntPtr.Zero && (root == IntPtr.Zero || entry.Root == root)) entry.Label.Hide();
        }

        private void UpdateTray()
        {
            if (DateTime.UtcNow < nextTrayUpdate) return;
            nextTrayUpdate = DateTime.UtcNow.AddSeconds(1);
            int detected, mapped, pending;
            double lease = ExplorerDrawingLeaseSeconds();
            lock (stateGate)
            {
                var recent = explorerSurfaces.Values.Where(x =>
                    (DateTime.UtcNow - x.CapturedUtc).TotalSeconds < lease && IsWindowVisible(x.RootHandle) && !IsIconic(x.RootHandle)).ToList();
                detected = recent.Count;
                mapped = recent.Sum(x => x.Snapshot == null ? 0 : x.Snapshot.Folders.Count);
                pending = recent.Count(x => x.MetadataPending);
            }
            string status = paused ? "Exibição pausada" :
                "Desktop: " + displayedDesktop + " · Explorador: " + displayedExplorer + " de " + mapped +
                " pastas · " + detected + " janela(s)";
            if (pending > 0) status += " · identificando itens";
            if (suppressed > 0) status += " · " + suppressed + " etiquetas não desenhadas";
            statusItem.Text = status;
            string tooltip = paused ? "Pasta GB 5.1 · pausado" :
                "Pasta GB 5.1 · D:" + displayedDesktop + " · E:" + displayedExplorer + "/" + mapped + " · J:" + detected;
            tray.Text = tooltip.Length > 63 ? tooltip.Substring(0, 63) : tooltip;
            if (!announced && ((DateTime.UtcNow - startedUtc).TotalSeconds > 7 || displayedDesktop + displayedExplorer > 0))
            {
                announced = true;
                tray.ShowBalloonTip(5500, "Pasta GB Ícones 5.1",
                    "Ativo no desktop e no Explorador. O menu mostra quantas janelas, pastas e etiquetas foram identificadas. O primeiro cálculo pode demorar.", ToolTipIcon.Info);
            }
        }

        private void ShowHelp()
        {
            MessageBox.Show(
                "O Pasta GB Ícones mostra tamanhos nas pastas da Área de Trabalho e nas janelas do Explorador de Arquivos.\n\n" +
                "Pastas locais, HDs externos e pendrives com letra de unidade usam a mesma leitura. Conectar ou remover uma unidade invalida seus resultados anteriores.\n\n" +
                "Ícones: tamanho abaixo do nome ou em uma pequena etiqueta dentro do ícone. Lista: ao lado do nome, se houver espaço comprovado. Detalhes: usa a célula Tamanho quando o Windows a expõe vazia e identificável; caso contrário tenta o espaço após o nome.\n\n" +
                "Seleção e foco preservam os cálculos. Mudanças de conteúdo solicitam atualização, e subtotais concluídos podem atender outras pastas. O último valor válido permanece com ↻ enquanto é atualizado. A primeira leitura pode demorar.\n\n" +
                "“aguardando” indica fila; “calculando” indica leitura em andamento; ↻ indica atualização; * indica soma parcial; “indisponível” indica falha com nova tentativa automática. O total usa bytes lógicos e GB decimais (1 GB = 1 bilhão de bytes).\n\n" +
                "Em Preferências você escolhe GB ou unidade automática, tema, escala, desempenho e se deve seguir junções e links locais. Ciclos e destinos já visitados são sinalizados e não são somados outra vez na mesma leitura. Rede e pontos de nova análise não reconhecidos não são percorridos.\n\n" +
                "Esta integração cobre o desktop e janelas compatíveis do Explorador. Não cobre todas as telas de Abrir/Salvar, aplicativos de terceiros ou pastas virtuais. Algumas apresentações não informam a posição necessária. A versão ainda precisa de validação visual no seu Windows.\n\n" +
                "“Iniciar com o Windows” ativa a próxima entrada na sua conta. “Encerrar” remove as etiquetas da tela.",
                "Pasta GB Ícones 5.1", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void CopyDiagnostic()
        {
            var report = new StringBuilder();
            report.AppendLine("Pasta GB Ícones 5.1 — diagnóstico local");
            report.AppendLine("Momento: " + DateTime.Now.ToString("s"));
            report.AppendLine("Sistema: " + Environment.OSVersion + "; processo 64 bits: " + Environment.Is64BitProcess);
            report.AppendLine("Etiquetas desktop: " + displayedDesktop + "; Explorador: " + displayedExplorer + "; ocultadas: " + suppressed);
            report.AppendLine("Eventos: " + events.Status);
            report.AppendLine("Monitor de conteúdo: " + changes.Diagnostic);
            report.AppendLine("Processos de cálculo: " + ProcessScanSession.DiagnosticSummary);
            report.AppendLine("Automação do desktop: " + DesktopProbe.AutomationDiagnostic);
            report.AppendLine("Automação do Explorador: " + ExplorerProbe.AutomationDiagnostic);
            report.AppendLine("Memória de identificação: " + ExplorerProbe.MetadataDiagnostic);
            report.AppendLine("Posicionamento: " + layoutPlans.Hits + " planos reutilizados; " + layoutPlans.Misses +
                " recalculados; " + layoutPlans.Count + " telas e " + layoutPlans.RetainedItems +
                " itens retidos; estimativa " + layoutPlans.EstimatedBytes + " bytes (limite 2 MiB).");
            foreach (ManagedWorker worker in workers) report.AppendLine("Componente: " + worker.Diagnostic);
            foreach (var reason in suppressionReasons) report.AppendLine("Não desenhado: " + reason.Key + " = " + reason.Value);
            report.AppendLine("Validade máxima da geometria nesta rodada: " + ExplorerDrawingLeaseSeconds().ToString("0.0", CultureInfo.InvariantCulture) + " s");
            ViewState desktop;
            List<ExplorerSurface> explorer;
            ExplorerCaptureResult capture;
            Dictionary<string, SizeRecord> values;
            AppPreferences settings;
            lock (stateGate)
            {
                desktop = view; explorer = explorerSurfaces.Values.ToList(); capture = lastExplorerCapture; values = sizes; settings = preferences;
                report.AppendLine("Preferências: unidades=" + settings.Units + "; tema=" + settings.Theme +
                    "; escala=" + settings.TextScale.ToString("0.##", CultureInfo.InvariantCulture) +
                    "; desempenho=" + settings.Performance + "; seguir ligações=" + settings.FollowDirectoryLinks +
                    "; cruzar volumes=" + settings.CrossVolumeLinks + "; política=" + policyGeneration);
                if (preferenceError != null) report.AppendLine(preferenceError);
                report.AppendLine("Desktop: " + desktopError);
                report.AppendLine("Explorador: " + explorerError);
                report.AppendLine("Cálculo: " + (scannerError ?? "sem erro reportado"));
                report.AppendLine("Atividade dos leitores: " + scannerActivity);
                foreach (string message in recentEvents) report.AppendLine("Evento local: " + message);
            }
            report.AppendLine("Desenho: " + (renderError ?? "sem erro reportado"));
            if (desktop != null && desktop.Desktop != null) AppendSnapshot(report, "Área de Trabalho", desktop.Desktop, values, settings.Units);
            if (capture != null)
                report.AppendLine("Tempo da descoberta do Explorador: " + capture.CaptureMilliseconds + " ms; capturas completas: " + capture.Surfaces.Count);
            report.AppendLine("Janelas/identidades retidas: " + explorer.Count);
                foreach (var surface in explorer)
                {
                    report.AppendLine("Janela: " + surface.RootHandle + "; view: " + surface.ShellViewHandle +
                        "; classe: " + surface.HostClass + "; modo: " + surface.ViewMode);
                    report.AppendLine("Captura: " + surface.CapturedUtc.ToLocalTime().ToString("s") +
                        "; duração: " + surface.CaptureMilliseconds + " ms; geração capturada/atual: " +
                        surface.GeometryRevision + "/" + events.ReadGeometryVersion(surface.RootHandle));
                    report.AppendLine("Estrutura capturada/atual: " + surface.StructureRevision + "/" + events.ReadStructureVersion(surface.RootHandle) +
                        "; identificando itens: " + surface.MetadataPending + "; índice reutilizado: " + surface.MetadataCached);
                    report.AppendLine("Nome/célula do nome/célula Tamanho: " + surface.NameBounds.Count + "/" + surface.NameCellBounds.Count + "/" + surface.SizeCellBounds.Count);
                    AppendSnapshot(report, surface.Location, surface.Snapshot, values, settings.Units);
                }
            try
            {
                Clipboard.SetText(report.ToString());
                tray.ShowBalloonTip(4000, "Diagnóstico copiado",
                    "O texto inclui os caminhos das pastas visíveis. Compartilhe somente se desejar.", ToolTipIcon.Info);
            }
            catch (Exception ex) { MessageBox.Show("Não foi possível copiar.\n\n" + ex.Message, "Pasta GB Ícones"); }
        }

        private static void AppendSnapshot(StringBuilder report, string title, DesktopSnapshot snapshot,
            Dictionary<string, SizeRecord> values, SizeUnits units)
        {
            if (snapshot == null) return;
            report.AppendLine("--- " + title);
            report.AppendLine("Estado: " + snapshot.Status);
            report.AppendLine("Host: " + snapshot.HostHandle + "; visível: " + snapshot.Visible +
                "; DPI: " + snapshot.Dpi + "; viewport: " + snapshot.HostScreenBounds);
            foreach (var folder in snapshot.Folders)
            {
                report.AppendLine(folder.Path + " | " + folder.Bounds + " | célula=" + folder.ItemBounds +
                    " | selecionado=" + folder.IsSelected + " | renomeando=" + folder.IsEditing);
                SizeRecord value;
                if (values.TryGetValue(folder.Path, out value))
                    report.AppendLine("  " + SizePresentation.Caption(value, units, false) +
                        " | estado=" + SizePresentation.PhaseName(value.Phase) + " | " + value.Detail +
                        " | dependências=" + String.Join("; ", value.Dependencies));
            }
        }

        private static string StartupCommand
        {
            get { return "\"" + Assembly.GetExecutingAssembly().Location + "\""; }
        }

        private static bool ReadStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", false))
                    return key != null && String.Equals(key.GetValue("PastaGBIcones") as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void ChangeStartup()
        {
            bool enable = !startupItem.Checked;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                {
                    if (enable) key.SetValue("PastaGBIcones", StartupCommand, RegistryValueKind.String);
                    else key.DeleteValue("PastaGBIcones", false);
                }
                startupItem.Checked = enable;
            }
            catch (Exception ex) { MessageBox.Show("Não foi possível alterar a inicialização.\n\n" + ex.Message, "Pasta GB Ícones"); }
        }

        protected override void ExitThreadCore()
        {
            stopped = true;
            if (renderTimer != null) renderTimer.Stop();
            if (events != null) events.Dispose();
            if (volumeMonitor != null) volumeMonitor.Dispose();
            foreach (ManagedWorker worker in workers) worker.Dispose();
            if (changes != null) changes.Dispose();
            foreach (var entry in labels.Values) entry.Label.Dispose();
            labels.Clear();
            layoutPlans.Clear();
            if (tray != null) tray.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            stopped = true;
            if (disposing)
            {
                if (renderTimer != null) renderTimer.Dispose();
                if (events != null) events.Dispose();
                if (volumeMonitor != null) volumeMonitor.Dispose();
                foreach (ManagedWorker worker in workers) worker.Dispose();
                if (changes != null) changes.Dispose();
                foreach (var entry in labels.Values) entry.Label.Dispose();
                labels.Clear();
                layoutPlans.Clear();
                if (tray != null)
                {
                    tray.Visible = false;
                    ContextMenuStrip menu = tray.ContextMenuStrip;
                    tray.Dispose();
                    if (menu != null) menu.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
