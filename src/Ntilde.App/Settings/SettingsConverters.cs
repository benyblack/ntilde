using Avalonia.Data.Converters;

namespace Ntilde.Settings;

/// <summary>Value converters for the Settings styles.</summary>
public static class SettingsConverters
{
    /// <summary>The text in capitals (invariant culture): a group header is written as it reads and shown upper-case.</summary>
    public static readonly IValueConverter UpperCase = new FuncValueConverter<string?, string?>(text => text?.ToUpperInvariant());
}
