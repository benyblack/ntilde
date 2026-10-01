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
            // `as` would silently yield null here, which is the worst possible currency for the
            // one untyped field in the request: a handler of the wrong type means no interactive
            // prompt, so a key passphrase or host-key confirmation the user was supposed to answer
            // never appears and the connection just fails somewhere further down. The descriptor
            // is `object?` to keep Ntilde.Pty below Ntilde.Platform; the bill for that opacity
            // comes due exactly here, and it is paid by naming the type that arrived.
            ISshInteractionHandler? interactionHandler = ssh.InteractionHandler switch
            {
                null => null,
                ISshInteractionHandler handler => handler,
                var other => throw new ArgumentException(
                    $"{nameof(SshSessionDescriptor)}.{nameof(SshSessionDescriptor.InteractionHandler)} must be " +
                    $"an {nameof(ISshInteractionHandler)} or null; got {other.GetType().FullName}.",
                    nameof(request)),
            };

            var sessionFactory = new SshSessionFactory(
                nativeInteractionHandler: interactionHandler,
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
            environmentOverrides: MaskInheritedMuxSession(
                request.EnvironmentOverrides,
                Environment.GetEnvironmentVariable(Ntilde.Mux.MuxServer.SessionEnvironmentVariable)));
    }

    /// <summary>
    /// A GUI started from inside a mux shell inherits that shell's NTILDE_MUX_SESSION, and its local
    /// panes would pass it on: `mux attach` there would then refuse a session it is not inside. The
    /// overrides can only add or replace, never remove (forkpty inherits the whole environment), so
    /// it is blanked: an empty value names no session. A daemon spawn always carries its own value,
    /// which is kept.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? MaskInheritedMuxSession(
        IReadOnlyDictionary<string, string>? overrides, string? inherited)
    {
        const string Key = Ntilde.Mux.MuxServer.SessionEnvironmentVariable;
        if (string.IsNullOrEmpty(inherited) || overrides?.ContainsKey(Key) == true) return overrides;

        var masked = overrides is null ? new Dictionary<string, string>() : new Dictionary<string, string>(overrides);
        masked[Key] = string.Empty;
        return masked;
    }
}
