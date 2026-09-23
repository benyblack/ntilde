namespace Ntilde.Platform.Ssh.Interactions;

public sealed class SshInteractionRequest
{
    public SshInteractionKind Kind { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? ProfileId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public string ProfileUser { get; init; } = string.Empty;
    public string ProfileHost { get; init; } = string.Empty;
    public bool AllowVaultPasswordReuse { get; init; }
    public bool RememberPasswordInVault { get; init; }

    /// <summary>
    /// The server this request is about. For a host-key prompt, the server presenting the key; for
    /// an authentication prompt, the server asking for the credential — which in a jump chain may
    /// be a bastion rather than the profile's target (see <see cref="IsJumpHop"/>).
    /// </summary>
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }

    /// <summary>The user being authenticated on <see cref="Host"/>. Set on authentication prompts.</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>
    /// True when an authentication prompt comes from a jump hop rather than the profile's final
    /// target. A profile's saved password belongs to its target alone, so a jump hop's prompt is
    /// never answered from, or saved into, the profile's vault entry.
    /// </summary>
    public bool IsJumpHop { get; init; }
    public string Algorithm { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public IReadOnlyList<SshKeyboardPrompt> KeyboardPrompts { get; init; } = Array.Empty<SshKeyboardPrompt>();

    /// <summary>
    /// Whether this request names the server it is for. Credentials are only ever matched to a
    /// named server; a request without one is answered by the user, never from a cache or vault.
    /// </summary>
    public bool HasHostIdentity =>
        !string.IsNullOrWhiteSpace(Host) && Port > 0 && !string.IsNullOrWhiteSpace(User);
}

public sealed class SshKeyboardPrompt
{
    public SshKeyboardPrompt(string prompt, bool echo)
    {
        Prompt = prompt ?? string.Empty;
        Echo = echo;
    }

    public string Prompt { get; }
    public bool Echo { get; }
}
