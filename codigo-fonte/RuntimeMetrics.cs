using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PastaGBIcons
{
    internal sealed class RuntimeSample
    {
        internal DateTime Utc;
        internal double CpuPercent;
        internal long PrivateBytes, WorkingSetBytes;
        internal int GdiObjects, UserObjects, Handles;
        internal long DesktopCaptures, ExplorerCaptures, Renders;
    }

    // No per-file metrics and no filesystem paths. The UI samples one process
    // at most every two seconds; the history has a fixed cap. Child-reader CPU
    // is deliberately not mislabeled as part of the main-process measurement.
    internal sealed class RuntimeMetrics : IDisposable
    {
        internal const int MaximumSamples = 720;
        private readonly object gate = new object();
        private readonly Queue<RuntimeSample> samples = new Queue<RuntimeSample>();
        private Process process;
        private long desktopCaptures, explorerCaptures, renders;
        private double desktopMilliseconds, explorerMilliseconds, renderMilliseconds;
        private double maxDesktopMilliseconds, maxExplorerMilliseconds, maxRenderMilliseconds;
        private long lastSampleClock;
        private TimeSpan lastCpu;
        private bool disposed;

        internal void RecordCapture(bool explorer, double milliseconds)
        {
            if (!Finite(milliseconds)) return;
            lock (gate)
            {
                if (disposed) return;
                if (explorer) { explorerCaptures++; explorerMilliseconds += milliseconds; maxExplorerMilliseconds = Math.Max(maxExplorerMilliseconds, milliseconds); }
                else { desktopCaptures++; desktopMilliseconds += milliseconds; maxDesktopMilliseconds = Math.Max(maxDesktopMilliseconds, milliseconds); }
            }
        }

        internal void RecordRender(double milliseconds)
        {
            if (!Finite(milliseconds)) return;
            lock (gate)
            {
                if (disposed) return;
                renders++; renderMilliseconds += milliseconds; maxRenderMilliseconds = Math.Max(maxRenderMilliseconds, milliseconds);
            }
        }

        private static bool Finite(double value)
        { return value >= 0 && !Double.IsNaN(value) && !Double.IsInfinity(value); }

        internal static double CpuPercent(double cpuSeconds, double wallSeconds, int logicalProcessors)
        {
            if (!Finite(cpuSeconds) || !Finite(wallSeconds) || wallSeconds <= 0 || logicalProcessors <= 0) return 0;
            return Math.Min(100, cpuSeconds / wallSeconds / logicalProcessors * 100);
        }

        internal void SampleProcess()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;
            lock (gate)
            {
                if (disposed) return;
                long now = Stopwatch.GetTimestamp();
                if (lastSampleClock != 0 && (now - lastSampleClock) / (double)Stopwatch.Frequency < 2) return;
                try
                {
                    if (process == null) process = Process.GetCurrentProcess();
                    process.Refresh();
                    TimeSpan cpu = process.TotalProcessorTime;
                    var sample = new RuntimeSample
                    {
                        Utc = DateTime.UtcNow,
                        CpuPercent = lastSampleClock == 0 ? 0 : CpuPercent((cpu - lastCpu).TotalSeconds,
                            (now - lastSampleClock) / (double)Stopwatch.Frequency, Environment.ProcessorCount),
                        PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
                        GdiObjects = GetGuiResources(process.Handle, 0), UserObjects = GetGuiResources(process.Handle, 1),
                        Handles = process.HandleCount, DesktopCaptures = desktopCaptures, ExplorerCaptures = explorerCaptures, Renders = renders
                    };
                    AddSample(sample); lastCpu = cpu; lastSampleClock = now;
                }
                catch { lastSampleClock = now; } // Diagnostics must not stop labels.
            }
        }

        internal void AddSample(RuntimeSample sample)
        {
            if (sample == null) return;
            lock (gate)
            {
                if (disposed) return;
                samples.Enqueue(sample);
                while (samples.Count > MaximumSamples) samples.Dequeue();
            }
        }

        internal string Summary
        {
            get
            {
                lock (gate)
                {
                    RuntimeSample last = null;
                    foreach (RuntimeSample sample in samples) last = sample;
                    var text = new StringBuilder();
                    text.AppendLine("Processo principal — leitores de pastas são processos separados.");
                    if (last != null)
                    {
                        text.AppendLine("CPU deste processo: " + last.CpuPercent.ToString("0.00", CultureInfo.CurrentCulture) + "% da capacidade total (última amostra)");
                        text.AppendLine("Memória privada: " + (last.PrivateBytes / 1048576d).ToString("0.0", CultureInfo.CurrentCulture) +
                            " MiB; residente: " + (last.WorkingSetBytes / 1048576d).ToString("0.0", CultureInfo.CurrentCulture) + " MiB");
                        text.AppendLine("Objetos GDI: " + last.GdiObjects + "; objetos USER: " + last.UserObjects + "; identificadores: " + last.Handles);
                    }
                    else text.AppendLine("Aguardando a primeira amostra de consumo no Windows.");
                    text.AppendLine(Line("Capturas desktop", desktopCaptures, desktopMilliseconds, maxDesktopMilliseconds));
                    text.AppendLine(Line("Capturas Explorador", explorerCaptures, explorerMilliseconds, maxExplorerMilliseconds));
                    text.AppendLine(Line("Passagens de desenho", renders, renderMilliseconds, maxRenderMilliseconds));
                    text.AppendLine("Histórico local: " + samples.Count + "/" + MaximumSamples + " amostras; intervalo mínimo de 2 s.");
                    return text.ToString();
                }
            }
        }

        private static string Line(string title, long count, double total, double peak)
        {
            return title + ": " + count + "; média " + (count == 0 ? 0 : total / count).ToString("0.00", CultureInfo.CurrentCulture) +
                " ms; máximo " + peak.ToString("0.00", CultureInfo.CurrentCulture) + " ms";
        }

        internal string ExportCsv()
        {
            var text = new StringBuilder("utc,scope,cpu_percent_of_machine,private_bytes,working_set_bytes,gdi_objects,user_objects,handles,desktop_captures,explorer_captures,renders\r\n");
            lock (gate)
                foreach (RuntimeSample sample in samples)
                    text.AppendFormat(CultureInfo.InvariantCulture, "{0:O},main_process,{1:0.0000},{2},{3},{4},{5},{6},{7},{8},{9}\r\n",
                        sample.Utc, sample.CpuPercent, sample.PrivateBytes, sample.WorkingSetBytes, sample.GdiObjects,
                        sample.UserObjects, sample.Handles, sample.DesktopCaptures, sample.ExplorerCaptures, sample.Renders);
            return text.ToString();
        }

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; if (process != null) process.Dispose(); }
        }

        [DllImport("user32.dll")]
        private static extern int GetGuiResources(IntPtr process, int flags);
    }
}
