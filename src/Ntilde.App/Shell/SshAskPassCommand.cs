using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.VT;

namespace Ntilde;

internal static class SshAskPassCommand
{
    internal const string ModeFlag = "--ssh-askpass";

    // The environment contract lives with the side that sets it (the exec transport, Phase 4 spec
    // §8.2); these names stay so the helper reads exactly what the transport writes.
    internal const string ModeEnvironmentVariable = SshAskPassEnvironment.ModeVariable;
    internal const string ProfileIdEnvironmentVariable = SshAskPassEnvironment.ProfileIdVariable;
    internal const string ProfileNameEnvironmentVariable = SshAskPassEnvironment.ProfileNameVariable;
    internal const string ProfileUserEnvironmentVariable = SshAskPassEnvironment.ProfileUserVariable;
    internal const string ProfileHostEnvironmentVariable = SshAskPassEnvironment.ProfileHostVariable;
    internal const string ProfilePortEnvironmentVariable = SshAskPassEnvironment.ProfilePortVariable;
    internal const string VaultOnlyEnvironmentVariable = SshAskPassEnvironment.VaultOnlyVariable;
    internal const string NoVaultEnvironmentVariable = SshAskPassEnvironment.NoVaultVariable;
    internal const string SessionEnvironmentVariable = SshAskPassEnvironment.SessionVariable;

    /// <summary>The app's own executable name, without its extension: the GUI answers askpass too (Program.cs).</summary>
    private const string AppExecutableName = "Ntilde";

    /// <summary>The CLI shim built next to the app (BuildCliShim), which answers askpass the same way.</summary>
    private const string CliExecutableName = "Ntilde.Cli";

    /// <summary>
    /// The askpass helper an OpenSSH exec channel names (Phase 4 spec §8.2), for this process: see
    /// <see cref="LocateHelper(string?, string, Func{string, bool})"/>.
    /// </summary>
    internal static string? LocateHelper() => LocateHelper(Environment.ProcessPath, AppContext.BaseDirectory, File.Exists);

    /// <summary>
    /// The running app itself when <paramref name="processPath"/> is the ntilde executable; otherwise (a
    /// dev build under a test host, say) <c>Ntilde.Cli</c> in <paramref name="baseDirectory"/> when it
    /// exists; otherwise null, and ssh cannot prompt.
    /// </summary>
    internal static string? LocateHelper(string? processPath, string baseDirectory, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrEmpty(processPath)
            && string.Equals(Path.GetFileNameWithoutExtension(processPath), AppExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        if (string.IsNullOrEmpty(baseDirectory)) return null;
        string cli = Path.Combine(baseDirectory, OperatingSystem.IsWindows() ? CliExecutableName + ".exe" : CliExecutableName);
        return fileExists(cli) ? cli : null;
    }

    public static bool IsSupportedCliMode(string[] args) => IsSupportedCliMode(args, Environment.GetEnvironmentVariable);

    /// <summary>Whether this run answers an ssh prompt: the <see cref="ModeFlag"/>, or <see cref="ModeEnvironmentVariable"/> set to 1.</summary>
    internal static bool IsSupportedCliMode(string[] args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        return Array.Exists(args, arg => string.Equals(arg, ModeFlag, StringComparison.Ordinal)) ||
               string.Equals(environment(ModeEnvironmentVariable), "1", StringComparison.Ordinal);
    }

    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr) =>
        Execute(
            args,
            stdout,
            stderr,
            Environment.GetEnvironmentVariable,
            static profile => new VaultService().GetSshPasswordForProfile(profile),
            AskUser,
            new SshAskPassSessionMarkers(static () => SshAskPassSessionMarkers.DefaultDirectory));

    /// <summary>
    /// Answers one ssh prompt: the target's own password (<see cref="IsTargetPasswordPrompt"/>) from
    /// <paramref name="savedPassword"/> when it has one; anything else from <paramref name="askUser"/> - unless the
    /// environment says vault-only (<see cref="VaultOnlyEnvironmentVariable"/>, an automatic reconnect): then every
    /// other prompt, and a target's password with nothing saved, exits 1 at once, with no UI built and, for a prompt
    /// that is not the target's password, the vault not even read. ssh treats that exit as no answer.
    /// <para>
    /// A user's attempt fills the target's password from the vault at most once per ssh process (its
    /// <see cref="SessionEnvironmentVariable"/> token, recorded in <paramref name="markers"/>): the same ssh asking again
    /// means the saved password was refused, so the user is asked instead of it being sent again. With
    /// <see cref="NoVaultEnvironmentVariable"/> (the host's saved password was refused before) the vault is not used at
    /// all. Either way the dialog's "Remember password" replaces the saved one.
    /// </para>
    /// </summary>
    /// <param name="environment">Reads an environment variable: the transport's askpass contract.</param>
    /// <param name="savedPassword">The profile's saved password (the vault), or null.</param>
    /// <param name="askUser">The dialog (<see cref="AskUser"/>): the user's answer, or null when cancelled. The seam tests replace.</param>
    /// <param name="markers">The record of the ssh processes already filled from the vault.</param>
    /// <returns>0 with the answer on <paramref name="stdout"/>; 1 when there is none; 2 when the helper failed.</returns>
    internal static int Execute(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        Func<string, string?> environment,
        Func<TerminalProfile, string?> savedPassword,
        Func<AskPassState, string?> askUser,
        SshAskPassSessionMarkers markers)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(savedPassword);
        ArgumentNullException.ThrowIfNull(askUser);
        ArgumentNullException.ThrowIfNull(markers);

        try
        {
            string prompt = GetPrompt(args);
            TerminalProfile profile = CreateProfileFromEnvironment(environment);
            bool vaultOnly = string.Equals(environment(VaultOnlyEnvironmentVariable), "1", StringComparison.Ordinal);

            if (IsTargetPasswordPrompt(prompt, profile) && FromVault(environment, savedPassword, markers, profile, vaultOnly, stderr) is { } vaultPassword)
            {
                stdout.WriteLine(vaultPassword);
                return 0;
            }

            if (vaultOnly)
            {
                // A prompt that names the target but asks for no password - a second factor after the saved password - is
                // recorded, so the app does not take the sign-in's failure for the saved password refused (review I-1).
                if (!IsTargetPasswordPrompt(prompt, profile) && NamesTarget(prompt, profile)
                    && environment(SessionEnvironmentVariable) is { } session && SshAskPassEnvironment.IsSessionToken(session))
                {
                    markers.RecordDeclined(session);
                }

                // Nobody is waiting (an automatic reconnect): no dialog, no window, no Avalonia app. Never the prompt
                // itself either, which a server's keyboard-interactive text is part of: ssh's stderr goes to the log.
                stderr.WriteLine("Ntilde SSH askpass: an automatic reconnect answers only the target's password, from the vault; no answer given.");
                return 1;
            }

            string? response = askUser(new AskPassState(prompt, profile));
            if (string.IsNullOrEmpty(response))
            {
                return 1;
            }

            stdout.WriteLine(response);
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Ntilde SSH askpass failed: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// The saved password to answer the target's password prompt with, or null for the dialog (or, vault-only, no answer).
    /// Vault-only answers every such prompt (ssh's <c>NumberOfPasswordPrompts=1</c> bounds it) and records the fill under
    /// the ssh's token, which the app reads back to know the saved password was asked for. Otherwise: never with
    /// <see cref="NoVaultEnvironmentVariable"/>; never twice for one ssh's <see cref="SessionEnvironmentVariable"/> (the
    /// claim is atomic), and not at all when the fill cannot be claimed - it could then be sent again. An ssh with no valid
    /// token gets it as before.
    /// </summary>
    private static string? FromVault(
        Func<string, string?> environment,
        Func<TerminalProfile, string?> savedPassword,
        SshAskPassSessionMarkers markers,
        TerminalProfile profile,
        bool vaultOnly,
        TextWriter stderr)
    {
        string? token = environment(SessionEnvironmentVariable);
        bool hasToken = SshAskPassEnvironment.IsSessionToken(token);
        if (vaultOnly)
        {
            string? fill = NonEmpty(savedPassword(profile));
            if (fill is not null && hasToken) markers.RecordAnswered(token!);
            return fill;
        }

        if (string.Equals(environment(NoVaultEnvironmentVariable), "1", StringComparison.Ordinal)) return null;
        if (!hasToken) return NonEmpty(savedPassword(profile));
        if (markers.HasAnswered(token!)) return null; // this ssh asks again: the saved password was refused

        if (NonEmpty(savedPassword(profile)) is not { } saved) return null;
        if (markers.TryClaim(token!)) return saved;

        stderr.WriteLine("Ntilde SSH askpass: the saved password's one use for this connection is taken or cannot be recorded; asking instead.");
        return null;

        static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }

    private static string GetPrompt(string[] args)
    {
        foreach (string arg in args)
        {
            if (!string.Equals(arg, ModeFlag, StringComparison.Ordinal))
            {
                return arg;
            }
        }

        return "SSH authentication required.";
    }

    private static TerminalProfile CreateProfileFromEnvironment(Func<string, string?> environment)
    {
        var profile = new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Name = environment(ProfileNameEnvironmentVariable) ?? string.Empty,
            SshUser = environment(ProfileUserEnvironmentVariable) ?? string.Empty,
            SshHost = environment(ProfileHostEnvironmentVariable) ?? string.Empty
        };

        if (Guid.TryParse(environment(ProfileIdEnvironmentVariable), out Guid profileId))
        {
            profile.Id = profileId;
        }

        if (int.TryParse(environment(ProfilePortEnvironmentVariable), out int port) && port > 0)
        {
            profile.SshPort = port;
        }

        return profile;
    }

    /// <summary>
    /// The dialog: an Avalonia app of its own, with one window, until the user answers or cancels. Null when cancelled.
    /// </summary>
    private static string? AskUser(AskPassState state)
    {
        BuildAskPassApp(state).StartWithClassicDesktopLifetime(
            Array.Empty<string>(),
            ShutdownMode.OnExplicitShutdown);
        return state.Response;
    }

    private static AppBuilder BuildAskPassApp(AskPassState state)
    {
        return AppBuilder.Configure(() => new AskPassApplication(state))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    private static bool IsPasswordPrompt(string prompt)
    {
        return prompt.Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="prompt"/> asks for the password of the profile's own target: the only
    /// prompt the profile's vault password may answer, or be remembered from.
    /// </summary>
    /// <remarks>
    /// Every ssh the transport starts inherits the askpass environment, including the <c>ssh -W</c>
    /// that ProxyJump runs for each hop. Answering any "password" prompt would hand the target's
    /// password to a jump host (and fail the jump's own auth), so the prompt must name the target,
    /// as OpenSSH writes it, anchored at the start where only ssh - never a server - writes:
    /// <list type="bullet">
    /// <item>password auth: <c>&lt;user&gt;@&lt;host&gt;'s password: </c>;</item>
    /// <item>keyboard-interactive (OpenSSH 8.4+): <c>(&lt;user&gt;@&lt;host&gt;) </c> then the server's
    /// own text, which must ask for a password.</item>
    /// </list>
    /// ssh writes the host as the config's HostName, lowercased, hence the case-insensitive match.
    /// A profile with no user (ssh then uses the local account) or no host never auto-fills.
    /// </remarks>
    internal static bool IsTargetPasswordPrompt(string prompt, TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(profile);

        string user = profile.SshUser?.Trim() ?? string.Empty;
        string host = profile.SshHost?.Trim() ?? string.Empty;
        if (user.Length == 0 || host.Length == 0)
        {
            return false;
        }

        string target = $"{user}@{host}";
        if (prompt.StartsWith(target + "'s password", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string keyboardInteractive = $"({target}) ";
        return prompt.StartsWith(keyboardInteractive, StringComparison.OrdinalIgnoreCase) &&
               prompt.AsSpan(keyboardInteractive.Length).Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="prompt"/> is the target's own - <c>(user@host) </c> or <c>user@host's </c> at its start, where
    /// only ssh writes - whatever it asks for (<see cref="IsTargetPasswordPrompt"/> narrows it to a password).
    /// </summary>
    internal static bool NamesTarget(string prompt, TerminalProfile profile)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(profile);

        string user = profile.SshUser?.Trim() ?? string.Empty;
        string host = profile.SshHost?.Trim() ?? string.Empty;
        if (user.Length == 0 || host.Length == 0)
        {
            return false;
        }

        string target = $"{user}@{host}";
        return prompt.StartsWith($"({target}) ", StringComparison.OrdinalIgnoreCase)
            || prompt.StartsWith(target + "'s ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSecretPrompt(string prompt)
    {
        return IsPasswordPrompt(prompt) ||
               prompt.Contains("passphrase", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The interface scale the user saved, read from settings.json the way the main app does.
    /// The helper is a separate process, so <see cref="UiScale.Current"/> starts at its default
    /// here; without this the authentication dialog stayed at 100% for a user on 150%. Any
    /// failure to read settings falls back to the default - authentication must never be blocked
    /// by a settings problem.
    /// </summary>
    internal static double ResolveSavedUiScale()
    {
        try
        {
            return UiScale.Clamp(TerminalSettings.Load().UiScale);
        }
        catch (Exception)
        {
            return UiScale.Default;
        }
    }

    internal sealed class AskPassState
    {
        public AskPassState(string prompt, TerminalProfile profile)
        {
            Prompt = string.IsNullOrWhiteSpace(prompt) ? "SSH authentication required." : prompt;
            Profile = profile;
        }

        public string Prompt { get; }
        public TerminalProfile Profile { get; }
        public string? Response { get; set; }
    }

    internal sealed class AskPassApplication : Application
    {
        private readonly AskPassState _state;

        public AskPassApplication(AskPassState state)
        {
            _state = state;
        }

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            // Same Window theme as App.axaml, so the interface scale reaches this process too.
            UiScale.InstallWindowTheme(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Before the window exists: FitWindow in its constructor reads UiScale.Current.
                UiScale.Apply(ResolveSavedUiScale());
                var window = new AskPassWindow(_state, () => desktop.Shutdown());
                desktop.MainWindow = window;
                window.Show();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }

    internal sealed class AskPassWindow : Window
    {
        private readonly AskPassState _state;
        private readonly Action _shutdown;
        private readonly TextBox _input;
        private readonly CheckBox _rememberPassword;

        public AskPassWindow(AskPassState state, Action shutdown)
        {
            _state = state;
            _shutdown = shutdown;

            Title = "Ntilde SSH Authentication";
            Width = 520;
            Height = 240;
            UiScale.FitWindow(this);
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _input = new TextBox
            {
                PasswordChar = IsSecretPrompt(_state.Prompt) ? '*' : default(char),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _rememberPassword = new CheckBox
            {
                Content = "Remember password",
                // Only the target's password is the profile's: a jump host's must not be stored as it.
                IsVisible = IsTargetPasswordPrompt(_state.Prompt, _state.Profile) && _state.Profile.Id != Guid.Empty
            };

            Content = BuildContent();
        }

        private Control BuildContent()
        {
            var cancelButton = new Button { Content = "Cancel", Width = 96 };
            cancelButton.Click += (_, _) => CloseWith(null);

            var submitButton = new Button { Content = "Submit", Width = 96 };
            submitButton.Click += (_, _) => CloseWith(_input.Text);

            return new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "SSH authentication",
                            FontSize = 18,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold
                        },
                        new TextBlock
                        {
                            Text = _state.Prompt,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        },
                        _input,
                        _rememberPassword,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, submitButton }
                        }
                    }
                }
            };
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _input.Focus();
        }

        private void CloseWith(string? response)
        {
            _state.Response = response;
            if (!string.IsNullOrEmpty(response) && _rememberPassword.IsChecked == true)
            {
                new VaultService().SetSshPasswordForProfile(_state.Profile, response);
            }

            Close();
            Dispatcher.UIThread.Post(_shutdown);
        }
    }
}
