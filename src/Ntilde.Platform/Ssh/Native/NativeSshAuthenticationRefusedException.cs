namespace Ntilde.Platform.Ssh.Native;

/// <summary>
/// A non-interactive native connection (an SFTP transfer, a remote listing) was refused at sign-in: the server turned down
/// the password it was offered (the native layer's <c>auth-failed</c> status). An <see cref="InvalidOperationException"/>,
/// as every other native failure of these calls is, so callers that do not care are unchanged; a caller that offered a
/// remembered password uses it to forget that value (release hardening item 5).
/// </summary>
public sealed class NativeSshAuthenticationRefusedException : InvalidOperationException
{
    public NativeSshAuthenticationRefusedException()
    {
    }

    public NativeSshAuthenticationRefusedException(string message)
        : base(message)
    {
    }

    public NativeSshAuthenticationRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
