using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ntilde.AgentHost;
using Ntilde.AgentHost.Contracts;
using Ntilde.VT;
using MuxReadScreenResult = Ntilde.Mux.Contracts.ReadScreenResult;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// The agent host's tools on windowless sessions (Phase 5 spec §3, rulings R4 and R5): daemon sessions no pane of the
/// window shows, reached through the window's <see cref="IWindowlessSessionSource"/> (here a
/// <see cref="StubWindowlessSource"/>). The registry is asked first, so a pane id never reaches the source. Reads of a
/// windowless session are journaled and light the window, because no pane indicator can show them; pane reads stay
/// unjournaled. Acts honour the act toggle and, on a remote endpoint, that SSH profile's allowlist, for input and kill
/// alike.
/// </summary>
public class AgentHostWindowlessProtocolTests
{
    /// <summary>The journal's target for a windowless session on this computer, and on the stub's SSH host.</summary>
    private const string LocalTarget = "windowless · this computer";
    private const string RemoteTarget = "windowless · nova@build-box";

    private static AgentSessionRegistration RegisterPane(AgentSessionRegistry registry, string content = "", Guid? id = null)
    {
        var buffer = new TerminalBuffer(80, 24);
        if (content.Length > 0)
        {
            new AnsiParser(buffer).Process(content);
        }
        var registration = new AgentSessionRegistration(
            id ?? Guid.NewGuid(), buffer, "pane title", "Profile", "local", isActive: true);
        registration.SetLifecycle(new InputStubSession());
        Assert.True(registry.Register(registration));
        return registration;
    }

    private static AgentHostService NewService(
        AgentSessionRegistry registry, AgentActivityJournal journal, Func<DateTimeOffset>? now = null)
    {
        var endpoint = AgentHostTestEndpoint.CreateEndpoint(Path.GetTempPath());
        return new AgentHostService(registry, endpoint, Path.GetTempPath(), null, journal, now);
    }

    private static string Line(string method, string? paramsJson, long id = 1)
        => $"{{\"v\":{AgentHostProtocol.Version},\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson ?? "null"}}}";

    private static string ReadScreenLine(Guid id, bool includeAttributes = false) => Line(
        AgentHostProtocol.Methods.ReadScreen,
        JsonSerializer.Serialize(new ReadScreenParams { PaneId = id, IncludeAttributes = includeAttributes }, AgentHostJsonContext.Default.ReadScreenParams));

    private static string ReadScrollbackLine(Guid id, int startLine, int maxLines) => Line(
        AgentHostProtocol.Methods.ReadScrollback,
        JsonSerializer.Serialize(new ReadScrollbackParams { PaneId = id, StartLine = startLine, MaxLines = maxLines }, AgentHostJsonContext.Default.ReadScrollbackParams));

    private static string StatusLine(Guid id) => Line(
        AgentHostProtocol.Methods.GetSessionStatus,
        JsonSerializer.Serialize(new GetSessionStatusParams { PaneId = id }, AgentHostJsonContext.Default.GetSessionStatusParams));

    private static string SendInputLine(Guid id, string text, bool submit = false) => Line(
        AgentHostProtocol.Methods.SendInput,
        JsonSerializer.Serialize(new SendInputParams { PaneId = id, Text = text, Submit = submit }, AgentHostJsonContext.Default.SendInputParams));

    private static string CloseLine(Guid id) => Line(
        AgentHostProtocol.Methods.CloseSession,
        JsonSerializer.Serialize(new CloseSessionParams { PaneId = id }, AgentHostJsonContext.Default.CloseSessionParams));

    private static AgentHostResponse Handle(AgentHostService service, string line)
        => service.HandleRequestLineAsync(line, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    /// <summary>The response as the endpoint writes it on the wire.</summary>
    private static string HandleRaw(AgentHostService service, string line)
        => JsonSerializer.Serialize(Handle(service, line), AgentHostJsonContext.Default.AgentHostResponse);

    private static T Result<T>(AgentHostResponse response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var result = response.Result!.Value.Deserialize(typeInfo);
        Assert.NotNull(result);
        return result!;
    }

    // ── listSessions ─────────────────────────────────────────────────────────

    [Fact]
    public void List_puts_the_windowless_sessions_after_the_panes_and_pane_rows_carry_no_windowless_key()
    {
        var registry = new AgentSessionRegistry();
        var pane = RegisterPane(registry);
        var source = new StubWindowlessSource();
        var profileId = Guid.NewGuid();
        var local = source.Add("top", cols: 100, rows: 30, title: "htop");
        var remote = source.Add(sshProfileId: profileId, running: false, exitCode: 3, title: "build");
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetWindowlessSource(source);

        string raw = HandleRaw(service, Line(AgentHostProtocol.Methods.ListSessions, null));

        using var document = JsonDocument.Parse(raw);
        var sessions = document.RootElement.GetProperty("result").GetProperty("sessions");
        Assert.Equal(3, sessions.GetArrayLength());

        // The pane's row is exactly what it was before windowless sessions existed: no new keys at all.
        var paneRow = sessions[0];
        Assert.Equal(pane.PaneId, paneRow.GetProperty("paneId").GetGuid());
        Assert.False(paneRow.TryGetProperty("windowless", out _), raw);
        Assert.False(paneRow.TryGetProperty("endpoint", out _), raw);

        var localRow = sessions[1];
        Assert.Equal(local.SessionId, localRow.GetProperty("paneId").GetGuid());
        Assert.True(localRow.GetProperty("windowless").GetBoolean());
        Assert.Equal("local", localRow.GetProperty("endpoint").GetString());
        Assert.Equal("local", localRow.GetProperty("kind").GetString());
        Assert.Equal("this computer", localRow.GetProperty("profileName").GetString());
        Assert.Equal("htop", localRow.GetProperty("title").GetString());
        Assert.Equal(30, localRow.GetProperty("rows").GetInt32());
        Assert.Equal(100, localRow.GetProperty("cols").GetInt32());
        Assert.False(localRow.GetProperty("isActive").GetBoolean());
        Assert.False(localRow.TryGetProperty("status", out _), raw); // running: no status machine, so no status
        Assert.False(localRow.TryGetProperty("tabId", out _), raw);

        var remoteRow = sessions[2];
        Assert.Equal(remote.SessionId, remoteRow.GetProperty("paneId").GetGuid());
        Assert.True(remoteRow.GetProperty("windowless").GetBoolean());
        Assert.Equal("ssh:" + profileId.ToString("D"), remoteRow.GetProperty("endpoint").GetString());
        Assert.Equal("ssh", remoteRow.GetProperty("kind").GetString());
        Assert.Equal("nova@build-box", remoteRow.GetProperty("profileName").GetString());
        Assert.Equal(AgentHostProtocol.StatusKinds.Exited, remoteRow.GetProperty("status").GetString());

        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.Methods.ListSessions, entry.Method);
        Assert.Null(entry.PaneId);
        Assert.Equal("windowless", entry.Target);
        Assert.Equal("ok", entry.Outcome);
    }

    [Fact]
    public void A_list_with_no_windowless_session_is_not_journaled()
    {
        var registry = new AgentSessionRegistry();
        RegisterPane(registry);
        var source = new StubWindowlessSource();
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetWindowlessSource(source);

        var result = Result(Handle(service, Line(AgentHostProtocol.Methods.ListSessions, null)), AgentHostJsonContext.Default.ListSessionsResult);

        Assert.Single(result.Sessions);
        Assert.Equal([nameof(IWindowlessSessionSource.ListAsync)], source.Calls);
        Assert.Empty(journal.Snapshot());
    }

    // ── readScreen ───────────────────────────────────────────────────────────

    [Fact]
    public void ReadScreen_of_a_windowless_session_returns_its_screen_and_is_journaled()
    {
        var source = new StubWindowlessSource();
        var session = source.Add("hello from the daemon\r\nsecond line", cols: 90, rows: 20);
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);

        var screen = Result(Handle(service, ReadScreenLine(session.SessionId)), AgentHostJsonContext.Default.ScreenSnapshotDto);

        Assert.Equal("hello from the daemon", screen.Lines[0]);
        Assert.Equal("second line", screen.Lines[1]);
        Assert.Equal(20, screen.Rows);
        Assert.Equal(90, screen.Cols);
        Assert.Equal(20, screen.Lines.Length);
        Assert.Equal(1, screen.CursorRow);
        Assert.Equal(11, screen.CursorCol);
        Assert.True(screen.CursorVisible);
        Assert.Equal([(session.SessionId, 0)], source.Reads); // the screen only: no scrollback is asked for

        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.Methods.ReadScreen, entry.Method);
        Assert.Equal(session.SessionId, entry.PaneId);
        Assert.Equal(LocalTarget, entry.Target);
        Assert.True(entry.Windowless); // the dialog calls its id a session's, not a pane's
        Assert.Equal("ok", entry.Outcome);
    }

    [Fact]
    public void ReadScreen_of_a_pane_never_asks_the_source_even_when_a_daemon_has_the_same_id()
    {
        // The registry is asked first: a pane id is answered by the pane. Ids are random, so a daemon session with
        // a pane's id should never exist; if one did, the pane would still win.
        var registry = new AgentSessionRegistry();
        var pane = RegisterPane(registry, "pane text");
        var source = new StubWindowlessSource();
        source.Add("daemon text", id: pane.PaneId);
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetWindowlessSource(source);

        var screen = Result(Handle(service, ReadScreenLine(pane.PaneId)), AgentHostJsonContext.Default.ScreenSnapshotDto);

        Assert.Equal("pane text", screen.Lines[0]);
        Assert.Empty(source.Calls);
        Assert.Empty(journal.Snapshot()); // a pane read stays an unjournaled observe-tier read
    }

    [Fact]
    public void ReadScreen_on_a_daemon_too_old_to_read_screens_is_unsupported()
    {
        var source = new StubWindowlessSource();
        var session = source.Add("unreadable");
        source.ReadOverride = id => new WindowlessScreen(
            WindowlessOutcome.Unsupported, session, null, new MuxReadScreenResult { Running = true });
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);

        var response = Handle(service, ReadScreenLine(session.SessionId));

        Assert.Equal("unsupported", AgentHostProtocol.ErrorCodes.Unsupported);
        Assert.Equal(AgentHostProtocol.ErrorCodes.Unsupported, response.Error?.Code);
        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.ErrorCodes.Unsupported, entry.Outcome);
    }

    [Fact]
    public void ReadScreen_of_an_id_no_daemon_has_is_sessionNotFound_and_one_no_daemon_answered_for_says_so()
    {
        var source = new StubWindowlessSource();
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);

        var unknown = Handle(service, ReadScreenLine(Guid.NewGuid()));
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, unknown.Error?.Code);

        source.ReadOverride = _ => new WindowlessScreen(WindowlessOutcome.Unreachable, null, null, null);
        var unanswered = Handle(service, ReadScreenLine(Guid.NewGuid()));
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, unanswered.Error?.Code);
        Assert.Contains("did not answer in time", unanswered.Error!.Message, StringComparison.Ordinal);

        // Neither named a windowless session, so there is nothing to journal.
        Assert.Empty(journal.Snapshot());
    }

    // ── readScrollback ───────────────────────────────────────────────────────

    [Fact]
    public void ReadScrollback_pages_over_the_newest_rows_the_daemon_holds()
    {
        var source = new StubWindowlessSource();
        string output = string.Concat(Enumerable.Range(0, 40).Select(i => $"line {i}\r\n"));
        var session = source.Add(output, cols: 40, rows: 10);
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);

        var page = Result(Handle(service, ReadScrollbackLine(session.SessionId, startLine: 2, maxLines: 3)), AgentHostJsonContext.Default.ReadScrollbackResult);

        // 40 lines and a cursor on a 10-row screen: 31 rows scrolled off.
        Assert.Equal(31, page.TotalLines);
        Assert.Equal(2, page.StartLine);
        Assert.Equal(["line 2", "line 3", "line 4"], page.Lines);
        Assert.Equal([(session.SessionId, Ntilde.Mux.Contracts.MuxReadScreenLimits.MaxScrollbackRows)], source.Reads);

        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.Methods.ReadScrollback, entry.Method);
        Assert.Equal("ok", entry.Outcome);
    }

    // ── getSessionStatus ─────────────────────────────────────────────────────

    [Fact]
    public void Status_maps_running_children_the_alt_screen_and_exit_from_the_daemons_facts()
    {
        var source = new StubWindowlessSource();
        var busy = source.Add("make", hasActiveChildProcesses: true, lastOutputUnixMs: 1_760_000_001_000);
        var fullScreen = source.Add("\u001b[?1049hvim", lastOutputUnixMs: 1_760_000_002_000);
        var prompt = source.Add("$ ", lastOutputUnixMs: 1_760_000_003_000);
        var exited = source.Add("bye", running: false, exitCode: 7, lastOutputUnixMs: null);
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.SetWindowlessSource(source);

        SessionStatusDto Status(WindowlessSessionInfo s)
            => Result(Handle(service, StatusLine(s.SessionId)), AgentHostJsonContext.Default.SessionStatusDto);

        var busyStatus = Status(busy);
        Assert.Equal(busy.SessionId, busyStatus.PaneId);
        Assert.Equal(AgentHostProtocol.StatusKinds.Running, busyStatus.Status);
        Assert.Equal(AgentHostProtocol.StatusConfidences.Heuristic, busyStatus.Confidence);
        Assert.Equal(1_760_000_001_000, busyStatus.LastOutputAtMs);
        Assert.Equal(1_760_000_001_000, busyStatus.StatusSinceMs);
        Assert.False(busyStatus.IsStalled);
        Assert.Equal(AgentSessionStatusMachine.StallThresholdSeconds, busyStatus.StallThresholdSeconds);
        Assert.Equal(AgentSessionStatusMachine.IdleThresholdSeconds, busyStatus.IdleThresholdSeconds);
        Assert.Null(busyStatus.ExitCode);

        Assert.Equal(AgentHostProtocol.StatusKinds.Running, Status(fullScreen).Status); // a full-screen app is running
        Assert.Equal(AgentHostProtocol.StatusKinds.AwaitingInput, Status(prompt).Status);

        var exitedStatus = Status(exited);
        Assert.Equal(AgentHostProtocol.StatusKinds.Exited, exitedStatus.Status);
        Assert.Equal(7, exitedStatus.ExitCode);
        Assert.Equal(0, exitedStatus.LastOutputAtMs); // the daemon never saw output
        Assert.Equal(0, exitedStatus.StatusSinceMs);
        Assert.All(source.Reads, r => Assert.Equal(0, r.Rows));
    }

    [Fact]
    public void Status_on_a_daemon_too_old_to_read_screens_comes_from_its_session_info()
    {
        var source = new StubWindowlessSource();
        var session = source.Add();
        source.ReadOverride = _ => new WindowlessScreen(WindowlessOutcome.Unsupported, session, null,
            new MuxReadScreenResult { Running = true, HasActiveChildProcesses = true });
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.SetWindowlessSource(source);

        var status = Result(Handle(service, StatusLine(session.SessionId)), AgentHostJsonContext.Default.SessionStatusDto);

        Assert.Equal(AgentHostProtocol.StatusKinds.Running, status.Status);
        Assert.Equal(0, status.LastOutputAtMs);
    }

    // ── sendInput ────────────────────────────────────────────────────────────

    [Fact]
    public void SendInput_with_act_off_is_actDisabled_and_never_asks_the_source()
    {
        var source = new StubWindowlessSource();
        var session = source.Add();
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = false;

        var response = Handle(service, SendInputLine(session.SessionId, "ls\r"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, response.Error?.Code);
        Assert.Empty(source.Calls);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void SendInput_to_a_remote_windowless_session_whose_profile_is_not_allowlisted_is_profileNotAllowed()
    {
        var profileId = Guid.NewGuid();
        var source = new StubWindowlessSource();
        var session = source.Add(sshProfileId: profileId);
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        var asked = new List<Guid>();
        service.SetSshProfileAllowlist(id => { asked.Add(id); return false; });

        var response = Handle(service, SendInputLine(session.SessionId, "rm -rf build\r"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotAllowed, response.Error?.Code);
        Assert.Empty(source.Inputs);
        Assert.Equal([profileId], asked);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotAllowed, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void SendInput_to_an_allowlisted_remote_session_and_to_a_local_one_is_sent_and_journaled()
    {
        var profileId = Guid.NewGuid();
        var source = new StubWindowlessSource();
        var remote = source.Add(sshProfileId: profileId);
        var local = source.Add();
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        service.SetSshProfileAllowlist(id => id == profileId);

        var toRemote = Result(Handle(service, SendInputLine(remote.SessionId, "make", submit: true)), AgentHostJsonContext.Default.SendInputResult);
        var toLocal = Result(Handle(service, SendInputLine(local.SessionId, "é")), AgentHostJsonContext.Default.SendInputResult);

        Assert.Equal(5, toRemote.BytesSent); // "make" plus the submitting CR
        Assert.Equal(2, toLocal.BytesSent);  // UTF-8 bytes, as for a pane
        Assert.Equal([(remote.SessionId, "make\r"), (local.SessionId, "é")], source.Inputs);
        Assert.Equal([profileId, null], source.MayActAsked); // local needs the act toggle alone

        var entries = journal.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(AgentHostProtocol.Methods.SendInput, e.Method);
            Assert.Equal("ok", e.Outcome);
            Assert.True(e.Windowless);
        });
        // Newest first, each named by the host it runs on.
        Assert.Equal([(local.SessionId, LocalTarget), (remote.SessionId, RemoteTarget)], entries.Select(e => (e.PaneId!.Value, e.Target)));
    }

    [Fact]
    public void SendInput_to_an_exited_windowless_session_is_sessionNotRunning_and_to_an_unknown_one_sessionNotFound()
    {
        var source = new StubWindowlessSource();
        var exited = source.Add(running: false, exitCode: 0);
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.SetWindowlessSource(source);
        service.ActEnabled = true;

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotRunning, Handle(service, SendInputLine(exited.SessionId, "x")).Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, Handle(service, SendInputLine(Guid.NewGuid(), "x")).Error?.Code);
        Assert.Empty(source.Inputs);
    }

    // ── closeSession ─────────────────────────────────────────────────────────

    [Fact]
    public void Close_with_act_off_is_actDisabled_and_never_asks_the_source()
    {
        var source = new StubWindowlessSource();
        var session = source.Add();
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = false;

        var response = Handle(service, CloseLine(session.SessionId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, response.Error?.Code);
        Assert.Empty(source.Calls);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void Close_of_a_remote_windowless_session_whose_profile_is_not_allowlisted_is_profileNotAllowed()
    {
        // Unlike a pane close (R5): a windowless kill is invisible and destructive, so it needs the allowlist too.
        var profileId = Guid.NewGuid();
        var source = new StubWindowlessSource();
        var session = source.Add(sshProfileId: profileId);
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        service.SetSshProfileAllowlist(_ => false);

        var response = Handle(service, CloseLine(session.SessionId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotAllowed, response.Error?.Code);
        Assert.Empty(source.Kills);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ProfileNotAllowed, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void Close_of_an_allowlisted_remote_session_kills_it_and_is_journaled()
    {
        var profileId = Guid.NewGuid();
        var source = new StubWindowlessSource();
        var session = source.Add(sshProfileId: profileId);
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        service.SetSshProfileAllowlist(id => id == profileId);
        // No executor is published: a windowless kill goes to the daemon, not through the window.

        var result = Result(Handle(service, CloseLine(session.SessionId)), AgentHostJsonContext.Default.CloseSessionResult);

        Assert.True(result.Closed);
        Assert.Equal([session.SessionId], source.Kills);
        var entry = Assert.Single(journal.Snapshot());
        Assert.Equal(AgentHostProtocol.Methods.CloseSession, entry.Method);
        Assert.Equal(session.SessionId, entry.PaneId);
        Assert.Equal(RemoteTarget, entry.Target);
        Assert.Equal("ok", entry.Outcome);

        // Gone now: a second close finds nothing.
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, Handle(service, CloseLine(session.SessionId)).Error?.Code);
    }

    [Fact]
    public void Close_of_a_pane_still_goes_through_the_window_and_never_asks_the_source()
    {
        var registry = new AgentSessionRegistry();
        var pane = RegisterPane(registry);
        var source = new StubWindowlessSource();
        using var service = NewService(registry, new AgentActivityJournal());
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        var executor = new StubExecutor { OnClose = _ => true };
        service.SetActionExecutor(executor);

        var result = Result(Handle(service, CloseLine(pane.PaneId)), AgentHostJsonContext.Default.CloseSessionResult);

        Assert.True(result.Closed);
        Assert.Equal(pane.PaneId, executor.LastClosePane);
        Assert.Empty(source.Calls);
    }

    // ── fix round 1: acts that land late, and acts the daemon did not confirm ─

    [Fact]
    public void An_act_whose_toggle_is_turned_off_during_its_look_up_is_actDisabled_and_does_nothing()
    {
        // The source's look-up can take seconds (its survey); the act check runs at its end, so it must read the toggle
        // then, not when the request arrived.
        var source = new StubWindowlessSource();
        var session = source.Add();
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        source.BeforeActCheck = () => service.ActEnabled = false;

        var input = Handle(service, SendInputLine(session.SessionId, "rm -rf ~\r"));
        service.ActEnabled = true; // on again as the close arrives, off again by the time its look-up ends
        var close = Handle(service, CloseLine(session.SessionId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, input.Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActDisabled, close.Error?.Code);
        Assert.Empty(source.Inputs);
        Assert.Empty(source.Kills);
        Assert.All(journal.Snapshot(), e => Assert.Equal((AgentHostProtocol.ErrorCodes.ActDisabled, LocalTarget), (e.Outcome, e.Target)));
    }

    [Fact]
    public void An_act_whose_source_is_withdrawn_during_its_look_up_is_actUnavailable_and_does_nothing()
    {
        // Observe turned off, or the window closing, while the survey ran: the source the act started with is no longer
        // the one the window publishes.
        var source = new StubWindowlessSource();
        var session = source.Add();
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        source.BeforeActCheck = () => service.SetWindowlessSource(null);

        var input = Handle(service, SendInputLine(session.SessionId, "x"));
        service.SetWindowlessSource(source);
        var close = Handle(service, CloseLine(session.SessionId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.ActUnavailable, input.Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.ActUnavailable, close.Error?.Code);
        Assert.Empty(source.Inputs);
        Assert.Empty(source.Kills);
    }

    [Fact]
    public void Input_whose_daemon_connection_closed_is_sessionNotFound_and_says_nothing_was_sent()
    {
        var source = new StubWindowlessSource();
        var session = source.Add();
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        source.ActOutcome = WindowlessOutcome.Unreachable;

        var response = Handle(service, SendInputLine(session.SessionId, "x"));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
        Assert.Contains("nothing was sent", response.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(source.Inputs);
        Assert.Equal((AgentHostProtocol.ErrorCodes.SessionNotFound, LocalTarget), (Assert.Single(journal.Snapshot()).Outcome, journal.Snapshot()[0].Target));
    }

    [Fact]
    public void A_close_whose_kill_its_daemon_did_not_confirm_says_the_session_may_have_ended()
    {
        var source = new StubWindowlessSource();
        var session = source.Add();
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;
        source.ActOutcome = WindowlessOutcome.Unreachable;

        var response = Handle(service, CloseLine(session.SessionId));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
        Assert.Contains("did not confirm the kill in time", response.Error!.Message, StringComparison.Ordinal);
        Assert.Contains("may already have ended", response.Error.Message, StringComparison.Ordinal);
        Assert.Contains("List sessions to check", response.Error.Message, StringComparison.Ordinal);
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void A_close_of_an_id_no_daemon_answered_for_keeps_the_general_message()
    {
        // Not found because a daemon did not answer the survey: no kill was sent, so nothing "may have ended".
        var source = new StubWindowlessSource();
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.SetWindowlessSource(new UnreachableActs(source));
        service.ActEnabled = true;

        var response = Handle(service, CloseLine(Guid.NewGuid()));

        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, response.Error?.Code);
        Assert.Contains("did not answer in time", response.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kill", response.Error.Message, StringComparison.Ordinal);
    }

    // ── fix round 1: the outcome map, and the branches it had no test for ────

    [Fact]
    public void An_outcome_the_host_has_no_error_for_is_an_internal_error_never_sessionNotFound()
    {
        var source = new StubWindowlessSource();
        var session = source.Add();
        source.ReadOverride = _ => new WindowlessScreen((WindowlessOutcome)99, session, null, null);
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.SetWindowlessSource(source);

        Assert.Equal(AgentHostProtocol.ErrorCodes.Internal, Handle(service, ReadScreenLine(session.SessionId)).Error?.Code);
    }

    [Fact]
    public void Status_of_a_session_that_ended_after_it_was_listed_is_exited_with_the_listings_exit_code()
    {
        var source = new StubWindowlessSource();
        var session = source.Add(running: false, exitCode: 4);
        source.ReadOverride = _ => new WindowlessScreen(WindowlessOutcome.NotRunning, session, null, null);
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);

        var status = Result(Handle(service, StatusLine(session.SessionId)), AgentHostJsonContext.Default.SessionStatusDto);

        Assert.Equal(AgentHostProtocol.StatusKinds.Exited, status.Status);
        Assert.Equal(4, status.ExitCode);
        Assert.Equal(0, status.LastOutputAtMs);
        Assert.Equal("ok", Assert.Single(journal.Snapshot()).Outcome);
    }

    [Fact]
    public void ReadScrollback_on_a_daemon_too_old_to_read_screens_is_unsupported()
    {
        var source = new StubWindowlessSource();
        var session = source.Add();
        source.ReadOverride = _ => new WindowlessScreen(WindowlessOutcome.Unsupported, session, null, new MuxReadScreenResult { Running = true });
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal());
        service.SetWindowlessSource(source);

        var response = Handle(service, ReadScrollbackLine(session.SessionId, 0, 100));

        Assert.Equal(AgentHostProtocol.ErrorCodes.Unsupported, response.Error?.Code);
    }

    // ── fix round 1: polling does not flood the journal ──────────────────────

    [Fact]
    public void A_polling_agent_does_not_push_an_earlier_act_out_of_the_journal()
    {
        var source = new StubWindowlessSource();
        var session = source.Add("$ ");
        var journal = new AgentActivityJournal();
        using var service = NewService(new AgentSessionRegistry(), journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;

        Assert.Null(Handle(service, SendInputLine(session.SessionId, "make\r")).Error);
        for (int i = 0; i < 300; i++)
        {
            Assert.Null(Handle(service, i % 2 == 0 ? StatusLine(session.SessionId) : ReadScreenLine(session.SessionId)).Error);
        }

        Assert.Equal(
            [
                (AgentHostProtocol.Methods.ReadScreen, 150),
                (AgentHostProtocol.Methods.GetSessionStatus, 150),
                (AgentHostProtocol.Methods.SendInput, 1),
            ],
            journal.Snapshot().Select(e => (e.Method, e.Count)));
    }

    /// <summary>A source whose every look-up ends <see cref="WindowlessOutcome.Unreachable"/> before it finds anything.</summary>
    private sealed class UnreachableActs(IWindowlessSessionSource inner) : IWindowlessSessionSource
    {
        public Task<IReadOnlyList<WindowlessSessionInfo>> ListAsync(CancellationToken ct) => inner.ListAsync(ct);

        public Task<WindowlessScreen> ReadScreenAsync(Guid sessionId, int maxScrollbackRows, CancellationToken ct)
            => Task.FromResult(new WindowlessScreen(WindowlessOutcome.Unreachable, null, null, null));

        public Task<WindowlessOutcome> SendInputAsync(Guid sessionId, string text, Func<Guid?, bool> mayAct, CancellationToken ct)
            => Task.FromResult(WindowlessOutcome.Unreachable);

        public Task<WindowlessOutcome> KillAsync(Guid sessionId, Func<Guid?, bool> mayAct, CancellationToken ct)
            => Task.FromResult(WindowlessOutcome.Unreachable);

        public Task<(WindowlessOutcome Outcome, Guid? SshProfileId)> ResolveAsync(Guid sessionId, CancellationToken ct)
            => Task.FromResult<(WindowlessOutcome, Guid?)>((WindowlessOutcome.Unreachable, null));

        public string? HostDisplayName(Guid? sshProfileId) => inner.HostDisplayName(sshProfileId);
    }

    // ── the window light ─────────────────────────────────────────────────────

    [Fact]
    public void A_windowless_read_lights_the_window_until_the_read_decays()
    {
        var clock = new ManualClock();
        var source = new StubWindowlessSource();
        var session = source.Add("watched");
        using var service = NewService(new AgentSessionRegistry(), new AgentActivityJournal(), clock.Now);
        service.SetWindowlessSource(source);
        int raised = 0;
        service.ObserveActivityChanged += () => raised++;

        Assert.False(service.WindowlessWatched);
        Assert.Null(Handle(service, ReadScreenLine(session.SessionId)).Error);
        Assert.True(service.WindowlessWatched);
        Assert.Equal(1, raised);

        // A second read inside the decay keeps it lit without another transition.
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(Handle(service, StatusLine(session.SessionId)).Error);
        clock.Advance(TimeSpan.FromSeconds(2));
        service.SweepStatuses();
        Assert.True(service.WindowlessWatched);
        Assert.Equal(1, raised);

        clock.Advance(TimeSpan.FromSeconds(AgentAttentionMachine.ReadDecaySeconds));
        service.SweepStatuses();
        Assert.False(service.WindowlessWatched);
        Assert.Equal(2, raised);

        service.SweepStatuses(); // nothing left to clear: no further transition
        Assert.Equal(2, raised);
    }

    [Fact]
    public void A_failed_windowless_read_and_a_pane_read_leave_the_window_light_alone()
    {
        var registry = new AgentSessionRegistry();
        var pane = RegisterPane(registry);
        var source = new StubWindowlessSource();
        using var service = NewService(registry, new AgentActivityJournal());
        service.SetWindowlessSource(source);

        Handle(service, ReadScreenLine(Guid.NewGuid()));
        Handle(service, ReadScreenLine(pane.PaneId));

        Assert.False(service.WindowlessWatched);
    }

    // ── observe off ──────────────────────────────────────────────────────────

    [Fact]
    public void With_the_endpoint_stopped_the_source_is_released_and_never_asked()
    {
        var registry = new AgentSessionRegistry();
        RegisterPane(registry);
        var source = new StubWindowlessSource();
        var session = source.Add();
        var journal = new AgentActivityJournal();
        using var service = NewService(registry, journal);
        service.SetWindowlessSource(source);
        service.ActEnabled = true;

        service.Stop(); // observe turned off, or the window closing

        Assert.Null(service.WindowlessSource);
        var list = Result(Handle(service, Line(AgentHostProtocol.Methods.ListSessions, null)), AgentHostJsonContext.Default.ListSessionsResult);
        Assert.Single(list.Sessions);
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, Handle(service, ReadScreenLine(session.SessionId)).Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, Handle(service, SendInputLine(session.SessionId, "x")).Error?.Code);
        Assert.Equal(AgentHostProtocol.ErrorCodes.SessionNotFound, Handle(service, CloseLine(session.SessionId)).Error?.Code);
        Assert.Empty(source.Calls);
        Assert.False(service.WindowlessWatched);
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock
    {
        private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
