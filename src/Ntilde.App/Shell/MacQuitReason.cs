using System;
using System.Runtime.InteropServices;

namespace Ntilde.Shell
{
    /// <summary>
    /// Why macOS asked the app to quit (release hardening item 2). Cmd+Q and a logout, restart or shutdown all arrive as the
    /// same <c>applicationShouldTerminate:</c>, which Avalonia raises as <c>ShutdownRequested</c> without saying which
    /// (its <c>IsOSShutdown</c> is internal). The session's end comes as a quit Apple event whose <c>'why?'</c> attribute
    /// names it; Cmd+Q comes from the menu, with no Apple event at all. The standard Cocoa check, read through the
    /// Objective-C runtime: <c>[[[NSAppleEventManager sharedAppleEventManager] currentAppleEvent]
    /// attributeDescriptorForKeyword:'why?'].enumCodeValue</c>. Only meaningful on the main thread, inside that call.
    /// </summary>
    internal static class MacQuitReason
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        /// <summary><c>kAEQuitReason</c>, <c>'why?'</c>.</summary>
        private const uint QuitReasonKeyword = 0x7768793F;

        /// <summary><c>kAEShutDown</c>, <c>'shut'</c>.</summary>
        internal const uint ShutDown = 0x73687574;

        /// <summary><c>kAERestart</c>, <c>'rest'</c>.</summary>
        internal const uint Restart = 0x72657374;

        /// <summary><c>kAEReallyLogOut</c>, <c>'rlgo'</c>.</summary>
        internal const uint ReallyLogOut = 0x726C676F;

        /// <summary><c>kAELogOut</c>, <c>'logo'</c>.</summary>
        internal const uint LogOut = 0x6C6F676F;

        /// <summary>
        /// True when the quit being asked for is the session ending - a logout, restart or shutdown - which must never wait
        /// on a question (R19: an OS shutdown never asks). False for Cmd+Q, off macOS, and whenever the reason cannot be read.
        /// </summary>
        public static bool IsSessionEnding()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            try
            {
                IntPtr managerClass = objc_getClass("NSAppleEventManager");
                if (managerClass == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr manager = SendReturningObject(managerClass, sel_registerName("sharedAppleEventManager"));
                IntPtr appleEvent = manager == IntPtr.Zero ? IntPtr.Zero : SendReturningObject(manager, sel_registerName("currentAppleEvent"));
                if (appleEvent == IntPtr.Zero)
                {
                    return false; // the menu's Quit (Cmd+Q): no Apple event
                }

                IntPtr why = SendReturningObject(appleEvent, sel_registerName("attributeDescriptorForKeyword:"), QuitReasonKeyword);
                return why != IntPtr.Zero && IsSessionEndReason(SendReturningUInt32(why, sel_registerName("enumCodeValue")));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                return false;
            }
        }

        /// <summary>Whether a quit Apple event's <c>'why?'</c> is the session ending.</summary>
        internal static bool IsSessionEndReason(uint reason) => reason is ShutDown or Restart or ReallyLogOut or LogOut;

        // CA2101: the selector and class names are ASCII, marshalled as UTF-8 C strings.
        [DllImport(ObjC, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(ObjC, BestFitMapping = false, ThrowOnUnmappableChar = true)]
        private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        // objc_msgSend is called through a prototype that matches each method exactly (arm64 requires it).
        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendReturningObject(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendReturningObject(IntPtr receiver, IntPtr selector, uint keyword);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern uint SendReturningUInt32(IntPtr receiver, IntPtr selector);
    }
}
