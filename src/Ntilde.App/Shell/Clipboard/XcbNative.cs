using System.Runtime.InteropServices;

namespace Ntilde.Shell.Clipboard;

// A separate XCB connection never installs Xlib's process-global error handler
// or touches Avalonia's display. X protocol errors remain connection-local.
internal static partial class XcbNative
{
    private const string Library = "libxcb.so.1";

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScreenIterator { public nint Data; public int Remaining; public int Index; }

    [LibraryImport(Library, EntryPoint = "xcb_connect")]
    internal static partial nint Connect(nint display, out int screen);
    [LibraryImport(Library, EntryPoint = "xcb_disconnect")]
    internal static partial void Disconnect(nint connection);
    [LibraryImport(Library, EntryPoint = "xcb_connection_has_error")]
    internal static partial int HasError(nint connection);
    [LibraryImport(Library, EntryPoint = "xcb_get_setup")]
    internal static partial nint GetSetup(nint connection);
    [LibraryImport(Library, EntryPoint = "xcb_setup_roots_iterator")]
    internal static partial ScreenIterator Roots(nint setup);
    [LibraryImport(Library, EntryPoint = "xcb_screen_next")]
    internal static partial void NextScreen(ref ScreenIterator iterator);
    [LibraryImport(Library, EntryPoint = "xcb_generate_id")]
    internal static partial uint GenerateId(nint connection);
    [LibraryImport(Library, EntryPoint = "xcb_create_window")]
    internal static partial uint CreateWindow(nint connection, byte depth, uint window, uint parent,
        short x, short y, ushort width, ushort height, ushort borderWidth, ushort windowClass, uint visual, uint mask, nint values);
    [LibraryImport(Library, EntryPoint = "xcb_destroy_window")]
    internal static partial uint DestroyWindow(nint connection, uint window);
    [LibraryImport(Library, EntryPoint = "xcb_change_window_attributes")]
    internal static partial uint ChangeAttributes(nint connection, uint window, uint mask, nint values);
    [LibraryImport(Library, EntryPoint = "xcb_intern_atom", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint InternAtom(nint connection, byte onlyIfExists, ushort length, string name);
    [LibraryImport(Library, EntryPoint = "xcb_get_atom_name")]
    internal static partial uint GetAtomName(nint connection, uint atom);
    [LibraryImport(Library, EntryPoint = "xcb_set_selection_owner")]
    internal static partial uint SetOwner(nint connection, uint owner, uint selection, uint time);
    [LibraryImport(Library, EntryPoint = "xcb_get_selection_owner")]
    internal static partial uint GetOwner(nint connection, uint selection);
    [LibraryImport(Library, EntryPoint = "xcb_convert_selection")]
    internal static partial uint ConvertSelection(nint connection, uint requestor, uint selection, uint target, uint property, uint time);
    [LibraryImport(Library, EntryPoint = "xcb_get_property")]
    internal static partial uint GetProperty(nint connection, byte delete, uint window, uint property, uint type, uint offset, uint length);
    [LibraryImport(Library, EntryPoint = "xcb_change_property")]
    internal static partial uint ChangeProperty(nint connection, byte mode, uint window, uint property, uint type, byte format, uint length, nint data);
    [LibraryImport(Library, EntryPoint = "xcb_delete_property")]
    internal static partial uint DeleteProperty(nint connection, uint window, uint property);
    [LibraryImport(Library, EntryPoint = "xcb_send_event")]
    internal static partial uint SendEvent(nint connection, byte propagate, uint destination, uint mask, nint data);
    [LibraryImport(Library, EntryPoint = "xcb_flush")]
    internal static partial int Flush(nint connection);
    [LibraryImport(Library, EntryPoint = "xcb_poll_for_event")]
    internal static partial nint PollEvent(nint connection);
    [LibraryImport(Library, EntryPoint = "xcb_poll_for_reply")]
    internal static partial int PollReply(nint connection, uint sequence, out nint reply, out nint error);
    [LibraryImport(Library, EntryPoint = "xcb_discard_reply")]
    internal static partial void DiscardReply(nint connection, uint sequence);
    [LibraryImport("libc.so.6", EntryPoint = "free")]
    internal static partial void Free(nint pointer);
}
