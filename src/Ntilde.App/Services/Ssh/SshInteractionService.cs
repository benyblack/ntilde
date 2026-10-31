using Ntilde.Platform.Security;
using Ntilde.Shell;
using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Native;
using Ntilde.ViewModels.Ssh;
using Ntilde.Views.Ssh;

namespace Ntilde.Services.Ssh;

public sealed class SshInteractionService : ISshInteractionService
{
    private readonly Func<Window?> _ownerProvider;
    private readonly Action<Window>? _prepareDialog;
    private readonly Func<Window?, HostKeyPromptViewModel, CancellationToken, Task<SshInteractionResponse>> _hostKeyPresenter;
    private readonly Func<Window?, AuthPromptViewModel, CancellationToken, Task<SshInteractionResponse>> _authPresenter;
    private readonly NativeKnownHostsStore _knownHostsStore;
    private readonly VaultService _vaultService;
    private readonly ActiveSshSessionRegistry _sessionRegistry;

    public SshInteractionService(
        Func<Window?>? ownerProvider = null,
        Action<Window>? prepareDialog = null,
        Func<Window?, HostKeyPromptViewModel, CancellationToken, Task<SshInteractionResponse>>? hostKeyPresenter = null,
        Func<Window?, AuthPromptViewModel, CancellationToken, Task<SshInteractionResponse>>? authPresenter = null,
        NativeKnownHostsStore? knownHostsStore = null,
        VaultService? vaultService = null,
        ActiveSshSessionRegistry? sessionRegistry = null)
    {
        _ownerProvider = ownerProvider ?? (() => null);
        _prepareDialog = prepareDialog;
        _hostKeyPresenter = hostKeyPresenter ?? PresentHostKeyAsync;
        _authPresenter = authPresenter ?? PresentAuthAsync;
        _knownHostsStore = knownHostsStore ?? NativeKnownHostsStore.ForPath(AppPaths.NativeKnownHostsFilePath);
        _vaultService = vaultService ?? new VaultService();
        _sessionRegistry = sessionRegistry ?? ActiveSshSessionRegistry.Instance;
    }

    public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
    {
        return HandleAsync(request, needsUser: null, cancellationToken);
    }

    public ISshInteractionHandler WithoutDialogs(Action<SshInteractionKind> needsUser)
    {
        ArgumentNullException.ThrowIfNull(needsUser);
        return new WithoutDialogsHandler(this, needsUser);
    }

    /// <param name="needsUser">Null: a prompt that needs the user shows its dialog. Otherwise it is told what was asked, and the prompt is refused (#610).</param>
    private async Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, Action<SshInteractionKind>? needsUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<SshInteractionResponse>(cancellationToken);
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(
                () => HandleOnUiThreadAsync(request, needsUser, cancellationToken),
                DispatcherPriority.Send);
        }

        return await HandleOnUiThreadAsync(request, needsUser, cancellationToken);
    }

    private async Task<SshInteractionResponse> HandleOnUiThreadAsync(SshInteractionRequest request, Action<SshInteractionKind>? needsUser, CancellationToken cancellationToken)
    {
        if (TryHandlePasswordFromVault(request, out SshInteractionResponse response))
        {
            return response;
        }

        Window? owner = _ownerProvider();
        return request.Kind switch
        {
            SshInteractionKind.UnknownHostKey or SshInteractionKind.ChangedHostKey => await HandleHostKeyAsync(owner, request, needsUser, cancellationToken),
            SshInteractionKind.Password => await HandlePasswordAsync(owner, request, needsUser, cancellationToken),
            SshInteractionKind.Passphrase => await AskUserAsync(needsUser, request.Kind,
                () => _authPresenter(owner, CreatePassphraseViewModel(request), cancellationToken)),
            // A round with no questions (sshd's PAM sends one after a correct password) has nothing to ask the user.
            SshInteractionKind.KeyboardInteractive when request.KeyboardPrompts.Count == 0 => SshInteractionResponse.FromKeyboardResponses(),
            SshInteractionKind.KeyboardInteractive => await AskUserAsync(needsUser, request.Kind,
                () => _authPresenter(owner, CreateKeyboardViewModel(request), cancellationToken)),
            _ => SshInteractionResponse.Cancel()
        };
    }

    /// <summary>
    /// The one place a prompt reaches the user: <paramref name="present"/> shows its dialog. A connection nobody is waiting
    /// at (<paramref name="needsUser"/> set; #610) must not open a window over the user's shell - its keystrokes would land
    /// in it - so the prompt is refused instead, and the caller told what it was.
    /// </summary>
    private static Task<SshInteractionResponse> AskUserAsync(
        Action<SshInteractionKind>? needsUser,
        SshInteractionKind kind,
        Func<Task<SshInteractionResponse>> present)
    {
        if (needsUser == null)
        {
            return present();
        }

        needsUser(kind);
        return Task.FromResult(SshInteractionResponse.Cancel());
    }

    private async Task<SshInteractionResponse> HandlePasswordAsync(Window? owner, SshInteractionRequest request, Action<SshInteractionKind>? needsUser, CancellationToken cancellationToken)
    {
        SshInteractionResponse response = await AskUserAsync(needsUser, request.Kind,
            () => _authPresenter(owner, CreatePasswordViewModel(request), cancellationToken));
        try
        {
            // Held under the server that asked for it, so the password is only ever replayed to that
            // server — never to the next hop of the chain, and never to a transfer's other hops.
            if (request.SessionId.HasValue && request.HasHostIdentity && !response.IsCanceled && response.Secret.Length > 0)
            {
                _sessionRegistry.SetRuntimePassword(request.SessionId.Value, request.Host, request.Port, request.User, response.Secret);
            }

            if (ShouldStorePasswordInVault(request, response))
            {
                _vaultService.SetSshPasswordBufferForProfile(CreateVaultProfile(request), response.Secret);
            }

            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private bool TryHandlePasswordFromVault(SshInteractionRequest request, out SshInteractionResponse response)
    {
        // A retry prompt follows a refusal of the password this server was just sent: neither store is read,
        // since either would send that password again.
        if (request.Kind != SshInteractionKind.Password ||
            !request.ProfileId.HasValue ||
            request.FollowsRefusal)
        {
            response = SshInteractionResponse.Cancel();
            return false;
        }

        if (request.SessionId.HasValue &&
            request.HasHostIdentity &&
            _sessionRegistry.TryGetRuntimePassword(request.SessionId.Value, request.Host, request.Port, request.User, out SecretBuffer? runtimePassword) &&
            runtimePassword is { Length: > 0 })
        {
            response = SshInteractionResponse.FromStoredSecret(runtimePassword);
            return true;
        }

        if (!request.AllowVaultPasswordReuse || !IsProfileTargetPrompt(request))
        {
            response = SshInteractionResponse.Cancel();
            return false;
        }

        SecretBuffer? password = _vaultService.GetSshPasswordBufferForProfile(CreateVaultProfile(request));
        if (password is null || password.Length == 0)
        {
            response = SshInteractionResponse.Cancel();
            return false;
        }

        response = SshInteractionResponse.FromStoredSecret(password);
        return true;
    }

    private static TerminalProfile CreateVaultProfile(SshInteractionRequest request)
    {
        return new TerminalProfile
        {
            Id = request.ProfileId!.Value,
            Name = request.ProfileName,
            SshUser = request.ProfileUser,
            SshHost = request.ProfileHost
        };
    }

    private async Task<SshInteractionResponse> HandleHostKeyAsync(Window? owner, SshInteractionRequest request, Action<SshInteractionKind>? needsUser, CancellationToken cancellationToken)
    {
        NativeKnownHostMatch match = _knownHostsStore.CheckHost(request.Host, request.Port, request.Algorithm, request.Fingerprint);
        if (match == NativeKnownHostMatch.Trusted)
        {
            return SshInteractionResponse.AcceptHostKey();
        }

        SshInteractionRequest requestToPresent = request;
        if (match == NativeKnownHostMatch.Mismatch)
        {
            requestToPresent = new SshInteractionRequest
            {
                Kind = SshInteractionKind.ChangedHostKey,
                Host = request.Host,
                Port = request.Port,
                Algorithm = request.Algorithm,
                Fingerprint = request.Fingerprint
            };
        }

        SshInteractionResponse response = await AskUserAsync(needsUser, requestToPresent.Kind,
            () => _hostKeyPresenter(owner, CreateHostKeyViewModel(requestToPresent), cancellationToken));
        if (response.IsAccepted && !response.IsCanceled)
        {
            try
            {
                _knownHostsStore.TrustHost(request.Host, request.Port, request.Algorithm, request.Fingerprint);
            }
            catch (IOException ex)
            {
                // The store could not be read, so it was left untouched. The user's decision still
                // applies to this connection; the key is simply not remembered.
                System.Diagnostics.Debug.WriteLine($"[KnownHosts] Host key not remembered: {ex.Message}");
            }
        }

        return response;
    }

    private async Task<SshInteractionResponse> PresentHostKeyAsync(Window? owner, HostKeyPromptViewModel viewModel, CancellationToken cancellationToken)
    {
        if (owner == null || cancellationToken.IsCancellationRequested)
        {
            return SshInteractionResponse.Cancel();
        }

        var dialog = new HostKeyPromptDialog(viewModel);
        _prepareDialog?.Invoke(dialog);
        return await dialog.ShowDialog<SshInteractionResponse?>(owner) ?? SshInteractionResponse.Cancel();
    }

    private async Task<SshInteractionResponse> PresentAuthAsync(Window? owner, AuthPromptViewModel viewModel, CancellationToken cancellationToken)
    {
        if (owner == null || cancellationToken.IsCancellationRequested)
        {
            return SshInteractionResponse.Cancel();
        }

        var dialog = new AuthPromptDialog(viewModel);
        _prepareDialog?.Invoke(dialog);
        return await dialog.ShowDialog<SshInteractionResponse?>(owner) ?? SshInteractionResponse.Cancel();
    }

    private static HostKeyPromptViewModel CreateHostKeyViewModel(SshInteractionRequest request)
    {
        return new HostKeyPromptViewModel
        {
            Host = request.Host,
            Port = request.Port,
            Algorithm = request.Algorithm,
            Fingerprint = request.Fingerprint,
            IsChangedHostKey = request.Kind == SshInteractionKind.ChangedHostKey
        };
    }

    private static AuthPromptViewModel CreatePasswordViewModel(SshInteractionRequest request)
    {
        var viewModel = new AuthPromptViewModel
        {
            Title = "Password",
            Message = CreatePasswordMessage(request),
            CanRememberPassword = IsProfileTargetPrompt(request)
        };
        viewModel.Prompts.Add(new AuthPromptEntryViewModel
        {
            Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? "Password:" : request.Prompt,
            IsSecret = true
        });
        return viewModel;
    }

    /// <summary>
    /// Names the server asking, so the user can tell a bastion's prompt from the target's —
    /// in a jump chain both used to read as the same bare "Password:".
    /// </summary>
    private static string CreatePasswordMessage(SshInteractionRequest request)
    {
        // After a refusal the dialog says so, so the user knows why it is asking again. It names the
        // sign-in, not the password: the retry also follows a wrong code alone, or a password
        // method that can never succeed (a PAM second factor), where "refused that password" is
        // untrue (final review, F2).
        string refused = request.FollowsRefusal ? "The server did not accept the sign-in. " : string.Empty;
        if (!request.HasHostIdentity)
        {
            return $"{refused}Enter the SSH password to continue.";
        }

        string endpoint = request.Port == 22
            ? $"{request.User}@{request.Host}"
            : $"{request.User}@{request.Host}:{request.Port}";
        return request.IsJumpHop
            ? $"{refused}Enter the SSH password for jump host {endpoint} to continue."
            : $"{refused}Enter the SSH password for {endpoint} to continue.";
    }

    /// <summary>
    /// Whether this prompt comes from the profile's final target — the only server the profile's
    /// vault entry belongs to. A jump hop's prompt, or one that does not say which server it is
    /// from, is never filled from the vault and never saved into it: either would put one server's
    /// password on another.
    /// </summary>
    private static bool IsProfileTargetPrompt(SshInteractionRequest request)
    {
        return request.ProfileId.HasValue && request.HasHostIdentity && !request.IsJumpHop;
    }

    private static bool ShouldStorePasswordInVault(SshInteractionRequest request, SshInteractionResponse response)
    {
        return request.Kind == SshInteractionKind.Password &&
            IsProfileTargetPrompt(request) &&
            !response.IsCanceled &&
            response.RememberPasswordInVault &&
            response.Secret.Length > 0;
    }

    private static AuthPromptViewModel CreatePassphraseViewModel(SshInteractionRequest request)
    {
        var viewModel = new AuthPromptViewModel
        {
            Title = "Passphrase",
            Message = "Enter the private key passphrase to continue."
        };
        viewModel.Prompts.Add(new AuthPromptEntryViewModel
        {
            Prompt = string.IsNullOrWhiteSpace(request.Prompt) ? "Passphrase:" : request.Prompt,
            IsSecret = true
        });
        return viewModel;
    }

    private static AuthPromptViewModel CreateKeyboardViewModel(SshInteractionRequest request)
    {
        var viewModel = new AuthPromptViewModel
        {
            Title = string.IsNullOrWhiteSpace(request.Name) ? "Keyboard Interactive" : request.Name,
            Message = request.Instructions,
            IsKeyboardInteractive = true
        };

        foreach (SshKeyboardPrompt prompt in request.KeyboardPrompts)
        {
            viewModel.Prompts.Add(new AuthPromptEntryViewModel
            {
                Prompt = prompt.Prompt,
                IsSecret = !prompt.Echo
            });
        }

        return viewModel;
    }

    /// <summary>
    /// What a prompt asked of the user, as its dialog would have said: a host key other than the one the user trusted is a
    /// changed one, which the native layer does not know.
    /// </summary>
    private SshInteractionKind NeedsUserKindOf(SshInteractionRequest request)
    {
        return request.Kind == SshInteractionKind.UnknownHostKey
            && _knownHostsStore.CheckHost(request.Host, request.Port, request.Algorithm, request.Fingerprint) == NativeKnownHostMatch.Mismatch
                ? SshInteractionKind.ChangedHostKey
                : request.Kind;
    }

    /// <summary><see cref="WithoutDialogs"/>: the window's prompts, minus their dialogs.</summary>
    private sealed class WithoutDialogsHandler(SshInteractionService owner, Action<SshInteractionKind> needsUser) : ISshInteractionHandler
    {
        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            return owner.HandleAsync(request, needsUser, cancellationToken);
        }

        /// <summary>
        /// The shared connection this tab joined was refused a prompt by the tab that opened it, and ends: this tab's shell
        /// stopped for the same reason (Codex review of #629). Reported on the UI thread, as a refusal of its own is, and
        /// ahead of the shell's exit, which is posted later.
        /// </summary>
        public void RefusedOnSharedConnection(SshInteractionRequest request)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                needsUser(owner.NeedsUserKindOf(request));
                return;
            }

            Dispatcher.UIThread.Post(() => needsUser(owner.NeedsUserKindOf(request)), DispatcherPriority.Send);
        }
    }
}

