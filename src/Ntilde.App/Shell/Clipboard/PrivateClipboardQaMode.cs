using Avalonia.Controls;
using Avalonia.Input.Platform;
using Ntilde.AgentHost.Contracts;

namespace Ntilde.Shell.Clipboard;

/// <summary>Explicit startup-only, process-wide fail-closed native clipboard policy. Metadata is not PTY attestation.</summary>
internal sealed class PrivateClipboardQaMode
{
    internal const string Flag = "--ntilde-terminfo-private-clipboard=";
    internal static PrivateClipboardQaMode Process { get; } = FromStartup(
        Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable("TERMINFO_RUN_ID"),
        Environment.GetEnvironmentVariable("NTILDE_APPDATA_ROOT"));
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

    internal DisposableClipboardFixture? Begin(string runId, object owner, Func<bool> isCurrent)
    {
        lock (_gate)
        {
            if (!IsEnabled || RunId != runId || _fixture != null || !isCurrent()) return null;
            _owner = owner;
            _fixture = new DisposableClipboardFixture(runId, isCurrent);
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
