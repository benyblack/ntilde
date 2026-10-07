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
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Algorithm { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public IReadOnlyList<SshKeyboardPrompt> KeyboardPrompts { get; init; } = Array.Empty<SshKeyboardPrompt>();

    /// <summary>
    /// A copy of this request that the window's handler must not answer from the vault
    /// (<see cref="AllowVaultPasswordReuse"/> false): for a user's attempt after the host's saved password was refused,
    /// so the password dialog comes at once instead of the refused password going out again.
    /// </summary>
    public SshInteractionRequest WithoutVaultPasswordReuse() => new()
    {
        Kind = Kind,
        SessionId = SessionId,
        ProfileId = ProfileId,
        ProfileName = ProfileName,
        ProfileUser = ProfileUser,
        ProfileHost = ProfileHost,
        AllowVaultPasswordReuse = false,
        RememberPasswordInVault = RememberPasswordInVault,
        Host = Host,
        Port = Port,
        Algorithm = Algorithm,
        Fingerprint = Fingerprint,
        Prompt = Prompt,
        Name = Name,
        Instructions = Instructions,
        KeyboardPrompts = KeyboardPrompts,
    };
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
