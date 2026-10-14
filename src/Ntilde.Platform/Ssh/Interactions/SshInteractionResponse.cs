namespace Ntilde.Platform.Ssh.Interactions;

public sealed class SshInteractionResponse
{
    public bool IsAccepted { get; init; }
    public bool IsCanceled { get; init; }
    public string Secret { get; init; } = string.Empty;
    public bool RememberPasswordInVault { get; init; }
    public IReadOnlyList<string> KeyboardResponses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The secret was filled from a store - the vault, or a password the session already holds - not typed by the user
    /// for this prompt. A persisted remote tab hands only typed passwords to its host's SFTP connections, which read the
    /// vault themselves (Phase 5 spec R8).
    /// </summary>
    public bool FilledFromStore { get; init; }

    public static SshInteractionResponse AcceptHostKey() => new() { IsAccepted = true };

    public static SshInteractionResponse Cancel() => new() { IsCanceled = true };

    public static SshInteractionResponse FromSecret(string secret, bool rememberPasswordInVault = false) =>
        new()
        {
            Secret = secret ?? string.Empty,
            RememberPasswordInVault = rememberPasswordInVault
        };

    /// <summary>A secret filled from a store rather than typed (<see cref="FilledFromStore"/>).</summary>
    public static SshInteractionResponse FromStoredSecret(string secret) =>
        new()
        {
            Secret = secret ?? string.Empty,
            FilledFromStore = true
        };

    public static SshInteractionResponse FromKeyboardResponses(params string[] responses) =>
        new() { KeyboardResponses = responses ?? Array.Empty<string>() };
}
