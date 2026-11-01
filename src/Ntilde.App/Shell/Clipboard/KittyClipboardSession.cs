using System.Text;
using Ntilde.VT;

namespace Ntilde.Shell.Clipboard;

internal interface IKittyClipboard
{
    bool Available { get; }
    Task<string[]> GetTypesAsync();
    Task<byte[]?> ReadAsync(string mime);
    Task WriteAsync(IReadOnlyDictionary<string, byte[]> data);
    Task<bool> ConfirmReadAsync(string name, CancellationToken cancellation);
}

/// <summary>
/// One TTY's bounded clipboard transactions and paste grant. Accept runs on the output thread;
/// clipboard operations and prompts run through the UI scheduler. Neither is run under the VT lock.
/// </summary>
internal sealed class KittyClipboardSession : IDisposable
{
    private static readonly SemaphoreSlim ClipboardGate = new(1, 1);
    private readonly object _sync = new();
    private readonly IKittyClipboard _clipboard;
    private readonly Action<string> _reply;
    private readonly Func<Func<Task>, Task> _schedule;
    private readonly Func<bool> _writeAllowed;
    private readonly Func<bool> _alive;
    private readonly TimeProvider _clock;
    private WriteTransfer? _write;
    private bool _busy, _disposed;
    private int _generation;
    private CancellationTokenSource _cancellation = new();
    private string? _pastePassword;
    private DateTimeOffset _pasteExpiry;

    public KittyClipboardSession(IKittyClipboard clipboard, Action<string> reply, Func<Func<Task>, Task> schedule,
        Func<bool> writeAllowed, Func<bool> alive, TimeProvider? clock = null)
    {
        _clipboard = clipboard;
        _reply = reply;
        _schedule = schedule;
        _writeAllowed = writeAllowed;
        _alive = alive;
        _clock = clock ?? TimeProvider.System;
    }

    public void Accept(string data)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var packet = KittyClipboardPacket.Parse(data);
            if (packet == null)
            {
                if (_write != null) Abort("EINVAL");
                else if (data.StartsWith("type=write", StringComparison.Ordinal)) Reply("write", "EINVAL", "");
                return;
            }
            if (packet.Type == "read") { Read(packet); return; }
            if (packet.Type == "write") { StartWrite(packet); return; }
            if (_write == null) return; // after error, ignore until the next start
            if (_clock.GetUtcNow() - _write.Started > TimeSpan.FromMinutes(1)) { Abort("EBUSY"); return; }
            try
            {
                switch (packet.Type)
                {
                    case "wdata":
                        if (packet.Mime.Length == 0 && packet.Payload.Length == 0) FinishWrite();
                        else _write.Add(packet);
                        break;
                    case "walias": _write.Alias(packet); break;
                }
            }
            catch (ClipboardPacketException error) { Abort(error.Status); }
        }
    }

    private void StartWrite(KittyClipboardPacket packet)
    {
        _write?.Dispose();
        _write = null;
        string? error = packet.Primary || !_clipboard.Available ? "ENOSYS" : _busy ? "EBUSY" : !_writeAllowed() ? "EPERM" : null;
        if (error != null) { Reply("write", error, packet.Id); return; }
        if (packet.Payload.Length != 0 || !KittyClipboardPacket.TryDecodeText(packet.Password, out _) ||
            !KittyClipboardPacket.TryDecodeText(packet.Name, out _)) { Reply("write", "EINVAL", packet.Id); return; }
        _write = new WriteTransfer(packet.Id, _clock.GetUtcNow());
    }

    private void Abort(string status)
    {
        if (_write == null) return;
        string id = _write.Id;
        _write.Dispose();
        _write = null;
        Reply("write", status, id);
    }

    private void FinishWrite()
    {
        var transfer = _write!;
        var values = transfer.Finish();
        string id = transfer.Id;
        transfer.Dispose();
        _write = null;
        _busy = true;
        int generation = _generation;
        _ = _schedule(async () =>
        {
            bool gate = false;
            try
            {
                if (!IsAlive(generation)) return;
                if (!_writeAllowed()) { Reply("write", "EPERM", id); return; }
                gate = ClipboardGate.Wait(0);
                if (!gate) { Reply("write", "EBUSY", id); return; }
                await _clipboard.WriteAsync(values);
                if (IsAlive(generation)) Reply("write", "DONE", id);
            }
            catch (NotSupportedException) { if (IsAlive(generation)) Reply("write", "ENOSYS", id); }
            catch (UnauthorizedAccessException) { if (IsAlive(generation)) Reply("write", "EPERM", id); }
            catch (Exception) { if (IsAlive(generation)) Reply("write", "EIO", id); }
            finally { if (gate) ClipboardGate.Release(); Complete(generation); }
        });
    }

    private void Read(KittyClipboardPacket packet)
    {
        if (!KittyClipboardPacket.TryDecodeText(packet.Payload, out string requested)) return;
        string[] types = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        bool listing = requested == ".";
        if (types.Length == 0 || types.Length > KittyClipboardPacket.MaxTypes || (!listing && types.Any(t => !KittyClipboardPacket.IsMime(t)))) return;
        if (!KittyClipboardPacket.TryDecodeText(packet.Name, out string name) || !KittyClipboardPacket.TryDecodeText(packet.Password, out string password)) return;
        string? error = packet.Primary || !_clipboard.Available ? "ENOSYS" : _busy || _write != null ? "EBUSY" : null;
        if (error != null) { Reply("read", error, packet.Id); return; }
        bool granted = !listing && name.Length > 0 && password.Length > 0 && password == _pastePassword && _clock.GetUtcNow() < _pasteExpiry;
        if (granted) _pastePassword = null; // consume before any await, once on this TTY and location
        _busy = true;
        int generation = _generation;
        _ = _schedule(() => ReadAsync(packet.Id, types, listing, name, granted, generation));
    }

    private async Task ReadAsync(string id, string[] types, bool listing, string name, bool granted, int generation)
    {
        bool gate = false;
        try
        {
            if (!IsAlive(generation)) return;
            gate = ClipboardGate.Wait(0);
            if (!gate) { Reply("read", "EBUSY", id); return; }
            if (!listing && !granted && !await _clipboard.ConfirmReadAsync(SafeName(name), _cancellation.Token))
            { if (IsAlive(generation)) Reply("read", "EPERM", id); return; }
            if (!IsAlive(generation)) return;
            var available = (await _clipboard.GetTypesAsync()).Where(KittyClipboardPacket.IsMime).Distinct(StringComparer.Ordinal).Take(KittyClipboardPacket.MaxTypes).ToArray();
            var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (listing) values["."] = Encoding.UTF8.GetBytes(string.Join(' ', available));
            else
            {
                long total = 0;
                foreach (string mime in types.Where(available.Contains))
                {
                    if (!IsAlive(generation)) return;
                    byte[]? bytes = await _clipboard.ReadAsync(mime);
                    if (bytes == null) continue;
                    total += bytes.Length;
                    if (total > KittyClipboardPacket.MaxDataBytes) { Reply("read", "EBUSY", id); return; }
                    values[mime] = bytes;
                }
            }
            if (IsAlive(generation)) SendData(id, values, generation: generation);
        }
        catch (NotSupportedException) { if (IsAlive(generation)) Reply("read", "ENOSYS", id); }
        catch (UnauthorizedAccessException) { if (IsAlive(generation)) Reply("read", "EPERM", id); }
        catch (Exception) { if (IsAlive(generation)) Reply("read", "EBUSY", id); }
        finally { if (gate) ClipboardGate.Release(); Complete(generation); }
    }

    public async Task NotifyPasteAsync()
    {
        int generation;
        lock (_sync)
        {
            if (_disposed) return;
            if (_busy || _write != null) { Reply("read", "EBUSY", ""); return; }
            _busy = true;
            generation = _generation;
        }
        try
        {
            string[] types = await _clipboard.GetTypesAsync();
            string password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            lock (_sync)
            {
                if (!IsAlive(generation)) return;
                _pastePassword = password;
                _pasteExpiry = _clock.GetUtcNow().AddSeconds(15);
                SendData("", new Dictionary<string, byte[]> { ["."] = Encoding.UTF8.GetBytes(string.Join(' ', types.Where(KittyClipboardPacket.IsMime).Distinct().Take(KittyClipboardPacket.MaxTypes))) }, ":pw=" + KittyClipboardPacket.Encode(password));
            }
        }
        catch (Exception) { if (IsAlive(generation)) Reply("read", "EBUSY", ""); }
        finally { Complete(generation); }
    }

    private void SendData(string id, IReadOnlyDictionary<string, byte[]> data, string metadata = "", int? generation = null)
    {
        _reply(KittyClipboardPacket.Response("read", "OK", id, metadata));
        foreach (var (mime, bytes) in data)
        {
            for (int offset = 0; offset < bytes.Length || offset == 0; offset += 4096)
            {
                if (generation is int expected && !IsAlive(expected)) return;
                int count = Math.Min(4096, bytes.Length - offset);
                _reply(KittyClipboardPacket.Response("read", "DATA", id, ":mime=" + KittyClipboardPacket.Encode(mime), Convert.ToBase64String(bytes, offset, count)));
            }
        }
        if (generation is not int current || IsAlive(current)) Reply("read", "DONE", id);
    }

    private static string SafeName(string name) => new(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).Take(80).ToArray());
    private void Reply(string type, string status, string id) => _reply(KittyClipboardPacket.Response(type, status, id));
    private bool IsAlive(int generation) { lock (_sync) return !_disposed && generation == _generation && _alive(); }
    private void Complete(int generation) { lock (_sync) _busy = false; }
    public void Reset()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _generation++;
            _cancellation.Cancel();
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
            _write?.Dispose();
            _write = null;
            _pastePassword = null;
        }
    }
    public void Dispose() { lock (_sync) { if (_disposed) return; Reset(); _disposed = true; _cancellation.Dispose(); } }

    private sealed class ClipboardPacketException(string status) : Exception
    {
        public string Status { get; } = status;
    }

    private sealed class WriteTransfer(string id, DateTimeOffset started) : IDisposable
    {
        public string Id { get; } = id;
        public DateTimeOffset Started { get; } = started;
        private readonly Dictionary<string, MemoryStream> _values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
        private string? _current;
        private string _carry = "";
        private bool _padded;
        private int _size;

        public void Add(KittyClipboardPacket packet)
        {
            if (!KittyClipboardPacket.TryDecodeText(packet.Mime, out string mime) || !KittyClipboardPacket.IsMime(mime)) throw new ClipboardPacketException("EINVAL");
            if (mime != _current)
            {
                FinishType();
                if (_values.ContainsKey(mime) || _aliases.ContainsKey(mime)) throw new ClipboardPacketException("EINVAL");
                if (_values.Count + _aliases.Count >= KittyClipboardPacket.MaxTypes) throw new ClipboardPacketException("EFBIG");
                _values[mime] = new MemoryStream();
                _current = mime;
                _padded = false;
            }
            if (_padded && packet.Payload.Length > 0) throw new ClipboardPacketException("EINVAL");
            string encoded = _carry + packet.Payload;
            if (encoded.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))) throw new ClipboardPacketException("EINVAL");
            int full = encoded.Length / 4 * 4;
            if (!KittyClipboardPacket.TryDecode(encoded[..full], out byte[] bytes)) throw new ClipboardPacketException("EINVAL");
            if (bytes.Length > KittyClipboardPacket.MaxDataBytes - _size) throw new ClipboardPacketException("EFBIG");
            _values[mime].Write(bytes);
            _size += bytes.Length;
            _padded = encoded.AsSpan(0, full).Contains('=');
            _carry = encoded[full..];
            if (_padded && _carry.Length != 0) throw new ClipboardPacketException("EINVAL");
        }

        private void FinishType() { if (_carry.Length != 0) throw new ClipboardPacketException("EINVAL"); }

        public void Alias(KittyClipboardPacket packet)
        {
            if (!KittyClipboardPacket.TryDecodeText(packet.Mime, out string target) || !KittyClipboardPacket.IsMime(target) ||
                !KittyClipboardPacket.TryDecodeText(packet.Payload, out string aliases) || string.IsNullOrWhiteSpace(aliases)) throw new ClipboardPacketException("EINVAL");
            foreach (string alias in aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!KittyClipboardPacket.IsMime(alias) || alias == target || _values.ContainsKey(alias) || !_aliases.TryAdd(alias, target)) throw new ClipboardPacketException("EINVAL");
                if (_values.Count + _aliases.Count > KittyClipboardPacket.MaxTypes) throw new ClipboardPacketException("EFBIG");
            }
        }

        public Dictionary<string, byte[]> Finish()
        {
            FinishType();
            // Native clipboards may materialize every alias separately. Bound that expansion
            // before flattening buffers or passing any data to the OS, while allowing a full
            // 64 MiB payload plus one alias.
            long materialized = _size;
            foreach (string target in _aliases.Values)
            {
                if (!_values.TryGetValue(target, out var stream)) throw new ClipboardPacketException("EINVAL");
                materialized += stream.Length;
                if (materialized > 2L * KittyClipboardPacket.MaxDataBytes) throw new ClipboardPacketException("EFBIG");
            }
            var values = _values.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
            foreach (var (alias, target) in _aliases) values[alias] = values[target];
            return values;
        }
        public void Dispose() { foreach (var stream in _values.Values) stream.Dispose(); _values.Clear(); }
    }
}
