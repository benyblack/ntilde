using Avalonia.Controls;
using Ntilde.Shell;

namespace Ntilde.Settings;

/// <summary>
/// The contract between the Settings frame and one page. A page is created once per window,
/// stays in it for the window's whole life, and is loaded again in place after a backup import or
/// restore replaced configuration on disk.
/// </summary>
public interface ISettingsPage
{
    SettingsPage Page { get; }

    /// <summary>The page's root control, hosted by the frame.</summary>
    Control View { get; }

    /// <summary>Fills the controls from <paramref name="session"/>. Runs at open and after an external reload.</summary>
    void Load(SettingsSession session);

    /// <summary>
    /// Checks the page before Save writes anything. Null means valid. A page that refuses shows its
    /// own message inline; the frame then navigates to the error's page and row.
    /// </summary>
    SettingsValidationError? Validate();

    /// <summary>Writes the page's controls into <paramref name="target"/>. Runs only on Save, after every page validated.</summary>
    void Collect(TerminalSettings target);
}

/// <summary>Why Save was refused, and where to take the user to fix it.</summary>
public sealed record SettingsValidationError(SettingsPage Page, string? RowId, string Message);
