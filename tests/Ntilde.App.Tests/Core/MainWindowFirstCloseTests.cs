using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Mux;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Controls; // FakeTerminalSession, RecordingSessionFactory
using Ntilde.Tests.Shell.Mux;
using FirstCloseAction = Ntilde.MainWindow.FirstCloseAction;
using FirstCloseAnswer = Ntilde.MainWindow.FirstCloseAnswer;

namespace Ntilde.Tests.Core;

/// <summary>
/// Spec R1: the first time a window closes with live local shells it asks whether they keep running, and
/// "Don't ask again" remembers the answer in a flag file under the app-data root. The window's panes spawn
/// into an in-memory MuxServer; the <see cref="MainWindow.ConfirmFirstClose"/> seam answers instead of a modal.
/// </summary>
/// <remarks>
/// <see cref="TestAppDataRoot"/> is taken for its lifetime: closing a real MainWindow saves the session, and the
/// Settings test writes settings.json and the default flag file, none of which may land in the developer's profile.
/// </remarks>
public sealed class MainWindowFirstCloseTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    private readonly TestAppDataRoot _appData;
    private readonly MuxTestHost _mux = new();
    private readonly List<MuxConnectionHost> _hosts = [];
    private readonly List<IDisposable> _owned = [];
    private readonly string _choiceRoot = Path.Combine(Path.GetTempPath(), $"ntilde_first_close_{Guid.NewGuid():N}");

    /// <summary>Every question any window of the test asked, by the count it was given.</summary>
    private readonly List<int> _asked = [];

    public MainWindowFirstCloseTests(TestAppDataRoot appData)
    {
        _appData = appData;
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        Directory.CreateDirectory(_choiceRoot);
    }

    public void Dispose()
    {
        TestMainWindowFactory.DisposeCreatedWindows();
        foreach (MuxConnectionHost host in _hosts) host.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _mux.Dispose();
        try { Directory.Delete(_choiceRoot, recursive: true); } catch { /* best effort */ }
    }

    private MuxCloseChoiceStore Store => new(_choiceRoot);

    private string FlagPath => Path.Combine(_choiceRoot, MuxCloseChoiceStore.FileName);

    private static Func<int, Task<FirstCloseAnswer>> Answer(FirstCloseAction action, bool remember = false) =>
        _ => Task.FromResult(new FirstCloseAnswer(action, remember));

    /// <summary>A window with session persistence on (unless <paramref name="persistence"/> says otherwise) over the test's daemon.</summary>
    private MainWindow CreateWindow(
        Func<int, Task<FirstCloseAnswer>> answer,
        string persistence = SessionPersistenceMode.KeepOnClose,
        Func<MuxEndpointId, MuxConnectionHost?>? createRemote = null,
        TimeSpan? disposeFlush = null,
        Func<Stream, Stream>? link = null)
    {
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(link is null ? _mux.Listener.Connect() : link(_mux.Listener.Connect()), null, ct), "test", null)
        {
            DisposeFlushTimeout = disposeFlush ?? TimeSpan.FromSeconds(1),
        };
        _hosts.Add(host);
        var factory = new MuxTerminalSessionFactory(
            new MuxConnectionHosts(host, createRemote ?? (_ => null)), new RecordingSessionFactory(new FakeTerminalSession()), null);
        MainWindow window = TestMainWindowFactory.Create(AppServices.BuildForDesigner() with
        {
            CommandAssist = TestCommandAssistServices.Instance,
            SessionFactory = factory,
            Settings = new TerminalSettings { SessionPersistence = persistence },
        });
        window.MuxCloseChoiceStore = Store;
        window.ConfirmFirstClose = count =>
        {
            _asked.Add(count);
            return answer(count);
        };
        window.Show();
        PumpUntil(() => LocalSession(window) is { IsAttached: true }, "the first pane attached");
        return window;
    }

    private static MuxClientSession? LocalSession(MainWindow window) =>
        window.AllPanesForTest().Select(p => p.Session).OfType<MuxClientSession>().FirstOrDefault();

    private static void PumpUntil(Func<bool> condition, string because, int ms = 10_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) Assert.Fail($"Timed out: {because}");
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private void AssertKeptRunning(Guid id)
    {
        PumpUntil(() => _mux.Mux(id).AttachedClients == 0, "the daemon saw the detach");
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(id).IsExited);
    }

    [AvaloniaFact]
    public void Closing_with_live_local_shells_asks_once()
    {
        var pending = new TaskCompletionSource<FirstCloseAnswer>();
        MainWindow window = CreateWindow(_ => pending.Task);
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;

        window.Close();
        Dispatcher.UIThread.RunJobs();
        window.Close(); // a second close while the question is open is held, not asked again
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([1], _asked);
        Assert.True(window.IsVisible);

        pending.SetResult(new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false));
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.NotNull(host.CurrentClient); // nothing was torn down
        Assert.Equal(1, _mux.Mux(id).AttachedClients);
        Assert.Contains(id, _mux.Server.GetSessionIds());
        Assert.False(_mux.Mux(id).IsExited);
        Assert.Equal(0, host.PendingKillCountForTest);
        Assert.False(File.Exists(FlagPath));

        // Cancel is not an answer: the next close asks again.
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([1, 1], _asked);
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void Keep_running_detaches_and_the_shell_survives()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Keep));
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([1], _asked);
        Assert.Null(host.CurrentClient);
        AssertKeptRunning(id);
        Assert.False(File.Exists(FlagPath));
        Assert.Contains(id.ToString(), File.ReadAllText(AppPaths.SessionFilePath)); // the next launch reattaches it
    }

    /// <remarks>
    /// The link holds every write back for a while once the close starts, and no ping flush runs at teardown
    /// (<see cref="MuxConnectionHost.DisposeFlushTimeout"/> zero): the kill lands only because the host tracks it and
    /// waits for its reply before it disconnects. One sent and forgotten would still be held when the client closes
    /// its end, and be dropped with it.
    /// </remarks>
    [AvaloniaFact]
    public void Close_them_ends_the_shells()
    {
        DelayingStream? link = null;
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close), disposeFlush: TimeSpan.Zero, link: s => link = new DelayingStream(s));
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;
        MuxClient client = host.CurrentClient!;
        // Read the instant the connection closes: the daemon must have handled the kill by then.
        var runningAtDisconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += _ => runningAtDisconnect.TrySetResult(_mux.Server.GetSessionIds().Contains(id));
        link!.Delay = TimeSpan.FromMilliseconds(300);

        window.Close();
        PumpUntil(() => !window.IsVisible, "the window closed after the answer");

        Assert.Equal([1], _asked);
        Assert.Null(host.CurrentClient);
        PumpUntil(() => runningAtDisconnect.Task.IsCompleted, "the client disconnected");
        Assert.False(runningAtDisconnect.Task.Result, "the kill reached the daemon before the client disconnected");
        Assert.DoesNotContain(id, _mux.Server.GetSessionIds());
        Assert.False(File.Exists(FlagPath));
        // Not named for reattach: the next launch starts a fresh shell quietly instead of reporting this one lost.
        Assert.DoesNotContain(id.ToString(), File.ReadAllText(AppPaths.SessionFilePath));
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dont_ask_again_remembers_the_answer(bool keep)
    {
        FirstCloseAction action = keep ? FirstCloseAction.Keep : FirstCloseAction.Close;
        MainWindow first = CreateWindow(Answer(action, remember: true));

        first.Close();
        PumpUntil(() => !first.IsVisible, "the first window closed after the answer");

        Assert.Equal(keep ? "keep" : "close", File.ReadAllText(FlagPath));

        // A later window over the same daemon, with a new shell of its own (no session file to restore).
        if (File.Exists(AppPaths.SessionFilePath)) File.Delete(AppPaths.SessionFilePath);
        MainWindow second = CreateWindow(Answer(FirstCloseAction.Cancel));
        MuxConnectionHost host = second.MuxHost!;
        Guid id = LocalSession(second)!.Id;

        second.Close();
        Assert.False(second.IsVisible); // closed at once: nothing was asked, nothing posted

        Assert.Equal([1], _asked); // the first window's question only
        Assert.Null(host.CurrentClient);
        if (keep)
        {
            AssertKeptRunning(id);
        }
        else
        {
            Assert.DoesNotContain(id, _mux.Server.GetSessionIds()); // killed, and the kill flushed before the disconnect
        }
    }

    [AvaloniaTheory]
    [InlineData("exited")]
    [InlineData("remote")]
    [InlineData("persistence off")]
    public void No_question_without_live_local_shells(string why)
    {
        MainWindow window;
        switch (why)
        {
            case "exited":
                window = CreateWindow(Answer(FirstCloseAction.Cancel));
                MuxClientSession mux = LocalSession(window)!;
                // Non-zero: under the default "Graceful" ShellExitPolicy the pane stays (exit 0 would close the window).
                _mux.Fake(mux.Id).Exit(3);
                PumpUntil(() => !mux.IsProcessRunning, "the pane saw the exit");
                break;
            case "remote":
                window = CreateRemoteOnlyWindow();
                break;
            default:
                window = CreateWindow(Answer(FirstCloseAction.Cancel), persistence: SessionPersistenceMode.Off);
                break;
        }

        window.Close();

        Assert.False(window.IsVisible);
        Assert.Empty(_asked);
    }

    /// <summary>A window whose only pane shows a live shell on a remote daemon (a second in-memory one).</summary>
    private MainWindow CreateRemoteOnlyWindow()
    {
        var remoteMux = new MuxTestHost();
        _owned.Add(remoteMux);
        Guid remoteSession = Task.Run(async () =>
        {
            MuxClient c = await remoteMux.ConnectClientAsync();
            return await MuxTestHost.SpawnAsync(c);
        }, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        MuxEndpointId remoteId = MuxEndpointId.ForSsh(Guid.NewGuid());
        var remoteHost = new MuxConnectionHost(ct => MuxClient.ConnectAsync(remoteMux.Listener.Connect(), null, ct), "remote", null, MuxHostPolicy.Remote("box"));
        _hosts.Add(remoteHost);
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel), createRemote: id => id == remoteId ? remoteHost : null);
        TerminalPane pane = window.AllPanesForTest().Single();

        // Until SSH requests are routed to a remote endpoint, a stand-in factory opens it there.
        pane.SessionFactory = new RemoteEndpointFactory(window.MuxHosts!, remoteId, remoteSession);
        pane.Reconnect();
        PumpUntil(() => pane.Session is MuxClientSession { IsConnected: true, IsProcessRunning: true } m && m.Id == remoteSession, "the pane shows the remote shell");
        Assert.Equal(remoteId.ToString(), pane.MuxEndpoint);
        return window;
    }

    /// <summary>Opens an existing session on the remote endpoint's host.</summary>
    private sealed class RemoteEndpointFactory(MuxConnectionHosts hosts, MuxEndpointId endpoint, Guid sessionId) : IPersistentSessionFactory
    {
        public Ntilde.Pty.ITerminalSession Create(Ntilde.Pty.TerminalSessionRequest request) => CreatePersistent(request).Session!;

        public PersistentSessionResult CreatePersistent(Ntilde.Pty.TerminalSessionRequest request)
        {
            MuxClient client = hosts.GetOrCreate(endpoint)!.GetClient(TimeSpan.FromSeconds(5))
                ?? throw new InvalidOperationException("the remote host did not connect");
            return new(client.OpenSession(sessionId, request.Command, request.Arguments), PersistentSessionOutcome.Spawned, endpoint.ToString(), null);
        }
    }

    /// <summary>
    /// A lifetime shutdown (the OS ending the session, or the application lifetime's own shutdown - macOS Cmd+Q)
    /// never asks and never kills, whatever was remembered: it behaves like Keep. Only a window close asks.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(WindowCloseReason.OSShutdown, false)]
    [InlineData(WindowCloseReason.OSShutdown, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false)]
    [InlineData(WindowCloseReason.ApplicationShutdown, true)]
    public void OS_shutdown_never_asks(WindowCloseReason reason, bool closeRemembered)
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Close));
        if (closeRemembered) Store.Remember(MuxCloseChoice.Close);
        MuxConnectionHost host = window.MuxHost!;
        Guid id = LocalSession(window)!.Id;

        bool held = window.HandleClosingForTest(reason);

        Assert.False(held);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(_asked);
        Assert.Null(host.CurrentClient); // the teardown ran
        AssertKeptRunning(id);
    }

    [AvaloniaFact]
    public void A_window_close_reason_asks()
    {
        MainWindow window = CreateWindow(_ => new TaskCompletionSource<FirstCloseAnswer>().Task);

        bool held = window.HandleClosingForTest(WindowCloseReason.WindowClosing);
        Dispatcher.UIThread.RunJobs();

        Assert.True(held);
        Assert.Equal([1], _asked);
        Assert.NotNull(window.MuxHost!.CurrentClient);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Changing_the_setting_forgets_the_choice(bool change)
    {
        // The default store follows the app-data root, which this class's fixture points at a scratch directory.
        Assert.StartsWith(_appData.RootPath, MuxCloseChoiceStore.Default.FilePath, StringComparison.Ordinal);
        MuxCloseChoiceStore.Default.Remember(MuxCloseChoice.Keep);
        Assert.True(File.Exists(MuxCloseChoiceStore.Default.FilePath));
        try
        {
            var settings = new SettingsWindow();
            ComboBox list = settings.FindControl<ComboBox>("SessionPersistenceList")!;
            var loaded = (ComboBoxItem)list.SelectedItem!;
            if (change) list.SelectedItem = list.Items.Cast<ComboBoxItem>().First(i => !ReferenceEquals(i, loaded));

            typeof(SettingsWindow).GetMethod("SaveAndClose", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(settings, null);

            Assert.Equal(!change, File.Exists(MuxCloseChoiceStore.Default.FilePath));
        }
        finally
        {
            MuxCloseChoiceStore.Default.Forget();
            if (File.Exists(AppPaths.SettingsFilePath)) File.Delete(AppPaths.SettingsFilePath);
        }
    }

    private static List<string?> Texts(Window dialog) =>
        dialog.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();

    private static List<Button> Buttons(Window dialog) =>
        dialog.GetLogicalDescendants().OfType<Button>().Where(b => b is not ToggleButton).ToList();

    [AvaloniaFact]
    public void The_dialog_offers_keep_and_close_and_Escape_cancels()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        Dispatcher.UIThread.RunJobs(); // the window's startup focus jobs run before the dialog opens (see PressKey)

        (Window dialog, Task<FirstCloseAnswer> result) = window.BuildFirstCloseDialog(3);

        Assert.Equal("Close Ntilde", dialog.Title);
        List<string?> texts = Texts(dialog);
        Assert.Contains("Your shells keep running in the background.", texts);
        Assert.Contains("Reopen ntilde to get them back. (3 shells)", texts);
        Assert.Contains("Turn this off in Settings → Keep shells running when the window closes.", texts);
        CheckBox dontAsk = Assert.Single(dialog.GetLogicalDescendants().OfType<CheckBox>());
        Assert.Equal("Don't ask again", dontAsk.Content);
        List<Button> buttons = Buttons(dialog);
        Assert.Equal(["Keep running", "Close them"], buttons.Select(b => b.Content as string ?? "").ToArray());
        Assert.True(buttons[0].IsDefault);
        Assert.False(buttons[1].IsDefault);

        dontAsk.IsChecked = true; // Escape still remembers nothing
        PressKey(dialog, PhysicalKey.Escape);

        PumpUntil(() => result.IsCompleted, "the dialog closed");
        Assert.Equal(new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false), result.Result);
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_dialogs_buttons_answer_with_the_checkbox(bool keep, bool dontAskAgain)
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        (Window dialog, Task<FirstCloseAnswer> result) = window.BuildFirstCloseDialog(1);
        Assert.Contains("Reopen ntilde to get them back.", Texts(dialog)); // one shell: no count
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(dialog.GetLogicalDescendants().OfType<CheckBox>()).IsChecked = dontAskAgain;
        Buttons(dialog)[keep ? 0 : 1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        PumpUntil(() => result.IsCompleted, "the dialog closed");
        Assert.Equal(new FirstCloseAnswer(keep ? FirstCloseAction.Keep : FirstCloseAction.Close, dontAskAgain), result.Result);
    }

    /// <summary>
    /// The production question (not the seam): a close from the taskbar can reach a minimized window, and a dialog that
    /// window owns would open out of sight, leaving a close that seems to do nothing. The window is restored first.
    /// </summary>
    [AvaloniaFact]
    public void The_question_restores_a_minimized_window_first()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        window.WindowState = WindowState.Minimized;
        Dispatcher.UIThread.RunJobs();

        var shown = (Task<FirstCloseAnswer>)typeof(MainWindow).GetMethod("ShowFirstCloseDialogAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(window, [2])!;
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(WindowState.Minimized, window.WindowState);
        Window dialog = Assert.Single(window.OwnedWindows);
        Assert.Equal("Close Ntilde", dialog.Title);
        dialog.Close();
        PumpUntil(() => shown.IsCompleted, "the dialog closed");
        Assert.Equal(new FirstCloseAnswer(FirstCloseAction.Cancel, Remember: false), shown.Result);
    }

    [AvaloniaFact]
    public void Enter_keeps_the_shells()
    {
        MainWindow window = CreateWindow(Answer(FirstCloseAction.Cancel));
        Dispatcher.UIThread.RunJobs();
        (Window dialog, Task<FirstCloseAnswer> result) = window.BuildFirstCloseDialog(2);

        PressKey(dialog, PhysicalKey.Enter);

        PumpUntil(() => result.IsCompleted, "the dialog closed");
        Assert.Equal(new FirstCloseAnswer(FirstCloseAction.Keep, Remember: false), result.Result);
    }

    /// <summary>
    /// The client's end of a connection whose writes can be held back: once <see cref="Delay"/> is set, each write reaches
    /// the daemon that long after it was made, in order. Whatever is still held when the stream is disposed is dropped,
    /// as a real link drops what was not yet sent when the client closes. Reads pass straight through.
    /// </summary>
    private sealed class DelayingStream : Stream
    {
        private readonly Stream _inner;
        private readonly System.Collections.Concurrent.BlockingCollection<(long Due, byte[] Bytes)> _held = new();
        private readonly CancellationTokenSource _closed = new();
        private long _delayMs;

        public DelayingStream(Stream inner)
        {
            _inner = inner;
            new Thread(Pump) { IsBackground = true, Name = "DelayingStream pump" }.Start();
        }

        public TimeSpan Delay { set => Volatile.Write(ref _delayMs, (long)value.TotalMilliseconds); }

        private void Pump()
        {
            try
            {
                foreach ((long due, byte[] bytes) in _held.GetConsumingEnumerable(_closed.Token))
                {
                    long wait = due - Environment.TickCount64;
                    if (wait > 0 && _closed.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(wait))) return; // closed first: dropped
                    _inner.Write(bytes);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closed: whatever is still held is dropped.
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            try
            {
                _held.Add((Environment.TickCount64 + Volatile.Read(ref _delayMs), buffer.ToArray()));
            }
            catch (InvalidOperationException)
            {
                throw new ObjectDisposedException(nameof(DelayingStream));
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed.IsCancellationRequested)
            {
                _closed.Cancel();
                _held.CompleteAdding();
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private static void PressKey(Window dialog, PhysicalKey key)
    {
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        IInputElement? focused = dialog.FocusManager?.GetFocusedElement();
        Assert.True(focused is null || TopLevel.GetTopLevel(focused as Avalonia.Visual) == dialog,
            $"the dialog must own keyboard focus before the key press, but {focused?.GetType().Name} in another window has it");
        dialog.KeyPressQwerty(key, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
}
