using Ntilde.Platform.Ssh.Interactions;

namespace Ntilde.Services.Ssh;

public interface ISshInteractionService : ISshInteractionHandler
{
    /// <summary>
    /// This window's prompts for a connection nobody is waiting at - one an agent opened (#610): they answer what needs no
    /// dialog (a saved or remembered password, a host key the user trusts, an empty keyboard-interactive round) as
    /// <see cref="ISshInteractionHandler.HandleAsync"/> does, and anything else - which would raise a dialog over the user's
    /// shell - they refuse, after telling <paramref name="needsUser"/> what was asked. The connection then fails.
    /// </summary>
    ISshInteractionHandler WithoutDialogs(Action<SshInteractionKind> needsUser);
}
