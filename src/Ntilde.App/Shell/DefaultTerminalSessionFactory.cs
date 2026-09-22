using System;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Sessions;
using Ntilde.Pty;
using Ntilde.VT;

namespace Ntilde.Shell;

/// <summary>
/// The factory the app runs on: the two spawn branches that used to sit inline in
/// <c>TerminalPane.InitializeSessionCore</c>, moved without behaviour change.
/// </summary>
/// <remarks>
/// Stateless and shared. Everything that used to be read off the pane at spawn time - the
/// interaction handler, the native-SSH toggle, the diagnostics level - arrives in the request,
/// which is what lets a multiplexer factory be substituted later without the pane knowing.
/// </remarks>
public sealed class DefaultTerminalSessionFactory : ITerminalSessionFactory
{
    public static DefaultTerminalSessionFactory Instance { get; } = new();

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Ssh is { } ssh)
        {
            var sessionFactory = new SshSessionFactory(
                nativeInteractionHandler: ssh.InteractionHandler as ISshInteractionHandler,
                nativeSshEnabled: ssh.NativeSshEnabled);

            return sessionFactory.Create(
                ssh.ProfileId,
                request.Cols,
                request.Rows,
                (SshDiagnosticsLevel)ssh.DiagnosticsLevel,
                null,
                log: TerminalLogger.Log);
        }

        return new RustPtySession(
            request.Command,
            request.Cols,
            request.Rows,
            request.Arguments,
            request.StartingDirectory,
            skipPowerShellPostLaunchInit: request.SkipPowerShellPostLaunchInit,
            environmentOverrides: request.EnvironmentOverrides);
    }
}
