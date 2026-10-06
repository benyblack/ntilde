using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Ntilde.VT;
using Velopack.Locators;
using Velopack.Logging;

namespace Ntilde.Shell;

/// <summary>
/// Puts the install directory on the user PATH, and takes it off again (Phase 4 spec §11.4). The Windows
/// installer never did, so <c>ntilde</c> typed at a prompt found nothing; now that directory holds
/// <c>ntilde.com</c> beside <c>Ntilde.exe</c>, and PATHEXT ranks <c>.COM</c> first, so a prompt runs the console
/// launcher and waits for it. Program.cs calls <see cref="Ensure"/> from Velopack's install and update hooks
/// and <see cref="Remove"/> from its uninstall hook, and nothing else calls either
/// (<c>CliCommandDispatchTests.The_PATH_is_registered_only_from_the_Velopack_hooks</c>).
/// </summary>
internal static class UserPathRegistration
{
    private const string EnvironmentKey = "Environment";
    private const string PathValue = "Path";

    private const nint HwndBroadcast = 0xffff;
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint BroadcastTimeoutMilliseconds = 5000;

    /// <summary>
    /// <paramref name="existing"/> with <paramref name="directory"/> appended (<paramref name="add"/>) or every
    /// entry naming it removed. Entries split on <c>;</c>, and empty ones are dropped from a PATH that is
    /// rewritten. Two entries name the same directory when they are equal, ignoring case, after
    /// <see cref="Environment.ExpandEnvironmentVariables"/> and <see cref="Path.TrimEndingDirectorySeparator(string)"/>.
    /// The other entries keep their order and their spelling, <c>%VAR%</c> and all. When there is nothing to
    /// do - adding a directory already there, removing one that is not - <paramref name="existing"/> comes back
    /// as it was (<c>""</c> for null), so the caller can tell and leave the registry alone. Pure.
    /// </summary>
    public static string Merge(string? existing, string directory, bool add)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        string current = existing ?? "";
        string wanted = Normalize(directory);
        string[] entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        bool Names(string entry) => string.Equals(Normalize(entry), wanted, StringComparison.OrdinalIgnoreCase);

        bool present = entries.Any(Names);
        if (add) return present ? current : string.Join(';', entries.Append(directory));
        return present ? string.Join(';', entries.Where(e => !Names(e))) : current;
    }

    private static string Normalize(string entry) =>
        Path.TrimEndingDirectorySeparator(Environment.ExpandEnvironmentVariables(entry));

    /// <summary>Adds <paramref name="directory"/> to HKCU <c>Environment\Path</c>. Windows only; a failure is logged, never thrown.</summary>
    public static void Ensure(string? directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Update(directory, add: true, ReadUserPath, WriteUserPath, AnnounceEnvironmentChange);
    }

    /// <summary>Removes every entry naming <paramref name="directory"/> from HKCU <c>Environment\Path</c>. Windows only; a failure is logged, never thrown.</summary>
    public static void Remove(string? directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        _ = Update(directory, add: false, ReadUserPath, WriteUserPath, AnnounceEnvironmentChange);
    }

    /// <summary>
    /// <see cref="Ensure"/> and <see cref="Remove"/> over injected registry access, the seam the tests use
    /// instead of the real user PATH. Writes and announces only a PATH that changed: the update hook runs on
    /// every update, and a PATH that already has the directory is left as it is. Returns whether it wrote.
    /// Every failure is logged and swallowed - the PATH is a convenience, and a Velopack hook that threw would
    /// take an install, update or uninstall down with it.
    /// </summary>
    internal static bool Update(string? directory, bool add, Func<string?> read, Action<string> write, Action announce)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(announce);
        string change = add ? "add" : "remove";
        if (string.IsNullOrWhiteSpace(directory))
        {
            Log(LogLevel.Warning, $"PATH: no install directory to {change}.");
            return false;
        }

        try
        {
            string? existing = read();
            string merged = Merge(existing, directory, add);
            if (string.Equals(merged, existing ?? "", StringComparison.Ordinal)) return false;
            write(merged);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"PATH: could not {change} {directory}: {ex.Message}");
            return false;
        }

        // The PATH is already right; without the broadcast only programs started before the next sign-in
        // (Explorer's own children included) keep the old one.
        try
        {
            announce();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"PATH: changed, but the WM_SETTINGCHANGE broadcast failed: {ex.Message}");
        }

        Log(LogLevel.Info, add ? $"PATH: added {directory}." : $"PATH: removed {directory}.");
        return true;
    }

    /// <summary>
    /// The raw value: <c>%VAR%</c> entries unexpanded, so they are written back as the user wrote them. A value
    /// that is not a string is refused rather than read as empty, which would replace the user's PATH.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadUserPath()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(EnvironmentKey, writable: false);
        return key?.GetValue(PathValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) switch
        {
            null => null,
            string value => value,
            object other => throw new InvalidDataException($@"HKCU\{EnvironmentKey}\{PathValue} is a {other.GetType().Name}, not a string; left alone."),
        };
    }

    /// <summary>REG_EXPAND_SZ, as Windows itself writes the user PATH, so <c>%VAR%</c> entries keep expanding.</summary>
    [SupportedOSPlatform("windows")]
    private static void WriteUserPath(string value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true);
        key.SetValue(PathValue, value, RegistryValueKind.ExpandString);
    }

    /// <summary>Tells running programs - Explorer, so the shells it starts next - that the environment changed.</summary>
    [SupportedOSPlatform("windows")]
    private static void AnnounceEnvironmentChange()
    {
        if (SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, EnvironmentKey, SmtoAbortIfHung, BroadcastTimeoutMilliseconds, out _) == 0)
            throw new IOException(Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError()));
    }

    /// <summary>
    /// A hook process exits inside Velopack's <c>Run</c>, long before <c>AppLogger.Initialize</c>, so
    /// <see cref="TerminalLogger"/> has no sink there; Velopack's own log, beside its install, update and
    /// uninstall lines, is where these belong. Anywhere else (the tests) they go to <see cref="TerminalLogger"/>.
    /// </summary>
    private static void Log(LogLevel level, string message)
    {
        if (VelopackLocator.IsCurrentSet)
        {
            VelopackLocator.Current.Log.Log(level >= LogLevel.Warning ? VelopackLogLevel.Warning : VelopackLogLevel.Information, message, null);
            return;
        }

        TerminalLogger.Log(level, message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wParam, string lParam, uint flags, uint timeout, out nuint result);
}
