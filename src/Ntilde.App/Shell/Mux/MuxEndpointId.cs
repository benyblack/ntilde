namespace Ntilde.Shell.Mux;

/// <summary>
/// Which daemon a pane's session lives on (Phase 4 spec §5): <c>local</c>, or <c>ssh:&lt;sshProfileId&gt;</c>
/// with the profile id as a Guid in <c>N</c> format. This is what <c>PaneNode.MuxEndpoint</c> holds, and
/// what restore, dedupe and orphan adoption key on together with the session id. The default value is
/// <see cref="Local"/>.
/// </summary>
internal readonly record struct MuxEndpointId
{
    private const string LocalText = "local";
    private const string SshPrefix = "ssh:";

    private MuxEndpointId(Guid? sshProfileId) => SshProfileId = sshProfileId;

    /// <summary>This computer's daemon. <c>default(MuxEndpointId)</c> is this value too.</summary>
    public static readonly MuxEndpointId Local;

    /// <summary>The daemon reached through the SSH profile <paramref name="profileId"/>.</summary>
    public static MuxEndpointId ForSsh(Guid profileId) => new(profileId);

    public bool IsLocal => SshProfileId is null;

    /// <summary>The SSH profile whose host runs this daemon; null for <see cref="Local"/>.</summary>
    public Guid? SshProfileId { get; }

    /// <summary>
    /// Never throws: a value that is not a well-formed <c>ssh:</c> endpoint is <see cref="Local"/>. That
    /// covers null and empty (files written before Phase 4), <c>local</c>, and the local daemon's pipe or
    /// socket name, which is what Phase 0 to 3 wrote here. Use <see cref="TryParse"/> to tell a malformed
    /// <c>ssh:</c> value apart, so it can be logged.
    /// </summary>
    public static MuxEndpointId Parse(string? persisted) =>
        TryParse(persisted, out MuxEndpointId id) ? id : Local;

    /// <summary>
    /// <see cref="Parse"/>, returning false only for an <c>ssh:</c> value whose profile id is not a Guid
    /// (a hand-edited file). <paramref name="id"/> is <see cref="Local"/> then, as it is for every other
    /// value that is not an ssh endpoint. Lenient on read (any Guid format, any prefix case); <see cref="ToString"/>
    /// writes the canonical form.
    /// </summary>
    public static bool TryParse(string? persisted, out MuxEndpointId id)
    {
        id = Local;
        if (persisted is null || !persisted.StartsWith(SshPrefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (!Guid.TryParse(persisted.AsSpan(SshPrefix.Length), out Guid profileId)) return false;
        id = ForSsh(profileId);
        return true;
    }

    /// <summary>The persisted form: <c>local</c>, or <c>ssh:</c> and the profile id in <c>N</c> format.</summary>
    public override string ToString() => SshProfileId is Guid profileId ? SshPrefix + profileId.ToString("N") : LocalText;
}
