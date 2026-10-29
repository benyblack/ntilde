using Ntilde.AgentHost;
using Ntilde.Platform.Ssh.Interactions;

namespace Ntilde.Controls
{
    /// <summary>
    /// #610: the SSH connections of a pane an agent opened. They never raise a sign-in dialog: it would open over the
    /// user's shell, often for a tab they are not looking at, and take their keystrokes. A server that asks for something
    /// only the user can give ends the connection instead, and the pane and the agent are both told what it was.
    /// </summary>
    /// <remarks>
    /// A profile whose sessions persist on its host connects through that host's own attempts instead: the request says
    /// it is not <see cref="Ntilde.Pty.SshSessionDescriptor.Interactive"/>, so the attempt never asks the user, and a
    /// sign-in that needs them is reported the same way (#631).
    /// </remarks>
    public partial class TerminalPane
    {
        // What the current connection's server asked for that only the user can give; null until it asks. Written on the UI
        // thread, where the window's prompt service answers, and only by the current connection's prompts.
        private SshInteractionKind? _sshNeedsUser;
        private int _sshConnection;

        /// <summary>
        /// An agent opened this pane, and the user has not reconnected it since: its SSH connections get the window's
        /// prompts without their dialogs (<see cref="Services.Ssh.ISshInteractionService.WithoutDialogs"/>).
        /// </summary>
        public bool OpenedByAgent { get; set; }

        /// <summary>
        /// The user's reconnect: Enter on a dead pane, or the palette's "Pane: Reconnect". It makes an agent's pane the
        /// user's, so its connection may ask them again. The other callers of <see cref="Reconnect"/> (a mux reattach, a
        /// restart that let go of the shell) are not the user's, and leave the pane as it was.
        /// </summary>
        public void ReconnectByUser()
        {
            OpenedByAgent = false;
            Reconnect();
        }

        /// <summary>The prompt handler for a new SSH connection of this pane, called as it starts.</summary>
        private ISshInteractionHandler? SshPromptsForNewConnection()
        {
            int connection = ++_sshConnection;
            _sshNeedsUser = null;
            if (!OpenedByAgent || SshInteractionHandler is null)
            {
                return SshInteractionHandler;
            }

            return SshInteractionHandler.WithoutDialogs(kind =>
            {
                // The first question is the one the connection stopped on.
                if (connection == _sshConnection) _sshNeedsUser ??= kind;
            });
        }

        /// <summary>
        /// The banner of a dead SSH pane whose connection stopped for the user, in place of the plain disconnect one; null
        /// when it did not. Nothing interpolated comes from the server.
        /// </summary>
        private string? SshNeedsUserBanner()
        {
            if (_sshNeedsUser is not { } kind) return null;

            (string asked, string next) = kind switch
            {
                SshInteractionKind.UnknownHostKey => ("to trust its host key, which you have not trusted yet",
                    " Trust it when you connect, and agents can connect on their own."),
                SshInteractionKind.ChangedHostKey => ("to trust a host key that is NOT the one you trusted for it before",
                    string.Empty),
                SshInteractionKind.Password => ("for a password Ntilde had no saved one for",
                    " If it is this profile's password, save it when you connect, and agents can connect on their own."),
                SshInteractionKind.Passphrase => ("for your key's passphrase", string.Empty),
                _ => ("a question only you can answer, such as a one-time code", string.Empty),
            };
            return "\r\n[SSH session disconnected]\r\n" +
                $"[An agent opened this connection, and the server asked {asked}. A connection an agent opens never shows a sign-in dialog.]\r\n" +
                $"[Press Enter to connect yourself; the agent can carry on in this tab once you have.{next}]\r\n";
        }

        /// <summary>
        /// What the agent is told a persistent-remote tab it opened has no session for (#631): the host's failure, when it is
        /// one that needs the user to sign in or to check a host key. Null for any other failure, and for the native backend
        /// switched off, which no sign-in fixes.
        /// </summary>
        private static AgentNeedsUserReason? AgentNeedsUserFor(Ntilde.Shell.Mux.Remote.RemoteMuxFailure? failure) => failure switch
        {
            { Kind: Ntilde.Shell.Mux.Remote.RemoteFailureKind.NeedsUser, Cause: var cause } => cause switch
            {
                Ntilde.Shell.Mux.Remote.RemoteNeedsUserCause.SignIn => AgentNeedsUserReason.SignIn,
                Ntilde.Shell.Mux.Remote.RemoteNeedsUserCause.SavedPasswordRefused => AgentNeedsUserReason.Password,
                Ntilde.Shell.Mux.Remote.RemoteNeedsUserCause.HostKey => AgentNeedsUserReason.HostKey,
                Ntilde.Shell.Mux.Remote.RemoteNeedsUserCause.HostKeyChanged => AgentNeedsUserReason.ChangedHostKey,
                _ => null,
            },
            _ => null,
        };

        /// <summary>What the agent is told its session stopped for (<see cref="AgentSessionStatusSnapshot.NeedsUser"/>).</summary>
        private AgentNeedsUserReason? AgentNeedsUser() => _sshNeedsUser switch
        {
            null => null,
            SshInteractionKind.UnknownHostKey => AgentNeedsUserReason.HostKey,
            SshInteractionKind.ChangedHostKey => AgentNeedsUserReason.ChangedHostKey,
            SshInteractionKind.Password => AgentNeedsUserReason.Password,
            SshInteractionKind.Passphrase => AgentNeedsUserReason.Passphrase,
            _ => AgentNeedsUserReason.KeyboardInteractive,
        };
    }
}
