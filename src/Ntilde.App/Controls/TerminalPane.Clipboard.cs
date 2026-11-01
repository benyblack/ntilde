using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Ntilde.Shell.Clipboard;

namespace Ntilde.Controls;

public partial class TerminalPane
{
    private KittyClipboardSession? _kittyClipboard;

    private void WireKittyClipboard(bool muxBacked)
    {
        _kittyClipboard?.Dispose();
        _kittyClipboard = null;
        if (muxBacked || Parser == null) return; // the authoritative daemon cannot perform desktop I/O
        var parser = Parser;
        parser.KittyClipboardEnabled = true;
        var clipboard = new AvaloniaKittyClipboard(() => TopLevel.GetTopLevel(this)?.Clipboard, ConfirmClipboardReadAsync);
        var session = new KittyClipboardSession(clipboard, reply =>
        {
            if (!_disposed && ReferenceEquals(Parser, parser))
            {
                _marklessSubmission.ObserveDeviceReply();
                if (Session is Ntilde.Pty.ITerminalDeviceReplySink sink) sink.TrySendDeviceReply(reply);
                else Session?.SendInput(reply);
            }
        }, work => Task.Run(() => Dispatcher.UIThread.InvokeAsync(work)),
        () => _settings == null || BuildEffectiveSettings(_settings).AllowOsc52ClipboardWrite,
        () => !_disposed && ReferenceEquals(Parser, parser) && Session?.IsProcessRunning == true);
        _kittyClipboard = session;
        parser.OnKittyClipboardPacket = session.Accept;
        parser.OnKittyClipboardReset = session.Reset;
    }

    /// <summary>Captures the pane before any await; MIME paste never falls back to plain text.</summary>
    internal async Task<bool> TryNotifyClipboardPasteAsync()
    {
        if (Buffer?.Modes.IsClipboardPasteMode != true || _kittyClipboard == null) return false;
        NotifyExternalInputSent();
        ScrollToInputLine();
        await _kittyClipboard.NotifyPasteAsync();
        return true;
    }

    private async Task<bool> ConfirmClipboardReadAsync(string name, CancellationToken cancellation)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        var dialog = new Window
        {
            Title = "Clipboard access",
            Width = 460,
            Height = 200,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        Ntilde.Shell.UiScale.FitWindow(dialog);
        Ntilde.Shell.Theming.ThemedWindowChrome.Apply(dialog);
        bool allowed = false;
        var deny = new Button { Content = "Deny", MinWidth = 90 };
        var allow = new Button { Content = "Allow once", MinWidth = 110 };
        deny.Click += (_, _) => dialog.Close();
        allow.Click += (_, _) => { allowed = true; dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "Terminal: " + new string((Profile?.Name ?? ShellCommand ?? "This pane").Where(c => !char.IsControl(c)).Take(80).ToArray()), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "Allow this terminal program to read your clipboard?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = string.IsNullOrEmpty(name) ? "This request can read clipboard contents." : "Program-supplied name: " + name, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { deny, allow } }
            }
        };
        using var registration = cancellation.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        if (cancellation.IsCancellationRequested) return false;
        await dialog.ShowDialog(owner);
        return allowed;
    }
}
