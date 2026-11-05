using Ntilde.Mux.Contracts;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>One refusal rule before process creation, subscription and snapshot delivery.</summary>
internal static class SixelPolicyAdmission
{
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

    /// <summary>Reports an unsupported local or peer requirement through the public client error contract.</summary>
    internal static void RequireClient(int required, int maximum)
    {
        try { Require(required, maximum); }
        catch (MuxRequestException ex) { throw new MuxProtocolException(ex.Code, ex.Message, ex); }
    }

    /// <summary>Requires exact session semantics instead of silently downgrading a received state.</summary>
    internal static void RequireClientSession(int actual, int required, int maximum)
    {
        try { RequireSession(actual, required, maximum); }
        catch (MuxRequestException ex) { throw new MuxProtocolException(ex.Code, ex.Message, ex); }
    }

    /// <summary>Refuses unsupported requirements before a server operation can have effects.</summary>
    internal static void Require(int required, int maximum)
    {
        if (required < 0 || required > SixelRasterPolicy.EnabledVersion || required > maximum)
            throw new MuxRequestException(MuxErrorCodes.VersionMismatch, "Unsupported SIXEL raster policy requirement.");
    }

    /// <summary>Admits a connection only to its explicitly selected session policy.</summary>
    internal static void RequireSession(int actual, int required, int maximum)
    {
        Require(required, maximum);
        Require(actual, maximum);
        if (actual != required)
            throw new MuxRequestException(MuxErrorCodes.VersionMismatch, "SIXEL raster policy does not match the requested session.");
    }
}
