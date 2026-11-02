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
    private readonly uint _root, _window, _targets, _timestamp, _multiple, _atomPair, _incr, _property, _utf8, _plain;
    private readonly HashSet<uint> _localWindows = [];
    private readonly PrimaryTransferRegistry _outgoing;
    private readonly PrimarySelectionOwnership _ownership = new();
    internal int LastAdvertisedTargetCount { get; private set; }
    internal int IncrementalSends { get; private set; }
    internal int IncrementalReceives { get; private set; }
    internal (int Sent, int Received) TransferCounts() => (IncrementalSends, IncrementalReceives);

    public XcbSelectionConnection()
    {
        _outgoing = new PrimaryTransferRegistry(Subscribe, window => _localWindows.Contains(window));
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
            _property = Intern("_NTILDE_PRIMARY_TRANSFER"); _plain = Intern("text/plain");
        }
        catch
        {
            if (_connection != 0) XcbNative.Disconnect(_connection);
            throw;
        }
    }

    private uint CreateWindow()
    {
        uint window = XcbNative.GenerateId(_connection);
        WithBytes(BitConverter.GetBytes(PropertyEvents), data =>
            XcbNative.CreateWindow(_connection, 0, window, _root, 0, 0, 1, 1, 0, 2, 0, 1u << 11, data));
        _localWindows.Add(window);
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

    private uint Intern(string name, bool onlyIfExists = false)
    {
        uint cookie = XcbNative.InternAtom(_connection, onlyIfExists ? (byte)1 : (byte)0, checked((ushort)Encoding.UTF8.GetByteCount(name)), name);
        using var reply = Reply(cookie);
        return ReadU32(reply.Pointer, 8);
    }

    private string AtomName(uint atom)
    {
        using var reply = Reply(XcbNative.GetAtomName(_connection, atom));
        int length = unchecked((ushort)Marshal.ReadInt16(reply.Pointer, 8));
        return length <= KittyClipboardPacket.MaxMimeChars ? Encoding.UTF8.GetString(Copy(reply.Pointer + 32, length)) : "";
    }

    internal uint Owner()
    {
        using var reply = Reply(XcbNative.GetOwner(_connection, Primary));
        return ReadU32(reply.Pointer, 8);
    }

    private XcbSelectionTargets CaptureTargets(IReadOnlyList<string>? requestedTypes)
    {
        uint owner = Owner();
        LastAdvertisedTargetCount = 0;
        if (owner == 0) return new(owner, [], false);
        byte[] bytes = Convert(owner, _targets, ChunkBytes, 32, Atom);
        if (bytes.Length % sizeof(uint) != 0) throw new IOException("Malformed PRIMARY targets.");
        uint[] atoms = Atoms(bytes);
        LastAdvertisedTargetCount = atoms.Length;
        var catalog = new PrimaryTargetCatalog(_utf8, _plain, AtomName, mime => Intern(mime, onlyIfExists: true));
        return new(owner, catalog.Select(atoms, requestedTypes), atoms.Contains(_timestamp));
    }

    public IKittyClipboardSnapshot CaptureSnapshot(IReadOnlyList<string>? requestedTypes)
    {
        var targets = CaptureTargets(requestedTypes);
        if (requestedTypes?.Count == 0) return PrimarySelectionSnapshot.Capture(targets.Formats, [], (_, _) => throw new InvalidOperationException("Listing cannot read data."));
        uint? timestamp = targets.HasTimestamp ? ReadTimestamp(targets.Owner) : null;
        var snapshot = PrimarySelectionSnapshot.Capture(targets.Formats, requestedTypes, (atom, budget) => Read(targets.Owner, atom, budget));
        try
        {
            if (Owner() != targets.Owner || (timestamp.HasValue && ReadTimestamp(targets.Owner) != timestamp.Value))
                throw new IOException("PRIMARY changed while capturing the snapshot.");
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    private uint ReadTimestamp(uint owner) => BitConverter.ToUInt32(Convert(owner, _timestamp, sizeof(uint), 32, Integer));

    internal byte[] ReadWithBudgetForTest(string mime, int budget) => Read(Owner(), Intern(mime, onlyIfExists: true), budget);

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
            var notify = Wait(e => e.Kind == 31 && e.Field(8) == window && e.Field(16) == target, started);
            if (notify.Field(20) == 0) throw new IOException("PRIMARY target was refused.");
            var first = ReadProperty(window, _property, budget, allowIncr: true);
            if (first.DataType != _incr)
            {
                if (Owner() != owner) throw new IOException("PRIMARY owner changed during the read.");
                return Validate(first, format, expectedType);
            }
            IncrementalReceives++;
            XcbPrimaryClipboard.TraceForTest?.Invoke($"INCR receive owner={owner} target={target} budget={budget}");
            if (first.BitWidth != 32 || first.Data.Length != sizeof(uint)) throw new IOException("Malformed PRIMARY INCR header.");
            if (BitConverter.ToUInt32(first.Data) > Math.Max(0, budget)) throw new IOException("PRIMARY exceeds the read budget.");
            _ = XcbNative.DeleteProperty(_connection, window, _property);
            using var result = new MemoryStream();
            var receiveDeadline = new PrimaryReceiveDeadline();
            receiveDeadline.Progress();
            while (true)
            {
                Wait(e => e.Kind == 28 && e.Field(4) == window && e.Field(8) == _property && e.Data[16] == 0, started, receiveDeadline);
                var chunk = ReadProperty(window, _property, budget - checked((int)result.Length), allowIncr: false);
                byte[] bytes = Validate(chunk, format, expectedType);
                _ = XcbNative.DeleteProperty(_connection, window, _property);
                receiveDeadline.Check();
                if (bytes.Length == 0) break;
                result.Write(bytes);
                receiveDeadline.Progress();
                XcbPrimaryClipboard.TraceForTest?.Invoke($"INCR received bytes={bytes.Length} total={result.Length}");
            }
            if (Owner() != owner) throw new IOException("PRIMARY owner changed during the read.");
            return result.ToArray();
        }
        finally { _outgoing.Destroyed(window); _localWindows.Remove(window); _ = XcbNative.DestroyWindow(_connection, window); Flush(); }
    }

    private static byte[] Validate(NativeProperty value, byte format, uint type)
    {
        if (value.BitWidth != format || value.DataType != type) throw new IOException("Unexpected PRIMARY property type.");
        return value.Data;
    }

    private NativeProperty ReadProperty(uint window, uint property, int budget, bool allowIncr)
    {
        using var value = new MemoryStream();
        uint offset = 0, type = 0; byte format = 0;
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            CheckDeadline(started);
            using var reply = Reply(XcbNative.GetProperty(_connection, 0, window, property, 0, offset, ChunkBytes / 4));
            var header = new PropertyHeader(ReadU32(reply.Pointer, 8), Marshal.ReadByte(reply.Pointer, 1), ReadU32(reply.Pointer, 16), ReadU32(reply.Pointer, 12));
            if (offset == 0) { type = header.DataType; format = header.BitWidth; }
            int limit = allowIncr && type == _incr ? sizeof(uint) : Math.Max(0, budget);
            int length = PropertyChunkLength(header, format, type, limit - value.Length);
            value.Write(Copy(reply.Pointer + 32, length));
            if (header.Remaining == 0) break;
            if (length == 0 || length % 4 != 0) throw new IOException("Invalid PRIMARY property continuation.");
            offset += checked((uint)(length / 4));
        }
        return new(type, format, value.ToArray());
    }

    private static int PropertyChunkLength(PropertyHeader header, byte format, uint type, long remaining)
    {
        if (format is not (8 or 32) || format != header.BitWidth || type != header.DataType) throw new IOException("Malformed PRIMARY property.");
        long length = (long)header.ItemCount * (format / 8);
        if (length > ChunkBytes || length + header.Remaining > remaining) throw new IOException("PRIMARY exceeds the read budget.");
        return checked((int)length);
    }

    public void Write(IReadOnlyDictionary<string, byte[]> values)
    {
        if (values.Count == 0)
        {
            _ = XcbNative.SetOwner(_connection, 0, Primary, 0);
            if (Owner() != 0) throw new IOException("Could not clear PRIMARY.");
            _ownership.Clear(); Flush(); return;
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
        var timestamp = Wait(e => e.Kind == 28 && e.Field(4) == _window && e.Field(8) == _property, Stopwatch.GetTimestamp());
        uint time = timestamp.Field(12);
        _ = XcbNative.SetOwner(_connection, _window, Primary, time);
        if (Owner() != _window) throw new IOException("Could not own PRIMARY.");
        _ownership.Publish(owned, time);
    }

    public void Pump()
    {
        CheckConnection();
        _outgoing.Expire();
        for (int count = 0; count < 256 && TakeEvent() is Event item; count++) Dispatch(item);
        Flush();
    }

    private void Dispatch(Event item)
    {
        switch (item.Kind)
        {
            case 30: Serve(item); break;
            case 29: _ownership.SelectionCleared(Owner(), _window); break;
            case 28 when item.Data[16] == 1: Advance(item.Field(4), item.Field(8)); break;
            case 17: _outgoing.Destroyed(item.Field(8)); break;
            default: break;
        }
    }

    private void Serve(Event request)
    {
        uint requestor = request.Field(12), target = request.Field(20), property = request.Field(24);
        XcbPrimaryClipboard.TraceForTest?.Invoke($"request window={requestor} target={target} property={property}");
        if (property == 0) property = target;
        bool served = request.Field(8) == _window && request.Field(16) == Primary &&
            _ownership.AcceptsRequest(request.Field(4)) && Owner() == _window &&
            (target == _multiple ? ServeMultiple(requestor, property) : ServeTarget(requestor, target, property));
        byte[] notify = new byte[32]; notify[0] = 31;
        Put(notify, 4, request.Field(4)); Put(notify, 8, requestor); Put(notify, 12, Primary);
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
            Change(requestor, property, _atomPair, 32, EncodeAtoms(pairs)); return true;
        }
        catch (IOException) { return false; }
    }

    private bool ServeTarget(uint window, uint target, uint property)
    {
        if (_ownership.Values.Count == 0) return false;
        if (target == _targets) { Change(window, property, Atom, 32, EncodeAtoms(_ownership.Values.Keys.Concat(new[] { _targets, _timestamp, _multiple }))); return true; }
        if (target == _timestamp) { Change(window, property, Integer, 32, BitConverter.GetBytes(_ownership.Timestamp)); return true; }
        if (!_ownership.Values.TryGetValue(target, out byte[]? bytes)) return false;
        XcbPrimaryClipboard.TraceForTest?.Invoke($"serve window={window} target={target} property={property} bytes={bytes.Length}");
        if (bytes.Length <= ChunkBytes) { Change(window, property, target, 8, bytes); return true; }
        if (!_outgoing.TryStart(window, property, target, bytes)) return false;
        IncrementalSends++;
        Change(window, property, _incr, 32, BitConverter.GetBytes(checked((uint)bytes.Length)));
        return true;
    }

    private void Advance(uint window, uint property)
    {
        _outgoing.Expire();
        var stream = _outgoing.Find(window, property);
        if (stream == null) return;
        int count = Math.Min(ChunkBytes, stream.Payload.Length - stream.Offset);
        Change(window, property, stream.Target, 8, stream.Payload.AsSpan(stream.Offset, count).ToArray());
        stream.Offset += count;
        _outgoing.Progress(stream);
        XcbPrimaryClipboard.TraceForTest?.Invoke($"INCR send window={window} property={property} bytes={count} offset={stream.Offset}");
        if (count == 0) _outgoing.Retire(stream);
        Flush();
    }

    private void Subscribe(uint window, bool enabled)
    {
        WithBytes(BitConverter.GetBytes(enabled ? PropertyEvents | StructureEvents : 0), data => XcbNative.ChangeAttributes(_connection, window, 1u << 11, data));
        XcbPrimaryClipboard.TraceForTest?.Invoke($"subscription window={window} enabled={enabled}");
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
                    return CheckedReply(reply, error);
                CheckDeadline(started); Thread.Sleep(1);
            }
        }
        catch { XcbNative.DiscardReply(_connection, sequence); throw; }
    }

    private static ReplyMemory CheckedReply(nint reply, nint error)
    {
        if (error != 0)
        {
            byte code = Marshal.ReadByte(error, 1);
            XcbNative.Free(error);
            if (reply != 0) XcbNative.Free(reply);
            if (code == 5) throw new PrimaryInvalidAtomException();
            throw new IOException("X11 PRIMARY request was refused.");
        }
        if (reply == 0) throw new IOException("X11 PRIMARY returned no reply.");
        return new(reply);
    }

    private Event Wait(Func<Event, bool> matches, long started, PrimaryReceiveDeadline? receiveDeadline = null)
    {
        Flush();
        while (true)
        {
            CheckConnection();
            if (receiveDeadline == null) CheckDeadline(started);
            else receiveDeadline.Check();
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
    private static uint ReadU32(nint pointer, int offset) => unchecked((uint)Marshal.ReadInt32(pointer, offset));
    private static void Put(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    private static uint[] Atoms(byte[] bytes) => Enumerable.Range(0, bytes.Length / 4).Select(i => BitConverter.ToUInt32(bytes, i * 4)).ToArray();
    private static byte[] EncodeAtoms(IEnumerable<uint> values) => values.SelectMany(BitConverter.GetBytes).ToArray();

    public void Dispose() { XcbNative.Disconnect(_connection); _ownership.Clear(); _localWindows.Clear(); }
    private sealed record PropertyHeader(uint DataType, byte BitWidth, uint ItemCount, uint Remaining);
    private sealed record NativeProperty(uint DataType, byte BitWidth, byte[] Data);
    private sealed record Event(byte[] Data) { public int Kind => Data[0] & 127; public uint Field(int offset) => BitConverter.ToUInt32(Data, offset); }
    private sealed class ReplyMemory(nint pointer) : IDisposable { public nint Pointer => pointer; public void Dispose() => XcbNative.Free(pointer); }
}
