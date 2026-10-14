using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Ntilde.Settings;

/// <summary>
/// One setting. The title and a short description sit on the left, with the control on the right
/// (or below, when <see cref="Stacked"/>) and a hairline underneath. Longer explanation goes in
/// <see cref="Details"/>, behind a More / Less button.
/// </summary>
/// <remarks>
/// Search indexes Title, Description and Keywords; deep links and search jump to RowId. The
/// template lives in NtStyles.axaml.
/// </remarks>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    /// <summary>Text shown only after "More". At most two lines belong in <see cref="Description"/>.</summary>
    public static readonly StyledProperty<string?> DetailsProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Details));

    /// <summary>Extra words search should match (synonyms, jargon). Never shown.</summary>
    public static readonly StyledProperty<string?> KeywordsProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Keywords));

    /// <summary>Feedback from the row's action ("Snippet saved."), shown under the description when set.</summary>
    public static readonly StyledProperty<string?> StatusProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Status));

    /// <summary>A sub-option of the row above it.</summary>
    public static readonly StyledProperty<bool> IndentProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(Indent));

    /// <summary>The control goes under the text rather than beside it, for wide controls.</summary>
    public static readonly StyledProperty<bool> StackedProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(Stacked));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(IsExpanded));

    private Button? _moreToggle;

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string? Description { get => GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    public string? Details { get => GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }

    public string? Keywords { get => GetValue(KeywordsProperty); set => SetValue(KeywordsProperty, value); }

    public string? Status { get => GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public bool Indent { get => GetValue(IndentProperty); set => SetValue(IndentProperty, value); }

    public bool Stacked { get => GetValue(StackedProperty); set => SetValue(StackedProperty, value); }

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>The id deep links and search use: the same attached property any control can carry.</summary>
    public string? RowId
    {
        get => SettingsNavigation.GetRowId(this);
        set => SettingsNavigation.SetRowId(this, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_moreToggle != null)
        {
            _moreToggle.Click -= OnMoreClick;
        }

        _moreToggle = e.NameScope.Find<Button>("PART_MoreToggle");
        if (_moreToggle != null)
        {
            _moreToggle.Click += OnMoreClick;
        }

        UpdateMoreToggle();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty || change.Property == TitleProperty)
        {
            UpdateMoreToggle();
        }
    }

    private void OnMoreClick(object? sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;

    private void UpdateMoreToggle()
    {
        if (_moreToggle is null)
        {
            return;
        }

        string verb = IsExpanded ? "Less" : "More";
        _moreToggle.Content = verb;
        AutomationProperties.SetName(_moreToggle, $"{verb} about {Title}");
    }
}
