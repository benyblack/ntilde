using System.Collections.Generic;

namespace Ntilde.Shell.Theming;

/// <summary>
/// Fluent control-level resource keys re-pointed at theme tokens. Fluent 12.0.4 builds its stock
/// brushes with {StaticResource SystemXColor}, so a palette change after first use never reaches
/// them. Its control themes, though, look up these control-level keys with {DynamicResource}, and
/// a key in Application.Resources wins that lookup. ThemeService.ApplyTo aliases each key to the
/// token's brush INSTANCE, so one in-place recolor updates the token and every stock control using
/// it. Stock brushes not listed here keep the palette of the theme active when they were first used:
/// correct at startup, stale after a same-variant switch until restart.
///
/// Every key below was verified against the Fluent 12.0.4 control-theme sources
/// (github.com/AvaloniaUI/Avalonia, tag release/12.0.4, src/Avalonia.Themes.Fluent/Controls/*.xaml)
/// as an actual {DynamicResource X} lookup in that control's ControlTheme - not a name that merely
/// exists as a string in the assembly. Three coverage-table rows are intentionally not (fully)
/// covered; see task-4-report.md, "Round: user decision" and "Fix round 1" for the detail:
/// - ProgressBar's Foreground and ListBoxItem's Selected states need no entry: their brushes
///   (SystemControlHighlightAccentBrush, SystemControlHighlightListAccent{Low,Medium,High}Brush) are
///   built with Color="{DynamicResource SystemAccentColor}", so they already follow the live accent
///   through FluentPaletteMapping's Accent reset.
/// - ListBoxItem's hover/pressed/foreground states are covered by the "Shared ListBoxItem keys"
///   group below, not by a ListBoxItem-scoped key (Fluent 12.0.4 has none).
/// - Separator (SystemControlForegroundBaseMediumLowBrush) stays palette-at-first-use: that brush is
///   the theme foreground at a fixed 0x66 opacity, and no token equals that exact color, so aliasing
///   it would silently change what every OTHER consumer of that shared key looks like at startup.
/// </summary>
internal static class FluentControlOverrides
{
    public static readonly IReadOnlyList<(string FluentKey, string TokenKey)> Map =
    [
        // FlyoutPresenter / MenuFlyoutPresenter (ContextMenu, MenuFlyout, plain Flyout)
        (FluentKey: "MenuFlyoutPresenterBackground", TokenKey: ThemeKeys.Overlay),
        (FluentKey: "MenuFlyoutPresenterBorderBrush", TokenKey: ThemeKeys.HairlineStrong),
        (FluentKey: "FlyoutPresenterBackground", TokenKey: ThemeKeys.Overlay),
        (FluentKey: "FlyoutBorderThemeBrush", TokenKey: ThemeKeys.HairlineStrong),

        // MenuItem (Fluent's own ControlTheme for MenuItem reads the MenuFlyoutItem* /
        // MenuFlyoutSubItemChevron* key families - there is no separate "MenuItem*" brush family)
        (FluentKey: "MenuFlyoutItemBackgroundPointerOver", TokenKey: ThemeKeys.HoverFill),
        (FluentKey: "MenuFlyoutItemBackgroundPressed", TokenKey: ThemeKeys.PressedFill),
        (FluentKey: "MenuFlyoutItemForeground", TokenKey: ThemeKeys.Fg),
        (FluentKey: "MenuFlyoutItemForegroundPointerOver", TokenKey: ThemeKeys.Fg),
        (FluentKey: "MenuFlyoutItemForegroundPressed", TokenKey: ThemeKeys.Fg),
        (FluentKey: "MenuFlyoutItemForegroundDisabled", TokenKey: ThemeKeys.Fg4),
        (FluentKey: "MenuFlyoutItemKeyboardAcceleratorTextForeground", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "MenuFlyoutSubItemChevron", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "MenuFlyoutSubItemChevronPointerOver", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "MenuFlyoutSubItemChevronPressed", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "MenuFlyoutSubItemChevronDisabled", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "MenuFlyoutSubItemChevronSubMenuOpened", TokenKey: ThemeKeys.Fg3),

        // ToolTip
        (FluentKey: "ToolTipBackground", TokenKey: ThemeKeys.Overlay),
        (FluentKey: "ToolTipForeground", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ToolTipBorderBrush", TokenKey: ThemeKeys.HairlineStrong),

        // TextBox (TextControl*)
        (FluentKey: "TextControlBackground", TokenKey: ThemeKeys.PanelAlt),
        (FluentKey: "TextControlBackgroundPointerOver", TokenKey: ThemeKeys.Panel),
        (FluentKey: "TextControlBackgroundFocused", TokenKey: ThemeKeys.PanelAlt),
        (FluentKey: "TextControlBackgroundDisabled", TokenKey: ThemeKeys.PanelAlt),
        (FluentKey: "TextControlForeground", TokenKey: ThemeKeys.Fg),
        (FluentKey: "TextControlForegroundPointerOver", TokenKey: ThemeKeys.Fg),
        (FluentKey: "TextControlForegroundFocused", TokenKey: ThemeKeys.Fg),
        (FluentKey: "TextControlForegroundDisabled", TokenKey: ThemeKeys.Fg4),
        (FluentKey: "TextControlBorderBrush", TokenKey: ThemeKeys.Hairline),
        (FluentKey: "TextControlBorderBrushPointerOver", TokenKey: ThemeKeys.HairlineStrong),
        (FluentKey: "TextControlBorderBrushFocused", TokenKey: ThemeKeys.Accent),
        (FluentKey: "TextControlPlaceholderForeground", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "TextControlPlaceholderForegroundPointerOver", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "TextControlPlaceholderForegroundFocused", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "TextControlPlaceholderForegroundDisabled", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "TextControlSelectionHighlightColor", TokenKey: ThemeKeys.AccentDim),

        // ComboBox
        (FluentKey: "ComboBoxBackground", TokenKey: ThemeKeys.PanelAlt),
        (FluentKey: "ComboBoxBackgroundPointerOver", TokenKey: ThemeKeys.Panel),
        (FluentKey: "ComboBoxBackgroundPressed", TokenKey: ThemeKeys.PressedFill),
        (FluentKey: "ComboBoxForeground", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ComboBoxForegroundDisabled", TokenKey: ThemeKeys.Fg4),
        (FluentKey: "ComboBoxBorderBrush", TokenKey: ThemeKeys.Hairline),
        (FluentKey: "ComboBoxBorderBrushPointerOver", TokenKey: ThemeKeys.HairlineStrong),
        (FluentKey: "ComboBoxDropDownBackground", TokenKey: ThemeKeys.Overlay),
        (FluentKey: "ComboBoxDropDownBorderBrush", TokenKey: ThemeKeys.HairlineStrong),
        (FluentKey: "ComboBoxDropDownGlyphForeground", TokenKey: ThemeKeys.Fg3),

        // ComboBoxItem (ListBoxItem has no dedicated keys of its own in 12.0.4 - see class doc)
        (FluentKey: "ComboBoxItemBackgroundPointerOver", TokenKey: ThemeKeys.HoverFill),
        (FluentKey: "ComboBoxItemBackgroundPressed", TokenKey: ThemeKeys.PressedFill),
        (FluentKey: "ComboBoxItemBackgroundSelected", TokenKey: ThemeKeys.SelectedFill),
        (FluentKey: "ComboBoxItemBackgroundSelectedPointerOver", TokenKey: ThemeKeys.PressedFill),
        (FluentKey: "ComboBoxItemBackgroundSelectedPressed", TokenKey: ThemeKeys.PressedFill),
        (FluentKey: "ComboBoxItemForeground", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ComboBoxItemForegroundPointerOver", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ComboBoxItemForegroundPressed", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ComboBoxItemForegroundSelected", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ComboBoxItemForegroundSelectedPointerOver", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ComboBoxItemForegroundSelectedPressed", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ComboBoxItemForegroundDisabled", TokenKey: ThemeKeys.Fg4),
        (FluentKey: "ComboBoxItemForegroundSelectedDisabled", TokenKey: ThemeKeys.Fg4),

        // Shared ListBoxItem keys: a shared System* key is aliased only when FluentPaletteMapping
        // writes its palette slot directly from the same token and the brush has no Opacity, so
        // every consumer keeps its startup color and just stops going stale.
        (FluentKey: "SystemControlHighlightListLowBrush", TokenKey: ThemeKeys.HoverFill),      // palette.ListLow = HoverFill
        (FluentKey: "SystemControlHighlightListMediumBrush", TokenKey: ThemeKeys.PressedFill), // palette.ListMedium = PressedFill
        (FluentKey: "SystemControlHighlightAltBaseHighBrush", TokenKey: ThemeKeys.Fg),          // palette.BaseHigh = Fg

        // Button
        (FluentKey: "ButtonBackground", TokenKey: ThemeKeys.Panel),
        (FluentKey: "ButtonBackgroundPointerOver", TokenKey: ThemeKeys.HairlineStrong),
        (FluentKey: "ButtonBackgroundPressed", TokenKey: ThemeKeys.Hairline),
        (FluentKey: "ButtonBackgroundDisabled", TokenKey: ThemeKeys.PanelAlt),
        (FluentKey: "ButtonForeground", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ButtonForegroundPointerOver", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ButtonForegroundPressed", TokenKey: ThemeKeys.Fg),
        (FluentKey: "ButtonForegroundDisabled", TokenKey: ThemeKeys.Fg4),
        (FluentKey: "ButtonBorderBrush", TokenKey: ThemeKeys.Hairline),

        // RepeatButton (its ControlTheme has its own key family, same shape as Button's)
        (FluentKey: "RepeatButtonBackground", TokenKey: ThemeKeys.Panel),
        (FluentKey: "RepeatButtonBackgroundPointerOver", TokenKey: ThemeKeys.HairlineStrong),
        (FluentKey: "RepeatButtonBackgroundPressed", TokenKey: ThemeKeys.Hairline),
        (FluentKey: "RepeatButtonBackgroundDisabled", TokenKey: ThemeKeys.PanelAlt),
        (FluentKey: "RepeatButtonForeground", TokenKey: ThemeKeys.Fg),
        (FluentKey: "RepeatButtonForegroundPointerOver", TokenKey: ThemeKeys.Fg),
        (FluentKey: "RepeatButtonForegroundPressed", TokenKey: ThemeKeys.Fg),
        (FluentKey: "RepeatButtonForegroundDisabled", TokenKey: ThemeKeys.Fg4),
        (FluentKey: "RepeatButtonBorderBrush", TokenKey: ThemeKeys.Hairline),

        // ScrollBar thumb only - track/background/buttons stay at Fluent defaults
        (FluentKey: "ScrollBarPanningThumbBackground", TokenKey: ThemeKeys.Fg4),
        (FluentKey: "ScrollBarThumbFillPointerOver", TokenKey: ThemeKeys.Fg3),
        (FluentKey: "ScrollBarThumbFillPressed", TokenKey: ThemeKeys.Fg2),

        // CheckBox - checked-state check background/glyph only
        (FluentKey: "CheckBoxCheckBackgroundFillChecked", TokenKey: ThemeKeys.Accent),
        (FluentKey: "CheckBoxCheckBackgroundFillCheckedPointerOver", TokenKey: ThemeKeys.AccentHover),
        (FluentKey: "CheckBoxCheckBackgroundStrokeCheckedPointerOver", TokenKey: ThemeKeys.AccentHover),
        (FluentKey: "CheckBoxCheckBackgroundFillCheckedPressed", TokenKey: ThemeKeys.AccentPressed),
        (FluentKey: "CheckBoxCheckBackgroundStrokeCheckedPressed", TokenKey: ThemeKeys.AccentPressed),
        (FluentKey: "CheckBoxCheckGlyphForegroundChecked", TokenKey: ThemeKeys.AccentFg),

        // ToggleSwitch - "on" fill/knob only
        (FluentKey: "ToggleSwitchFillOn", TokenKey: ThemeKeys.Accent),
        (FluentKey: "ToggleSwitchFillOnPointerOver", TokenKey: ThemeKeys.AccentHover),
        (FluentKey: "ToggleSwitchFillOnPressed", TokenKey: ThemeKeys.AccentPressed),
        (FluentKey: "ToggleSwitchKnobFillOn", TokenKey: ThemeKeys.AccentFg),
        (FluentKey: "ToggleSwitchKnobFillOnPointerOver", TokenKey: ThemeKeys.AccentFg),
        (FluentKey: "ToggleSwitchKnobFillOnPressed", TokenKey: ThemeKeys.AccentFg),
        (FluentKey: "ToggleSwitchKnobFillOnDisabled", TokenKey: ThemeKeys.AccentFg),

        // Slider
        (FluentKey: "SliderTrackValueFill", TokenKey: ThemeKeys.Accent),
        (FluentKey: "SliderTrackValueFillPointerOver", TokenKey: ThemeKeys.AccentHover),
        (FluentKey: "SliderTrackValueFillPressed", TokenKey: ThemeKeys.AccentPressed),
        (FluentKey: "SliderThumbBackground", TokenKey: ThemeKeys.Accent),
        (FluentKey: "SliderThumbBackgroundPointerOver", TokenKey: ThemeKeys.AccentHover),
        (FluentKey: "SliderThumbBackgroundPressed", TokenKey: ThemeKeys.AccentPressed),
        (FluentKey: "SliderTrackFill", TokenKey: ThemeKeys.Hairline),
    ];
}
