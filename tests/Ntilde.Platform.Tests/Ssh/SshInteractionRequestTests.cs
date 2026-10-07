using System.Reflection;
using Ntilde.Platform.Ssh.Interactions;

namespace Ntilde.Platform.Tests.Ssh;

/// <summary>
/// <see cref="SshInteractionRequest.WithoutVaultPasswordReuse"/>: the same request, but one the window's handler must not
/// answer from the vault - for a user's attempt after the host's saved password was refused, so the dialog comes at once.
/// </summary>
public sealed class SshInteractionRequestTests
{
    private static SshInteractionRequest Full() => new()
    {
        Kind = SshInteractionKind.Password,
        SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        ProfileId = Guid.Parse("e15099d2-ac29-40cb-bf1f-f466eb2622b7"),
        ProfileName = "Prod box",
        ProfileUser = "ops",
        ProfileHost = "prod.internal",
        AllowVaultPasswordReuse = true,
        RememberPasswordInVault = true,
        Host = "prod.internal",
        Port = 2222,
        Algorithm = "ssh-ed25519",
        Fingerprint = "SHA256:abc",
        Prompt = "Password:",
        Name = "name",
        Instructions = "instructions",
        KeyboardPrompts = [new SshKeyboardPrompt("Code:", echo: false)],
    };

    [Fact]
    public void WithoutVaultPasswordReuse_turns_vault_reuse_off_and_keeps_everything_else()
    {
        SshInteractionRequest original = Full();

        SshInteractionRequest copy = original.WithoutVaultPasswordReuse();

        Assert.False(copy.AllowVaultPasswordReuse);
        Assert.True(original.AllowVaultPasswordReuse);   // a copy: the original is not changed
        foreach (PropertyInfo property in typeof(SshInteractionRequest).GetProperties())
        {
            if (property.Name == nameof(SshInteractionRequest.AllowVaultPasswordReuse)) continue;
            Assert.Equal(property.GetValue(original), property.GetValue(copy));
        }
    }

    /// <summary>
    /// What makes the copy test above hold for a property added later: <see cref="Full"/> must give every property a value
    /// other than a new request's, or the copy could drop it unnoticed. A new property fails here until it is set there.
    /// </summary>
    [Fact]
    public void The_full_request_sets_every_property()
    {
        SshInteractionRequest full = Full();
        var blank = new SshInteractionRequest();

        foreach (PropertyInfo property in typeof(SshInteractionRequest).GetProperties())
        {
            object? value = property.GetValue(full);
            Assert.False(Equals(value, property.GetValue(blank)), $"Full() leaves {property.Name} at its default");
            if (value is System.Collections.ICollection collection) Assert.NotEqual(0, collection.Count);
        }
    }
}
