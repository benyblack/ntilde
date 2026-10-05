namespace Ntilde.Shell.Mux;

/// <summary>
/// How one <see cref="MuxConnectionHost"/> waits and retries (Phase 4 spec §5). <see cref="Local"/> keeps
/// the numbers the local daemon has always had. A remote host waits long enough for an askpass prompt to
/// be answered, has no failure cooldown so a user's retry is never swallowed, and gives a round trip over
/// SSH more time.
/// </summary>
/// <param name="ConnectTimeout">How long a pane's spawn waits for the connection (the factory's GetClient).</param>
/// <param name="FailureCooldown">After a failed attempt, how long GetClient answers null at once instead of trying again.</param>
/// <param name="RpcTimeout">How long the factory waits for one request (list, spawn, kill).</param>
/// <param name="IsRemote">True for an endpoint reached over SSH.</param>
/// <param name="DisplayName">Where the daemon runs, for messages ("this computer", or the profile's name).</param>
internal sealed record MuxHostPolicy(TimeSpan ConnectTimeout, TimeSpan FailureCooldown, TimeSpan RpcTimeout, bool IsRemote, string DisplayName)
{
    public static readonly MuxHostPolicy Local = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3), false, "this computer");

    /// <summary>A remote host's <see cref="ConnectTimeout"/>: long enough for the user to answer an SSH prompt.</summary>
    public static readonly TimeSpan RemoteConnectTimeout = TimeSpan.FromSeconds(120);

    public static MuxHostPolicy Remote(string displayName) => new(RemoteConnectTimeout, TimeSpan.Zero, TimeSpan.FromSeconds(10), true, displayName);
}
