using System.Globalization;
using Ntilde.Mux.Contracts;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Shell.Mux;

/// <summary>
/// One line of the "Attach to session…" picker (spec §7.2, Phase 5 spec §5): a session to attach
/// (<see cref="MuxSessionPickerRow"/>), a host to connect to first (<see cref="MuxSessionPickerConnectRow"/>), or a host
/// that could not be listed (<see cref="MuxSessionPickerErrorRow"/>), which is shown but cannot be chosen.
/// </summary>
internal abstract record MuxPickerItem
{
    /// <summary>The line as the picker shows it.</summary>
    public abstract string Display { get; }

    /// <summary>False for a line that only informs: the picker shows it disabled and never returns it.</summary>
    public virtual bool IsSelectable => true;
}

/// <summary>
/// A session on <paramref name="Endpoint"/>'s daemon, whose host is <paramref name="HostDisplayName"/> ("this computer", or
/// <c>user@host</c>). Built by <see cref="MuxSessionPicker.BuildRows"/>, which cleans the title, command and cwd first.
/// </summary>
internal sealed record MuxSessionPickerRow(
    MuxEndpointId Endpoint, string HostDisplayName,
    Guid SessionId, string Title, string Command, string? Cwd, int Cols, int Rows,
    int AttachedClients, bool Running, int? ExitCode, bool OpenHere) : MuxPickerItem
{
    public string State => Running ? "running" : $"exited {ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}";

    /// <summary>The session's title, or its command when it has none: what the picker sorts by, and the tab it opens is called.</summary>
    public string Name => string.IsNullOrWhiteSpace(Title) ? Command : Title;

    /// <summary>A remote row names its host first (<c>[user@host] </c>); the local daemon's rows do not.</summary>
    public override string Display
    {
        get
        {
            string host = Endpoint.IsLocal ? string.Empty : $"[{HostDisplayName}] ";
            string where = string.IsNullOrEmpty(Cwd) ? string.Empty : "  " + Cwd;
            string attached = AttachedClients switch
            {
                0 => "detached",
                _ => string.Create(CultureInfo.InvariantCulture, $"{AttachedClients} attached"),
            };
            string here = OpenHere ? "  ·  open here" : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"{host}{Name}  —  {Command}{where}  ·  {Cols}x{Rows}  ·  {attached}  ·  {State}{here}");
        }
    }
}

/// <summary>
/// A profile that keeps its remote sessions, whose host the window has no connection to now (Phase 5 spec §5): a host
/// is connected only while a pane needs it, so the shell just detached from it would not be listed otherwise.
/// </summary>
internal sealed record MuxSessionPickerConnectRow(Guid ProfileId, string HostDisplayName) : MuxPickerItem
{
    // A connect, unlike ls --all's listing (release hardening item 7), starts the host's daemon on demand: the row says so.
    public override string Display => $"Connect to {HostDisplayName}… (starts its multiplexer if none is running)";
}

/// <summary>A host whose sessions could not be listed: one line, worded from <paramref name="Error"/> alone.</summary>
internal sealed record MuxSessionPickerErrorRow(MuxEndpointId Endpoint, string HostDisplayName, MuxPickerHostError Error) : MuxPickerItem
{
    public override bool IsSelectable => false;

    public override string Display => $"[{HostDisplayName}] not reachable: {MuxSessionPicker.Reason(Error)}";
}

/// <summary>Why a host's sessions could not be listed. The picker words each in its own words, never a server's or an exception's.</summary>
internal enum MuxPickerHostError
{
    /// <summary>No connection could be had, or it failed during the listing.</summary>
    ConnectionFailed,

    /// <summary>The daemon did not answer within the host's wait.</summary>
    TimedOut,

    /// <summary>The daemon answered with a protocol error.</summary>
    NotUsable,

    /// <summary>A restart of that daemon is under way (Phase 5 Task 23): its sessions are about to end.</summary>
    Restarting,
}

/// <summary>One host's listing for the picker: its sessions, or why there are none to show (<paramref name="Error"/> set).</summary>
internal sealed record MuxPickerHostListing(MuxEndpointId Endpoint, string HostDisplayName, IReadOnlyList<SessionSummary>? Sessions, MuxPickerHostError? Error);

internal static class MuxSessionPicker
{
    /// <summary>
    /// The picker's session and error rows, grouped by host: the local daemon's first, then the remote hosts in the order
    /// given (the window's <c>MuxConnectionHosts.All</c>). Within a host, running sessions come first, then by name; faulted
    /// sessions cannot be attached and are left out. A host with an <see cref="MuxPickerHostListing.Error"/> gives one
    /// <see cref="MuxSessionPickerErrorRow"/>. "Open here" is keyed by endpoint: one id on two daemons is two sessions.
    /// </summary>
    /// <remarks>
    /// Ruling P2 (Task 5): a remote session's title, command and cwd come from another machine, so they are
    /// <see cref="RemoteOutputText.Quote"/>d - control, bidi and format characters stripped, the length capped. A local
    /// one's are <see cref="RemoteOutputText.Clean"/>ed: a local shell's OSC title can carry the same characters.
    /// </remarks>
    public static IReadOnlyList<MuxPickerItem> BuildRows(IEnumerable<MuxPickerHostListing> hosts, IReadOnlySet<(MuxEndpointId, Guid)> openHere)
    {
        var items = new List<MuxPickerItem>();
        foreach (MuxPickerHostListing host in hosts.OrderBy(h => h.Endpoint.IsLocal ? 0 : 1)) // stable: the remotes keep their order
        {
            if (host.Error is { } error)
            {
                items.Add(new MuxSessionPickerErrorRow(host.Endpoint, host.HostDisplayName, error));
                continue;
            }

            Func<string?, string> fit = host.Endpoint.IsLocal ? RemoteOutputText.Clean : RemoteOutputText.Quote;
            items.AddRange((host.Sessions ?? [])
                .Where(s => !s.Faulted)
                .Select(s => new MuxSessionPickerRow(
                    host.Endpoint, host.HostDisplayName, s.SessionId, fit(s.Title), fit(s.Command), s.Cwd is null ? null : fit(s.Cwd),
                    s.Cols, s.Rows, s.AttachedClients, s.Running, s.ExitCode, openHere.Contains((host.Endpoint, s.SessionId))))
                .OrderByDescending(r => r.Running)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase));
        }

        return items;
    }

    /// <summary>What a failed listing's exception was, as a kind: its text is never shown.</summary>
    public static MuxPickerHostError ErrorOf(Exception ex) => ex switch
    {
        MuxProtocolException => MuxPickerHostError.NotUsable,
        TimeoutException or OperationCanceledException => MuxPickerHostError.TimedOut,
        _ => MuxPickerHostError.ConnectionFailed,
    };

    /// <summary>The words for <paramref name="error"/> in an error row.</summary>
    public static string Reason(MuxPickerHostError error) => error switch
    {
        MuxPickerHostError.TimedOut => "it did not answer in time",
        MuxPickerHostError.NotUsable => "its multiplexer is not usable",
        MuxPickerHostError.Restarting => "the multiplexer is restarting",
        _ => "the connection failed",
    };
}
