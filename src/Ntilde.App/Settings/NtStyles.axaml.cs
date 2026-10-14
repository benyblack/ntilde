using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Ntilde.Settings;

/// <summary>Code-behind for <c>NtStyles.axaml</c>, the shared Settings styles.</summary>
public partial class NtStyles : Styles
{
    public NtStyles()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
