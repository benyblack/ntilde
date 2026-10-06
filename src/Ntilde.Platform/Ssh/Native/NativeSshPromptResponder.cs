using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Platform.Ssh.Native;

/// <summary>
/// Answers one native connection's prompt events (host key, password, passphrase, keyboard-interactive):
/// it parses the event, adds the profile's context, asks the <see cref="ISshInteractionHandler"/>, and
/// submits the answer. A shell session (<see cref="Sessions.NativeSshSession"/>) and an exec channel
/// (<see cref="Exec.NativeSshExecTransport"/>) both use it, so their prompts behave the same.
/// </summary>
/// <remarks>
/// Use one instance per connection. The vault's stored password is offered for the first password
/// prompt only: a second prompt means the server refused it, and offering it again would loop.
/// </remarks>
internal sealed class NativeSshPromptResponder
{
    private readonly Guid _profileId;
    private readonly string _profileName;
    private readonly string _profileUser;
    private readonly string _profileHost;
    private readonly Guid? _sessionId;
    private bool _allowVaultPasswordReuse;

    /// <param name="profile">The profile the prompts are for: the vault and the dialogs key on it.</param>
    /// <param name="sessionId">
    /// The <c>ActiveSshSessionRegistry</c> session, which holds a typed password for reconnects; null for
    /// a connection that is not registered there. Without one, the password would be kept under an id
    /// that is never unregistered.
    /// </param>
    public NativeSshPromptResponder(SshProfile profile, Guid? sessionId)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _profileId = profile.Id;
        _profileName = profile.Name;
        _profileUser = profile.User;
        _profileHost = profile.Host;
        _sessionId = sessionId;
        _allowVaultPasswordReuse = profile.Id != Guid.Empty;
    }

    /// <summary>
    /// Asks <paramref name="handler"/> about <paramref name="promptEvent"/> and submits its answer. With no
    /// handler, the answer is a cancel. The native session waits for the answer, so its caller should
    /// stop polling until this completes.
    /// </summary>
    public async Task RespondAsync(
        NativeSshEvent promptEvent,
        INativeSshInterop interop,
        NovaSshSafeHandle sessionHandle,
        ISshInteractionHandler? handler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promptEvent);
        ArgumentNullException.ThrowIfNull(interop);

        SshInteractionRequest request = WithProfileContext(NativeSshInteractionJson.ParseRequest(promptEvent.Kind, promptEvent.Payload));
        SshInteractionResponse response = handler == null
            ? SshInteractionResponse.Cancel()
            : await handler.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        NativeSshResponseKind responseKind = promptEvent.Kind switch
        {
            NativeSshEventKind.HostKeyPrompt => NativeSshResponseKind.HostKeyDecision,
            NativeSshEventKind.PasswordPrompt => NativeSshResponseKind.Password,
            NativeSshEventKind.PassphrasePrompt => NativeSshResponseKind.Passphrase,
            NativeSshEventKind.KeyboardInteractivePrompt => NativeSshResponseKind.KeyboardInteractive,
            _ => throw new InvalidOperationException($"Unsupported interaction event '{promptEvent.Kind}'.")
        };

        byte[] payload = NativeSshInteractionJson.BuildResponsePayload(responseKind, response);
        interop.SubmitResponse(sessionHandle, responseKind, payload);
    }

    private SshInteractionRequest WithProfileContext(SshInteractionRequest request)
    {
        if (_profileId == Guid.Empty)
        {
            return request;
        }

        SshInteractionRequest requestWithContext = new()
        {
            Kind = request.Kind,
            SessionId = _sessionId,
            ProfileId = _profileId,
            ProfileName = _profileName,
            ProfileUser = _profileUser,
            ProfileHost = _profileHost,
            AllowVaultPasswordReuse = request.Kind == SshInteractionKind.Password && _allowVaultPasswordReuse && _profileId != Guid.Empty,
            RememberPasswordInVault = request.Kind == SshInteractionKind.Password && _profileId != Guid.Empty,
            Host = request.Host,
            Port = request.Port,
            Algorithm = request.Algorithm,
            Fingerprint = request.Fingerprint,
            Prompt = request.Prompt,
            Name = request.Name,
            Instructions = request.Instructions,
            KeyboardPrompts = request.KeyboardPrompts
        };

        if (request.Kind == SshInteractionKind.Password)
        {
            _allowVaultPasswordReuse = false;
        }

        return requestWithContext;
    }
}
