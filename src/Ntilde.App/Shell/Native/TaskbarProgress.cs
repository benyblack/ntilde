using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Ntilde.Shell.Native
{
    /// <summary>
    /// Windows taskbar progress (ITaskbarList3) for OSC 9;4 reports (issue #271), via
    /// CoCreateInstance + raw vtable calls. NOT RCW COM: this app runs with
    /// <c>System.Runtime.InteropServices.BuiltInComInterop.IsSupported=false</c>
    /// (AOT/trimming posture), where <c>Marshal.GetObjectForIUnknown</c> throws
    /// <see cref="NotSupportedException"/> — function-pointer marshaling is unaffected
    /// by that switch. Same raw-P/Invoke posture as the rest of this directory.
    /// Nothing in this class may throw: <see cref="Apply"/> runs during window startup
    /// (the tab SelectionChanged handler), and an escape there takes the whole app down.
    /// Any failure logs once and permanently stands the feature down.
    /// </summary>
    internal sealed class TaskbarProgress : IDisposable
    {
        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr punkOuter, uint context, ref Guid riid, out IntPtr ppv);

        private const uint CLSCTX_INPROC_SERVER = 0x1;

        private static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
        private static readonly Guid IID_ITaskbarList3 = new("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");

        // ITaskbarList3 vtable slots; IUnknown occupies 0-2 (QueryInterface, AddRef,
        // Release). ITaskbarList follows, then ITaskbarList2's single method, then the
        // ITaskbarList3 progress pair this class exists to call.
        private const int SlotRelease = 2;
        private const int SlotHrInit = 3;
        private const int SlotSetProgressValue = 9;
        private const int SlotSetProgressState = 10;

        // COM methods receive the interface pointer ("this") as the delegate's first
        // argument. StdCall is the x86 COM convention; x64 has one convention and
        // ignores the attribute.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int HrInitDelegate(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetProgressValueDelegate(IntPtr self, IntPtr hwnd, ulong completed, ulong total);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetProgressStateDelegate(IntPtr self, IntPtr hwnd, int tbpFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseDelegate(IntPtr self);

        // The handle is NOT captured at construction: the first Apply can arrive from
        // the startup SelectionChanged, before the window is shown and
        // TryGetPlatformHandle returns anything. _hwnd starts zero and is re-read from
        // the window on every init attempt until a handle exists, so an early
        // construction cannot permanently dead-end the feature.
        private readonly Window? _window;
        private IntPtr _hwnd;
        private IntPtr _taskbar;
        private SetProgressValueDelegate? _setProgressValue;
        private SetProgressStateDelegate? _setProgressState;
        private bool _initFailed;
        private bool _disposed;

        public TaskbarProgress(Window window)
        {
            _window = window;
        }

        /// <summary>Test-only seam: construct against a raw handle.</summary>
        internal TaskbarProgress(IntPtr hwnd) => _hwnd = hwnd;

        private static T VtableMethod<T>(IntPtr pUnk, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(pUnk);
            IntPtr functionPointer = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(functionPointer);
        }

        private bool TryInit()
        {
            if (_disposed || _initFailed || _taskbar != IntPtr.Zero)
            {
                return _taskbar != IntPtr.Zero;
            }

            if (_hwnd == IntPtr.Zero && OperatingSystem.IsWindows())
            {
                _hwnd = _window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            }

            if (_hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            {
                return false;
            }

            Guid clsid = CLSID_TaskbarList;
            Guid iid = IID_ITaskbarList3;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr ppv);
            if (hr < 0 || ppv == IntPtr.Zero)
            {
                _initFailed = true;
                AppLogger.Log($"[TaskbarProgress] CoCreateInstance(ITaskbarList3) failed hr=0x{hr:X8}; taskbar progress disabled.");
                return false;
            }

            int initHr = VtableMethod<HrInitDelegate>(ppv, SlotHrInit)(ppv);
            if (initHr < 0)
            {
                VtableMethod<ReleaseDelegate>(ppv, SlotRelease)(ppv);
                _initFailed = true;
                AppLogger.Log($"[TaskbarProgress] HrInit failed hr=0x{initHr:X8}; taskbar progress disabled.");
                return false;
            }

            _taskbar = ppv;
            _setProgressValue = VtableMethod<SetProgressValueDelegate>(ppv, SlotSetProgressValue);
            _setProgressState = VtableMethod<SetProgressStateDelegate>(ppv, SlotSetProgressState);
            return true;
        }

        /// <summary>
        /// Applies one resolved progress state (see
        /// <see cref="TabProgressPresentation.ResolveTaskbar"/>). A null report clears.
        /// </summary>
        internal void Apply(TerminalProgressReport? report)
        {
            try
            {
                if (!TryInit())
                {
                    return;
                }

                var (flag, value) = TabProgressPresentation.ResolveTaskbar(report);
                if (flag == TabProgressPresentation.TaskbarFlagNoProgress)
                {
                    _setProgressState!(_taskbar, _hwnd, TabProgressPresentation.TaskbarFlagNoProgress);
                    return;
                }

                _setProgressValue!(_taskbar, _hwnd, (ulong)(value ?? 0), 100);
                _setProgressState!(_taskbar, _hwnd, flag);
            }
            catch (Exception ex)
            {
                // COM can fail transiently (explorer restarting, window closing); log and
                // stand down rather than retrying into a live failure loop.
                _initFailed = true;
                AppLogger.Log($"[TaskbarProgress] SetProgress* threw: {ex.Message}; taskbar progress disabled.");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_taskbar != IntPtr.Zero)
            {
                try
                {
                    if (_setProgressState != null)
                    {
                        _setProgressState(_taskbar, _hwnd, TabProgressPresentation.TaskbarFlagNoProgress);
                    }
                }
                catch
                {
                    // Window teardown ordering: the HWND may already be gone.
                }

                try
                {
                    VtableMethod<ReleaseDelegate>(_taskbar, SlotRelease)(_taskbar);
                }
                catch
                {
                }

                _taskbar = IntPtr.Zero;
            }
        }
    }
}
