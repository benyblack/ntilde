using Ntilde.Platform.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ntilde.Platform.Ssh.Interactions;

namespace Ntilde.Platform.Ssh.Native;

public static class NativeSshInteractionJson
{
    public static SshInteractionRequest ParseRequest(NativeSshEventKind eventKind, ReadOnlySpan<byte> payload)
    {
        return eventKind switch
        {
            NativeSshEventKind.HostKeyPrompt => CreateHostKeyRequest(payload),
            NativeSshEventKind.PasswordPrompt => CreateTextPromptRequest(SshInteractionKind.Password, payload),
            NativeSshEventKind.PassphrasePrompt => CreateTextPromptRequest(SshInteractionKind.Passphrase, payload),
            NativeSshEventKind.KeyboardInteractivePrompt => CreateKeyboardRequest(payload),
            _ => throw new InvalidOperationException($"Unsupported interaction event '{eventKind}'.")
        };
    }

    public static SecretBuffer BuildResponsePayload(NativeSshResponseKind responseKind, SshInteractionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var empty = SecretBuffer.FromText("");
        return responseKind switch
        {
            NativeSshResponseKind.HostKeyDecision => SecretJson.Serialize(
                new NativeHostKeyDecisionPayload { Accept = response.IsAccepted && !response.IsCanceled },
                NativeSshInteractionJsonContext.Default.NativeHostKeyDecisionPayload),
            NativeSshResponseKind.Password or NativeSshResponseKind.Passphrase => SecretJson.Serialize(
                new NativeTextResponsePayload
                {
                    Text = response.IsCanceled ? empty : response.Secret,
                    Canceled = response.IsCanceled
                },
                NativeSshInteractionJsonContext.Default.NativeTextResponsePayload),
            NativeSshResponseKind.KeyboardInteractive => SecretJson.Serialize(
                new NativeKeyboardInteractiveResponsePayload
                {
                    Responses = response.IsCanceled ? Array.Empty<SecretBuffer>() : response.KeyboardResponses.ToArray(),
                    Canceled = response.IsCanceled
                },
                NativeSshInteractionJsonContext.Default.NativeKeyboardInteractiveResponsePayload),
            _ => throw new InvalidOperationException($"Unsupported native SSH response kind '{responseKind}'.")
        };
    }

    private static SshInteractionRequest CreateHostKeyRequest(ReadOnlySpan<byte> payload)
    {
        NativeHostKeyPromptPayload? model = JsonSerializer.Deserialize(
            payload,
            NativeSshInteractionJsonContext.Default.NativeHostKeyPromptPayload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse host key prompt payload.");
        }

        return new SshInteractionRequest
        {
            Kind = SshInteractionKind.UnknownHostKey,
            Host = model.Host,
            Port = model.Port,
            Algorithm = model.Algorithm,
            Fingerprint = model.Fingerprint
        };
    }

    private static SshInteractionRequest CreateTextPromptRequest(SshInteractionKind kind, ReadOnlySpan<byte> payload)
    {
        NativeTextPromptPayload? model = JsonSerializer.Deserialize(
            payload,
            NativeSshInteractionJsonContext.Default.NativeTextPromptPayload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse authentication prompt payload.");
        }

        return new SshInteractionRequest
        {
            Kind = kind,
            Prompt = model.Prompt,
            Host = model.Host,
            Port = model.Port,
            User = model.User,
            IsJumpHop = model.IsJumpHop,
            FollowsRefusal = model.FollowsRefusal
        };
    }

    private static SshInteractionRequest CreateKeyboardRequest(ReadOnlySpan<byte> payload)
    {
        NativeKeyboardInteractivePromptPayload? model = JsonSerializer.Deserialize(
            payload,
            NativeSshInteractionJsonContext.Default.NativeKeyboardInteractivePromptPayload);
        if (model == null)
        {
            throw new InvalidOperationException("Failed to parse keyboard-interactive payload.");
        }

        return new SshInteractionRequest
        {
            Kind = SshInteractionKind.KeyboardInteractive,
            Name = model.Name,
            Instructions = model.Instructions,
            KeyboardPrompts = model.Prompts.Select(prompt => new SshKeyboardPrompt(prompt.Prompt, prompt.Echo)).ToArray(),
            Host = model.Host,
            Port = model.Port,
            User = model.User,
            IsJumpHop = model.IsJumpHop
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NativeHostKeyPromptPayload))]
[JsonSerializable(typeof(NativeTextPromptPayload))]
[JsonSerializable(typeof(NativeKeyboardInteractivePromptPayload))]
[JsonSerializable(typeof(NativeHostKeyDecisionPayload))]
[JsonSerializable(typeof(NativeTextResponsePayload))]
[JsonSerializable(typeof(NativeKeyboardInteractiveResponsePayload))]
internal partial class NativeSshInteractionJsonContext : JsonSerializerContext
{
}

internal sealed class NativeHostKeyPromptPayload
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Algorithm { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
}

/// <summary>
/// Which server of the chain an authentication prompt comes from — the native layer's
/// <c>AuthHop</c>, flattened into every auth prompt payload. Mirrors it field for field.
/// </summary>
internal abstract class NativeAuthHopPayload
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string User { get; set; } = string.Empty;
    public bool IsJumpHop { get; set; }
}

internal sealed class NativeTextPromptPayload : NativeAuthHopPayload
{
    public string Prompt { get; set; } = string.Empty;

    /// <summary>The server just refused the password it was sent; absent from older payloads, so false.</summary>
    public bool FollowsRefusal { get; set; }
}

internal sealed class NativeKeyboardInteractivePromptPayload : NativeAuthHopPayload
{
    public string Name { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public List<NativeKeyboardInteractivePromptItem> Prompts { get; set; } = new();
}

internal sealed class NativeKeyboardInteractivePromptItem
{
    public string Prompt { get; set; } = string.Empty;
    public bool Echo { get; set; }
}

internal sealed class NativeHostKeyDecisionPayload
{
    public bool Accept { get; set; }
}

internal sealed class NativeTextResponsePayload
{
    public SecretBuffer? Text { get; set; }

    // Omitted when false so an ordinary answer stays byte-identical. rusty_ssh stops sign-in on true
    // instead of treating the empty text as a password.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Canceled { get; set; }
}

internal sealed class NativeKeyboardInteractiveResponsePayload
{
    public SecretBuffer[] Responses { get; set; } = Array.Empty<SecretBuffer>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Canceled { get; set; }
}
