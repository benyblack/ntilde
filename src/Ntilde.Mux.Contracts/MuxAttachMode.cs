namespace Ntilde.Mux.Contracts;

/// <summary>How an attach treats other clients (Phase 3 spec §3).</summary>
public enum MuxAttachMode
{
    /// <summary>Today's behaviour: attach whatever else is attached; this client's size wins.</summary>
    Shared = 0,

    /// <summary>Fail with <see cref="MuxErrorCodes.SessionAttached"/> if another interactive client is attached; decided on the session's parse thread.</summary>
    IfUnattached = 1,

    /// <summary>Receive the stream, but the server drops this client's input and resizes. A convenience, not a security boundary.</summary>
    ReadOnly = 2,
}

/// <summary>The wire strings of <see cref="MuxAttachMode"/>. <see cref="MuxAttachMode.Shared"/> travels as null, so a shared attach is the v1 shape.</summary>
public static class MuxAttachModes
{
    public const string Shared = "shared";
    public const string IfUnattached = "ifUnattached";
    public const string ReadOnly = "readOnly";

    public static string? ToWire(MuxAttachMode mode) => mode switch
    {
        MuxAttachMode.Shared => null,
        MuxAttachMode.IfUnattached => IfUnattached,
        MuxAttachMode.ReadOnly => ReadOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown attach mode."),
    };

    /// <summary>Exact (ordinal) match; null means shared. False for anything else.</summary>
    public static bool TryParse(string? wire, out MuxAttachMode mode)
    {
        switch (wire)
        {
            case null:
            case Shared:
                mode = MuxAttachMode.Shared;
                return true;
            case IfUnattached:
                mode = MuxAttachMode.IfUnattached;
                return true;
            case ReadOnly:
                mode = MuxAttachMode.ReadOnly;
                return true;
            default:
                mode = MuxAttachMode.Shared;
                return false;
        }
    }
}
