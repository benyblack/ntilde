using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;

namespace Ntilde.Tests.Core;

public sealed class SettingsSaveFailureTests
{
    [AvaloniaFact]
    public void Failed_save_keeps_the_editor_and_drafts_open_and_announces_the_error()
    {
        using var root = SettingsTestRoot.WithSettings("{}");
        var window = new Ntilde.SettingsWindow
        {
            PersistSettings = _ => (false, "Unlock credential storage and retry. Settings were not saved.")
        };
        try
        {
            window.Show();
            window.Session.ShortcutDraft["settings"] = "Ctrl+Alt+F12";
            window.FindControl<Button>("BtnSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsVisible);
            Assert.Equal("Ctrl+Alt+F12", window.Session.ShortcutDraft["settings"]);
            Assert.Contains("were not saved", window.FindControl<TextBlock>("SettingsSaveStatus")!.Text, StringComparison.Ordinal);
            using var unchanged = root.ReadSettings();
            Assert.Equal("{}", unchanged.RootElement.GetRawText());
        }
        finally { window.Close(); }
    }
}
