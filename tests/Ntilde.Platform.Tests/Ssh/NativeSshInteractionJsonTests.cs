using System.Text;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class NativeSshInteractionJsonTests
{
    [Fact]
    public void ParseRequest_ParsesHostKeyPromptPayload()
    {
        SshInteractionRequest request = NativeSshInteractionJson.ParseRequest(
            NativeSshEventKind.HostKeyPrompt,
            Encoding.UTF8.GetBytes("""{"host":"srv","port":22,"algorithm":"ssh-ed25519","fingerprint":"SHA256:test"}"""));

        Assert.Equal(SshInteractionKind.UnknownHostKey, request.Kind);
        Assert.Equal("srv", request.Host);
        Assert.Equal(22, request.Port);
        Assert.Equal("ssh-ed25519", request.Algorithm);
        Assert.Equal("SHA256:test", request.Fingerprint);
    }

    [Fact]
    public void ParseRequest_PasswordPrompt_CarriesTheHopThatIsAsking()
    {
        // The payload shape the native layer emits (AuthHop flattened into the prompt payload).
        SshInteractionRequest request = NativeSshInteractionJson.ParseRequest(
            NativeSshEventKind.PasswordPrompt,
            Encoding.UTF8.GetBytes("""{"prompt":"Password:","host":"bastion-one","port":2200,"user":"ops","isJumpHop":true}"""));

        Assert.Equal(SshInteractionKind.Password, request.Kind);
        Assert.Equal("Password:", request.Prompt);
        Assert.Equal("bastion-one", request.Host);
        Assert.Equal(2200, request.Port);
        Assert.Equal("ops", request.User);
        Assert.True(request.IsJumpHop);
        Assert.True(request.HasHostIdentity);
    }

    [Fact]
    public void ParseRequest_PasswordPrompt_FromTheTarget_IsNotAJumpHop()
    {
        SshInteractionRequest request = NativeSshInteractionJson.ParseRequest(
            NativeSshEventKind.PasswordPrompt,
            Encoding.UTF8.GetBytes("""{"prompt":"Password:","host":"target.internal","port":22,"user":"nova","isJumpHop":false}"""));

        Assert.Equal("target.internal", request.Host);
        Assert.Equal("nova", request.User);
        Assert.False(request.IsJumpHop);
    }

    [Fact]
    public void ParseRequest_PasswordPrompt_WithoutAHop_HasNoHostIdentity()
    {
        // What the rest of the app keys credential reuse on: a prompt that does not say which server
        // it is from must not look like it came from any particular one.
        SshInteractionRequest request = NativeSshInteractionJson.ParseRequest(
            NativeSshEventKind.PasswordPrompt,
            Encoding.UTF8.GetBytes("""{"prompt":"Password:"}"""));

        Assert.False(request.HasHostIdentity);
    }

    [Fact]
    public void ParseRequest_KeyboardInteractivePrompt_CarriesTheHopThatIsAsking()
    {
        SshInteractionRequest request = NativeSshInteractionJson.ParseRequest(
            NativeSshEventKind.KeyboardInteractivePrompt,
            Encoding.UTF8.GetBytes("""{"name":"Duo","instructions":"","prompts":[{"prompt":"Passcode:","echo":false}],"host":"bastion-one","port":2200,"user":"ops","isJumpHop":true}"""));

        Assert.Equal(SshInteractionKind.KeyboardInteractive, request.Kind);
        Assert.Equal("Passcode:", Assert.Single(request.KeyboardPrompts).Prompt);
        Assert.Equal("bastion-one", request.Host);
        Assert.Equal(2200, request.Port);
        Assert.Equal("ops", request.User);
        Assert.True(request.IsJumpHop);
    }

    [Fact]
    public void BuildResponsePayload_SerializesKeyboardInteractiveResponsesDeterministically()
    {
        byte[] payload = NativeSshInteractionJson.BuildResponsePayload(
            NativeSshResponseKind.KeyboardInteractive,
            SshInteractionResponse.FromKeyboardResponses("code", "backup"));

        Assert.Equal("""{"responses":["code","backup"]}""", Encoding.UTF8.GetString(payload));
    }
}
