using System;
using System.Diagnostics;
using System.Threading;
using PastaGB;

namespace PastaGBIcons
{
    internal sealed partial class IconContext
    {
        private long lastResumeTick;
        private void RequestRender()
        {
            if (stopped) return;
            Interlocked.Increment(ref renderRequestVersion);
            if (uiDispatch != null) uiDispatch.Signal();
        }

        private void ScheduleRender()
        {
            if (stopped || renderTimer == null) return;
            // A bounded frame rate coalesces bursts of discovery/progress. The
            // one-second heartbeat remains as a guard for missing native events.
            if (renderTimer.Interval != 32) renderTimer.Interval = 32;
        }

        private void Render()
        {
            if (stopped) return;
            if (rendering) { RequestRender(); return; }
            rendering = true;
            long requested = Interlocked.Read(ref renderRequestVersion);
            Stopwatch watch = Stopwatch.StartNew();
            try { using (LayeredLabel.BeginBatch()) RenderCore(); }
            catch (Exception ex)
            {
                renderError = ex.Message; HideAll();
                RecordEvent("Desenho interrompido; nova tentativa automática: " + ex.Message);
            }
            finally
            {
                metrics.RecordRender(watch.Elapsed.TotalMilliseconds);
                metrics.SampleProcess();
                lastRenderedRequest = requested;
                rendering = false;
                if (!stopped && renderTimer != null)
                    renderTimer.Interval = Interlocked.Read(ref renderRequestVersion) == lastRenderedRequest ? 1000 : 32;
            }
        }

        private void SurfaceChanged(IntPtr root)
        {
            if (stopped) return;
            bool desktop;
            lock (stateGate) desktop = view != null && view.RootHandle == root;
            if (desktop)
            {
                foreach (LabelEntry entry in labels.Values) if (entry.Root == IntPtr.Zero) entry.Label.Hide();
                desktopWake.Signal();
            }
            else { HideExplorer(root); explorerWake.Signal(); }
            WakeScanner(); RequestRender();
        }

        private void FocusChanged(IntPtr root)
        {
            if (stopped) return;
            desktopWake.Signal(); explorerWake.Signal();
            ScanDemandChanged(); RequestRender();
        }

        private void SurfaceRefresh(IntPtr root)
        {
            if (stopped) return;
            bool desktop;
            lock (stateGate) desktop = view != null && view.RootHandle == root;
            if (desktop) desktopWake.Signal(); else explorerWake.Signal();
            RequestRender();
        }

        private void EnvironmentChanged(bool resumed)
        {
            if (stopped) return;
            if (resumed)
            {
                long now = Stopwatch.GetTimestamp();
                if (lastResumeTick != 0 && now - lastResumeTick < Stopwatch.Frequency * 2) return;
                lastResumeTick = now;
                const uint allDrives = (1u << 26) - 1;
                volumes.Changed(allDrives); VolumesChanged(allDrives);
            }
            else if (events != null) events.InvalidateGeometry(IntPtr.Zero);
            desktopWake.Signal(); explorerWake.Signal();
            ScanDemandChanged(); RequestRender();
        }

        private void ShowPerformance()
        {
            using (var dialog = new PerformanceDialog(metrics, delegate
            {
                return "Coordenador: " + CoordinatorDiagnostic + Environment.NewLine +
                    LayeredLabel.Diagnostic + Environment.NewLine +
                    (persistentCache == null ? "Cache persistente indisponível." : persistentCache.Diagnostic) + Environment.NewLine +
                    "Leitores: " + ProcessScanSession.DiagnosticSummary;
            })) dialog.ShowDialog();
        }
    }
}
