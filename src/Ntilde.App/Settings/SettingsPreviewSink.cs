using System;
using Ntilde.Shell;
using Ntilde.VT;

namespace Ntilde.Settings;

/// <summary>
/// Where pages send live previews. The Settings window re-raises them as its own public events
/// (OnOpacityChanged and the rest), which MainWindow already subscribes to, and repaints itself
/// for <see cref="RepaintWindow"/>.
/// </summary>
public sealed class SettingsPreviewSink
{
    public event Action<double>? OpacityChanged;

    public event Action<string>? BlurChanged;

    public event Action<string, double, string>? BackgroundImageChanged;

    public event Action<string>? FontChanged;

    public event Action<double>? FontSizeChanged;

    public event Action<double>? UiScaleChanged;

    public event Action<string>? ThemeChanged;

    public event Action<TerminalTheme?>? RepaintRequested;

    public void Opacity(double value) => OpacityChanged?.Invoke(value);

    public void Blur(string effect) => BlurChanged?.Invoke(effect);

    public void BackgroundImage(string path, double opacity, string stretch) => BackgroundImageChanged?.Invoke(path, opacity, stretch);

    public void Font(string family) => FontChanged?.Invoke(family);

    public void FontSize(double size) => FontSizeChanged?.Invoke(size);

    /// <summary>The value is already clamped.</summary>
    public void UiScale(double scale) => UiScaleChanged?.Invoke(scale);

    public void Theme(string name) => ThemeChanged?.Invoke(name);

    /// <summary>Repaints the Settings window's own palette with <paramref name="theme"/>, or the active theme when null.</summary>
    public void RepaintWindow(TerminalTheme? theme) => RepaintRequested?.Invoke(theme);
}
