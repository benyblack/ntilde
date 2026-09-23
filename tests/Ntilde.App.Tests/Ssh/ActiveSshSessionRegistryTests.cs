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
