using Ntilde.Platform.Security;
using Ntilde.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Storage;
using Ntilde.Models;

namespace Ntilde.Services.Ssh;

public sealed class RemotePathAutocompleteService : IRemotePathAutocompleteService
{
    private const int MaxSuggestions = 12;
    private readonly INativeSshInterop _nativeInterop;
    private readonly ActiveSshSessionRegistry _sessionRegistry;
    private readonly Func<SshConnectionService> _sshServiceFactory;
    private readonly Func<TerminalProfile, SecretBuffer?> _passwordResolver;
    private readonly NativeSshConnectionPool _connectionPool;

    /// <param name="connectionPool">As for <see cref="RemoteDirectoryBrowserService"/>: where a shared connection is found.</param>
    public RemotePathAutocompleteService(
        INativeSshInterop? nativeInterop = null,
        ActiveSshSessionRegistry? sessionRegistry = null,
        Func<SshConnectionService>? sshServiceFactory = null,
        Func<TerminalProfile, SecretBuffer?>? passwordResolver = null,
        NativeSshConnectionPool? connectionPool = null)
    {
        _connectionPool = connectionPool ?? NativeSshConnectionPool.Shared;
        _nativeInterop = nativeInterop ?? new NativeSshInterop();
        _sessionRegistry = sessionRegistry ?? ActiveSshSessionRegistry.Instance;
        _sshServiceFactory = sshServiceFactory ?? (() => new SshConnectionService());
        _passwordResolver = passwordResolver ?? (profile => (MainWindow.Vault ?? new VaultService()).GetSshPasswordBufferForProfile(profile));
    }

    public async Task<IReadOnlyList<RemotePathSuggestion>> GetSuggestionsAsync(
        Guid profileId,
        Guid sessionId,
        string input,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty || sessionId == Guid.Empty || string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!RemoteDirectoryBrowserService.TryCreateNativeListingConnection(
                    profileId,
                    sessionId,
                    _sessionRegistry,
                    _sshServiceFactory,
                    _passwordResolver,
                    out NativeSshConnectionOptions? connectionOptions,
                    out NativeHopPasswords? passwords,
                    out SshProfile? storedProfile,
                    out _))
            {
                return [];
            }

            using var passwordLifetime = passwords;
            RemotePathAutocompleteQuery query = RemotePathAutocompleteQuery.Parse(input);

            IReadOnlyList<NativeRemotePathEntry> entries;
            try
            {
                entries = await BackgroundWork.RunBlockingAsync(
                    token => RemoteDirectoryBrowserService.ListOnSharedConnection(_connectionPool, storedProfile, query.ParentPath, token)
                        ?? _nativeInterop.ListRemoteDirectory(connectionOptions!, query.ParentPath, token),
                    cancellationToken);
            }
            catch (NativeSshAuthenticationRefusedException)
            {
                // Release hardening item 5, as the sidebar's listing does.
                passwords!.ForgetRefusedTarget(_sessionRegistry, connectionOptions!.Host, connectionOptions.Port, connectionOptions.User);
                return [];
            }

            return RemotePathAutocompleteQuery.Rank(
                    entries.Select(entry => new RemotePathSuggestion(entry.Name, entry.FullPath, entry.IsDirectory)),
                    query.Prefix)
                .Take(MaxSuggestions)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }
}
