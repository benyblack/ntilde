using Avalonia;
using Avalonia.Controls;

namespace Ntilde.Settings;

/// <summary>
/// A section of a Settings page: a header and its rows, with the same space before the next section
/// on every page. The header is written as it reads ("Background image"), so search can show it as
/// is, and the template shows it in capitals. The rows are its items, so search reads a row's group from its
/// logical parent. A null header shows no title, for a page with a single section.
/// </summary>
public class SettingGroup : ItemsControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingGroup, string?>(nameof(Header));

    public string? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
}
