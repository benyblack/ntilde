using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.AgentHost;
using Ntilde.VT;
using MuxReadScreenResult = Ntilde.Mux.Contracts.ReadScreenResult;

namespace Ntilde.AppTests.AgentHost;

/// <summary>
/// A scripted <see cref="IWindowlessSessionSource"/> for the agent host's windowless tests (Phase 5 Task 13). Each
/// session has a buffer of its own, read the way the real source reads a daemon's: a snapshot of it with the requested
/// scrollback rows, plus the status the daemon reports with it. Acts go the real source's way too: look-up, then the
/// caller's <c>mayAct</c>, then the running state. Every call is recorded, so a test can say the source was never asked.
/// </summary>
internal sealed class StubWindowlessSource : IWindowlessSessionSource
{
    private readonly object _gate = new();
    private readonly List<Session> _sessions = new();
    private readonly List<string> _calls = new();
    private readonly List<(Guid Id, int Rows)> _reads = new();
    private readonly List<(Guid Id, string Text)> _inputs = new();
    private readonly List<Guid> _kills = new();
    private readonly List<Guid?> _mayActAsked = new();
    private readonly Dictionary<Guid, string> _hostNames = new();

    private sealed record Session(WindowlessSessionInfo Info, TerminalBuffer Buffer, MuxReadScreenResult Status);

    /// <summary>When it returns non-null, what <see cref="ReadScreenAsync"/> answers instead of reading the buffer.</summary>
    public Func<Guid, WindowlessScreen?>? ReadOverride { get; set; }

    /// <summary>Runs inside an act's look-up, just before its <c>mayAct</c> is asked: a test changes the world there.</summary>
    public Action? BeforeActCheck { get; set; }

    /// <summary>
    /// When set, what an act whose check passed ends with instead of sending or killing: <see cref="WindowlessOutcome.Unreachable"/>
    /// is a connection that closed before the input, or a kill its daemon did not confirm in time.
    /// </summary>
    public WindowlessOutcome? ActOutcome { get; set; }

    /// <summary>Every call, by member name, in order.</summary>
    public IReadOnlyList<string> Calls { get { lock (_gate) return _calls.ToArray(); } }

    public IReadOnlyList<(Guid Id, int Rows)> Reads { get { lock (_gate) return _reads.ToArray(); } }

    public IReadOnlyList<(Guid Id, string Text)> Inputs { get { lock (_gate) return _inputs.ToArray(); } }

    public IReadOnlyList<Guid> Kills { get { lock (_gate) return _kills.ToArray(); } }

    /// <summary>What each act's <c>mayAct</c> was asked: the session's SSH profile id, null for this computer.</summary>
    public IReadOnlyList<Guid?> MayActAsked { get { lock (_gate) return _mayActAsked.ToArray(); } }

    /// <summary>
    /// Adds a session whose screen is <paramref name="content"/> fed through a parser. A session with an
    /// <paramref name="sshProfileId"/> is on that profile's remote endpoint; one without is on this computer's.
    /// </summary>
    public WindowlessSessionInfo Add(
        string content = "",
        int cols = 80,
        int rows = 24,
        Guid? sshProfileId = null,
        bool running = true,
        int? exitCode = null,
        bool hasActiveChildProcesses = false,
        long? lastOutputUnixMs = 1_760_000_000_000,
        string title = "shell",
        Guid? id = null)
    {
        var buffer = new TerminalBuffer(cols, rows);
        if (content.Length > 0)
        {
            new AnsiParser(buffer).Process(content);
        }

        var info = new WindowlessSessionInfo(
            id ?? Guid.NewGuid(),
            sshProfileId is { } profile ? "ssh:" + profile.ToString("D") : "local",
            sshProfileId,
            sshProfileId is null ? "this computer" : "nova@build-box",
            title,
            cols,
            rows,
            running,
            exitCode);
        var status = new MuxReadScreenResult
        {
            Running = running,
            ExitCode = exitCode,
            HasActiveChildProcesses = hasActiveChildProcesses,
            AttachedClients = 0,
            Title = title,
            LastOutputUnixMs = lastOutputUnixMs,
        };

        lock (_gate)
        {
            _sessions.Add(new Session(info, buffer, status));
            if (sshProfileId is { } profileId) _hostNames[profileId] = info.HostDisplayName;
        }
        return info;
    }

    public Task<IReadOnlyList<WindowlessSessionInfo>> ListAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(nameof(ListAsync));
            return Task.FromResult<IReadOnlyList<WindowlessSessionInfo>>(_sessions.Select(s => s.Info).ToArray());
        }
    }

    public Task<WindowlessScreen> ReadScreenAsync(Guid sessionId, int maxScrollbackRows, CancellationToken ct)
    {
        Session? session;
        lock (_gate)
        {
            _calls.Add(nameof(ReadScreenAsync));
            _reads.Add((sessionId, maxScrollbackRows));
            session = Find(sessionId);
        }

        if (ReadOverride?.Invoke(sessionId) is { } scripted)
        {
            return Task.FromResult(scripted);
        }
        if (session is null)
        {
            return Task.FromResult(new WindowlessScreen(WindowlessOutcome.NotFound, null, null, null));
        }

        return Task.FromResult(new WindowlessScreen(
            WindowlessOutcome.Ok, session.Info, session.Buffer.ExportState(maxScrollbackRows), session.Status));
    }

    public Task<WindowlessOutcome> SendInputAsync(Guid sessionId, string text, Func<Guid?, bool> mayAct, CancellationToken ct)
    {
        Session? session;
        lock (_gate)
        {
            _calls.Add(nameof(SendInputAsync));
            session = Find(sessionId);
        }
        if (session is null) return Task.FromResult(WindowlessOutcome.NotFound);
        if (!Allowed(session, mayAct)) return Task.FromResult(WindowlessOutcome.NotAllowed);
        if (!session.Info.Running) return Task.FromResult(WindowlessOutcome.NotRunning);
        if (ActOutcome is { } scripted) return Task.FromResult(scripted);

        lock (_gate)
        {
            _inputs.Add((sessionId, text));
        }
        return Task.FromResult(WindowlessOutcome.Ok);
    }

    public Task<WindowlessOutcome> KillAsync(Guid sessionId, Func<Guid?, bool> mayAct, CancellationToken ct)
    {
        Session? session;
        lock (_gate)
        {
            _calls.Add(nameof(KillAsync));
            session = Find(sessionId);
        }
        if (session is null) return Task.FromResult(WindowlessOutcome.NotFound);
        if (!Allowed(session, mayAct)) return Task.FromResult(WindowlessOutcome.NotAllowed);
        if (!session.Info.Running) return Task.FromResult(WindowlessOutcome.NotRunning);
        if (ActOutcome is { } scripted) return Task.FromResult(scripted);

        lock (_gate)
        {
            _kills.Add(sessionId);
            _sessions.Remove(session);
        }
        return Task.FromResult(WindowlessOutcome.Ok);
    }

    public Task<(WindowlessOutcome Outcome, Guid? SshProfileId)> ResolveAsync(Guid sessionId, CancellationToken ct)
    {
        Session? session;
        lock (_gate)
        {
            _calls.Add(nameof(ResolveAsync));
            session = Find(sessionId);
        }
        return Task.FromResult<(WindowlessOutcome, Guid?)>(session is null
            ? (WindowlessOutcome.NotFound, null)
            : (WindowlessOutcome.Ok, session.Info.SshProfileId));
    }

    /// <summary>
    /// As the real source: "this computer" for null, the host's name for an endpoint a session was ever added on (a host
    /// outlives its sessions), else null.
    /// </summary>
    public string? HostDisplayName(Guid? sshProfileId)
    {
        if (sshProfileId is not { } profileId) return "this computer";
        lock (_gate)
        {
            return _hostNames.TryGetValue(profileId, out string? name) ? name : null;
        }
    }

    private bool Allowed(Session session, Func<Guid?, bool> mayAct)
    {
        lock (_gate)
        {
            _mayActAsked.Add(session.Info.SshProfileId);
        }
        BeforeActCheck?.Invoke();
        return mayAct(session.Info.SshProfileId);
    }

    private Session? Find(Guid sessionId) => _sessions.Find(s => s.Info.SessionId == sessionId);
}
