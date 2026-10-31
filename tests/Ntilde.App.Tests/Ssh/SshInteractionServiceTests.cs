using Ntilde.Platform.Security;
using Ntilde.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Services.Ssh;
using Ntilde.ViewModels.Ssh;

namespace Ntilde.Tests.Ssh;

/// <summary>
/// Every test is an <c>[AvaloniaFact]</c>, so it runs on the session's UI thread with a dispatcher of its own, and
/// <see cref="SshInteractionService.HandleAsync"/> runs the request inline. A plain <c>[Fact]</c> depends on whichever
/// thread last created <c>Dispatcher.UIThread</c>: if that is not its own, the request is posted there, nothing ever
/// runs it, and the test (and with it the whole App.Tests lane) waits forever - a CI hang on PR #573.
/// </summary>
public sealed class SshInteractionServiceTests
{
    [AvaloniaFact]
    public async Task BackgroundThreadRequests_AreMarshalledToUiThread()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var owner = new Window();
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            int presenterThreadId = -1;
            int ownerThreadId = -1;

            var service = new SshInteractionService(
                ownerProvider: () =>
                {
                    Assert.True(Dispatcher.UIThread.CheckAccess());
                    ownerThreadId = Environment.CurrentManagedThreadId;
                    return owner;
                },
                hostKeyPresenter: (_, _, _) =>
                {
                    Assert.True(Dispatcher.UIThread.CheckAccess());
                    presenterThreadId = Environment.CurrentManagedThreadId;
                    return Task.FromResult(SshInteractionResponse.AcceptHostKey());
                },
                knownHostsStore: knownHosts);

            int workerThreadId = -1;
            var response = await Task.Run(async () =>
            {
                workerThreadId = Environment.CurrentManagedThreadId;
                return await service.HandleAsync(new SshInteractionRequest
                {
                    Kind = SshInteractionKind.UnknownHostKey,
                    Host = "example.internal",
                    Port = 22,
                    Algorithm = "ssh-ed25519",
                    Fingerprint = "SHA256:test"
                }, CancellationToken.None);
            });

            Assert.True(response.IsAccepted);
            Assert.NotEqual(-1, ownerThreadId);
            Assert.NotEqual(-1, presenterThreadId);
            Assert.NotEqual(workerThreadId, ownerThreadId);
            Assert.NotEqual(workerThreadId, presenterThreadId);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task HostKeyRequestsMapToHostKeyPromptViewModel()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            HostKeyPromptViewModel? capturedVm = null;
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            var service = new SshInteractionService(
                hostKeyPresenter: (_, vm, _) =>
                {
                    capturedVm = vm;
                    return Task.FromResult(SshInteractionResponse.AcceptHostKey());
                },
                knownHostsStore: knownHosts);

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.UnknownHostKey,
                Host = "example.internal",
                Port = 2222,
                Algorithm = "ssh-ed25519",
                Fingerprint = "SHA256:test"
            }, CancellationToken.None);

            Assert.NotNull(capturedVm);
            Assert.Equal("example.internal", capturedVm!.Host);
            Assert.Equal(2222, capturedVm.Port);
            Assert.Equal("ssh-ed25519", capturedVm.Algorithm);
            Assert.Equal("SHA256:test", capturedVm.Fingerprint);
            Assert.False(capturedVm.IsChangedHostKey);
            Assert.True(response.IsAccepted);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task TrustedHostKey_SkipsDialogAndAcceptsImmediately()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            knownHosts.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:test");
            int promptCount = 0;
            var service = new SshInteractionService(
                hostKeyPresenter: (_, _, _) =>
                {
                    promptCount++;
                    return Task.FromResult(SshInteractionResponse.Cancel());
                },
                knownHostsStore: knownHosts);

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.UnknownHostKey,
                Host = "example.internal",
                Port = 22,
                Algorithm = "ssh-ed25519",
                Fingerprint = "SHA256:test"
            }, CancellationToken.None);

            Assert.True(response.IsAccepted);
            Assert.Equal(0, promptCount);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ChangedHostKey_MapsToChangedPromptViewModel()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            knownHosts.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:old");
            HostKeyPromptViewModel? capturedVm = null;
            var service = new SshInteractionService(
                hostKeyPresenter: (_, vm, _) =>
                {
                    capturedVm = vm;
                    return Task.FromResult(SshInteractionResponse.AcceptHostKey());
                },
                knownHostsStore: knownHosts);

            var response = await service.HandleAsync(new SshInteractionRequest
            {
                Kind = SshInteractionKind.UnknownHostKey,
                Host = "example.internal",
                Port = 22,
                Algorithm = "ssh-ed25519",
                Fingerprint = "SHA256:new"
            }, CancellationToken.None);

            Assert.NotNull(capturedVm);
            Assert.True(capturedVm!.IsChangedHostKey);
            Assert.True(response.IsAccepted);
            Assert.Equal(NativeKnownHostMatch.Trusted, knownHosts.CheckHost("example.internal", 22, "ssh-ed25519", "SHA256:new"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task PasswordRequestsMapToSingleSecretPrompt()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.FromSecret("password"));
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:"
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.Equal("Password", capturedVm!.Title);
        Assert.Single(capturedVm.Prompts);
        Assert.True(capturedVm.Prompts[0].IsSecret);
        Assert.Equal("Password:", capturedVm.Prompts[0].Prompt);
        Assert.Equal("password", response.Secret.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task NativePasswordRequests_WithProfileIdentity_ExposeRememberPasswordOption()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:",
            ProfileId = Guid.Parse("531365f9-b488-4475-ab7d-7221e5056cae"),
            ProfileName = "Native Prod",
            ProfileUser = "alice",
            ProfileHost = "example.internal",
            Host = "example.internal",
            Port = 22,
            User = "alice"
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.True(capturedVm!.CanRememberPassword);
    }

    [AvaloniaFact]
    public async Task PasswordRequests_WithoutProfileIdentity_DoNotExposeRememberPasswordOption()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:"
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.False(capturedVm!.CanRememberPassword);
    }

    [AvaloniaFact]
    public async Task NativePasswordRequests_StoreSubmittedPassword_WhenRememberPasswordIsChecked()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("0c25f4e2-2c2d-4f16-9d70-4d01e7c8fdb1"),
            Type = ConnectionType.SSH,
            Name = "Native Prod",
            SshHost = "example.internal",
            SshUser = "alice"
        };

        var service = new SshInteractionService(
            vaultService: vault,
            authPresenter: (_, _, _) =>
            {
                return Task.FromResult(SshInteractionResponse.FromSecret("manual-secret", rememberPasswordInVault: true));
            });

        var response = await service.HandleAsync(TargetPasswordRequest(profile), CancellationToken.None);

        Assert.Equal("manual-secret", response.Secret.DangerousGetString());
        Assert.Equal("manual-secret", vault.GetSshPasswordForProfile(profile));
    }

    [AvaloniaFact]
    public async Task NativePasswordRequests_DoNotStoreSubmittedPassword_WhenRememberPasswordIsUnchecked()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("f0dbb9df-8e7e-4e11-9e0a-91de4a2f84be"),
            Type = ConnectionType.SSH,
            Name = "Native Prod",
            SshHost = "example.internal",
            SshUser = "alice"
        };

        var service = new SshInteractionService(
            vaultService: vault,
            authPresenter: (_, _, _) =>
                Task.FromResult(SshInteractionResponse.FromSecret("manual-secret")));

        var response = await service.HandleAsync(TargetPasswordRequest(profile), CancellationToken.None);

        Assert.Equal("manual-secret", response.Secret.DangerousGetString());
        Assert.Null(vault.GetSshPasswordForProfile(profile));
    }

    [AvaloniaFact]
    public async Task NativePasswordRequests_StoreSubmittedPassword_InRuntimeSessionCache()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.Parse("6e392be4-b615-496f-b1f6-c559d7f4c4f3");
        TerminalProfile profile = CreateProfile("b15431d2-30e6-46de-99cd-c984f4995aaf");
        registry.Register(new ActiveSshSessionDescriptor(
            sessionId,
            profile.Id,
            Ntilde.Platform.Ssh.Models.SshBackendKind.Native));

        var service = new SshInteractionService(
            vaultService: vault,
            sessionRegistry: registry,
            authPresenter: (_, _, _) =>
                Task.FromResult(SshInteractionResponse.FromSecret("manual-secret")));

        SshInteractionResponse response = await service.HandleAsync(
            TargetPasswordRequest(profile, sessionId),
            CancellationToken.None);

        Assert.Equal("manual-secret", response.Secret.DangerousGetString());
        Assert.True(registry.TryGetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, out SecretBuffer? password));
        Assert.Equal("manual-secret", password?.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task NativePasswordRequests_LeavingRememberUnchecked_PreservesExistingVaultSecret()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("6752a7e9-6fcb-4108-813a-45411cba6a6c"),
            Type = ConnectionType.SSH,
            Name = "Native Prod",
            SshHost = "example.internal",
            SshUser = "alice"
        };
        vault.SetSshPasswordForProfile(profile, "vault-secret");

        var service = new SshInteractionService(
            vaultService: vault,
            authPresenter: (_, _, _) =>
                Task.FromResult(SshInteractionResponse.FromSecret("manual-secret")));

        var response = await service.HandleAsync(TargetPasswordRequest(profile), CancellationToken.None);

        Assert.Equal("manual-secret", response.Secret.DangerousGetString());
        Assert.Equal("vault-secret", vault.GetSshPasswordForProfile(profile));
    }

    [AvaloniaFact]
    public async Task PasswordRequests_AreAnsweredFromVaultWithoutShowingDialog_WhenProfileIdentityIsProvided()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("3b0a5c25-9b4d-4d6b-80b4-5cf8c2ef8e6a"),
            Type = ConnectionType.SSH,
            Name = "Native Prod",
            SshHost = "example.internal",
            SshUser = "alice"
        };
        vault.SetSshPasswordForProfile(profile, "vault-secret");

        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: vault,
            authPresenter: (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        var response = await service.HandleAsync(
            TargetPasswordRequest(profile, allowVaultPasswordReuse: true),
            CancellationToken.None);

        Assert.Equal(0, promptCount);
        Assert.Equal("vault-secret", response.Secret.DangerousGetString());
        Assert.False(response.IsCanceled);
    }

    /// <summary>
    /// Phase 5 Task 28: a persisted remote tab hands only what the user typed to its host's SFTP connections, so a
    /// password filled from the vault, or from a session's remembered one, says so; a typed one does not.
    /// </summary>
    [AvaloniaFact]
    public async Task PasswordAnswers_SayWhetherTheyWereFilledFromAStoreOrTyped()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var registry = new ActiveSshSessionRegistry();
        TerminalProfile profile = CreateProfile("0a6f3e1d-2b4c-4d5e-8f60-718293a4b5c6");
        vault.SetSshPasswordForProfile(profile, "vault-secret");
        Guid sessionId = Guid.NewGuid();
        registry.SetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, "remembered-secret");
        var service = new SshInteractionService(
            vaultService: vault,
            sessionRegistry: registry,
            authPresenter: (_, _, _) => Task.FromResult(SshInteractionResponse.FromSecret("typed-secret")));

        SshInteractionResponse fromVault = await service.HandleAsync(TargetPasswordRequest(profile, allowVaultPasswordReuse: true), CancellationToken.None);
        SshInteractionResponse fromSession = await service.HandleAsync(TargetPasswordRequest(profile, sessionId), CancellationToken.None);
        SshInteractionResponse typed = await service.HandleAsync(TargetPasswordRequest(profile), CancellationToken.None);

        Assert.Equal(("vault-secret", true), (fromVault.Secret.DangerousGetString(), fromVault.FilledFromStore));
        Assert.Equal(("remembered-secret", true), (fromSession.Secret.DangerousGetString(), fromSession.FilledFromStore));
        Assert.Equal(("typed-secret", false), (typed.Secret.DangerousGetString(), typed.FilledFromStore));
    }

    /// <summary>The server just refused the password it was sent: asking the stores again would send it a second time.</summary>
    [AvaloniaFact]
    public async Task RetryPrompt_IsNeverFilledFromTheRuntimeStoreOrTheVault()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var registry = new ActiveSshSessionRegistry();
        TerminalProfile profile = CreateProfile("5d2c7a90-3b1e-4f68-9a07-c4e8d1b2f3a4");
        vault.SetSshPasswordForProfile(profile, "vault-secret");
        Guid sessionId = Guid.NewGuid();
        registry.SetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, "remembered-secret");
        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: vault,
            sessionRegistry: registry,
            authPresenter: (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(SshInteractionResponse.FromSecret("typed-secret"));
            });

        SshInteractionResponse response = await service.HandleAsync(
            TargetPasswordRequest(profile, sessionId, allowVaultPasswordReuse: true, followsRefusal: true),
            CancellationToken.None);

        Assert.Equal(1, promptCount);
        Assert.Equal(("typed-secret", false), (response.Secret.DangerousGetString(), response.FilledFromStore));
    }

    [AvaloniaFact]
    public async Task RetryPrompt_OffersRemember_AndReplacesTheStoredPasswords()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var registry = new ActiveSshSessionRegistry();
        TerminalProfile profile = CreateProfile("8e4b1d63-7c2a-4a95-b0f1-26d3c9a7e8b5");
        vault.SetSshPasswordForProfile(profile, "old-secret");
        Guid sessionId = Guid.NewGuid();
        registry.SetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, "old-secret");
        AuthPromptViewModel? presented = null;
        var service = new SshInteractionService(
            vaultService: vault,
            sessionRegistry: registry,
            authPresenter: (_, viewModel, _) =>
            {
                presented = viewModel;
                return Task.FromResult(SshInteractionResponse.FromSecret("new-secret", rememberPasswordInVault: true));
            });

        await service.HandleAsync(
            TargetPasswordRequest(profile, sessionId, allowVaultPasswordReuse: true, followsRefusal: true),
            CancellationToken.None);

        Assert.NotNull(presented);
        Assert.True(presented!.CanRememberPassword);
        Assert.Equal("new-secret", vault.GetSshPasswordForProfile(profile));
        Assert.True(registry.TryGetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, out SecretBuffer? runtime));
        Assert.Equal("new-secret", runtime?.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task RetryPrompt_SaysTheSignInWasNotAccepted()
    {
        TerminalProfile profile = CreateProfile("2f9a6c18-d4b7-4e03-8c5a-71b0e3d9a6f2");
        AuthPromptViewModel? presented = null;
        var service = new SshInteractionService(
            vaultService: new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore()),
            sessionRegistry: new ActiveSshSessionRegistry(),
            authPresenter: (_, viewModel, _) =>
            {
                presented = viewModel;
                return Task.FromResult(SshInteractionResponse.FromSecret("typed-secret"));
            });

        await service.HandleAsync(TargetPasswordRequest(profile, followsRefusal: true), CancellationToken.None);

        Assert.Equal(
            "The server did not accept the sign-in. Enter the SSH password for alice@example.internal to continue.",
            presented!.Message);
    }

    [AvaloniaFact]
    public async Task PasswordRequests_UseLegacyVaultSecret_WhenFullProfileIdentityIsProvided()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("19c4e5f0-62ed-4d56-90b1-1d53e4c90d19"),
            Type = ConnectionType.SSH,
            Name = "Legacy Prod",
            SshHost = "legacy.internal",
            SshUser = "alice"
        };
        string legacyKey = $"SSH:{profile.Name}:{profile.SshUser}@{profile.SshHost}";
        vault.SetSecret(legacyKey, "legacy-secret");

        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: vault,
            authPresenter: (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        var response = await service.HandleAsync(
            TargetPasswordRequest(profile, allowVaultPasswordReuse: true),
            CancellationToken.None);

        Assert.Equal(0, promptCount);
        Assert.Equal("legacy-secret", response.Secret.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task PasswordRequests_FallBackToDialog_WhenVaultReuseIsNotAllowed()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("52e5e0a7-51c6-4c9e-8d1a-7f74e9e6d7b8"),
            Type = ConnectionType.SSH,
            Name = "Native Prod",
            SshHost = "example.internal",
            SshUser = "alice"
        };
        vault.SetSshPasswordForProfile(profile, "vault-secret");

        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: vault,
            authPresenter: (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(SshInteractionResponse.FromSecret("manual-secret"));
            });

        SshInteractionResponse response = await service.HandleAsync(TargetPasswordRequest(profile), CancellationToken.None);

        Assert.Equal("manual-secret", response.Secret.DangerousGetString());
        Assert.Equal(1, promptCount);
    }

    // ---- jump chains: a password only ever reaches the server it belongs to ----

    [AvaloniaFact]
    public async Task JumpHopPasswordPrompt_IsNeverAnsweredWithTheTargetsSavedPassword()
    {
        // The reported leak: the first password prompt of a chain comes from the bastion, and the
        // profile's saved (target) password was auto-sent to it. Even with reuse allowed on the
        // request, a jump hop's prompt must go to the user.
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        TerminalProfile profile = CreateProfile("0f8a6f55-4bd8-4f6e-8a5b-1c2f0ad9e101");
        vault.SetSshPasswordForProfile(profile, "target-vault-secret");

        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: vault,
            sessionRegistry: new ActiveSshSessionRegistry(),
            authPresenter: (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(SshInteractionResponse.FromSecret("bastion-secret"));
            });

        SshInteractionResponse response = await service.HandleAsync(
            JumpHopPasswordRequest(profile, allowVaultPasswordReuse: true),
            CancellationToken.None);

        Assert.Equal(1, promptCount);
        Assert.Equal("bastion-secret", response.Secret.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task JumpHopPassword_IsNeverRememberedAsTheTargetsPassword()
    {
        // The other half of the leak: "remember" on the bastion's prompt saved the bastion's
        // password under the TARGET's vault key.
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        TerminalProfile profile = CreateProfile("6c1b8f0e-2a7d-4f39-9d8e-4b1a2c3d4e02");
        vault.SetSshPasswordForProfile(profile, "target-vault-secret");

        var service = new SshInteractionService(
            vaultService: vault,
            sessionRegistry: new ActiveSshSessionRegistry(),
            authPresenter: (_, _, _) =>
                Task.FromResult(SshInteractionResponse.FromSecret("bastion-secret", rememberPasswordInVault: true)));

        await service.HandleAsync(JumpHopPasswordRequest(profile), CancellationToken.None);

        Assert.Equal("target-vault-secret", vault.GetSshPasswordForProfile(profile));
    }

    [AvaloniaFact]
    public async Task JumpHopPasswordPrompt_DoesNotOfferRemember_AndNamesTheJumpHost()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            vaultService: new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore()),
            sessionRegistry: new ActiveSshSessionRegistry(),
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        await service.HandleAsync(
            JumpHopPasswordRequest(CreateProfile("a4d0e6a1-7c55-4a0b-9f63-1f2e3d4c5b03")),
            CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.False(capturedVm!.CanRememberPassword);
        Assert.Contains("jump host jump@bastion.internal:2200", capturedVm.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task TargetPasswordPrompt_NamesTheTargetItAuthenticates()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            vaultService: new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore()),
            sessionRegistry: new ActiveSshSessionRegistry(),
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        await service.HandleAsync(
            TargetPasswordRequest(CreateProfile("d2b7c1e4-3f5a-4e6b-8c9d-0a1b2c3d4e04")),
            CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.True(capturedVm!.CanRememberPassword);
        Assert.Contains("alice@example.internal", capturedVm.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("jump host", capturedVm.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task BastionPasswordTypedInASession_IsNotReplayedToTheTarget()
    {
        // The runtime cache used to be keyed by session alone, so the password typed for the
        // bastion was auto-sent to the next prompt — the target's.
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        TerminalProfile profile = CreateProfile("7e3f9a2b-1c4d-4e5f-8a6b-9c0d1e2f3a05");
        var answers = new Queue<string>(["bastion-secret", "target-secret"]);
        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore()),
            sessionRegistry: registry,
            authPresenter: (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(SshInteractionResponse.FromSecret(answers.Dequeue()));
            });

        SshInteractionResponse bastion = await service.HandleAsync(
            JumpHopPasswordRequest(profile, sessionId),
            CancellationToken.None);
        SshInteractionResponse target = await service.HandleAsync(
            TargetPasswordRequest(profile, sessionId),
            CancellationToken.None);

        Assert.Equal(2, promptCount);
        Assert.Equal("bastion-secret", bastion.Secret.DangerousGetString());
        Assert.Equal("target-secret", target.Secret.DangerousGetString());
        Assert.True(registry.TryGetRuntimePassword(sessionId, "bastion.internal", 2200, "jump", out SecretBuffer? heldForBastion));
        Assert.True(registry.TryGetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, out SecretBuffer? heldForTarget));
        Assert.Equal("bastion-secret", heldForBastion?.DangerousGetString());
        Assert.Equal("target-secret", heldForTarget?.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task RuntimePassword_IsReplayedToTheServerItWasEnteredFor()
    {
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        TerminalProfile profile = CreateProfile("3a9c7e1f-5b2d-4c8e-9f0a-1b2c3d4e5f06");
        registry.SetRuntimePassword(sessionId, "bastion.internal", 2200, "jump", "bastion-secret");
        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore()),
            sessionRegistry: registry,
            authPresenter: (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(SshInteractionResponse.Cancel());
            });

        SshInteractionResponse response = await service.HandleAsync(
            JumpHopPasswordRequest(profile, sessionId),
            CancellationToken.None);

        Assert.Equal(0, promptCount);
        Assert.Equal("bastion-secret", response.Secret.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task PasswordPrompt_ThatDoesNotNameItsServer_IsNeverFilledFromTheVault()
    {
        // Fail closed: a prompt that cannot say which server is asking might be any of them.
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        TerminalProfile profile = CreateProfile("8b4d2f6a-9e1c-4a3b-8d7e-6f5a4b3c2d07");
        vault.SetSshPasswordForProfile(profile, "target-vault-secret");
        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: vault,
            sessionRegistry: new ActiveSshSessionRegistry(),
            authPresenter: (_, _, _) =>
            {
                promptCount++;
                return Task.FromResult(SshInteractionResponse.FromSecret("manual-secret"));
            });

        SshInteractionResponse response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:",
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProfileUser = profile.SshUser,
            ProfileHost = profile.SshHost,
            AllowVaultPasswordReuse = true
        }, CancellationToken.None);

        Assert.Equal(1, promptCount);
        Assert.Equal("manual-secret", response.Secret.DangerousGetString());
    }

    private static TerminalProfile CreateProfile(string id)
    {
        return new TerminalProfile
        {
            Id = Guid.Parse(id),
            Type = ConnectionType.SSH,
            Name = "Native Prod",
            SshHost = "example.internal",
            SshUser = "alice"
        };
    }

    /// <summary>A password prompt from the profile's final target, as NativeSshSession builds it.</summary>
    private static SshInteractionRequest TargetPasswordRequest(
        TerminalProfile profile,
        Guid? sessionId = null,
        bool allowVaultPasswordReuse = false,
        bool followsRefusal = false)
    {
        return new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:",
            SessionId = sessionId,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProfileUser = profile.SshUser,
            ProfileHost = profile.SshHost,
            AllowVaultPasswordReuse = allowVaultPasswordReuse,
            Host = profile.SshHost,
            Port = 22,
            User = profile.SshUser,
            IsJumpHop = false,
            FollowsRefusal = followsRefusal
        };
    }

    /// <summary>A password prompt from a bastion in front of the profile's target.</summary>
    private static SshInteractionRequest JumpHopPasswordRequest(
        TerminalProfile profile,
        Guid? sessionId = null,
        bool allowVaultPasswordReuse = false)
    {
        return new SshInteractionRequest
        {
            Kind = SshInteractionKind.Password,
            Prompt = "Password:",
            SessionId = sessionId,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProfileUser = profile.SshUser,
            ProfileHost = profile.SshHost,
            AllowVaultPasswordReuse = allowVaultPasswordReuse,
            Host = "bastion.internal",
            Port = 2200,
            User = "jump",
            IsJumpHop = true
        };
    }

    [AvaloniaFact]
    public async Task PassphraseRequests_ShowDialogEvenWhenVaultHasPassword()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var profile = new TerminalProfile
        {
            Id = Guid.Parse("6e1df53b-5b8f-4d5c-bfd7-9ad6f4d3d5d5"),
            Type = ConnectionType.SSH,
            Name = "Native Prod",
            SshHost = "example.internal",
            SshUser = "alice"
        };
        vault.SetSshPasswordForProfile(profile, "vault-secret");

        int promptCount = 0;
        var service = new SshInteractionService(
            vaultService: vault,
            authPresenter: (_, vm, _) =>
            {
                promptCount++;
                Assert.Equal("Passphrase", vm.Title);
                return Task.FromResult(SshInteractionResponse.FromSecret("entered-passphrase"));
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Passphrase,
            Prompt = "Key passphrase:",
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ProfileUser = profile.SshUser,
            ProfileHost = profile.SshHost
        }, CancellationToken.None);

        Assert.Equal(1, promptCount);
        Assert.Equal("entered-passphrase", response.Secret.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task PassphraseRequestsMapToSecretPrompt()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.FromSecret("hunter2"));
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.Passphrase,
            Prompt = "Key passphrase:"
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.Equal("Passphrase", capturedVm!.Title);
        Assert.Single(capturedVm.Prompts);
        Assert.True(capturedVm.Prompts[0].IsSecret);
        Assert.Equal("hunter2", response.Secret.DangerousGetString());
    }

    [AvaloniaFact]
    public async Task KeyboardInteractiveRoundWithOneQuestion_SendsOneAnswer()
    {
        // PAM's usual round: a single "Password: " question. The dialog answered it as a password
        // (one field read as a secret), so the server got zero answers to one question - sshd then
        // drops the connection ("wrong number of replies"), seen as "Unable to receive more messages
        // from the channel".
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                vm.Prompts[0].Value = "typed-password";
                return Task.FromResult(vm.BuildResponse());
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.KeyboardInteractive,
            KeyboardPrompts = [new SshKeyboardPrompt("Password: ", false)]
        }, CancellationToken.None);

        Assert.Equal(["typed-password"], response.KeyboardResponses.Select(value => value.DangerousGetString()));
        using var payload = NativeSshInteractionJson.BuildResponsePayload(NativeSshResponseKind.KeyboardInteractive, response);
        using var sent = System.Text.Json.JsonDocument.Parse(payload.Span.ToArray());
        Assert.Equal(1, sent.RootElement.GetProperty("responses").GetArrayLength());
    }

    [AvaloniaFact]
    public async Task KeyboardInteractiveRoundWithNoQuestions_IsAnsweredWithoutADialog()
    {
        // sshd's PAM sends an empty round after a correct password. It asks nothing, so a dialog
        // would be a blank window the user has to dismiss.
        int presented = 0;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                presented++;
                return Task.FromResult(vm.BuildResponse());
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.KeyboardInteractive,
            KeyboardPrompts = []
        }, CancellationToken.None);

        Assert.Equal(0, presented);
        Assert.False(response.IsCanceled);
        Assert.Empty(response.KeyboardResponses);
    }

    [AvaloniaFact]
    public async Task KeyboardInteractiveRequestsMapAllPromptFields()
    {
        AuthPromptViewModel? capturedVm = null;
        var service = new SshInteractionService(
            authPresenter: (_, vm, _) =>
            {
                capturedVm = vm;
                return Task.FromResult(SshInteractionResponse.FromKeyboardResponses("code", "otp"));
            });

        var response = await service.HandleAsync(new SshInteractionRequest
        {
            Kind = SshInteractionKind.KeyboardInteractive,
            Name = "Duo",
            Instructions = "Provide challenge response",
            KeyboardPrompts =
            [
                new SshKeyboardPrompt("Passcode:", false),
                new SshKeyboardPrompt("OTP:", false)
            ]
        }, CancellationToken.None);

        Assert.NotNull(capturedVm);
        Assert.Equal("Duo", capturedVm!.Title);
        Assert.Equal("Provide challenge response", capturedVm.Message);
        Assert.Equal(2, capturedVm.Prompts.Count);
        Assert.Equal("Passcode:", capturedVm.Prompts[0].Prompt);
        Assert.True(capturedVm.Prompts[0].IsSecret);
        Assert.Equal(["code", "otp"], response.KeyboardResponses.Select(value => value.DangerousGetString()));
    }

    // #610: a connection an agent opened never raises a dialog over the user's shell. What can be answered without one
    // still is; anything else fails the attempt and says what it needed.

    [AvaloniaFact]
    public async Task WithoutDialogs_AnUnknownHostKey_FailsAsNeedingTheUser_AndIsNotTrusted()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            int presented = 0;
            var needed = new List<SshInteractionKind>();
            var service = new SshInteractionService(
                ownerProvider: () => new Window(),
                hostKeyPresenter: (_, _, _) =>
                {
                    presented++;
                    return Task.FromResult(SshInteractionResponse.AcceptHostKey());
                },
                knownHostsStore: knownHosts);

            SshInteractionResponse response = await service.WithoutDialogs(needed.Add)
                .HandleAsync(HostKeyRequest("SHA256:test"), CancellationToken.None);

            Assert.Equal(0, presented);
            Assert.True(response.IsCanceled);
            Assert.False(response.IsAccepted);
            Assert.Equal([SshInteractionKind.UnknownHostKey], needed);
            Assert.Equal(NativeKnownHostMatch.Unknown, knownHosts.CheckHost("example.internal", 22, "ssh-ed25519", "SHA256:test"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task WithoutDialogs_AHostKeyThatChanged_SaysSo()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            knownHosts.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:old");
            var needed = new List<SshInteractionKind>();
            var service = new SshInteractionService(ownerProvider: () => new Window(), knownHostsStore: knownHosts);

            SshInteractionResponse response = await service.WithoutDialogs(needed.Add)
                .HandleAsync(HostKeyRequest("SHA256:new"), CancellationToken.None);

            Assert.True(response.IsCanceled);
            Assert.Equal([SshInteractionKind.ChangedHostKey], needed);
            Assert.Equal(NativeKnownHostMatch.Mismatch, knownHosts.CheckHost("example.internal", 22, "ssh-ed25519", "SHA256:new"));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task WithoutDialogs_AHostKeyTheUserTrusts_IsStillAccepted()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            knownHosts.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:test");
            var needed = new List<SshInteractionKind>();
            var service = new SshInteractionService(ownerProvider: () => new Window(), knownHostsStore: knownHosts);

            SshInteractionResponse response = await service.WithoutDialogs(needed.Add)
                .HandleAsync(HostKeyRequest("SHA256:test"), CancellationToken.None);

            Assert.True(response.IsAccepted);
            Assert.Empty(needed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task WithoutDialogs_TheProfilesSavedPassword_IsStillUsed()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        TerminalProfile profile = CreateProfile("6d1f3a52-2c8b-4e07-9a61-0f5e8c7b2d14");
        vault.SetSshPasswordForProfile(profile, "vault-secret");
        var needed = new List<SshInteractionKind>();
        var service = new SshInteractionService(ownerProvider: () => new Window(), vaultService: vault);

        SshInteractionResponse response = await service.WithoutDialogs(needed.Add)
            .HandleAsync(TargetPasswordRequest(profile, allowVaultPasswordReuse: true), CancellationToken.None);

        Assert.Equal("vault-secret", response.Secret.DangerousGetString());
        Assert.False(response.IsCanceled);
        Assert.Empty(needed);
    }

    [AvaloniaFact]
    public async Task WithoutDialogs_APasswordNobodySaved_FailsAsNeedingTheUser_AndStoresNothing()
    {
        var vault = new VaultService(new Ntilde.Shell.Secrets.InMemorySecretStore());
        var registry = new ActiveSshSessionRegistry();
        Guid sessionId = Guid.NewGuid();
        TerminalProfile profile = CreateProfile("8e2a4b63-3d9c-4f18-8b72-1a6f9d8c3e25");
        int presented = 0;
        var needed = new List<SshInteractionKind>();
        var service = new SshInteractionService(
            ownerProvider: () => new Window(),
            vaultService: vault,
            sessionRegistry: registry,
            authPresenter: (_, _, _) =>
            {
                presented++;
                return Task.FromResult(SshInteractionResponse.FromSecret("typed", rememberPasswordInVault: true));
            });

        SshInteractionResponse response = await service.WithoutDialogs(needed.Add)
            .HandleAsync(TargetPasswordRequest(profile, sessionId, allowVaultPasswordReuse: true), CancellationToken.None);

        Assert.Equal(0, presented);
        Assert.True(response.IsCanceled);
        Assert.Equal([SshInteractionKind.Password], needed);
        Assert.False(registry.TryGetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, out _));
        Assert.Null(vault.GetSshPasswordForProfile(profile));
    }

    [AvaloniaFact]
    public async Task WithoutDialogs_APassphraseOrAQuestion_FailsAsNeedingTheUser()
    {
        int presented = 0;
        var needed = new List<SshInteractionKind>();
        var service = new SshInteractionService(
            ownerProvider: () => new Window(),
            authPresenter: (_, vm, _) =>
            {
                presented++;
                return Task.FromResult(vm.BuildResponse());
            });
        ISshInteractionHandler agent = service.WithoutDialogs(needed.Add);

        SshInteractionResponse passphrase = await agent.HandleAsync(
            new SshInteractionRequest { Kind = SshInteractionKind.Passphrase, Prompt = "Passphrase:" },
            CancellationToken.None);
        SshInteractionResponse question = await agent.HandleAsync(
            new SshInteractionRequest
            {
                Kind = SshInteractionKind.KeyboardInteractive,
                KeyboardPrompts = [new SshKeyboardPrompt("Verification code:", false)]
            },
            CancellationToken.None);
        // An empty round asks nothing, so it is answered as it always is.
        SshInteractionResponse emptyRound = await agent.HandleAsync(
            new SshInteractionRequest { Kind = SshInteractionKind.KeyboardInteractive, KeyboardPrompts = [] },
            CancellationToken.None);

        Assert.Equal(0, presented);
        Assert.True(passphrase.IsCanceled);
        Assert.True(question.IsCanceled);
        Assert.False(emptyRound.IsCanceled);
        Assert.Equal([SshInteractionKind.Passphrase, SshInteractionKind.KeyboardInteractive], needed);
    }

    /// <summary>
    /// #610: a tab that joined a shared connection is not asked its prompts - the tab that opened it is - but one that is
    /// refused ends the connection for it too. It is told what was asked, on the UI thread, with the reason its own
    /// refusal would have given: a host key that is not the trusted one says so.
    /// </summary>
    [AvaloniaFact]
    public async Task WithoutDialogs_APromptRefusedOnASharedConnection_IsReportedAsNeedingTheUser()
    {
        string tempRoot = CreateTempDirectory();
        try
        {
            var knownHosts = new NativeKnownHostsStore(Path.Combine(tempRoot, "native_known_hosts.json"));
            knownHosts.TrustHost("example.internal", 22, "ssh-ed25519", "SHA256:old");
            var needed = new List<SshInteractionKind>();
            var service = new SshInteractionService(ownerProvider: () => new Window(), knownHostsStore: knownHosts);
            ISshInteractionHandler joined = service.WithoutDialogs(kind =>
            {
                Assert.True(Dispatcher.UIThread.CheckAccess());
                needed.Add(kind);
            });

            // The shared connection's poll thread reports it.
            await Task.Run(() => joined.RefusedOnSharedConnection(
                new SshInteractionRequest { Kind = SshInteractionKind.Password, Prompt = "Password:" }));
            await Task.Run(() => joined.RefusedOnSharedConnection(HostKeyRequest("SHA256:new")));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([SshInteractionKind.Password, SshInteractionKind.ChangedHostKey], needed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static SshInteractionRequest HostKeyRequest(string fingerprint) => new()
    {
        Kind = SshInteractionKind.UnknownHostKey,
        Host = "example.internal",
        Port = 22,
        Algorithm = "ssh-ed25519",
        Fingerprint = fingerprint
    };

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_interaction_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
