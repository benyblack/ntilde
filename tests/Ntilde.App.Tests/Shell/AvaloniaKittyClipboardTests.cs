using Avalonia.Input;
using Avalonia.Input.Platform;
using Ntilde.Shell.Clipboard;
using Xunit;

namespace Ntilde.Tests.Shell;

public class AvaloniaKittyClipboardTests
{
    // This backend contains only test bytes; no test obtains a TopLevel/system clipboard.
    private sealed class Clipboard : IDisposable
    {
        private IAsyncDataTransfer? _stored;
        public int Clears;
        public Task SetDataAsync(IAsyncDataTransfer? dataTransfer) { _stored?.Dispose(); _stored = dataTransfer; return Task.CompletedTask; }
        public Task ClearAsync() { Clears++; _stored?.Dispose(); _stored = null; return Task.CompletedTask; }
        public Task FlushAsync() => Task.CompletedTask;
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() => Task.FromResult(_stored);
        public async Task<IAsyncDataTransfer?> TryGetDataAsync()
        {
            if (_stored == null) return null;
            var copy = new DataTransfer();
            var target = new DataTransferItem();
            foreach (var item in _stored.Items)
                foreach (var format in item.Formats)
                {
                    object? value = await item.TryGetRawAsync(format);
                    if (format is DataFormat<byte[]> bytes && value is byte[] data) target.Set(bytes, data);
                    if (format is DataFormat<string> text && value is string s) target.Set(text, s);
                }
            copy.Add(target);
            return copy;
        }
        public void Dispose() => _stored?.Dispose();
    }

    [Fact]
    public async Task Binary_MIME_aliases_and_plain_text_round_trip_through_Avalonia_formats()
    {
        using var clipboard = new Clipboard();
        var adapter = new AvaloniaKittyClipboard(clipboard.TryGetDataAsync, clipboard.SetDataAsync, clipboard.ClearAsync, (_, _) => Task.FromResult(false));
        byte[] binary = [0, 1, 2, 255];
        await adapter.WriteAsync(new Dictionary<string, byte[]>
        {
            ["text/plain"] = System.Text.Encoding.UTF8.GetBytes("héllo"),
            ["image/png"] = binary,
            ["application/x-ntilde-alias"] = binary
        });
        string[] types = await adapter.GetTypesAsync();
        Assert.Equal(3, types.Length);
        Assert.Contains("text/plain", types);
        Assert.Contains("image/png", types);
        Assert.Contains("application/x-ntilde-alias", types);
        Assert.Equal(binary, await adapter.ReadAsync("image/png"));
        Assert.Equal(binary, await adapter.ReadAsync("application/x-ntilde-alias"));
        Assert.Equal("héllo", System.Text.Encoding.UTF8.GetString((await adapter.ReadAsync("text/plain"))!));
        Assert.Equal(3, (await adapter.GetTypesAsync()).Length); // disposing read handles must not invalidate the clipboard's ownership
        await adapter.WriteAsync(new Dictionary<string, byte[]>());
        Assert.Equal(1, clipboard.Clears);
        Assert.Empty(await adapter.GetTypesAsync());
    }
}
