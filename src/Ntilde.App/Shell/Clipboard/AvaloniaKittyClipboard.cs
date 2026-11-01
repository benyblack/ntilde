using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using System.Text;

namespace Ntilde.Shell.Clipboard;

/// <summary>UI-thread clipboard adapter. It never falls back from primary to the system clipboard.</summary>
internal sealed class AvaloniaKittyClipboard : IKittyClipboard
{
    private readonly Func<Task<IAsyncDataTransfer?>> _read;
    private readonly Func<IAsyncDataTransfer?, Task> _write;
    private readonly Func<Task> _clear;
    private readonly Func<string, CancellationToken, Task<bool>> _confirm;

    public AvaloniaKittyClipboard(Func<IClipboard?> getClipboard, Func<string, CancellationToken, Task<bool>> confirm)
        : this(() => Require(getClipboard).TryGetDataAsync(), data => Require(getClipboard).SetDataAsync(data),
            () => Require(getClipboard).ClearAsync(), confirm)
    { }

    internal AvaloniaKittyClipboard(Func<Task<IAsyncDataTransfer?>> read, Func<IAsyncDataTransfer?, Task> write,
        Func<Task> clear, Func<string, CancellationToken, Task<bool>> confirm)
    {
        _read = read; _write = write; _clear = clear; _confirm = confirm;
    }

    private static IClipboard Require(Func<IClipboard?> get) => get() ?? throw new NotSupportedException("No clipboard is available.");
    public bool Available => true; // TopLevel is resolved only on the UI thread, when an operation runs.

    public async Task<IKittyClipboardSnapshot> OpenReadAsync()
    {
        var data = await _read();
        try { return new Snapshot(data); }
        catch { data?.Dispose(); throw; }
    }

    private sealed class Snapshot : IKittyClipboardSnapshot
    {
        private IAsyncDataTransfer? _data;
        private readonly Dictionary<string, DataFormat[]> _formats;
        public IReadOnlyList<string> Types { get; }

        public Snapshot(IAsyncDataTransfer? data)
        {
            _data = data;
            _formats = (data?.Formats ?? []).Select(format => (Mime: ToMime(format), Format: format))
                .Where(pair => pair.Mime != null).GroupBy(pair => pair.Mime ?? "", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.Format)
                    .OrderBy(FormatPriority)
                    .ToArray(), StringComparer.Ordinal);
            Types = _formats.Keys.ToArray();
        }

        private static int FormatPriority(DataFormat format)
        {
            if (format.Identifier.Equals(ClipboardPng.OriginalFormat, StringComparison.OrdinalIgnoreCase)) return 0;
            return format == DataFormat.Bitmap ? 2 : 1;
        }

        public async Task<byte[]?> ReadAsync(string mime, int maxBytes = Ntilde.VT.KittyClipboardPacket.MaxDataBytes)
        {
            if (_data == null || !_formats.TryGetValue(mime, out var formats)) return null;
            foreach (var format in formats)
            {
                foreach (var item in _data.Items)
                {
                    object? value = await item.TryGetRawAsync(format);
                    if (OperatingSystem.IsWindows() && format.Kind == DataFormatKind.Platform &&
                        format.Identifier.Equals(WindowsClipboardFormats.Html, StringComparison.OrdinalIgnoreCase) && value is byte[] html)
                    {
                        if (TryDecodeHtml(html, maxBytes, out byte[]? decoded)) return decoded;
                        continue;
                    }
                    byte[]? bytes = ToBytes(value, maxBytes);
                    if (bytes != null) return bytes;
                }
            }
            return null;
        }

        private static bool TryDecodeHtml(byte[] html, int maxBytes, out byte[]? decoded)
        {
            decoded = null;
            try { decoded = WindowsClipboardFormats.DecodeHtml(html, maxBytes); return true; }
            catch (InvalidDataException) { return false; }
            catch (DecoderFallbackException) { return false; }
        }

        private static byte[]? ToBytes(object? value, int maxBytes)
        {
            switch (value)
            {
                case byte[] bytes:
                    EnsureSize(bytes.Length, maxBytes);
                    return bytes;
                case string text:
                    EnsureSize(Encoding.UTF8.GetByteCount(text), maxBytes);
                    return Encoding.UTF8.GetBytes(text);
                case Bitmap bitmap:
                    using (bitmap)
                    {
                        EnsureSize((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4, maxBytes);
                        using var stream = new ClipboardReadLimitStream(maxBytes);
                        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                        return stream.ToArray();
                    }
                default: return null;
            }
        }

        private static void EnsureSize(long size, int maxBytes)
        {
            if (size > maxBytes) throw new IOException("Clipboard item exceeds the remaining read budget.");
        }

        public void Dispose()
        {
            _data?.Dispose();
            _data = null;
        }
    }

    public async Task WriteAsync(IReadOnlyDictionary<string, byte[]> data)
    {
        if (data.Count == 0) { await _clear(); return; }
        var transfer = new DataTransfer();
        var resources = new List<IDisposable>();
        var owned = new OwnedClipboardTransfer(transfer, resources);
        try
        {
            var item = new DataTransferItem();
            long nativeBytes = 0;
            foreach (var (mime, bytes) in data)
            {
                byte[] encoded = OperatingSystem.IsWindows() && mime == "text/html" ? WindowsClipboardFormats.EncodeHtml(bytes) : bytes;
                nativeBytes += encoded.Length;
                int budget = (int)Math.Max(0, 2L * Ntilde.VT.KittyClipboardPacket.MaxDataBytes - nativeBytes);
                if (nativeBytes > 2L * Ntilde.VT.KittyClipboardPacket.MaxDataBytes)
                    throw new ClipboardSizeLimitException("Native clipboard data exceeds the expansion budget.");
                if (OperatingSystem.IsWindows() && mime == "image/png")
                {
                    int pixels = ClipboardPng.PixelBytes(bytes, budget);
                    var bitmap = ClipboardPng.Decode(bytes, budget);
                    resources.Add(bitmap);
                    nativeBytes += pixels;
                    item.SetBitmap(bitmap);
                    item.Set(DataFormat.CreateBytesPlatformFormat(ClipboardPng.OriginalFormat), bytes);
                }
                else item.Set(DataFormat.CreateBytesPlatformFormat(ClipboardMimeNames.ToPlatform(mime)), encoded);
                if (mime == "text/plain") item.SetText(Encoding.UTF8.GetString(bytes));
            }
            transfer.Add(item);
            await _write(owned);
        }
        catch { owned.Dispose(); throw; } // ownership passes to Avalonia only on success
    }

    public Task<bool> ConfirmReadAsync(string name, CancellationToken cancellation) => _confirm(name, cancellation);

    internal static string? ToMime(DataFormat format)
    {
        if (format.Kind == DataFormatKind.Platform && format.Identifier.Equals(ClipboardPng.OriginalFormat, StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (format == DataFormat.Text) return "text/plain";
        if (format == DataFormat.Bitmap) return "image/png";
        if (format.Kind != DataFormatKind.Platform) return null;
        string? mime = ClipboardMimeNames.ToMime(format.Identifier);
        return mime != null && Ntilde.VT.KittyClipboardPacket.IsMime(mime) ? mime : null;
    }
}
