using System;
using System.Collections.Generic;
using System.IO;
using Ntilde.CommandAssist.ShellIntegration.Contracts;

namespace Ntilde.CommandAssist.ShellIntegration.Zsh;

public sealed class ZshShellIntegrationProvider : IShellIntegrationProvider
{
    private readonly Func<string> _bootstrapDirectory;

    /// <param name="bootstrapDirectory">
    /// Resolves the directory the generated bootstrap script is written to. Supplied by the App
    /// (<c>() =&gt; AppPaths.CommandAssistDirectory</c>). Deliberately a factory, not a string: the
    /// path is resolved per <see cref="CreateLaunchPlan"/> call so it tracks app-state changes and
    /// so a resolution failure surfaces inside the caller's try/catch rather than at construction
    /// time.
    /// </param>
    /// <param name="getEnvironmentVariable">
    /// Reads the environment the shell will inherit; defaults to the process environment. A seam
    /// so the user's-own-ZDOTDIR handling is testable without mutating process state.
    /// </param>
    public ZshShellIntegrationProvider(Func<string> bootstrapDirectory, Func<string, string?>? getEnvironmentVariable = null)
    {
        _bootstrapDirectory = bootstrapDirectory;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    private readonly Func<string, string?> _getEnvironmentVariable;

    public bool CanIntegrate(string? shellKind, string? shellCommand)
    {
        if (string.Equals(shellKind, "zsh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string command = shellCommand ?? string.Empty;
        return command.Contains("zsh", StringComparison.OrdinalIgnoreCase);
    }

    public ShellIntegrationLaunchPlan CreateLaunchPlan(string shellCommand, string? shellArguments, string? workingDirectory)
    {
        if (HasIncompatibleStartupMode(shellArguments))
        {
            return new ShellIntegrationLaunchPlan(
                IsIntegrated: false,
                ShellCommand: shellCommand,
                ShellArguments: shellArguments,
                BootstrapScriptPath: null);
        }

        string bootstrapScriptPath = ZshBootstrapBuilder.WriteScript(_bootstrapDirectory());
        string? zdotdir = Path.GetDirectoryName(bootstrapScriptPath);
        if (string.IsNullOrEmpty(zdotdir))
        {
            return new ShellIntegrationLaunchPlan(
                IsIntegrated: false,
                ShellCommand: shellCommand,
                ShellArguments: shellArguments,
                BootstrapScriptPath: null);
        }

        var envOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ZDOTDIR"] = zdotdir
        };

        // Our ZDOTDIR replaces the user's, so their own (an XDG setup's ~/.config/zsh) rides
        // along for the shims to restore. Our own directory is not the user's: that is an
        // Ntilde launched from inside an Ntilde pane mid-startup, and passing it would make the
        // shims source themselves.
        string? userZdotdir = _getEnvironmentVariable("ZDOTDIR");
        if (userZdotdir is not null &&
            !string.Equals(userZdotdir.TrimEnd('/'), zdotdir.TrimEnd('/'), StringComparison.Ordinal))
        {
            envOverrides[ZshBootstrapBuilder.UserZdotdirVariable] = userZdotdir;
        }

        return new ShellIntegrationLaunchPlan(
            IsIntegrated: true,
            ShellCommand: shellCommand,
            ShellArguments: shellArguments,
            BootstrapScriptPath: bootstrapScriptPath,
            EnvironmentOverrides: envOverrides);
    }

    private static bool HasIncompatibleStartupMode(string? shellArguments)
    {
        if (string.IsNullOrWhiteSpace(shellArguments))
        {
            return false;
        }

        foreach (string token in shellArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // -c runs zsh in non-interactive mode; --no-rcs / -f skip startup
            // files, defeating the bootstrap. Either is incompatible with
            // automatic shell integration injection.
            if (token == "-c" || token == "--no-rcs" || token == "-f")
            {
                return true;
            }
        }

        return false;
    }
}
