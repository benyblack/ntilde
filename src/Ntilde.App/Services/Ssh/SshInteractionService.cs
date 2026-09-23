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
        _knownHostsStore = knownHostsStore ?? new NativeKnownHostsStore(AppPaths.NativeKnownHostsFilePath);
        _vaultService = vaultService ?? new VaultService();
        _sessionRegistry = sessionRegistry ?? ActiveSshSessionRegistry.Instance;
    }

    public async Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return await Task.FromCanceled<SshInteractionResponse>(cancellationToken);
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(
                () => HandleOnUiThreadAsync(request, cancellationToken),
                DispatcherPriority.Send);
        }

        return await HandleOnUiThreadAsync(request, cancellationToken);
    }

    private async Task<SshInteractionResponse> HandleOnUiThreadAsync(SshInteractionRequest request, CancellationToken cancellationToken)
    {
        if (TryHandlePasswordFromVault(request, out SshInteractionResponse response))
        {
            return response;
        }

        Window? owner = _ownerProvider();
        return request.Kind switch
        {
            SshInteractionKind.UnknownHostKey or SshInteractionKind.ChangedHostKey => await HandleHostKeyAsync(owner, request, cancellationToken),
            SshInteractionKind.Password => await HandlePasswordAsync(owner, request, cancellationToken),
            SshInteractionKind.Passphrase => await _authPresenter(owner, CreatePassphraseViewModel(request), cancellationToken),
            SshInteractionKind.KeyboardInteractive => await _authPresenter(owner, CreateKeyboardViewModel(request), cancellationToken),
            _ => SshInteractionResponse.Cancel()
        };
    }

    private async Task<SshInteractionResponse> HandlePasswordAsync(Window? owner, SshInteractionRequest request, CancellationToken cancellationToken)
    {
        SshInteractionResponse response = await _authPresenter(owner, CreatePasswordViewModel(request), cancellationToken);
        // Held under the server that asked for it, so the password is only ever replayed to that
        // server — never to the next hop of the chain, and never to a transfer's other hops.
        if (request.SessionId.HasValue && request.HasHostIdentity && !response.IsCanceled && !string.IsNullOrEmpty(response.Secret))
        {
            _sessionRegistry.SetRuntimePassword(request.SessionId.Value, request.Host, request.Port, request.User, response.Secret);
        }

        if (ShouldStorePasswordInVault(request, response))
        {
            _vaultService.SetSshPasswordForProfile(CreateVaultProfile(request), response.Secret);
        }

        return response;
    }

    private bool TryHandlePasswordFromVault(SshInteractionRequest request, out SshInteractionResponse response)
    {
        if (request.Kind != SshInteractionKind.Password ||
            !request.ProfileId.HasValue)
        {
            response = SshInteractionResponse.Cancel();
            return false;
        }

        if (request.SessionId.HasValue &&
            request.HasHostIdentity &&
            _sessionRegistry.TryGetRuntimePassword(request.SessionId.Value, request.Host, request.Port, request.User, out string? runtimePassword) &&
            !string.IsNullOrEmpty(runtimePassword))
        {
            response = SshInteractionResponse.FromSecret(runtimePassword);
            return true;
        }

        if (!request.AllowVaultPasswordReuse || !IsProfileTargetPrompt(request))
        {
            response = SshInteractionResponse.Cancel();
            return false;
        }

        string? password = _vaultService.GetSshPasswordForProfile(CreateVaultProfile(request));
        if (string.IsNullOrEmpty(password))
        {
            response = SshInteractionResponse.Cancel();
            return false;
        }

        response = SshInteractionResponse.FromSecret(password);
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

    private async Task<SshInteractionResponse> HandleHostKeyAsync(Window? owner, SshInteractionRequest request, CancellationToken cancellationToken)
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

        SshInteractionResponse response = await _hostKeyPresenter(owner, CreateHostKeyViewModel(requestToPresent), cancellationToken);
        if (response.IsAccepted && !response.IsCanceled)
        {
            _knownHostsStore.TrustHost(request.Host, request.Port, request.Algorithm, request.Fingerprint);
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
        if (!request.HasHostIdentity)
        {
            return "Enter the SSH password to continue.";
        }

        string endpoint = request.Port == 22
            ? $"{request.User}@{request.Host}"
            : $"{request.User}@{request.Host}:{request.Port}";
        return request.IsJumpHop
            ? $"Enter the SSH password for jump host {endpoint} to continue."
            : $"Enter the SSH password for {endpoint} to continue.";
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
            !string.IsNullOrEmpty(response.Secret);
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
            Message = request.Instructions
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
}

