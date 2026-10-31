using Ntilde.Platform.Security;

namespace Ntilde.Platform.Ssh.Interactions;

public sealed class SshInteractionResponse : IDisposable
{
    public bool IsAccepted { get; init; }
    public bool IsCanceled { get; init; }
    public SecretBuffer Secret { get; init; } = SecretBuffer.FromText("");
    public bool RememberPasswordInVault { get; init; }
    public IReadOnlyList<SecretBuffer> KeyboardResponses { get; init; } = Array.Empty<SecretBuffer>();

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
            Secret = SecretBuffer.FromText(secret ?? string.Empty),
            RememberPasswordInVault = rememberPasswordInVault
        };

    /// <summary>Takes ownership of the buffer.</summary>
    public static SshInteractionResponse FromSecret(SecretBuffer secret, bool rememberPasswordInVault = false) =>
        new() { Secret = secret, RememberPasswordInVault = rememberPasswordInVault };


    /// <summary>A secret filled from a store rather than typed (<see cref="FilledFromStore"/>).</summary>
    public static SshInteractionResponse FromStoredSecret(string secret) =>
        new()
        {
            Secret = SecretBuffer.FromText(secret ?? string.Empty),
            FilledFromStore = true
        };

    /// <summary>Takes ownership of the buffer returned by a store.</summary>
    public static SshInteractionResponse FromStoredSecret(SecretBuffer secret) =>
        new() { Secret = secret, FilledFromStore = true };


    public static SshInteractionResponse FromKeyboardResponses(params string[] responses) =>
        new() { KeyboardResponses = (responses ?? Array.Empty<string>()).Select(value => SecretBuffer.FromText(value)).ToArray() };
    public void Dispose()
    {
        Secret.Dispose();
        foreach (SecretBuffer answer in KeyboardResponses) answer.Dispose();
    }
}
