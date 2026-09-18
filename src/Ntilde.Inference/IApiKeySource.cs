namespace Ntilde.Inference;

/// <summary>
/// Where <see cref="SystemOneClient"/> gets its bearer token. Implemented by the App over the
/// OS secret store; the client never sees settings or the vault type.
/// </summary>
public interface IApiKeySource
{
    /// <summary>The key, or null when none is configured.</summary>
    string? TryGetKey();
}
