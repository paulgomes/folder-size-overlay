using System;
using System.Diagnostics;
using System.Threading;

namespace PastaGBIcons
{
    // One long-lived MTA, at most one accepted job including the running job.
    // A timeout abandons its RESULT; it cannot interrupt a native provider call.
    // Never start replacement threads while such a call is outstanding. Inputs
    // and outputs must be plain data, not RCWs, AutomationElements or delegates
    // closing over a caller's apartment-affine objects.
    internal sealed class BoundedMtaWorker<TRequest, TResult>
    {
        private sealed class Work
        {
            internal TRequest Request;
            internal TResult Result;
            internal string Error;
            internal bool Done, Abandoned;
        }

        private readonly object gate = new object();
        private readonly Func<TRequest, TResult> execute;
        private readonly string name;
        private Thread thread;
        private Work pending;
        private bool busy;
        private DateTime startedUtc;
        private long completed, timedOut, rejected;
        private string lastError;

        internal BoundedMtaWorker(string name, Func<TRequest, TResult> execute)
        {
            if (execute == null) throw new ArgumentNullException("execute");
            this.name = name;
            this.execute = execute;
        }

        internal string Diagnostic
        {
            get
            {
                lock (gate)
                    return name + ": " + (busy ? "ocupado há " +
                        Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds).ToString("0.0",
                            System.Globalization.CultureInfo.InvariantCulture) + " s" : "disponível") +
                        "; concluídas=" + completed + "; espera expirada=" + timedOut +
                        "; recusadas por ocupação=" + rejected +
                        (String.IsNullOrEmpty(lastError) ? String.Empty : "; último erro=" + lastError);
            }
        }

        internal bool TryExecute(TRequest request, int timeoutMilliseconds, out TResult result, out string error)
        {
            if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            result = default(TResult);
            error = null;
            lock (gate)
            {
                if (busy)
                {
                    rejected++;
                    error = name + " está concluindo a consulta anterior.";
                    return false;
                }
                if (thread == null)
                {
                    thread = new Thread(Run) { IsBackground = true, Name = name };
                    // Linux tests exercise the queue/timeout behavior only.
                    if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                        thread.SetApartmentState(ApartmentState.MTA);
                    try { thread.Start(); }
                    catch { thread = null; throw; }
                }
                Work work = new Work { Request = request };
                pending = work;
                busy = true;
                startedUtc = DateTime.UtcNow;
                Monitor.PulseAll(gate);
                Stopwatch wait = Stopwatch.StartNew();
                while (!work.Done)
                {
                    long remaining = timeoutMilliseconds - wait.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        work.Abandoned = true;
                        timedOut++;
                        error = name + " excedeu a espera de " + timeoutMilliseconds + " ms; a interface continua independente.";
                        lastError = error;
                        return false;
                    }
                    Monitor.Wait(gate, (int)Math.Min(Int32.MaxValue, remaining));
                }
                error = work.Error;
                result = work.Result;
                return error == null;
            }
        }

        private void Run()
        {
            while (true)
            {
                Work work;
                lock (gate)
                {
                    while (pending == null) Monitor.Wait(gate);
                    work = pending;
                    pending = null;
                }
                TResult value = default(TResult);
                string failure = null;
                try { value = execute(work.Request); }
                catch (Exception exception)
                {
                    // Only a string crosses out of the MTA. In particular, an
                    // exception's Data or inner objects cannot carry providers.
                    failure = exception.GetType().Name + ": " + exception.Message;
                }
                lock (gate)
                {
                    if (!work.Abandoned)
                    {
                        work.Result = value;
                        work.Error = failure;
                    }
                    work.Request = default(TRequest);
                    work.Done = true;
                    busy = false;
                    completed++;
                    if (failure != null) lastError = failure;
                    Monitor.PulseAll(gate);
                }
            }
        }
    }
}
