using Ntilde.Shell;
using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Platform.Ssh.Models;
using Ntilde.ViewModels.Ssh;

namespace Ntilde.Views.Ssh;

public partial class NewSshConnectionView : Window
{
    /// <summary>The "listen -> destination" half of a forward row; the kind is shown as its own badge.</summary>
    public static readonly IValueConverter ForwardEndpoints =
        new FuncValueConverter<PortForward?, string>(forward => forward?.DescribeEndpoints() ?? string.Empty);

    /// <summary>Accessible name for a row's Remove button, so each one says which forward it removes.</summary>
    public static readonly IValueConverter RemoveForwardName =
        new FuncValueConverter<PortForward?, string>(forward => forward == null ? "Remove forward" : $"Remove forward {forward}");

    /// <summary>Whether the selected kind has a destination (everything except Dynamic/SOCKS).</summary>
    public static readonly IValueConverter KindHasDestination =
        new FuncValueConverter<object?, bool>(kind => KindOrDefault(kind) != PortForwardKind.Dynamic);

    /// <summary>What the selected kind does, and which side resolves each address.</summary>
    public static readonly IValueConverter KindHint = new FuncValueConverter<object?, string>(kind => KindOrDefault(kind) switch
    {
        PortForwardKind.Remote =>
            "Remote (ssh -R): opens the port on the server and tunnels connections back to the destination, as reached from this computer.",
        PortForwardKind.Dynamic =>
            "Dynamic (ssh -D): runs a SOCKS proxy on this computer that sends traffic out through the server.",
        _ =>
            "Local (ssh -L): opens the port on this computer and tunnels connections to the destination, as reached from the server.",
    });

    /// <summary>The listen row's label: a Remote forward listens on the server.</summary>
    public static readonly IValueConverter KindListenLabel =
        new FuncValueConverter<object?, string>(kind => KindOrDefault(kind) == PortForwardKind.Remote ? "Listen on server" : "Listen on");

    private const int DefaultTunnelPort = 8080;
    private const int DefaultSocksPort = 1080;

    // True once the user has changed the listen port themselves; from then on switching the kind
    // leaves it alone instead of swapping between the 8080/1080 defaults.
    private bool _sourcePortEdited;
    private bool _settingSourcePortDefault;

    private static PortForwardKind KindOrDefault(object? kind) => kind as PortForwardKind? ?? PortForwardKind.Local;

    public NewSshConnectionView()
    {
        InitializeComponent();
        Ntilde.Shell.UiScale.FitWindow(this);
        ConfigureAuthModeCombo();
        ConfigureBackendKindCombo();
        ConfigureRemoteShellKindCombo();
        ConfigureForwardKindCombo();
    }

    public NewSshConnectionView(NewSshConnectionViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private NewSshConnectionViewModel? ViewModel => DataContext as NewSshConnectionViewModel;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ConfigureAuthModeCombo()
    {
        var combo = this.FindControl<ComboBox>("AuthModeCombo");
        if (combo != null)
        {
            combo.ItemsSource = Enum.GetValues<NewSshAuthMode>();
        }
    }

    private void ConfigureBackendKindCombo()
    {
        var combo = this.FindControl<ComboBox>("BackendKindCombo");
        if (combo != null)
        {
            combo.ItemsSource = Enum.GetValues<SshBackendKind>();
        }
    }

    private void ConfigureRemoteShellKindCombo()
    {
        var combo = this.FindControl<ComboBox>("RemoteShellKindCombo");
        if (combo != null)
        {
            combo.ItemsSource = Enum.GetValues<RemoteShellKind>();
        }
    }

    private void ConfigureForwardKindCombo()
    {
        var combo = this.FindControl<ComboBox>("ForwardKindCombo");
        if (combo != null)
        {
            combo.ItemsSource = Enum.GetValues<PortForwardKind>();
            combo.SelectedItem = PortForwardKind.Local;
        }

        SetSourcePortDefault(DefaultTunnelPort);

        var destPortInput = this.FindControl<NumericUpDown>("ForwardDestPortInput");
        if (destPortInput != null)
        {
            destPortInput.Value = 80;
        }
    }

    private async void OnBrowseIdentityFileClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select SSH Identity File",
                AllowMultiple = false
            });

        if (files.Count > 0)
        {
            ViewModel.IdentityFilePath = files[0].Path.LocalPath;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        CompleteSave(connectAfterSave: false);
    }

    private void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        CompleteSave(connectAfterSave: true);
    }

    private void OnAddJumpHostClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var hostInput = this.FindControl<TextBox>("JumpHostInput");
        var userInput = this.FindControl<TextBox>("JumpUserInput");
        var portInput = this.FindControl<NumericUpDown>("JumpPortInput");
        if (hostInput == null || portInput == null)
        {
            return;
        }

        string host = hostInput.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(host))
        {
            ViewModel.ValidationError = "Jump host requires a host value.";
            return;
        }

        int port = portInput.Value.HasValue ? (int)portInput.Value.Value : 22;
        ViewModel.JumpHops.Add(new SshJumpHop
        {
            Host = host,
            User = userInput?.Text?.Trim() ?? string.Empty,
            Port = port > 0 ? port : 22
        });

        ViewModel.ValidationError = string.Empty;
        hostInput.Text = string.Empty;
        if (userInput != null)
        {
            userInput.Text = string.Empty;
        }
        portInput.Value = 22;
    }

    private void OnRemoveJumpHostClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var list = this.FindControl<ListBox>("JumpHostsList");
        if (list?.SelectedItem is not SshJumpHop selected)
        {
            return;
        }

        ViewModel.JumpHops.Remove(selected);
    }

    private void OnMoveJumpUpClick(object? sender, RoutedEventArgs e)
    {
        MoveSelectedJumpHop(-1);
    }

    private void OnMoveJumpDownClick(object? sender, RoutedEventArgs e)
    {
        MoveSelectedJumpHop(1);
    }

    private void MoveSelectedJumpHop(int offset)
    {
        if (ViewModel == null)
        {
            return;
        }

        var list = this.FindControl<ListBox>("JumpHostsList");
        if (list?.SelectedItem is not SshJumpHop selected)
        {
            return;
        }

        int currentIndex = ViewModel.JumpHops.IndexOf(selected);
        if (currentIndex < 0)
        {
            return;
        }

        int newIndex = currentIndex + offset;
        if (newIndex < 0 || newIndex >= ViewModel.JumpHops.Count)
        {
            return;
        }

        ViewModel.JumpHops.Move(currentIndex, newIndex);
        list.SelectedItem = selected;
    }

    private void OnForwardKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The hint, label and destination row are bound to the combo in XAML; only the default
        // port is imperative, because it must not overwrite a port the user chose.
        if (_sourcePortEdited)
        {
            return;
        }

        var kind = KindOrDefault((sender as ComboBox)?.SelectedItem);
        SetSourcePortDefault(kind == PortForwardKind.Dynamic ? DefaultSocksPort : DefaultTunnelPort);
    }

    private void SetSourcePortDefault(int port)
    {
        if (this.FindControl<NumericUpDown>("ForwardSourcePortInput") is not { } sourcePort)
        {
            return;
        }

        _settingSourcePortDefault = true;
        try
        {
            sourcePort.Value = port;
        }
        finally
        {
            _settingSourcePortDefault = false;
        }
    }

    private void OnForwardSourcePortChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_settingSourcePortDefault)
        {
            _sourcePortEdited = true;
        }
    }

    private void OnForwardEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.Source is not ComboBox)
        {
            OnAddForwardClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnAddForwardClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var kindCombo = this.FindControl<ComboBox>("ForwardKindCombo");
        var bindInput = this.FindControl<TextBox>("ForwardBindInput");
        var sourcePortInput = this.FindControl<NumericUpDown>("ForwardSourcePortInput");
        var destHostInput = this.FindControl<TextBox>("ForwardDestHostInput");
        var destPortInput = this.FindControl<NumericUpDown>("ForwardDestPortInput");

        if (kindCombo?.SelectedItem is not PortForwardKind kind || sourcePortInput == null)
        {
            return;
        }

        if (!sourcePortInput.Value.HasValue)
        {
            ViewModel.ValidationError = "Forward listen port must be between 1 and 65535.";
            return;
        }

        int sourcePort = (int)sourcePortInput.Value.Value;
        int destinationPort = destPortInput?.Value.HasValue == true ? (int)destPortInput.Value.Value : 0;
        string destinationHost = destHostInput?.Text?.Trim() ?? string.Empty;
        if (kind == PortForwardKind.Dynamic)
        {
            destinationHost = string.Empty;
            destinationPort = 0;
        }
        else if (string.IsNullOrWhiteSpace(destinationHost))
        {
            // The placeholder reads "localhost"; an empty box means exactly that.
            destinationHost = "localhost";
        }

        if (sourcePort <= 0)
        {
            ViewModel.ValidationError = "Forward listen port must be between 1 and 65535.";
            return;
        }

        if (kind != PortForwardKind.Dynamic && destinationPort <= 0)
        {
            ViewModel.ValidationError = "Forward destination port must be between 1 and 65535.";
            return;
        }

        ViewModel.Forwards.Add(new PortForward
        {
            Kind = kind,
            BindAddress = bindInput?.Text?.Trim() ?? string.Empty,
            SourcePort = sourcePort,
            DestinationHost = destinationHost,
            DestinationPort = destinationPort
        });

        ViewModel.ValidationError = string.Empty;
        if (bindInput != null)
        {
            bindInput.Text = string.Empty;
        }
        if (destHostInput != null)
        {
            destHostInput.Text = string.Empty;
        }
        if (destPortInput != null)
        {
            destPortInput.Value = 80;
        }
    }

    private void OnRemoveForwardClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        // Each row carries its own Remove button; the row's PortForward is its DataContext.
        if ((sender as Control)?.DataContext is not PortForward forward)
        {
            return;
        }

        ViewModel.Forwards.Remove(forward);
    }

    private void CompleteSave(bool connectAfterSave)
    {
        if (ViewModel == null)
        {
            Close(false);
            return;
        }

        ViewModel.ConnectAfterSave = connectAfterSave;
        if (!ViewModel.Validate())
        {
            return;
        }

        Close(true);
    }
}
