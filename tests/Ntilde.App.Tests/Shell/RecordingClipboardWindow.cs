using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using System.Reflection;

namespace Ntilde.AppTests.Shell;

/// <summary>Wraps the headless backend only. No OS clipboard implementation is resolved or called.</summary>
public static class RecordingClipboardWindow
{
    internal static (Window Window, ClipboardRecorder Clipboard) Create()
    {
        var window = new Window();
        var original = window.PlatformImpl ?? throw new InvalidOperationException("A headless backend is required.");
        var backend = DispatchProxy.Create<IWindowImpl, ClipboardWindowProxy>();
        var proxy = (ClipboardWindowProxy)(object)backend;
        proxy.Inner = original;
        var recorder = new ClipboardRecorder();
        recorder.Facade = DispatchProxy.Create<IClipboard, ClipboardFacadeProxy>();
        ((ClipboardFacadeProxy)(object)recorder.Facade).Recorder = recorder;
        proxy.Clipboard = recorder.Facade;
        proxy.Recorder = recorder;
        typeof(TopLevel).GetProperty(nameof(TopLevel.PlatformImpl))!.SetValue(window, backend);
        return (window, recorder);
    }

    public class ClipboardWindowProxy : DispatchProxy
    {
        public IWindowImpl Inner { get; set; } = null!;
        public IClipboard Clipboard { get; set; } = null!;
        public ClipboardRecorder Recorder { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "TryGetFeature" && args is [Type type] && type == typeof(IClipboard))
            {
                Recorder.Resolutions++;
                return Clipboard;
            }
            return (targetMethod ?? throw new InvalidOperationException("Missing backend method.")).Invoke(Inner, args);
        }
    }

    public class ClipboardFacadeProxy : DispatchProxy
    {
        public ClipboardRecorder Recorder { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "ClearAsync" => Recorder.ClearAsync(),
            "SetDataAsync" => Recorder.SetDataAsync((IAsyncDataTransfer?)args?[0]),
            "FlushAsync" => Recorder.FlushAsync(),
            "TryGetInProcessDataAsync" => Recorder.TryGetInProcessDataAsync(),
            "TryGetDataAsync" => Recorder.TryGetDataAsync(),
            _ => null, // Avalonia's non-client-implementable marker is never invoked.
        };
    }

    public sealed class ClipboardRecorder : IDisposable
    {
        public int Resolutions { get; set; }
        public int Reads { get; set; }
        public int Writes { get; set; }
        public int Flushes { get; set; }
        internal IClipboard Facade { get; set; } = null!;
        private IAsyncDataTransfer? _stored;
        public Task ClearAsync() { Writes++; _stored?.Dispose(); _stored = null; return Task.CompletedTask; }
        public Task SetDataAsync(IAsyncDataTransfer? data) { Writes++; _stored?.Dispose(); _stored = data; return Task.CompletedTask; }
        public Task FlushAsync() { Flushes++; return Task.CompletedTask; }
        public Task<IAsyncDataTransfer?> TryGetInProcessDataAsync() { Reads++; return Task.FromResult<IAsyncDataTransfer?>(null); }
        public Task<IAsyncDataTransfer?> TryGetDataAsync() { Reads++; return Task.FromResult<IAsyncDataTransfer?>(null); }
        public void Dispose() => _stored?.Dispose();
    }
}
