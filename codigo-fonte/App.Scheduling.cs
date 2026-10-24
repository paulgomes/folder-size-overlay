using System;
using System.Diagnostics;
using System.Threading;
using PastaGB;

namespace PastaGBIcons
{
    internal sealed partial class IconContext
    {
        private long lastResumeTick;
        private DateTime lastFullRenderUtc;
        // Cadências do render: 32 ms enquanto há mudança a desenhar; 2 s de
        // "batida" ociosa (só uma comparação de versão, sem redesenhar); e um
        // render de segurança no máximo a cada HeartbeatSeconds, como rede para
        // algum WinEvent nativo que tenhamos perdido.
        private const int ActiveRenderMs = 32;
        private const int IdleRenderMs = 2000;
        private const double HeartbeatSeconds = 10d;
        // Recuo longo da descoberta quando não há nada para observar (pausado,
        // ou sem nenhuma janela do Explorer): em vez de capturar UI Automation a
        // cada ~2 s para sempre, dorme e acorda sob demanda pelos WinEvents já
        // assinados (foco/ambiente/resume sinalizam desktopWake/explorerWake).
        // O teto também funciona como re-varredura de segurança periódica.
        private const int DiscoveryIdleNoSurfaceMs = 30000;
        private void RequestRender()
        {
            if (stopped) return;
            Interlocked.Increment(ref renderRequestVersion);
            if (uiDispatch != null) uiDispatch.Signal();
        }

        private void ScheduleRender()
        {
            if (stopped || renderTimer == null) return;
            // A bounded frame rate coalesces bursts of discovery/progress.
            if (renderTimer.Interval != ActiveRenderMs) renderTimer.Interval = ActiveRenderMs;
        }

        private void Render()
        {
            if (stopped) return;
            if (rendering) { RequestRender(); return; }
            long requested = Interlocked.Read(ref renderRequestVersion);
            // Nada novo para desenhar: NÃO redesenhar todas as etiquetas a cada
            // segundo (era a maior fonte de CPU/GDI em repouso — cada etiqueta
            // custa dezenas de chamadas USER32/GDI). Só repassa por RenderCore
            // de tempos em tempos (HeartbeatSeconds) como rede de segurança para
            // eventos nativos perdidos; nas demais batidas ociosas apenas volta.
            if (requested == lastRenderedRequest &&
                (DateTime.UtcNow - lastFullRenderUtc).TotalSeconds < HeartbeatSeconds)
            {
                if (!stopped && renderTimer != null && renderTimer.Interval != IdleRenderMs)
                    renderTimer.Interval = IdleRenderMs;
                return;
            }
            rendering = true;
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
                lastFullRenderUtc = DateTime.UtcNow;
                rendering = false;
                if (!stopped && renderTimer != null)
                    renderTimer.Interval = Interlocked.Read(ref renderRequestVersion) == lastRenderedRequest ? IdleRenderMs : ActiveRenderMs;
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
