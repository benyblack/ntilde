using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Ntilde.Shell
{
    public class GlobalHotkey : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;

        private IntPtr _handle;
        private bool _registered;
        private bool _disposed;

        public event Action? OnHotkeyPressed;

        public GlobalHotkey(Window window)
        {
            if (OperatingSystem.IsWindows())
            {
                var platformHandle = window.TryGetPlatformHandle();
                if (platformHandle != null)
                {
                    _handle = platformHandle.Handle;
                }
            }
        }



        public void Unregister()
        {
            if (_handle == IntPtr.Zero || !_registered) return;

            UnregisterHotKey(_handle, HOTKEY_ID);
            _registered = false;
        }

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WndProcDelegate? _wndProcDelegate;
        private IntPtr _oldWndProc;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const int GWLP_WNDPROC = -4;

        public void SetupHook()
        {
            if (_handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
            if (_oldWndProc != IntPtr.Zero) return; // already hooked

            // _wndProcDelegate must stay rooted for as long as the window's WNDPROC slot
            // points at its thunk. It is a field, so it lives as long as this instance does.
            // RemoveHook() restores the original WNDPROC *before* this instance (and the
            // delegate) can be collected — see the comment there for why that ordering is
            // load-bearing.
            _wndProcDelegate = WndProc;
            IntPtr newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
            _oldWndProc = SetWindowLongPtr(_handle, GWLP_WNDPROC, newWndProcPtr);

            // GWLP_WNDPROC always has a non-null previous value on a real window, so a zero
            // return means SetWindowLongPtr failed: the WNDPROC slot was NOT replaced. Drop
            // the delegate we just rooted — there is no installed thunk backing it, and
            // RemoveHook (guarded on _oldWndProc != Zero) would otherwise never release it.
            if (_oldWndProc == IntPtr.Zero)
            {
                _wndProcDelegate = null;
                AppLogger.Log("[GlobalHotkey] SetWindowLongPtr(GWLP_WNDPROC) failed; hotkey hook not installed.");
            }
        }

        private void RemoveHook()
        {
            // Restore the original window procedure BEFORE dropping the managed delegate.
            //
            // Skipping this was a confirmed crash (ExecutionEngineException on the
            // GlobalHotkey.WndProc -> CallWindowProc frame, captured in a WER dump): on
            // window teardown the WNDPROC slot still pointed at our managed thunk, and once
            // this instance became collectable the GC freed _wndProcDelegate. The next window
            // message Win32 dispatched (WM_DESTROY / WM_NCDESTROY, or a stray WM_HOTKEY) then
            // called into freed memory and aborted the process with no managed exception.
            // Heavy terminal output (e.g. an agent streaming) supplies the GC pressure that
            // collects the delegate, which is why it surfaced as a random
            // "window vanishes instantly".
            if (_handle == IntPtr.Zero || _oldWndProc == IntPtr.Zero || !OperatingSystem.IsWindows())
            {
                return;
            }

            SetWindowLongPtr(_handle, GWLP_WNDPROC, _oldWndProc);
            _oldWndProc = IntPtr.Zero;
            _wndProcDelegate = null;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                // An exception escaping a reverse-P/Invoke WndProc unwinds into native Win32
                // (DispatchMessage), which is undefined behavior and crashes the process.
                // Catch at this boundary so a misbehaving handler can never take the app down;
                // toggling visibility is best-effort. Log (don't silently swallow) so the
                // failure is diagnosable in debug.log.
                try
                {
                    OnHotkeyPressed?.Invoke();
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[GlobalHotkey] hotkey handler threw: {ex}");
                }

                return IntPtr.Zero; // Handled
            }

            return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        /// <summary>Registers the hotkey; false when Windows refused it (another program holds that key, say).</summary>
        public bool Register(uint modifiers, uint key)
        {
            if (_handle == IntPtr.Zero || !OperatingSystem.IsWindows()) return false;

            if (_oldWndProc == IntPtr.Zero) SetupHook();

            if (_registered) Unregister();

            _registered = RegisterHotKey(_handle, HOTKEY_ID, modifiers, key);
            return _registered;
        }

        public const uint ModAlt = 1, ModControl = 2, ModShift = 4, ModWin = 8;

        /// <summary>The default, <c>Alt+`</c>: what <see cref="TryParse"/> falls back to.</summary>
        public const uint DefaultModifiers = ModAlt, DefaultKey = 0xC0;

        /// <summary>
        /// Reads a <c>GlobalHotkey</c> setting such as <c>"Alt+OemTilde"</c> or <c>"Ctrl+Shift+Space"</c>
        /// into <c>RegisterHotKey</c>'s modifiers and virtual-key code. Names are case-insensitive and
        /// follow Avalonia's <c>Key</c> names (<c>A</c>-<c>Z</c>, <c>D0</c>-<c>D9</c>, <c>F1</c>-<c>F24</c>,
        /// <c>OemTilde</c>, ...). At least one modifier is required: a bare key registered system-wide
        /// would stop working in every other program. F12 is refused: Windows reserves it for the
        /// debugger, and <c>RegisterHotKey</c> does not take it.
        /// </summary>
        public static bool TryParse(string? text, out uint modifiers, out uint key)
        {
            modifiers = 0;
            key = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                uint mod = parts[i].ToLowerInvariant() switch
                {
                    "alt" => ModAlt,
                    "ctrl" or "control" => ModControl,
                    "shift" => ModShift,
                    "win" or "meta" or "super" => ModWin,
                    _ => 0,
                };
                if (mod == 0) return false;
                modifiers |= mod;
            }

            if (modifiers == 0 || !TryParseKey(parts[^1], out key)) return false;
            return true;
        }

        private static bool TryParseKey(string name, out uint key)
        {
            key = 0;
            if (name.Length == 0) return false;
            if (name.Length == 1)
            {
                char c = char.ToUpperInvariant(name[0]);
                if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { key = c; return true; }
                if (c is '`' or '~') { key = 0xC0; return true; }
                return false;
            }

            string n = name.ToLowerInvariant();
            if (n.Length == 2 && n[0] == 'd' && char.IsAsciiDigit(n[1])) { key = n[1]; return true; }
            if (n[0] == 'f' && int.TryParse(n.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24 and not 12)
            {
                key = (uint)(0x70 + f - 1);
                return true;
            }

            key = n switch
            {
                "oemtilde" or "oem3" or "backtick" or "grave" => 0xC0,
                "space" => 0x20,
                "tab" => 0x09,
                "enter" or "return" => 0x0D,
                "oemminus" => 0xBD,
                "oemplus" => 0xBB,
                "oemcomma" => 0xBC,
                "oemperiod" => 0xBE,
                "oemquestion" => 0xBF,
                "oemsemicolon" => 0xBA,
                "oemquotes" => 0xDE,
                "oemopenbrackets" => 0xDB,
                "oemclosebrackets" => 0xDD,
                "oempipe" => 0xDC,
                _ => 0,
            };
            return key != 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Unregister();
            RemoveHook();
            GC.SuppressFinalize(this);
        }
    }
}
