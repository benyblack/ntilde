using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Ntilde.VT;

namespace Ntilde.Shell.Clipboard;

// All methods run on the PRIMARY worker. Wire structures use XCB's fixed-width
// protocol fields, rather than Xlib's platform-dependent long/XEvent layout.
internal sealed class XcbSelectionConnection : IDisposable
{
    private const uint Primary = 1, Atom = 4, Integer = 19;
    private const uint PropertyEvents = 1u << 22, StructureEvents = 1u << 17;
    private const int ChunkBytes = 64 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly nint _connection;
    private readonly uint _root, _window, _targets, _timestamp, _multiple, _atomPair, _incr, _property, _utf8;
    private readonly List<Outgoing> _outgoing = [];
    private Dictionary<uint, byte[]> _owned = [];
    private uint _ownedAt;
    internal int IncrementalSends { get; private set; }
    internal int IncrementalReceives { get; private set; }

    public XcbSelectionConnection()
    {
        _connection = XcbNative.Connect(0, out int screen);
        try
        {
            CheckConnection();
            var iterator = XcbNative.Roots(XcbNative.GetSetup(_connection));
            for (int i = 0; i < screen && iterator.Remaining > 0; i++) XcbNative.NextScreen(ref iterator);
            if (iterator.Remaining == 0) throw new NotSupportedException("No X11 screen is available.");
            _root = unchecked((uint)Marshal.ReadInt32(iterator.Data));
            _window = CreateWindow();
            _targets = Intern("TARGETS"); _timestamp = Intern("TIMESTAMP"); _multiple = Intern("MULTIPLE");
            _atomPair = Intern("ATOM_PAIR"); _incr = Intern("INCR"); _utf8 = Intern("UTF8_STRING");
            _property = Intern("_NTILDE_PRIMARY_TRANSFER");
        }
        catch { if (_connection != 0) XcbNative.Disconnect(_connection); throw; }
    }

    private uint CreateWindow()
    {
        uint window = XcbNative.GenerateId(_connection);
        WithBytes(BitConverter.GetBytes(PropertyEvents), data =>
            XcbNative.CreateWindow(_connection, 0, window, _root, 0, 0, 1, 1, 0, 2, 0, 1u << 11, data));
        return window;
    }

    private void Flush()
    {
        if (XcbNative.Flush(_connection) <= 0) throw new IOException("X11 PRIMARY connection failed to flush.");
    }

    private void CheckConnection()
    {
        if (_connection == 0 || XcbNative.HasError(_connection) != 0) throw new NotSupportedException("X11 PRIMARY connection is unavailable.");
    }

    private uint Intern(string name)
    {
        uint cookie = XcbNative.InternAtom(_connection, 0, checked((ushort)Encoding.UTF8.GetByteCount(name)), name);
        using var reply = Reply(cookie);
        return U32(reply.Pointer, 8);
    }

    private string AtomName(uint atom)
    {
        using var reply = Reply(XcbNative.GetAtomName(_connection, atom));
        int length = unchecked((ushort)Marshal.ReadInt16(reply.Pointer, 8));
        return length <= KittyClipboardPacket.MaxMimeChars ? Encoding.UTF8.GetString(Copy(reply.Pointer + 32, length)) : "";
    }

    private uint Owner()
    {
        using var reply = Reply(XcbNative.GetOwner(_connection, Primary));
        return U32(reply.Pointer, 8);
    }

    public XcbSelectionTargets CaptureTargets()
    {
        uint owner = Owner();
        var formats = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (owner == 0) return new(owner, formats);
        byte[] bytes = Convert(owner, _targets, (KittyClipboardPacket.MaxTypes + 16) * sizeof(uint), 32, Atom);
        if (bytes.Length % sizeof(uint) != 0) throw new IOException("Malformed PRIMARY targets.");
        foreach (uint target in Atoms(bytes))
        {
            string mime = target == _utf8 || target == 31 ? "text/plain" : AtomName(target);
            if (!KittyClipboardPacket.IsMime(mime)) continue;
            if (formats.TryGetValue(mime, out uint previous))
            {
                if (Priority(target) < Priority(previous)) formats[mime] = target;
            }
            else if (formats.Count < KittyClipboardPacket.MaxTypes) formats.Add(mime, target);
        }
        return new(owner, formats);
    }

    private int Priority(uint target) => target == 31 ? 2 : target == _utf8 ? 1 : 0;

    public byte[] Read(uint owner, uint target, int budget)
    {
        byte[] bytes = Convert(owner, target, budget, 8, target);
        if (target != 31) return bytes;
        if (bytes.Length + (long)bytes.Count(b => b >= 128) > Math.Max(0, budget)) throw new IOException("PRIMARY text exceeds the read budget.");
        return Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(bytes));
    }

    private byte[] Convert(uint owner, uint target, int budget, byte format, uint expectedType)
    {
        if (Owner() != owner) throw new IOException("PRIMARY owner changed during the read.");
        uint window = CreateWindow(); // Destroying it also cancels an incomplete INCR receive.
        try
        {
            _ = XcbNative.ConvertSelection(_connection, window, Primary, target, _property, 0);
            long started = Stopwatch.GetTimestamp();
            var notify = Wait(e => e.Type == 31 && e.U32(8) == window && e.U32(16) == target, started);
            if (notify.U32(20) == 0) throw new IOException("PRIMARY target was refused.");
            var first = ReadProperty(window, _property, budget, allowIncr: true);
            if (first.Type != _incr)
            {
                if (Owner() != owner) throw new IOException("PRIMARY owner changed during the read.");
                return Validate(first, format, expectedType);
            }
            IncrementalReceives++;
            if (first.Format != 32 || first.Bytes.Length != sizeof(uint)) throw new IOException("Malformed PRIMARY INCR header.");
            if (BitConverter.ToUInt32(first.Bytes) > Math.Max(0, budget)) throw new IOException("PRIMARY exceeds the read budget.");
            _ = XcbNative.DeleteProperty(_connection, window, _property);
            using var result = new MemoryStream();
            while (true)
            {
                Wait(e => e.Type == 28 && e.U32(4) == window && e.U32(8) == _property && e.Bytes[16] == 0, started);
                var chunk = ReadProperty(window, _property, budget - checked((int)result.Length), allowIncr: false);
                byte[] bytes = Validate(chunk, format, expectedType);
                _ = XcbNative.DeleteProperty(_connection, window, _property);
                if (bytes.Length == 0) break;
                result.Write(bytes);
            }
            if (Owner() != owner) throw new IOException("PRIMARY owner changed during the read.");
            return result.ToArray();
        }
        finally { _ = XcbNative.DestroyWindow(_connection, window); Flush(); }
    }

    private static byte[] Validate(Property value, byte format, uint type)
    {
        if (value.Format != format || value.Type != type) throw new IOException("Unexpected PRIMARY property type.");
        return value.Bytes;
    }

    private Property ReadProperty(uint window, uint property, int budget, bool allowIncr)
    {
        using var value = new MemoryStream();
        uint offset = 0, type = 0; byte format = 0;
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            CheckDeadline(started);
            using var reply = Reply(XcbNative.GetProperty(_connection, 0, window, property, 0, offset, ChunkBytes / 4));
            uint currentType = U32(reply.Pointer, 8), after = U32(reply.Pointer, 12), count = U32(reply.Pointer, 16);
            byte currentFormat = Marshal.ReadByte(reply.Pointer, 1);
            if (offset == 0) { type = currentType; format = currentFormat; }
            if (format is not (8 or 32) || format != currentFormat || type != currentType) throw new IOException("Malformed PRIMARY property.");
            long length = (long)count * (format / 8);
            int limit = allowIncr && type == _incr ? sizeof(uint) : Math.Max(0, budget);
            if (length > ChunkBytes || value.Length + length + after > limit) throw new IOException("PRIMARY exceeds the read budget.");
            value.Write(Copy(reply.Pointer + 32, checked((int)length)));
            if (after == 0) break;
            if (length == 0 || length % 4 != 0) throw new IOException("Invalid PRIMARY property continuation.");
            offset += checked((uint)(length / 4));
        }
        return new(type, format, value.ToArray());
    }

    public void Write(IReadOnlyDictionary<string, byte[]> values)
    {
        if (values.Count == 0)
        {
            if (Owner() == _window) _ = XcbNative.SetOwner(_connection, 0, Primary, 0);
            _owned.Clear(); Flush(); return;
        }
        if (values.Count > KittyClipboardPacket.MaxTypes || values.Any(p => !KittyClipboardPacket.IsMime(p.Key)) ||
            values.Values.Sum(v => (long)v.Length) > 2L * KittyClipboardPacket.MaxDataBytes)
            throw new ClipboardSizeLimitException("PRIMARY exceeds the native expansion budget.");
        var copied = new Dictionary<byte[], byte[]>(ReferenceEqualityComparer.Instance);
        var owned = new Dictionary<uint, byte[]>();
        foreach (var (mime, bytes) in values)
        {
            if (!copied.TryGetValue(bytes, out byte[]? copy)) { copy = bytes.ToArray(); copied.Add(bytes, copy); }
            owned[Intern(mime)] = copy;
            if (mime == "text/plain") owned[_utf8] = copy;
        }
        // Obtain a server timestamp through our own property notification.
        Change(_window, _property, Integer, 32, BitConverter.GetBytes(0));
        var timestamp = Wait(e => e.Type == 28 && e.U32(4) == _window && e.U32(8) == _property, Stopwatch.GetTimestamp());
        uint time = timestamp.U32(12);
        _ = XcbNative.SetOwner(_connection, _window, Primary, time);
        if (Owner() != _window) throw new IOException("Could not own PRIMARY.");
        _owned = owned; _ownedAt = time;
    }

    public void Pump()
    {
        CheckConnection();
        _outgoing.RemoveAll(s => Stopwatch.GetElapsedTime(s.Started) >= Deadline);
        for (int count = 0; count < 256 && TakeEvent() is Event item; count++) Dispatch(item);
        Flush();
    }

    private void Dispatch(Event item)
    {
        switch (item.Type)
        {
            case 30: Serve(item); break;
            case 29: _owned.Clear(); break;
            case 28 when item.Bytes[16] == 1: Advance(item.U32(4), item.U32(8)); break;
            case 17: _outgoing.RemoveAll(s => s.Window == item.U32(8)); break;
            default: break;
        }
    }

    private void Serve(Event request)
    {
        uint requestor = request.U32(12), target = request.U32(20), property = request.U32(24);
        if (property == 0) property = target;
        bool served = request.U32(8) == _window && request.U32(16) == Primary &&
            (target == _multiple ? ServeMultiple(requestor, property) : ServeTarget(requestor, target, property));
        byte[] notify = new byte[32]; notify[0] = 31;
        Put(notify, 4, request.U32(4)); Put(notify, 8, requestor); Put(notify, 12, Primary);
        Put(notify, 16, target); Put(notify, 20, served ? property : 0);
        WithBytes(notify, pointer => XcbNative.SendEvent(_connection, 0, requestor, 0, pointer));
        Flush();
    }

    private bool ServeMultiple(uint requestor, uint property)
    {
        try
        {
            var value = ReadProperty(requestor, property, KittyClipboardPacket.MaxTypes * 8, allowIncr: false);
            uint[] pairs = Atoms(Validate(value, 32, _atomPair));
            if (pairs.Length % 2 != 0) return false;
            var used = new HashSet<uint> { property };
            for (int i = 0; i < pairs.Length; i += 2)
                if (pairs[i] == _multiple || pairs[i + 1] == 0 || !used.Add(pairs[i + 1]) || !ServeTarget(requestor, pairs[i], pairs[i + 1])) pairs[i + 1] = 0;
            Change(requestor, property, _atomPair, 32, Bytes(pairs)); return true;
        }
        catch (IOException) { return false; }
    }

    private bool ServeTarget(uint window, uint target, uint property)
    {
        if (_owned.Count == 0) return false;
        if (target == _targets) { Change(window, property, Atom, 32, Bytes(_owned.Keys.Concat(new[] { _targets, _timestamp, _multiple }))); return true; }
        if (target == _timestamp) { Change(window, property, Integer, 32, BitConverter.GetBytes(_ownedAt)); return true; }
        if (!_owned.TryGetValue(target, out byte[]? bytes)) return false;
        if (bytes.Length <= ChunkBytes) { Change(window, property, target, 8, bytes); return true; }
        if (_outgoing.Count >= 16 || _outgoing.Sum(s => (long)s.Bytes.Length) + bytes.Length > 2L * KittyClipboardPacket.MaxDataBytes ||
            _outgoing.Any(s => s.Window == window && s.Property == property)) return false;
        WithBytes(BitConverter.GetBytes(PropertyEvents | StructureEvents), data => XcbNative.ChangeAttributes(_connection, window, 1u << 11, data));
        _outgoing.Add(new(window, property, target, bytes, Stopwatch.GetTimestamp()));
        IncrementalSends++;
        Change(window, property, _incr, 32, BitConverter.GetBytes(checked((uint)bytes.Length)));
        return true;
    }

    private void Advance(uint window, uint property)
    {
        var stream = _outgoing.FirstOrDefault(s => s.Window == window && s.Property == property);
        if (stream == null) return;
        if (Stopwatch.GetElapsedTime(stream.Started) >= Deadline) { _outgoing.Remove(stream); return; }
        int count = Math.Min(ChunkBytes, stream.Bytes.Length - stream.Offset);
        Change(window, property, stream.Target, 8, stream.Bytes.AsSpan(stream.Offset, count).ToArray());
        stream.Offset += count;
        if (count == 0) _outgoing.Remove(stream);
        Flush();
    }

    private void Change(uint window, uint property, uint type, byte format, byte[] bytes) =>
        WithBytes(bytes, pointer => XcbNative.ChangeProperty(_connection, 0, window, property, type, format, checked((uint)bytes.Length / (uint)(format / 8)), pointer));

    private ReplyMemory Reply(uint sequence)
    {
        long started = Stopwatch.GetTimestamp(); Flush();
        try
        {
            while (true)
            {
                CheckConnection();
                if (XcbNative.PollReply(_connection, sequence, out nint reply, out nint error) != 0)
                {
                    if (error != 0) { XcbNative.Free(error); if (reply != 0) XcbNative.Free(reply); throw new IOException("X11 PRIMARY request was refused."); }
                    if (reply == 0) throw new IOException("X11 PRIMARY returned no reply.");
                    return new(reply);
                }
                CheckDeadline(started); Thread.Sleep(1);
            }
        }
        catch { XcbNative.DiscardReply(_connection, sequence); throw; }
    }

    private Event Wait(Func<Event, bool> matches, long started)
    {
        Flush();
        while (true)
        {
            CheckConnection(); CheckDeadline(started);
            if (TakeEvent() is Event item)
            {
                if (matches(item)) return item;
                Dispatch(item);
            }
            else Thread.Sleep(1);
        }
    }

    private Event? TakeEvent()
    {
        nint pointer = XcbNative.PollEvent(_connection);
        if (pointer == 0) return null;
        try { return new(Copy(pointer, 32)); }
        finally { XcbNative.Free(pointer); }
    }

    private static void CheckDeadline(long started)
    {
        if (Stopwatch.GetElapsedTime(started) >= Deadline) throw new IOException("X11 PRIMARY transfer timed out.");
    }

    private static void WithBytes(byte[] bytes, Func<nint, uint> action)
    {
        nint pointer = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); _ = action(pointer); }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static byte[] Copy(nint source, int length) { byte[] bytes = new byte[length]; Marshal.Copy(source, bytes, 0, length); return bytes; }
    private static uint U32(nint pointer, int offset) => unchecked((uint)Marshal.ReadInt32(pointer, offset));
    private static void Put(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    private static uint[] Atoms(byte[] bytes) => Enumerable.Range(0, bytes.Length / 4).Select(i => BitConverter.ToUInt32(bytes, i * 4)).ToArray();
    private static byte[] Bytes(IEnumerable<uint> values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    public void Dispose() { XcbNative.Disconnect(_connection); _owned.Clear(); _outgoing.Clear(); }
    private sealed record Property(uint Type, byte Format, byte[] Bytes);
    private sealed record Event(byte[] Bytes) { public int Type => Bytes[0] & 127; public uint U32(int offset) => BitConverter.ToUInt32(Bytes, offset); }
    private sealed class ReplyMemory(nint pointer) : IDisposable { public nint Pointer => pointer; public void Dispose() => XcbNative.Free(pointer); }
    private sealed class Outgoing(uint window, uint property, uint target, byte[] bytes, long started)
    {
        public uint Window => window;
        public uint Property => property;
        public uint Target => target;
        public byte[] Bytes => bytes;
        public long Started => started;
        public int Offset;
    }
}
