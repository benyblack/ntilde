using System;
using System.Collections.Generic;
using System.Linq;
using Ntilde.Settings.Pages;

namespace Ntilde.Settings;

/// <summary>A sidebar entry: which page, under which group, with what title and glyph, and how to build it.</summary>
public sealed record SettingsPageDescriptor(
    SettingsPage Page,
    string Group,
    string Title,
    string Glyph,
    Func<ISettingsPage> Create,
    bool MonoGlyph = false);

/// <summary>
/// The Settings pages in sidebar order. The sidebar, navigation and search are all driven from this
/// list, so a new page is one line here plus its control.
/// </summary>
public static class SettingsPageRegistry
{
    /// <summary>Where Settings opens when nothing asks for a page and none was visited in this run.</summary>
    public const SettingsPage DefaultPage = SettingsPage.Appearance;

    public static IReadOnlyList<SettingsPageDescriptor> Pages { get; } =
    [
        new(SettingsPage.Appearance, "INTERFACE", "Appearance", "◐", () => new AppearancePage()),
        new(SettingsPage.Profiles, "INTERFACE", "Shells & Profiles", ">_", () => new ProfilesPage(), MonoGlyph: true),
        new(SettingsPage.Shortcuts, "INTERFACE", "Shortcuts", "⌘", () => new ShortcutsPage()),
        new(SettingsPage.CommandAssist, "ASSISTANT", "Command Assist", "❯", () => new CommandAssistPage(), MonoGlyph: true),
        new(SettingsPage.AgentAccess, "ASSISTANT", "Agent Access", "◌", () => new AgentAccessPage()),
        new(SettingsPage.Ssh, "CONNECTION", "SSH", "⇄", () => new SshPage()),
        new(SettingsPage.Backup, "DATA", "Backup & Restore", "⇩", () => new BackupPage()),
    ];

    public static SettingsPageDescriptor Get(SettingsPage page) => Pages.Single(descriptor => descriptor.Page == page);
}

/// <summary>Row ids that code (not just XAML) refers to.</summary>
public static class SettingsRowIds
{
    /// <summary>The TITLE BAR section on Appearance; Customize Title Bar… opens here until PR 3 gives it a page.</summary>
    public const string TitleBar = "title-bar";
}
