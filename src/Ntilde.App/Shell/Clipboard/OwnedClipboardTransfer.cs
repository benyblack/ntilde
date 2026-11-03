using Avalonia.Input;

namespace Ntilde.Shell.Clipboard;

/// <summary>DataTransfer itself does not dispose values; keep native bitmap ownership explicit.</summary>
internal sealed class OwnedClipboardTransfer(DataTransfer data, IReadOnlyList<IDisposable> resources) : IDataTransfer, IAsyncDataTransfer
{
    private int _disposed;
    public IReadOnlyList<DataFormat> Formats => data.Formats;
    IReadOnlyList<IDataTransferItem> IDataTransfer.Items => data.Items;
    IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => data.Items;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var resource in resources) resource.Dispose();
        ((IDisposable)data).Dispose();
    }
}
