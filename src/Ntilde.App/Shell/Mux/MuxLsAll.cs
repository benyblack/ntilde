using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Services.Ssh;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Where <c>ntilde mux ls --all</c> finds its remote hosts: the SSH profile store, the connector for each profile
/// (<see cref="RemoteMuxLister.CreateConnector"/>), the wait per host, and the run's log.
/// </summary>
internal sealed record MuxLsAllRemotes(
    SshConnectionService Profiles,
    Func<SshProfile, RemoteMuxConnector> ConnectorFor,
    TimeSpan PerHost,
    Action<string>? Log,
    bool IsWindows,
    Action<Ntilde.Mux.MuxClient>? ReleaseClient = null);

/// <summary>
/// <c>ntilde mux ls --all [--json]</c> (Phase 5 spec §5): this computer's sessions, read as <c>ls</c> reads them, then those of
/// every SSH profile that keeps its remote sessions (<see cref="RemoteMuxLister"/>). <c>MuxCli</c> cannot reach SSH and
/// <c>ntilde-mux</c> must stay lean, so the App's adapter takes this before <c>MuxCli</c> does.
/// </summary>
/// <remarks>
/// <para>
/// Text: one table, <c>ls</c>'s own with a HOST column in front - "this computer", or the profile's <c>user@host</c>. A host with
/// no sessions has a "No sessions." line, one that could not be listed an "unreachable: " line in the command's own words
/// (<see cref="RemoteMuxLister.Reason"/>), and with no profile keeping its sessions the table ends with "No remote hosts keep
/// sessions.". Ruling P2: a title another machine reports is <see cref="RemoteOutputText.Quote"/>d, this computer's
/// <see cref="RemoteOutputText.Clean"/>ed (a local shell's OSC title can carry the same characters).
/// </para>
/// <para>
/// JSON: <c>{"endpoints":[{"endpoint":"local","host":"this computer","sessions":[…]},{"endpoint":"ssh:&lt;id&gt;","host":"user@host","error":"…"}]}</c>,
/// the sessions as <c>ls --json</c> writes them, the error in the same words as the text.
/// </para>
/// <para>
/// Exit codes: this computer's part keeps <c>ls</c>'s - 1 when no daemon runs here or it could not be listed, 0 otherwise.
/// Unreachable remote hosts alone do not fail the command; the remote hosts are listed either way.
/// </para>
/// </remarks>
internal static class MuxLsAll
{
    private const string AllOption = "--all";
    private const string JsonOption = "--json";
    private const string HostHeading = "HOST";

    /// <summary>The run's log, in the daemon's log folder: the last run's lines only.</summary>
    internal const string LogFileName = "mux-ls-all.log";

    /// <summary>
    /// Whether <paramref name="verbArgs"/> is <c>ls --all</c>: <c>ls</c> in any case, as <c>MuxCli</c> matches verbs, with
    /// <c>--all</c> and nothing but <c>--json</c> beside it. Anything else stays <c>MuxCli</c>'s: an unknown option prints its usage.
    /// </summary>
    public static bool Handles(IReadOnlyList<string> verbArgs)
    {
        ArgumentNullException.ThrowIfNull(verbArgs);
        return verbArgs.Count > 1
            && string.Equals(verbArgs[0], "ls", StringComparison.OrdinalIgnoreCase)
            && verbArgs.Skip(1).Contains(AllOption)
            && verbArgs.Skip(1).All(a => a is AllOption or JsonOption);
    }

    /// <summary>Lists every host and prints the table, or the JSON. Returns the exit code.</summary>
    public static int Run(IReadOnlyList<string> verbArgs, TextWriter stdout, MuxCliHost host, MuxLsAllRemotes remotes)
    {
        ArgumentNullException.ThrowIfNull(verbArgs);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(remotes);

        // The remote hosts all at once, on the pool; this computer's daemon answers meanwhile.
        Task<IReadOnlyList<RemoteListing>> remote = RemoteMuxLister.ListAsync(
            remotes.Profiles, remotes.ConnectorFor, remotes.PerHost, remotes.Log, remotes.IsWindows, CancellationToken.None, remotes.ReleaseClient);
        HostListing local = ListLocal(host, remotes.Log);
        IReadOnlyList<RemoteListing> others = remote.GetAwaiter().GetResult();
        HostListing[] hosts =
        [
            local,
            .. others.Select(r => new HostListing(MuxEndpointId.ForSsh(r.ProfileId).ToString(), r.Host, IsLocal: false, r.Sessions, r.Error)),
        ];

        if (verbArgs.Skip(1).Contains(JsonOption)) WriteJson(stdout, hosts);
        else WriteText(stdout, hosts, anyRemote: others.Count > 0);
        return local.Error is null ? 0 : 1;
    }

    /// <summary>
    /// The app's remote half: the SSH profile store, and per profile a connector whose transport the profile's backend decides
    /// (<see cref="RemoteMuxHostFactory.CreateTransport"/>, as a window's automatic reconnect builds it), signing in with the
    /// vault's saved password where a reconnect would. The log goes to <see cref="LogFileName"/> in <paramref name="host"/>'s
    /// log folder. Built only for <c>ls --all</c>: nothing else reads the store or the vault.
    /// </summary>
    public static MuxLsAllRemotes ForThisApp(MuxCliHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var service = new SshConnectionService();
        Action<string> log = FileLog(Path.Combine(host.Paths.LogDirectory, LogFileName));
        // Read only for a Native profile, which the switch can refuse; as the window reads it, from its settings.
        var nativeSshEnabled = new Lazy<bool>(static () => TerminalSettings.Load().ExperimentalNativeSshEnabled);
        var vault = new Lazy<VaultService>(static () => new VaultService());
        string? askPassHelper = SshAskPassCommand.LocateHelper();

        ISshExecTransport TransportFor(SshProfile profile, RemoteMuxTransportRequest request) => RemoteMuxHostFactory.CreateTransport(
            profile,
            request,
            (p, selfContained) => service.BuildLaunchDetailsFor(p, SshDiagnosticsLevel.None, selfContained),
            static () => new NativeSshInterop(),
            () => nativeSshEnabled.Value,
            askPassHelper,
            log,
            OpenSshClientVersionCache.Shared);

        return new MuxLsAllRemotes(
            service,
            profile => RemoteMuxLister.CreateConnector(
                profile,
                TransportFor,
                p => RemoteMuxHostFactory.ReadSavedPassword(vault.Value, p),
                new SshAskPassSessionMarkers(static () => SshAskPassSessionMarkers.DefaultDirectory),
                log),
            RemoteMuxLister.PerHostTimeout,
            log,
            OperatingSystem.IsWindows());
    }

    /// <summary>This computer's sessions, as <c>ls</c> reads them: never starting a daemon.</summary>
    private static HostListing ListLocal(MuxCliHost host, Action<string>? log)
    {
        string name = MuxHostPolicy.Local.DisplayName;
        string endpoint = MuxEndpointId.Local.ToString();
        try
        {
            IReadOnlyList<SessionSummary>? sessions = MuxCli.ListSessions(host);
            return new HostListing(endpoint, name, IsLocal: true, sessions, sessions is null ? MuxListingError.NotRunning : null);
        }
        catch (Exception ex) when (MuxCli.IsReportableFailure(ex))
        {
            // What ls would print as "mux: ...", with exit 1: worded by its kind here, like a remote host's.
            MuxListingError error = RemoteMuxLister.ErrorOf(ex);
            log?.Invoke($"[mux] ls --all: {name}: {error}: {ex.GetType().Name}: {ex.Message}");
            return new HostListing(endpoint, name, IsLocal: true, null, error);
        }
    }

    private static void WriteText(TextWriter stdout, IReadOnlyList<HostListing> hosts, bool anyRemote)
    {
        int width = Math.Max(HostHeading.Length, hosts.Max(h => h.Host.Length));
        string Pad(string text) => text.PadRight(width);

        stdout.WriteLine($"{Pad(HostHeading)}  {MuxCli.ListHeader}");
        foreach (HostListing h in hosts)
        {
            if (h.Error is { } error)
            {
                stdout.WriteLine($"{Pad(h.Host)}  unreachable: {RemoteMuxLister.Reason(error)}");
                continue;
            }

            IReadOnlyList<SessionSummary> sessions = h.Sessions ?? [];
            if (sessions.Count == 0)
            {
                stdout.WriteLine($"{Pad(h.Host)}  No sessions.");
                continue;
            }

            Func<string?, string> fit = h.IsLocal ? RemoteOutputText.Clean : RemoteOutputText.Quote;
            foreach (SessionSummary s in sessions) stdout.WriteLine($"{Pad(h.Host)}  {MuxCli.ListRow(s, fit(s.Title))}");
        }

        if (!anyRemote) stdout.WriteLine("No remote hosts keep sessions.");
    }

    private static void WriteJson(TextWriter stdout, IReadOnlyList<HostListing> hosts)
    {
        var result = new MuxLsAllResult
        {
            Endpoints = [.. hosts.Select(h => new MuxLsAllEndpoint
            {
                Endpoint = h.Endpoint,
                Host = h.Host,
                Sessions = h.Sessions,
                Error = h.Error is { } error ? RemoteMuxLister.Reason(error) : null,
            })],
        };
        stdout.WriteLine(JsonSerializer.Serialize(result, MuxCommandJsonContext.Default.MuxLsAllResult));
    }

    /// <summary>
    /// The run's log: the connectors' lines and each failure's own text, which the output never shows. A file of its own,
    /// started afresh by the run's first line - never the window's debug log, which a CLI run that opened it would truncate.
    /// A line that cannot be written is dropped; the listing goes on.
    /// </summary>
    private static Action<string> FileLog(string path)
    {
        var gate = new object();
        bool started = false;
        return line =>
        {
            lock (gate)
            {
                try
                {
                    if (!started)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllText(path, string.Empty);
                        started = true;
                    }

                    File.AppendAllText(path, string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}{Environment.NewLine}"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Dropped: a diagnostic never fails the command.
                }
            }
        };
    }

    /// <summary>One host's listing, as both outputs print it.</summary>
    private sealed record HostListing(string Endpoint, string Host, bool IsLocal, IReadOnlyList<SessionSummary>? Sessions, MuxListingError? Error);
}

/// <summary><c>ntilde mux ls --all --json</c>: every host's listing, this computer's first (Phase 5 spec §5).</summary>
internal sealed record MuxLsAllResult
{
    public IReadOnlyList<MuxLsAllEndpoint> Endpoints { get; init; } = [];
}

/// <summary>
/// One host in <see cref="MuxLsAllResult"/>: <c>local</c> or <c>ssh:&lt;profile id&gt;</c>, its name, and its sessions as
/// <c>ls --json</c> writes them - or, in their place, why there are none to show, in <c>ls --all</c>'s own words.
/// </summary>
internal sealed record MuxLsAllEndpoint
{
    public required string Endpoint { get; init; }

    public required string Host { get; init; }

    public IReadOnlyList<SessionSummary>? Sessions { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Source-generated JSON for <c>ls --all --json</c>, reflection-free (NativeAOT): <c>MuxJsonContext</c>'s options and its
/// default encoder, so a session is written exactly as <c>ls --json</c> writes it.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = false)]
[JsonSerializable(typeof(MuxLsAllResult))]
internal sealed partial class MuxCommandJsonContext : JsonSerializerContext
{
}
