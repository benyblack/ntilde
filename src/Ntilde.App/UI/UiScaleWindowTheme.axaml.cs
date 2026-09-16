using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ntilde.UI;

/// <summary>
/// Code-behind for <c>UiScaleWindowTheme.axaml</c>: the interface-scale transform resource and
/// the Window control theme that applies it. A class rather than a <c>ResourceInclude</c> URI so
/// that a second <see cref="Avalonia.Application"/> (the askpass helper) can merge it from C#
/// through compiled XAML - <c>ResourceInclude(Uri)</c> goes through the runtime loader and trips
/// IL2026, which the NativeAOT release build treats as an error.
/// </summary>
public partial class UiScaleWindowTheme : ResourceDictionary
{
    public UiScaleWindowTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
