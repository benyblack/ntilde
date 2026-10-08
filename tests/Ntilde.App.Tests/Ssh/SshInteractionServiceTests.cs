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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
        Assert.Equal("password", response.Secret);
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

        Assert.Equal("manual-secret", response.Secret);
        Assert.Equal("manual-secret", vault.GetSshPasswordForProfile(profile));
    }

    [Fact]
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

        Assert.Equal("manual-secret", response.Secret);
        Assert.Null(vault.GetSshPasswordForProfile(profile));
    }

    [Fact]
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

        Assert.Equal("manual-secret", response.Secret);
        Assert.True(registry.TryGetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, out string? password));
        Assert.Equal("manual-secret", password);
    }

    [Fact]
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

        Assert.Equal("manual-secret", response.Secret);
        Assert.Equal("vault-secret", vault.GetSshPasswordForProfile(profile));
    }

    [Fact]
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
        Assert.Equal("vault-secret", response.Secret);
        Assert.False(response.IsCanceled);
    }

    [Fact]
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
        Assert.Equal("legacy-secret", response.Secret);
    }

    [Fact]
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

        Assert.Equal("manual-secret", response.Secret);
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
        Assert.Equal("bastion-secret", response.Secret);
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
        Assert.Equal("bastion-secret", bastion.Secret);
        Assert.Equal("target-secret", target.Secret);
        Assert.True(registry.TryGetRuntimePassword(sessionId, "bastion.internal", 2200, "jump", out string? heldForBastion));
        Assert.True(registry.TryGetRuntimePassword(sessionId, profile.SshHost, 22, profile.SshUser, out string? heldForTarget));
        Assert.Equal("bastion-secret", heldForBastion);
        Assert.Equal("target-secret", heldForTarget);
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
        Assert.Equal("bastion-secret", response.Secret);
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
        Assert.Equal("manual-secret", response.Secret);
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
            Host = profile.SshHost,
            Port = 22,
            User = profile.SshUser,
            IsJumpHop = false
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

    [Fact]
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
        Assert.Equal("entered-passphrase", response.Secret);
    }

    [Fact]
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
        Assert.Equal("hunter2", response.Secret);
    }

    [Fact]
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
        Assert.Equal(["code", "otp"], response.KeyboardResponses);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"nova_ssh_interaction_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
