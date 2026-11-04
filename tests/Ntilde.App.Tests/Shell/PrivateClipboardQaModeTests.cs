using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Ntilde.Shell.Clipboard;

namespace Ntilde.AppTests.Shell;

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
        Assert.Null(mode.Begin(Run, new object(), () => false));
        var owner = new object();
        var fixture = mode.Begin(Run, owner, () => true);
        Assert.NotNull(fixture);
        fixture.Interrupt();
        Assert.Null(mode.Begin(Run, new object(), () => true));
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
    public void Framework_text_controls_refuse_clipboard_events_before_native_provider_resolution()
    {
        var mode = Create();
        var guards = mode.InstallControlGuards();
        try
        {
            var text = new TextBox { Text = "private QA text", SelectionStart = 0, SelectionEnd = 7 };
            foreach (var routedEvent in new[] { TextBox.CopyingToClipboardEvent, TextBox.CuttingToClipboardEvent, TextBox.PastingFromClipboardEvent })
            {
                var args = new RoutedEventArgs(routedEvent);
                text.RaiseEvent(args);
                Assert.True(args.Handled);
            }
            // Exercise the actual framework paths as well; handled events return before resolving Clipboard.
            text.Copy(); text.Cut(); text.Paste();
            Assert.Equal("private QA text", text.Text);
            var selectable = new SelectableTextBlock { Text = "private QA selection" };
            var copy = new RoutedEventArgs(SelectableTextBlock.CopyingToClipboardEvent);
            selectable.RaiseEvent(copy);
            Assert.True(copy.Handled);
            mode.Interrupt(new object());
            var after = new RoutedEventArgs(TextBox.PastingFromClipboardEvent);
            new TextBox().RaiseEvent(after);
            Assert.True(after.Handled);
        }
        finally { foreach (var guard in guards) guard.Dispose(); }
        var normal = new RoutedEventArgs(TextBox.PastingFromClipboardEvent);
        new TextBox().RaiseEvent(normal);
        Assert.False(normal.Handled);
    }

    [Fact]
    public void Ordinary_process_preserves_provider_resolution_and_cannot_begin_a_fixture()
    {
        var mode = PrivateClipboardQaMode.FromStartup([], null, null);
        int calls = 0;
        Assert.False(mode.IsEnabled);
        Assert.Null(mode.ResolveHostClipboard(() => { calls++; return null; }));
        Assert.Equal(1, calls);
        Assert.Null(mode.Begin(Run, new object(), () => true));
    }
}
