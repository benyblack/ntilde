using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ntilde.Shell.Clipboard;

namespace Ntilde.AppTests.Shell;

[Collection(Ntilde.Tests.Infra.PrivateClipboardProcessScope.Name)]
public sealed class PrivateClipboardQaModeTests : IDisposable
{
    internal const string Run = "0123456789abcdef0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ntilde-private-clipboard-" + Guid.NewGuid().ToString("N"));
    public PrivateClipboardQaModeTests()
    {
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    public void Dispose() => Directory.Delete(_root, true);
    internal PrivateClipboardQaMode Create() => PrivateClipboardQaMode.FromStartup(
        ["--ntilde-terminfo-owner=" + Run, PrivateClipboardQaMode.Flag + Run], Run, _root);

    [Fact]
    public void Explicit_valid_startup_blocks_provider_before_begin_after_failed_begin_and_forever_after_close()
    {
        var mode = Create();
        int calls = 0;
        Assert.Null(mode.ResolveHostClipboard(() => { calls++; throw new InvalidOperationException("Must never inspect the host clipboard."); }));
        Assert.Null(mode.Begin(Run, new object(), new object(), () => false));
        var owner = new object();
        var fixture = mode.Begin(Run, owner, new object(), () => true);
        Assert.NotNull(fixture);
        fixture.Interrupt();
        Assert.Null(mode.Begin(Run, new object(), new object(), () => true));
        Assert.Null(mode.For(new object()));
        Assert.Null(mode.ResolveHostClipboard(() => { calls++; return null; }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("missingOwner")]
    [InlineData("bareFlag")]
    [InlineData("duplicateFlag")]
    [InlineData("wrongEnvironment")]
    [InlineData("missingRoot")]
    public void Invalid_explicit_activation_fails_startup(string defect)
    {
        string[] args = ["--ntilde-terminfo-owner=" + Run, PrivateClipboardQaMode.Flag + Run];
        if (defect == "bareFlag") args = ["--ntilde-terminfo-owner=" + Run, "--ntilde-terminfo-private-clipboard"];
        if (defect == "missingOwner") args = [PrivateClipboardQaMode.Flag + Run];
        if (defect == "duplicateFlag") args = [.. args, PrivateClipboardQaMode.Flag + Run];
        Assert.Throws<InvalidOperationException>(() => PrivateClipboardQaMode.FromStartup(args,
            defect == "wrongEnvironment" ? new string('f', 32) : Run, defect == "missingRoot" ? null : _root));
    }

    [AvaloniaFact]
    public void Framework_text_controls_refuse_clipboard_access_and_positive_control_reaches_recorder()
    {
        var mode = Create();
        var (window, recorder) = RecordingClipboardWindow.Create();
        using var clipboard = recorder;
        try
        {
            var text = new TextBox { Text = "private QA text" };
            var selectable = new SelectableTextBlock { Text = "private QA selection" };
            window.Content = new StackPanel { Children = { text, selectable } };
            window.Show(); Dispatcher.UIThread.RunJobs();
            text.SelectionStart = 0; text.SelectionEnd = 7;
            selectable.SelectionStart = 0; selectable.SelectionEnd = 7;
            var guards = mode.InstallControlGuards();
            try
            {
                text.Copy(); text.Cut(); text.Paste(); selectable.Copy();
                Assert.Equal("private QA text", text.Text);
                Assert.Equal((0, 0, 0), (recorder.Resolutions, recorder.Reads, recorder.Writes));
                foreach (var routedEvent in new[] { TextBox.CopyingToClipboardEvent, TextBox.CuttingToClipboardEvent, TextBox.PastingFromClipboardEvent })
                {
                    var args = new RoutedEventArgs(routedEvent); text.RaiseEvent(args); Assert.True(args.Handled);
                }
            }
            finally { foreach (var guard in guards) guard.Dispose(); }

            // The same attached controls/backend must detect every path when guards are absent.
            text.Copy(); Assert.Equal(1, recorder.Writes);
            text.Cut(); Assert.Equal(2, recorder.Writes);
            text.Paste(); Assert.True(recorder.Reads > 0);
            selectable.Copy(); Assert.Equal(3, recorder.Writes);
            Assert.True(recorder.Resolutions >= 4);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void One_process_override_drives_host_resolution_and_refuses_replacement_until_window_teardown()
    {
        var ordinary = PrivateClipboardQaMode.Process;
        var mode = Create();
        var (window, recorder) = RecordingClipboardWindow.Create();
        using var clipboard = recorder;
        IDisposable? scope = null;
        DisposableClipboardFixture? fixture = null;
        try
        {
            scope = PrivateClipboardQaMode.OverrideProcessForTesting(mode);
            Assert.Same(mode, PrivateClipboardQaMode.Process);
            fixture = mode.Begin(Run, new object(), new object(), () => true);
            Assert.NotNull(fixture);
            Assert.Null(HostClipboardAccess.Get(window));
            Assert.Equal(0, recorder.Resolutions);
            Assert.Throws<InvalidOperationException>(() => PrivateClipboardQaMode.OverrideProcessForTesting(Create()));
            Assert.Same(mode, PrivateClipboardQaMode.Process);
            Assert.Equal("active", fixture.Observe().State);
        }
        finally { window.Close(); scope?.Dispose(); }
        Assert.Equal("aborted", fixture?.Observe().State);
        Assert.False(fixture?.Observe().Complete);
        Assert.Same(ordinary, PrivateClipboardQaMode.Process);
        // The recorder remains a pure headless feature: no system provider is consulted.
        Assert.Same(recorder.Facade, ordinary.ResolveHostClipboard(() => recorder.Facade));
    }

    [Fact]
    public void Ordinary_process_preserves_provider_resolution_and_cannot_begin_a_fixture()
    {
        var mode = PrivateClipboardQaMode.FromStartup([], null, null);
        int calls = 0;
        Assert.False(mode.IsEnabled);
        Assert.Null(mode.ResolveHostClipboard(() => { calls++; return null; }));
        Assert.Equal(1, calls);
        Assert.Null(mode.Begin(Run, new object(), new object(), () => true));
    }
}
