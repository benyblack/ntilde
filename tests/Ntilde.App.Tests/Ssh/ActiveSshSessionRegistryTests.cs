using Ntilde.Platform.Ssh.Models;
using Ntilde.Services.Ssh;

namespace Ntilde.Tests.Ssh;

public sealed class ActiveSshSessionRegistryTests
{
    [Fact]
    public void TryGet_WhenRegisteredActiveNativeSessionExists_ReturnsDescriptor()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();

        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            profileId,
            SshBackendKind.Native));

        Assert.True(registry.TryGet(sessionId, out ActiveSshSessionDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal(profileId, descriptor!.ProfileId);
        Assert.Equal(SshBackendKind.Native, descriptor.BackendKind);
    }

    [Fact]
    public void Unregister_RemovesRegisteredSession()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();

        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            Guid.NewGuid(),
            SshBackendKind.Native));
        registry.Unregister(sessionId);

        Assert.False(registry.TryGet(sessionId, out _));
    }

    [Fact]
    public void SetRuntimePassword_StoresAndReturnsPassword_ForSession()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();

        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            Guid.NewGuid(),
            SshBackendKind.Native));
        registry.SetRuntimePassword(sessionId, "prod.internal", 22, "ops", "session-secret");

        Assert.True(registry.TryGetRuntimePassword(sessionId, "prod.internal", 22, "ops", out string? password));
        Assert.Equal("session-secret", password);
    }

    [Fact]
    public void Unregister_RemovesRuntimePassword_ForSession()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();

        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            Guid.NewGuid(),
            SshBackendKind.Native));
        registry.SetRuntimePassword(sessionId, "prod.internal", 22, "ops", "session-secret");
        registry.SetRuntimePassword(sessionId, "bastion.internal", 2200, "jump", "bastion-secret");
        registry.Unregister(sessionId);

        Assert.False(registry.TryGetRuntimePassword(sessionId, "prod.internal", 22, "ops", out _));
        Assert.False(registry.TryGetRuntimePassword(sessionId, "bastion.internal", 2200, "jump", out _));
    }

    [Theory]
    // Every coordinate of the server is part of the key. A password entered for a bastion must not
    // come back for the target behind it — that replay is exactly how a jump chain leaked credentials.
    [InlineData("bastion.internal", 22, "ops")]
    [InlineData("prod.internal", 2200, "ops")]
    [InlineData("prod.internal", 22, "root")]
    public void TryGetRuntimePassword_DoesNotReturnAPasswordEnteredForAnotherServer(string host, int port, string user)
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        registry.SetRuntimePassword(sessionId, "prod.internal", 22, "ops", "target-secret");

        Assert.False(registry.TryGetRuntimePassword(sessionId, host, port, user, out string? password));
        Assert.Null(password);
    }

    [Fact]
    public void RuntimePasswords_ForDifferentServersOfOneSession_AreHeldSeparately()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();

        registry.SetRuntimePassword(sessionId, "bastion.internal", 2200, "jump", "bastion-secret");
        registry.SetRuntimePassword(sessionId, "prod.internal", 22, "ops", "target-secret");

        Assert.True(registry.TryGetRuntimePassword(sessionId, "bastion.internal", 2200, "jump", out string? bastion));
        Assert.True(registry.TryGetRuntimePassword(sessionId, "prod.internal", 22, "ops", out string? target));
        Assert.Equal("bastion-secret", bastion);
        Assert.Equal("target-secret", target);
    }

    [Fact]
    public void RuntimePasswordKey_MatchesHostCaseInsensitively_AndTreatsPortZeroAs22()
    {
        // The prompt stores under whatever spelling the native layer reported; a transfer looks up
        // with the profile's spelling. DNS names are case-insensitive, and port 0 is "default".
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        registry.SetRuntimePassword(sessionId, "Prod.Internal", 0, "ops", "target-secret");

        Assert.True(registry.TryGetRuntimePassword(sessionId, "prod.internal", 22, "ops", out string? password));
        Assert.Equal("target-secret", password);
    }

    [Fact]
    public void RuntimePasswords_AreNotSharedAcrossSessions()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        registry.SetRuntimePassword(sessionId, "prod.internal", 22, "ops", "target-secret");

        Assert.False(registry.TryGetRuntimePassword(Guid.NewGuid(), "prod.internal", 22, "ops", out _));
    }

    [Fact]
    public void TryGetActiveNativeSession_WhenProfileAndBackendMatch_ReturnsDescriptor()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.Native));

        Assert.True(registry.TryGetActiveNativeSession(profileId, sessionId, out ActiveSshSessionDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal(sessionId, descriptor!.SessionId);
    }

    [Fact]
    public void TryGetActiveNativeSession_WhenProfileDiffers_ReturnsFalse()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, Guid.NewGuid(), SshBackendKind.Native));

        Assert.False(registry.TryGetActiveNativeSession(Guid.NewGuid(), sessionId, out _));
    }

    /// <summary>
    /// Phase 5 Task 28 (spec R8): a persisted remote tab registers under its daemon session id with its host's password
    /// scope, and a lookup through its descriptor finds the passwords that scope holds - none under the session id itself.
    /// </summary>
    [Fact]
    public void A_lookup_through_a_scoped_descriptor_reads_its_hosts_scope()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        Guid scope = Guid.NewGuid();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, Guid.NewGuid(), SshBackendKind.Native, scope));
        registry.SetRuntimePassword(scope, "prod.internal", 22, "ops", "typed-secret");

        Assert.Equal(scope, registry.PasswordScopeOf(sessionId));
        Assert.True(registry.TryGetRuntimePassword(registry.PasswordScopeOf(sessionId), "prod.internal", 22, "ops", out string? password));
        Assert.Equal("typed-secret", password);
        Assert.False(registry.TryGetRuntimePassword(sessionId, "prod.internal", 22, "ops", out _));
    }

    [Fact]
    public void UnregisterScope_clears_that_scopes_passwords_and_nothing_else()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid gone = Guid.NewGuid();
        Guid kept = Guid.NewGuid();
        Guid session = Guid.NewGuid();
        registry.SetRuntimePassword(gone, "prod.internal", 22, "ops", "gone-secret");
        registry.SetRuntimePassword(gone, "other.internal", 22, "ops", "gone-too");
        registry.SetRuntimePassword(kept, "prod.internal", 22, "ops", "kept-secret");
        registry.SetRuntimePassword(session, "prod.internal", 22, "ops", "session-secret");

        registry.UnregisterScope(gone);

        Assert.False(registry.TryGetRuntimePassword(gone, "prod.internal", 22, "ops", out _));
        Assert.False(registry.TryGetRuntimePassword(gone, "other.internal", 22, "ops", out _));
        Assert.True(registry.TryGetRuntimePassword(kept, "prod.internal", 22, "ops", out string? keptPassword));
        Assert.Equal("kept-secret", keptPassword);
        Assert.True(registry.TryGetRuntimePassword(session, "prod.internal", 22, "ops", out string? sessionPassword));
        Assert.Equal("session-secret", sessionPassword);
    }

    /// <summary>A plain session, registered or not, keeps its passwords under its own id, as before Task 28.</summary>
    [Fact]
    public void A_session_without_a_scope_is_its_own_scope()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid registered = Guid.NewGuid();
        Guid unregistered = Guid.NewGuid();
        registry.Register(new ActiveSshSessionDescriptor(registered, Guid.NewGuid(), SshBackendKind.Native));
        registry.SetRuntimePassword(registered, "prod.internal", 22, "ops", "session-secret");

        Assert.Equal(registered, registry.PasswordScopeOf(registered));
        Assert.Equal(unregistered, registry.PasswordScopeOf(unregistered));
        Assert.True(registry.TryGetRuntimePassword(registry.PasswordScopeOf(registered), "prod.internal", 22, "ops", out string? password));
        Assert.Equal("session-secret", password);

        registry.Unregister(registered);

        Assert.False(registry.TryGetRuntimePassword(registered, "prod.internal", 22, "ops", out _));
    }

    /// <summary>
    /// Two panes can show one remote session (a share opened in another window): a pane lets go of its own registration
    /// only, never one registered after it. A scoped registration's passwords are its host's, so they stay.
    /// </summary>
    [Fact]
    public void Unregistering_a_descriptor_leaves_a_later_registration_of_the_same_session_and_the_hosts_scope()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        Guid scope = Guid.NewGuid();
        var first = new ActiveSshSessionDescriptor(sessionId, Guid.NewGuid(), SshBackendKind.Native, scope);
        var later = new ActiveSshSessionDescriptor(sessionId, first.ProfileId, SshBackendKind.Native, scope);
        registry.Register(first);
        registry.Register(later);
        registry.SetRuntimePassword(scope, "prod.internal", 22, "ops", "typed-secret");

        Assert.False(registry.Unregister(first));
        Assert.True(registry.TryGet(sessionId, out ActiveSshSessionDescriptor? still));
        Assert.Same(later, still);

        Assert.True(registry.Unregister(later));
        Assert.False(registry.TryGet(sessionId, out _));
        Assert.True(registry.TryGetRuntimePassword(scope, "prod.internal", 22, "ops", out _));
    }

    [Fact]
    public void TryGetActiveNativeSession_WhenBackendIsNotNative_ReturnsFalse()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        Guid profileId = Guid.NewGuid();
        registry.Register(new ActiveSshSessionDescriptor(sessionId, profileId, SshBackendKind.OpenSsh));

        Assert.False(registry.TryGetActiveNativeSession(profileId, sessionId, out _));
    }
}
