using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PastaGBIcons
{
    // Construct, invalidate and dispose on the WinForms message-loop thread.
    // Read/register are safe on the probe's STA worker. No DLL is injected.
    internal sealed class FolderEventTracker : IDisposable
    {
        private const uint ObjectDestroy = 0x8001;
        private const uint ObjectParentChange = 0x800F;
        private sealed class SurfaceRegistration
        {
            internal readonly IntPtr Root, ShellView, Host;
            internal readonly uint ProcessId;
            internal SurfaceRegistration(IntPtr root, IntPtr shellView,
                IntPtr host, uint processId)
            { Root = root; ShellView = shellView; Host = host; ProcessId = processId; }
        }

        private readonly object gate = new object();
        private readonly Dictionary<IntPtr, long> geometryRevisions = new Dictionary<IntPtr, long>();
        private readonly Dictionary<IntPtr, long> structureRevisions = new Dictionary<IntPtr, long>();
        private readonly Dictionary<IntPtr, SurfaceRegistration> surfaces =
            new Dictionary<IntPtr, SurfaceRegistration>();
        private readonly List<IntPtr> hooks = new List<IntPtr>();
        private readonly WinEventProc callback;
        private readonly int ownerManagedThread;
        private readonly uint ownerNativeThread, ownerProcess;
        private GCHandle callbackRoot;
        private long serial;
        private volatile bool disposed;

        // Refresh leaves the last valid frame visible; the periodic capture
        // updates its appearance. Only Geometry/Structure invoke Changed.
        internal event Action<IntPtr> Changed;
        internal string Status { get; private set; }

        internal FolderEventTracker()
        {
            ownerManagedThread = Thread.CurrentThread.ManagedThreadId;
            ownerNativeThread = GetCurrentThreadId();
            ownerProcess = GetCurrentProcessId();
            callback = OnEvent;
            callbackRoot = GCHandle.Alloc(callback);
            try
            {
                AddHook(0x8000, 0x800F); // lifecycle, layout, name, parent
                AddHook(0x8014, 0x8018); // text selection, scroll, cloak
                AddHook(0x0003, 0x0003); // foreground: soft only
                AddHook(0x000A, 0x000B); // move/resize
                AddHook(0x000E, 0x000F); // drag/drop
                AddHook(0x0012, 0x0013); // scrollbar gesture
                AddHook(0x0016, 0x0017); // minimize/restore
                Status = hooks.Count == 7 ? "Eventos de navegação e posição ativos." :
                    "Alguns eventos não estão disponíveis; a leitura periódica continua ativa.";
            }
            catch { Dispose(); throw; }
        }

        private void AddHook(uint first, uint last)
        {
            // WINEVENT_OUTOFCONTEXT = 0; WINEVENT_SKIPOWNPROCESS = 2.
            IntPtr hook = SetWinEventHook(first, last, IntPtr.Zero, callback, 0, 0, 2);
            if (hook != IntPtr.Zero) hooks.Add(hook);
        }

        // The probe already verified the Explorer executable and obtained the
        // active Shell view through Shell APIs. Recheck native ancestry/PID to
        // scope events. Host zero registers the view BEFORE its first UIA read.
        // Registration never invokes Changed or changes either version.
        internal void RegisterSurface(IntPtr root, IntPtr shellView, IntPtr host)
        {
            if (disposed || root == IntPtr.Zero || shellView == IntPtr.Zero ||
                !IsExplorerRoot(root) || !IsChild(root, shellView)) return;
            uint rootProcess, viewProcess, hostProcess = 0;
            if (GetWindowThreadProcessId(root, out rootProcess) == 0 ||
                rootProcess == 0 || rootProcess == ownerProcess ||
                GetWindowThreadProcessId(shellView, out viewProcess) == 0 ||
                viewProcess != rootProcess) return;
            if (host != IntPtr.Zero &&
                ((host != shellView && !IsChild(shellView, host)) ||
                 GetWindowThreadProcessId(host, out hostProcess) == 0 ||
                 hostProcess != rootProcess)) return;
            lock (gate)
            {
                if (disposed) return;
                SurfaceRegistration previous;
                if (surfaces.TryGetValue(root, out previous) &&
                    previous.ShellView == shellView && previous.ProcessId == rootProcess)
                {
                    // Each capture starts with host zero. Keep the refined host
                    // when the proven Shell view itself has not changed.
                    if (host == IntPtr.Zero) host = previous.Host;
                    if (host == previous.Host) return;
                }
                surfaces[root] = new SurfaceRegistration(root, shellView, host, rootProcess);
            }
        }

        // Compatibility: the original frame revision is the geometry version.
        internal Dictionary<IntPtr, long> CaptureVersions()
        {
            lock (gate) { return new Dictionary<IntPtr, long>(geometryRevisions); }
        }
        internal Dictionary<IntPtr, long> CaptureStructureVersions()
        {
            lock (gate) { return new Dictionary<IntPtr, long>(structureRevisions); }
        }
        internal long ReadVersion(IntPtr root) { return ReadGeometryVersion(root); }
        internal long ReadGeometryVersion(IntPtr root)
        {
            lock (gate)
            {
                long version;
                return geometryRevisions.TryGetValue(root, out version) ? version : 0;
            }
        }
        internal long ReadStructureVersion(IntPtr root)
        {
            lock (gate)
            {
                long version;
                return structureRevisions.TryGetValue(root, out version) ? version : 0;
            }
        }

        // Media invalidation makes both index and placements stale. Zero targets
        // all registered roots. UI thread only: Changed may hide native labels.
        internal void Invalidate(IntPtr root)
        {
            EnsureOwnerThread();
            var affected = new List<IntPtr>();
            lock (gate)
            {
                if (disposed) return;
                if (root == IntPtr.Zero) affected.AddRange(surfaces.Keys);
                else affected.Add(root);
                foreach (IntPtr item in affected)
                    AdvanceVersionLocked(item, ViewChangeKind.Structure);
            }
            foreach (IntPtr item in affected) RaiseChanged(item);
        }

        private void OnEvent(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint threadId, uint eventTime)
        {
            // Caret lifecycle may accompany every click; never resolve its root.
            // Source-thread filtering also guards our child-label/Z-order events.
            if (disposed || window == IntPtr.Zero || objectId == -8 || objectId == -9 ||
                eventType == 0x800D || (threadId != 0 && threadId == ownerNativeThread)) return;
            try
            {
                IntPtr root;
                ViewEventRole role;
                SurfaceRegistration registration;
                if (!TryResolveEvent(window, eventType, out root, out role, out registration)) return;
                ViewChangeKind change = ViewChangePolicy.Classify(eventType, objectId, childId, role);
                if (change < ViewChangeKind.Geometry) return;
                lock (gate)
                {
                    if (disposed) return;
                    if (registration != null)
                    {
                        SurfaceRegistration current;
                        if (!surfaces.TryGetValue(root, out current) ||
                            current.ShellView != registration.ShellView ||
                            current.ProcessId != registration.ProcessId)
                            return; // another active view was registered during classification
                    }
                    AdvanceVersionLocked(root, change);
                    if (eventType == ObjectDestroy && objectId == 0 && childId == 0)
                        ForgetDestroyedHandleLocked(root, window);
                }
                RaiseChanged(root); // no lock across native window operations
            }
            catch { /* Never let an exception escape an unmanaged callback. */ }
        }

        private bool TryResolveEvent(IntPtr window, uint eventType, out IntPtr root,
            out ViewEventRole role, out SurfaceRegistration registration)
        {
            root = GetAncestor(window, 2); // GA_ROOT
            role = ViewEventRole.Other;
            registration = null;
            lock (gate)
            {
                // A queued destroy may arrive after ancestry is gone. Fallback
                // is allowed only for an EXACT known root/view/host, not an old
                // arbitrary descendant or any HWND with a previous revision.
                if (eventType == ObjectDestroy || eventType == ObjectParentChange)
                {
                    foreach (SurfaceRegistration item in surfaces.Values)
                    {
                        if (window == item.Root || window == item.ShellView || window == item.Host)
                        { registration = item; root = item.Root; break; }
                    }
                }
                if (registration == null && root != IntPtr.Zero)
                    surfaces.TryGetValue(root, out registration);
            }
            if (registration == null)
            {
                // Guard root changes during an initial capture, before the view
                // is registered. Unregistered descendant/chrome events are ignored.
                if (window != root || !IsExplorerRoot(root)) return false;
                uint rootProcess;
                if (GetWindowThreadProcessId(root, out rootProcess) == 0 ||
                    rootProcess == ownerProcess) return false;
                role = ViewEventRole.Root;
                return true;
            }
            uint sourceProcess;
            GetWindowThreadProcessId(window, out sourceProcess);
            if (sourceProcess != 0 && sourceProcess != registration.ProcessId) return false;
            if (sourceProcess == 0 && eventType != ObjectDestroy &&
                eventType != ObjectParentChange) return false;
            role = ClassifyRole(window, registration);
            return role != ViewEventRole.Other;
        }

        private static ViewEventRole ClassifyRole(IntPtr window, SurfaceRegistration registration)
        {
            if (window == registration.Root) return ViewEventRole.Root;
            if (window == registration.ShellView || window == registration.Host)
                return ViewEventRole.View;
            IntPtr anchor = IntPtr.Zero;
            if (registration.Host != IntPtr.Zero && IsChild(registration.Host, window))
                anchor = registration.Host;
            else if (registration.ShellView != IntPtr.Zero && IsChild(registration.ShellView, window))
                anchor = registration.ShellView;
            if (anchor != IntPtr.Zero)
                return HasChromeInPath(window, anchor) ? ViewEventRole.Other : ViewEventRole.ContentChild;
            // A true layout ancestor can move the active viewport. Sibling
            // toolbars, address bars and tree panes fail this ancestry check.
            if ((registration.ShellView != IntPtr.Zero && IsChild(window, registration.ShellView)) ||
                (registration.Host != IntPtr.Zero && IsChild(window, registration.Host)))
                return IsChromeClass(ReadClass(window)) ? ViewEventRole.Other : ViewEventRole.ViewAncestor;
            return ViewEventRole.Other;
        }

        private static bool HasChromeInPath(IntPtr window, IntPtr anchor)
        {
            IntPtr current = window;
            for (int depth = 0; depth < 64 && current != IntPtr.Zero; depth++)
            {
                if (current == anchor) return false;
                if (IsChromeClass(ReadClass(current))) return true;
                current = GetAncestor(current, 1); // GA_PARENT, excludes popup owners
            }
            return true; // ancestry changed, or no proven path to the file view
        }

        private static bool IsChromeClass(string name)
        {
            // A plain Edit inside the file view can be the native rename editor.
            // Address/search edits are excluded by their actual ancestors.
            return String.Equals(name, "ToolbarWindow32", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "ReBarWindow32", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "msctls_progress32", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "msctls_statusbar32", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "SysTreeView32", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "NamespaceTreeControl", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "ComboBox", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "ComboBoxEx32", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("Breadcrumb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Address Band", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("SearchBox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("SearchEdit", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void AdvanceVersionLocked(IntPtr root, ViewChangeKind change)
        {
            long version = ++serial;
            geometryRevisions[root] = version;
            if (change == ViewChangeKind.Structure) structureRevisions[root] = version;
        }
        private void ForgetDestroyedHandleLocked(IntPtr root, IntPtr window)
        {
            SurfaceRegistration current;
            if (!surfaces.TryGetValue(root, out current)) return;
            if (window == current.Root)
                surfaces.Remove(root);
            else if (window == current.ShellView)
                surfaces[root] = new SurfaceRegistration(root, IntPtr.Zero, IntPtr.Zero, current.ProcessId);
            else if (window == current.Host)
                surfaces[root] = new SurfaceRegistration(root, current.ShellView, IntPtr.Zero, current.ProcessId);
            // Keep revision tombstones: an in-flight capture of a closed HWND
            // must not become valid again because its old version was zero.
        }
        private void RaiseChanged(IntPtr root)
        {
            if (disposed) return;
            Action<IntPtr> handler = Changed;
            if (handler != null) handler(root);
        }
        private static bool IsExplorerRoot(IntPtr root)
        {
            string name = ReadClass(root);
            return name == "CabinetWClass" || name == "ExploreWClass";
        }
        private static string ReadClass(IntPtr window)
        {
            if (window == IntPtr.Zero) return String.Empty;
            var name = new StringBuilder(128);
            return GetClassName(window, name, name.Capacity) == 0 ? String.Empty : name.ToString();
        }
        private void EnsureOwnerThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != ownerManagedThread)
                throw new InvalidOperationException(
                    "A invalidação e o encerramento dos eventos devem ocorrer na thread da interface.");
        }
        public void Dispose()
        {
            EnsureOwnerThread();
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                surfaces.Clear();
                Changed = null;
            }
            // Unhook on the installing thread; workers only read/register.
            foreach (IntPtr hook in hooks) UnhookWinEvent(hook);
            hooks.Clear();
            if (callbackRoot.IsAllocated) callbackRoot.Free();
        }

        private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window,
            int objectId, int childId, uint threadId, uint eventTime);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module,
            WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsChild(IntPtr parent, IntPtr window);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder value, int capacity);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();
    }
}
