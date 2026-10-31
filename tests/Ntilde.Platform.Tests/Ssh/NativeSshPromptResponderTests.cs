using Ntilde.Platform.Security;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Tests.Ssh.Exec;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class NativeSshPromptResponderTests
{
    private sealed class FixedHandler(SshInteractionResponse response) : ISshInteractionHandler
    {
        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class RecordingHandler : ISshInteractionHandler
    {
        public SshInteractionRequest? Seen { get; private set; }

        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            Seen = request;
            return Task.FromResult(SshInteractionResponse.FromSecret("typed"));
        }
    }

    [Fact]
    public async Task A_retry_prompt_keeps_its_flag_through_the_profile_context()
    {
        var interop = new ScriptedNativeSshInterop();
        var profile = new SshProfile { Id = Guid.Parse("7b1e4c92-5d3a-4f08-a6c1-92e0d8b3f417") };
        var responder = new NativeSshPromptResponder(profile, sessionId: null);
        var handler = new RecordingHandler();
        var retryPrompt = new NativeSshEvent(
            NativeSshEventKind.PasswordPrompt,
            """{"prompt":"Password:","host":"h","port":22,"user":"u","isJumpHop":false,"followsRefusal":true}"""u8.ToArray(),
            flags: NativeSshEventFlags.Json);

        await responder.RespondAsync(retryPrompt, interop, interop.Handle, handler, CancellationToken.None);

        SshInteractionRequest seen = Assert.IsType<SshInteractionRequest>(handler.Seen);
        Assert.True(seen.FollowsRefusal);
        Assert.Equal(profile.Id, seen.ProfileId);
        // Even as the connection's first password prompt: a refusal came before it, so the vault is not offered.
        Assert.False(seen.AllowVaultPasswordReuse);
    }

    [Fact]
    public async Task A_failure_before_submit_wipes_both_response_and_serialized_payload()
    {
        var interop = new ScriptedNativeSshInterop();
        var responder = new NativeSshPromptResponder(new SshProfile(), sessionId: null);
        using var response = SshInteractionResponse.FromSecret("café-🔑");
        byte[] secretBytes = SecretBufferTests.RetainedBytes(response.Secret);
        byte[]? payloadBytes = null;
        responder.PayloadObserver = payload =>
        {
            payloadBytes = SecretBufferTests.RetainedBytes(payload);
            throw new InvalidOperationException("submit interrupted");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => responder.RespondAsync(
            ScriptedNativeSshInterop.PasswordPrompt(), interop, interop.Handle,
            new FixedHandler(response), CancellationToken.None));
        Assert.All(secretBytes, value => Assert.Equal(0, value));
        Assert.NotNull(payloadBytes);
        Assert.All(payloadBytes, value => Assert.Equal(0, value));
        Assert.Empty(interop.Submissions);
    }

    [Fact]
    public async Task The_response_payload_is_cleared_after_submit()
    {
        var interop = new ScriptedNativeSshInterop();
        var responder = new NativeSshPromptResponder(new SshProfile(), sessionId: null);
        byte[]? observed = null;
        responder.PayloadObserver = payload => observed = (byte[])typeof(SecretBuffer).GetField("_bytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(payload)!;

        await responder.RespondAsync(
            ScriptedNativeSshInterop.PasswordPrompt(),
            interop,
            interop.Handle,
            new FixedHandler(SshInteractionResponse.FromSecret("hunter2")),
            CancellationToken.None);

        // The submit saw the real bytes ...
        Assert.Contains("hunter2", Assert.Single(interop.Submissions).PayloadJson, StringComparison.Ordinal);
        // ... and the array the responder built holds none of them afterwards.
        Assert.NotNull(observed);
        Assert.NotEmpty(observed);
        Assert.All(observed, b => Assert.Equal(0, b));
    }
}
