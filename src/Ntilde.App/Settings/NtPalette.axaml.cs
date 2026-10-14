using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ntilde.Settings;

/// <summary>Code-behind for <c>NtPalette.axaml</c>. Each instance loads its own brushes.</summary>
public partial class NtPalette : ResourceDictionary
{
    public NtPalette()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
