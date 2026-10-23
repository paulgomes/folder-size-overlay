using System;
using System.Threading;

namespace PastaGBIcons
{
    // One retained thread per component. An exception restarts the action only
    // after unwinding its finally blocks; there are no abandoned replacement threads.
    internal sealed class ManagedWorker : IDisposable
    {
        private readonly ManualResetEvent stopping = new ManualResetEvent(false);
        private readonly Thread thread;
        private readonly string name;
        private readonly object gate = new object();
        private string lastError;
        private int failures;
        private bool disposed;

        internal ManagedWorker(string name, ThreadStart action, Action<string> report,
            Func<bool> applicationStopped, bool sta) : this(name, action, report, applicationStopped, sta, 1000) { }

        internal ManagedWorker(string name, ThreadStart action, Action<string> report,
            Func<bool> applicationStopped, bool sta, int retryUnitMilliseconds)
        {
            this.name = name;
            thread = new Thread(delegate()
            {
                while (!applicationStopped() && !stopping.WaitOne(0))
                {
                    DateTime started = DateTime.UtcNow;
                    try
                    {
                        action();
                        if (applicationStopped() || stopping.WaitOne(0)) break;
                        throw new InvalidOperationException("O componente encerrou inesperadamente.");
                    }
                    catch (Exception ex)
                    {
                        if (applicationStopped() || stopping.WaitOne(0)) break;
                        int count;
                        lock (gate)
                        {
                            if ((DateTime.UtcNow - started).TotalMinutes >= 2) failures = 0;
                            failures++; count = failures;
                            lastError = ex.GetType().Name + ": " + ex.Message;
                        }
                        try { report(lastError); } catch { }
                        int multiplier = 1 << Math.Min(5, Math.Max(0, count - 1));
                        int delay = (int)Math.Min(30000L, Math.Max(1, retryUnitMilliseconds) * (long)multiplier);
                        if (stopping.WaitOne(delay)) break;
                    }
                }
            }) { IsBackground = true, Name = name };
            if (sta) thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        internal string Diagnostic
        {
            get { lock (gate) return name + ": " + (thread.IsAlive ? "ativo" : "encerrado") +
                "; recuperações=" + failures + (lastError == null ? String.Empty : "; " + lastError); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true; stopping.Set();
            // Do not dispose the event while an in-flight native call may return
            // and the retained background thread may still need to inspect it.
            if (thread.Join(150)) stopping.Dispose();
        }
    }
}
