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

    [Fact]
    public async Task The_response_payload_is_cleared_after_submit()
    {
        var interop = new ScriptedNativeSshInterop();
        var responder = new NativeSshPromptResponder(new SshProfile(), sessionId: null);
        byte[]? observed = null;
        responder.PayloadObserver = payload => observed = payload;

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
