using Ntilde.VT;
using Velopack.Locators;
using Velopack.Logging;

namespace Ntilde.Shell;

/// <summary>
/// Where a Velopack hook's lines go. A hook process exits inside Velopack's <c>Run</c>, long before
/// <c>AppLogger.Initialize</c>, so <see cref="TerminalLogger"/> has no sink there; Velopack's own log, beside its install,
/// update and uninstall lines, is where these belong. Anywhere else (the tests) they go to <see cref="TerminalLogger"/>.
/// </summary>
internal static class VelopackHookLog
{
    public static void Write(LogLevel level, string message)
    {
        if (VelopackLocator.IsCurrentSet)
        {
            VelopackLocator.Current.Log.Log(level >= LogLevel.Warning ? VelopackLogLevel.Warning : VelopackLogLevel.Information, message, null);
            return;
        }

        TerminalLogger.Log(level, message);
    }
}
