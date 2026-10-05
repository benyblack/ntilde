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

    public static bool IsSupportedCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Array.Exists(args, arg => string.Equals(arg, ModeFlag, StringComparison.Ordinal)) ||
               string.Equals(Environment.GetEnvironmentVariable(ModeEnvironmentVariable), "1", StringComparison.Ordinal);
    }

    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            string prompt = GetPrompt(args);
            TerminalProfile profile = CreateProfileFromEnvironment();

            if (IsTargetPasswordPrompt(prompt, profile))
            {
                string? vaultPassword = new VaultService().GetSshPasswordForProfile(profile);
                if (!string.IsNullOrEmpty(vaultPassword))
                {
                    stdout.WriteLine(vaultPassword);
                    return 0;
                }
            }

            var state = new AskPassState(prompt, profile);
            BuildAskPassApp(state).StartWithClassicDesktopLifetime(
                Array.Empty<string>(),
                ShutdownMode.OnExplicitShutdown);

            if (string.IsNullOrEmpty(state.Response))
            {
                return 1;
            }

            stdout.WriteLine(state.Response);
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"Ntilde SSH askpass failed: {ex.Message}");
            return 2;
        }
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

    private static TerminalProfile CreateProfileFromEnvironment()
    {
        var profile = new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Name = Environment.GetEnvironmentVariable(ProfileNameEnvironmentVariable) ?? string.Empty,
            SshUser = Environment.GetEnvironmentVariable(ProfileUserEnvironmentVariable) ?? string.Empty,
            SshHost = Environment.GetEnvironmentVariable(ProfileHostEnvironmentVariable) ?? string.Empty
        };

        if (Guid.TryParse(Environment.GetEnvironmentVariable(ProfileIdEnvironmentVariable), out Guid profileId))
        {
            profile.Id = profileId;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable(ProfilePortEnvironmentVariable), out int port) && port > 0)
        {
            profile.SshPort = port;
        }

        return profile;
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
