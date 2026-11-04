using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Ntilde.Shell.Clipboard;

namespace Ntilde.Controls;

public partial class TerminalPane
{
    private KittyClipboardSession? _kittyClipboard;
    internal PrivateClipboardQaMode PrivateClipboardQa { get; set; } = PrivateClipboardQaMode.Process;
    internal Func<Func<Task>, Task>? ClipboardSchedulerForTest { get; set; }

    /// <summary>A private process never falls back to desktop I/O; retired parser callbacks cannot acquire a later fixture.</summary>
    private bool TryHandlePrivateClipboardWrite(Ntilde.VT.AnsiParser source, string target, byte[] data, bool allowed)
    {
        if (!PrivateClipboardQa.IsEnabled) return false;
        if (!ReferenceEquals(Parser, source)) PrivateClipboardQa.Interrupt(this);
        else if (allowed) PrivateClipboardQa.For(this)?.AcceptOsc52(target, data);
        return true;
    }

    private void WireKittyClipboard(bool muxBacked)
    {
        _kittyClipboard?.Dispose();
        _kittyClipboard = null;
        if (Buffer != null) Buffer.Modes.IsClipboardPasteMode = false;
        if (PrivateClipboardQa.IsEnabled)
        {
            PrivateClipboardQa.Interrupt(this);
            if (Parser != null) Parser.OnKittyClipboardReset = () => PrivateClipboardQa.Interrupt(this);
            return;
        }
        if (muxBacked || Parser == null) return; // the authoritative daemon cannot perform desktop I/O
        var parser = Parser;
        parser.KittyClipboardEnabled = true;
        var clipboard = new AvaloniaKittyClipboard(() => Ntilde.Shell.Clipboard.HostClipboardAccess.Get(TopLevel.GetTopLevel(this)), ConfirmClipboardReadAsync);
        bool Alive() => !_disposed && ReferenceEquals(Parser, parser) && Session?.IsProcessRunning == true;
        bool TryReply(string reply)
        {
            if (!Alive()) return false;
            _marklessSubmission.ObserveDeviceReply();
            if (Session is Ntilde.Pty.ITerminalDeviceReplySink sink) return sink.TrySendDeviceReply(reply);
            Session?.SendInput(reply);
            return true;
        }
        var session = new KittyClipboardSession(new ClipboardLocations(clipboard, new XcbPrimaryClipboard(ConfirmClipboardReadAsync)), TryReply,
            ClipboardSchedulerForTest ?? (work => Task.Run(() => Dispatcher.UIThread.InvokeAsync(work))),
            () => _settings == null || BuildEffectiveSettings(_settings).AllowOsc52ClipboardWrite,
            Alive);
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

    private async Task<ClipboardReadConsent> ConfirmClipboardReadAsync(ClipboardReadRequest request, CancellationToken cancellation)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return ClipboardReadConsent.Deny;
        var dialog = new Window
        {
            Title = request.Primary ? "Primary selection access" : "Clipboard access",
            Width = 560,
            Height = 360,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        Ntilde.Shell.UiScale.FitWindow(dialog);
        Ntilde.Shell.Theming.ThemedWindowChrome.Apply(dialog);
        string location = request.Primary ? "primary selection" : "clipboard";
        var consent = ClipboardReadConsent.Deny;
        var deny = new Button { Content = "Deny", MinWidth = 90 };
        var allow = new Button { Content = "Allow once", MinWidth = 110 };
        deny.Click += (_, _) => dialog.Close();
        allow.Click += (_, _) => { consent = ClipboardReadConsent.AllowOnce; dialog.Close(); };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { deny, allow }
        };
        if (request.CanRemember)
        {
            var remember = new Button { Content = "Allow this session", MinWidth = 150 };
            remember.Click += (_, _) => { consent = ClipboardReadConsent.AllowSession; dialog.Close(); };
            actions.Children.Add(remember);
        }
        DockPanel.SetDock(actions, Dock.Bottom);
        dialog.Content = new DockPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                actions,
                new ScrollViewer
                {
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new StackPanel
                    {
                        Spacing = 16,
                        Children =
                        {
                            new TextBlock { Text = "Terminal: " + new string((Profile?.Name ?? ShellCommand ?? "This pane").Where(c => !char.IsControl(c)).Take(80).ToArray()), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            new TextBlock { Text = "Allow this terminal program to read your " + location + "?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            new TextBlock { Text = string.IsNullOrEmpty(request.Name) ? "This request can read " + location + " contents." : "Program-supplied name: " + request.Name, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            new TextBlock { IsVisible = request.CanRemember, Text = "Allow this session lets any program on this terminal using this name and password read the " + location + " until you reset or close this session. Nothing is saved to disk.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }
                        }
                    }
                }
            }
        };
        using var registration = cancellation.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        if (cancellation.IsCancellationRequested) return ClipboardReadConsent.Deny;
        await dialog.ShowDialog(owner);
        return cancellation.IsCancellationRequested ? ClipboardReadConsent.Deny : consent;
    }
}
