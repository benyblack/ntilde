using Ntilde.Mux.Contracts;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>One refusal rule before process creation, subscription and snapshot delivery.</summary>
internal static class SixelPolicyAdmission
{
    private static readonly PolicyAdmission Policy = new("SIXEL raster", SixelRasterPolicy.EnabledVersion);

    /// <summary>Admits every placement before a snapshot reaches a client callback.</summary>
    internal static void RequireClientSnapshot(TerminalStateSnapshot snapshot, int maximum)
    {
        RequireClient(snapshot.RequiredSixelRasterPolicy, maximum);
        if (snapshot.Sixels is null) return;
        // Check every entry, including ones a source/placement budget would discard later.
        foreach (var placement in snapshot.Sixels)
            if (placement?.NativeRaster is { } native)
                RequireClientSession(native.Version, snapshot.RequiredSixelRasterPolicy, maximum);
    }

    /// <inheritdoc cref="PolicyAdmission.RequireClient"/>
    internal static void RequireClient(int required, int maximum) => Policy.RequireClient(required, maximum);

    /// <inheritdoc cref="PolicyAdmission.RequireClientSession"/>
    internal static void RequireClientSession(int actual, int required, int maximum) => Policy.RequireClientSession(actual, required, maximum);

    /// <inheritdoc cref="PolicyAdmission.Require"/>
    internal static void Require(int required, int maximum) => Policy.Require(required, maximum);

    /// <inheritdoc cref="PolicyAdmission.RequireSession"/>
    internal static void RequireSession(int actual, int required, int maximum) => Policy.RequireSession(actual, required, maximum);
}

/// <summary>
/// The image policy (#838, <see cref="PersistentImagePolicy"/>): admitted like the SIXEL raster policy, and a
/// snapshot for a SIXEL-only session must not carry Kitty or iTerm2 images.
/// </summary>
internal static class ImagePolicyAdmission
{
    internal static readonly PolicyAdmission Policy = new("image", PersistentImagePolicy.EnabledVersion);

    /// <summary>Refuses a snapshot whose images the session's policy does not carry, before any callback sees it.</summary>
    internal static void RequireClientSnapshot(TerminalStateSnapshot snapshot, int sessionPolicy)
    {
        if (PersistentImagePolicy.AllowsImages(sessionPolicy)) return;
        if (snapshot.KittyImages is { Count: > 0 } || snapshot.KittyPlacements is { Count: > 0 } || snapshot.ITermImages is { Count: > 0 })
            throw new MuxProtocolException(MuxErrorCodes.VersionMismatch, "A snapshot carries images its session's image policy does not allow.");
    }
}

/// <summary>
/// One negotiated stream policy: a version both peers support, fixed per session, required exactly and never
/// silently downgraded.
/// </summary>
internal sealed class PolicyAdmission(string name, int enabledVersion)
{
    /// <summary>Reports an unsupported local or peer requirement through the public client error contract.</summary>
    internal void RequireClient(int required, int maximum)
    {
        try { Require(required, maximum); }
        catch (MuxRequestException ex) { throw new MuxProtocolException(ex.Code, ex.Message, ex); }
    }

    /// <summary>Requires exact session semantics instead of silently downgrading a received state.</summary>
    internal void RequireClientSession(int actual, int required, int maximum)
    {
        try { RequireSession(actual, required, maximum); }
        catch (MuxRequestException ex) { throw new MuxProtocolException(ex.Code, ex.Message, ex); }
    }

    /// <summary>Refuses unsupported requirements before a server operation can have effects.</summary>
    internal void Require(int required, int maximum)
    {
        if (required < 0 || required > enabledVersion || required > maximum)
            throw new MuxRequestException(MuxErrorCodes.VersionMismatch, $"Unsupported {name} policy requirement.");
    }

    /// <summary>Admits a connection only to its explicitly selected session policy.</summary>
    internal void RequireSession(int actual, int required, int maximum)
    {
        Require(required, maximum);
        Require(actual, maximum);
        if (actual != required)
            throw new MuxRequestException(MuxErrorCodes.VersionMismatch, $"{char.ToUpperInvariant(name[0])}{name[1..]} policy does not match the requested session.");
    }
}
