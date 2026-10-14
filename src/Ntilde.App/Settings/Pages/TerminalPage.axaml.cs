using System;
using System.Linq;
using Avalonia.Controls;
using Ntilde.Platform;
using Ntilde.Shell;

namespace Ntilde.Settings.Pages;

/// <summary>Terminal: font, text rendering and scrollback.</summary>
public partial class TerminalPage : UserControl, ISettingsPage
{
    private SettingsSession? _session;

    public TerminalPage()
    {
        InitializeComponent();

        var fontList = this.FindControl<ComboBox>("FontList")!;
        fontList.SelectionChanged += (_, _) =>
        {
            if (_session != null && fontList.SelectedItem is ComboBoxItem { Content: { } content })
            {
                _session.Preview.Font(content.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily);
            }
        };

        var fontSize = this.FindControl<NumericUpDown>("FontSizeInput")!;
        fontSize.ValueChanged += (_, _) =>
        {
            if (_session != null && fontSize.Value.HasValue)
            {
                _session.Preview.FontSize((double)fontSize.Value.Value);
            }
        };
    }

    public SettingsPage Page => SettingsPage.Terminal;

    public Control View => this;

    public void Load(SettingsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        TerminalSettings settings = session.Settings;

        var fontList = this.FindControl<ComboBox>("FontList")!;
        fontList.Items.Clear();
        foreach (string family in session.FontChoices)
        {
            fontList.Items.Add(new ComboBoxItem { Content = family });
        }

        if (fontList.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Content?.ToString() == settings.FontFamily) is { } match)
        {
            fontList.SelectedItem = match;
        }

        this.FindControl<NumericUpDown>("FontSizeInput")!.Value = (decimal)settings.FontSize;
        Toggle("LigatureToggle").IsChecked = settings.EnableLigatures;
        Toggle("ComplexShapingToggle").IsChecked = settings.EnableComplexShaping;
        this.FindControl<NumericUpDown>("ScrollbackInput")!.Value = settings.MaxHistory;
    }

    public SettingsValidationError? Validate() => null;

    public void Collect(TerminalSettings target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.FontSize = (double)(this.FindControl<NumericUpDown>("FontSizeInput")!.Value ?? 14);
        target.MaxHistory = (int)(this.FindControl<NumericUpDown>("ScrollbackInput")!.Value ?? 10000);
        target.EnableLigatures = Toggle("LigatureToggle").IsChecked == true;
        target.EnableComplexShaping = Toggle("ComplexShapingToggle").IsChecked == true;
        if (this.FindControl<ComboBox>("FontList")!.SelectedItem is ComboBoxItem fontItem)
        {
            target.FontFamily = fontItem.Content?.ToString() ?? BundledFontCatalog.DefaultTerminalFontFamily;
        }
    }

    private CheckBox Toggle(string name) => this.FindControl<CheckBox>(name)!;
}
