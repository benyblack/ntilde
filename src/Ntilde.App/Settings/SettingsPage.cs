namespace Ntilde.Settings;

/// <summary>
/// One page of the Settings window. Declaration order means nothing: the sidebar order, groups,
/// titles and glyphs come from <see cref="SettingsPageRegistry"/>.
/// </summary>
public enum SettingsPage
{
    General,
    Appearance,
    Terminal,
    TabsAndTitleBar,
    Profiles,
    Shortcuts,
    CommandAssist,
    AgentAccess,
    Ssh,
    Backup,
}

/// <summary>
/// Where Settings opens: a page, and optionally the row on it to scroll to and highlight (see
/// <see cref="SettingsNavigation.RowIdProperty"/>). The window's only "where to open" input.
/// </summary>
public sealed record SettingsTarget(SettingsPage Page, string? RowId = null);
