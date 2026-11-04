using Avalonia.Controls;
using Avalonia.Input.Platform;
using Ntilde.AgentHost.Contracts;

namespace Ntilde.Shell.Clipboard;

/// <summary>Explicit startup-only, process-wide fail-closed native clipboard policy. Metadata is not PTY attestation.</summary>
internal sealed class PrivateClipboardQaMode
{
    internal const string Flag = "--ntilde-terminfo-private-clipboard=";
    private static readonly PrivateClipboardQaMode StartupProcess = FromStartup(
        Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable("TERMINFO_RUN_ID"),
        Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT"));
    private static PrivateClipboardQaMode? s_processForTesting;
    internal static PrivateClipboardQaMode Process => Volatile.Read(ref s_processForTesting) ?? StartupProcess;

    /// <summary>Serialized headless tests only; production's explicit startup latch cannot be changed.</summary>
    internal static IDisposable OverrideProcessForTesting(PrivateClipboardQaMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        if (StartupProcess.IsEnabled || Interlocked.CompareExchange(ref s_processForTesting, mode, null) != null)
            throw new InvalidOperationException("An active startup/test process latch cannot be replaced.");
        try { return new ProcessOverrideScope(mode); }
        catch
        {
            Interlocked.CompareExchange(ref s_processForTesting, null, mode);
            throw;
        }
    }

    private sealed class ProcessOverrideScope(PrivateClipboardQaMode mode) : IDisposable
    {
        private PrivateClipboardQaMode? _mode = mode;
        private readonly IDisposable[] _controlGuards = mode.InstallControlGuards();
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _mode, null);
            if (current == null) return;
            // Windows are torn down first. Do not leave a live fixture if test cleanup failed.
            current._fixture?.Interrupt();
            foreach (var guard in _controlGuards) guard.Dispose();
            Interlocked.CompareExchange(ref s_processForTesting, null, current);
        }
    }

    private readonly object _gate = new();
    private object? _owner;
    private DisposableClipboardFixture? _fixture;
    internal string? RunId { get; }
    internal bool IsEnabled => RunId != null;

    private PrivateClipboardQaMode(string? runId) => RunId = runId;

    internal static PrivateClipboardQaMode FromStartup(string[] args, string? runId, string? scratchRoot)
    {
        var flags = args.Where(arg => arg == "--ntilde-terminfo-private-clipboard" || arg.StartsWith(Flag, StringComparison.Ordinal)).ToArray();
        if (flags.Length == 0) return new(null);
        if (flags.Length != 1 || !ClipboardFixtureValidation.IsRunId(runId) || flags[0] != Flag + runId
            || args.Count(arg => arg.StartsWith("--ntilde-terminfo-owner=", StringComparison.Ordinal)) != 1
            || !args.Contains("--ntilde-terminfo-owner=" + runId, StringComparer.Ordinal)
            || !IsPrivateRoot(scratchRoot))
            throw new InvalidOperationException("Private clipboard QA requires matching explicit run markers and an existing private scratch root.");
        return new(runId);
    }

    private static bool IsPrivateRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return false;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        if (OperatingSystem.IsWindows()) return true; // Caller must independently verify ACL/process/PTY ownership.
        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;
    }

    /// <summary>Avalonia's built-in text controls otherwise bypass App clipboard resolution.</summary>
    internal IDisposable[] InstallControlGuards()
    {
        if (!IsEnabled) return [];
        return
        [
            TextBox.CopyingToClipboardEvent.AddClassHandler<TextBox>((_, e) => e.Handled = true),
            TextBox.CuttingToClipboardEvent.AddClassHandler<TextBox>((_, e) => e.Handled = true),
            TextBox.PastingFromClipboardEvent.AddClassHandler<TextBox>((_, e) => e.Handled = true),
            SelectableTextBlock.CopyingToClipboardEvent.AddClassHandler<SelectableTextBlock>((_, e) => e.Handled = true),
        ];
    }

    internal IClipboard? ResolveHostClipboard(Func<IClipboard?> provider) => IsEnabled ? null : provider();

    internal DisposableClipboardFixture? Begin(string runId, object owner, object sourceParser, Func<bool> isCurrent)
    {
        lock (_gate)
        {
            if (!IsEnabled || RunId != runId || _fixture != null || !isCurrent()) return null;
            _owner = owner;
            _fixture = new DisposableClipboardFixture(runId, sourceParser, isCurrent);
            return _fixture;
        }
    }

    internal DisposableClipboardFixture? For(object owner)
    {
        lock (_gate) return ReferenceEquals(owner, _owner) ? _fixture : null;
    }

    internal void Interrupt(object owner) => For(owner)?.Interrupt();
}

/// <summary>All App UI clipboard resolution shares the startup latch, including replacement windows/panes.</summary>
internal static class HostClipboardAccess
{
    internal static IClipboard? Get(TopLevel? topLevel)
        => PrivateClipboardQaMode.Process.ResolveHostClipboard(() => topLevel?.Clipboard);
}
