using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Ntilde
{
    /// <summary>
    /// Focus mode: a per-window toggle that hides the app chrome around the selected tab behind a
    /// slim strip. This file owns the flag and the window-level chrome (the tab header area and the
    /// title bar items); nothing here touches selection, zoom, splits or any persisted setting.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Height of the focus-mode strip, which stands in for the 36px title band.</summary>
        internal const double FocusStripHeight = 24;

        private bool _isFocusMode;

        internal bool IsFocusMode => _isFocusMode;

        /// <summary>The top band that drags the window: the strip in focus mode, the title band otherwise.</summary>
        internal static double WindowDragBandHeight(bool focusMode) => focusMode ? FocusStripHeight : 36;

        /// <summary>What a double-click on the strip does: restore a maximised window, maximise anything else.</summary>
        internal static WindowState ToggledMaximizeState(WindowState current) =>
            current == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        internal void EnterFocusMode()
        {
            if (_isFocusMode) return;
            _isFocusMode = true;
            WindowDecorationsTheme = (ControlTheme)this.FindResource("NtildeFocusWindowDecorationsTheme")!;
            ApplyFocusModeChrome();
        }

        internal void LeaveFocusMode()
        {
            if (!_isFocusMode) return;
            _isFocusMode = false;
            WindowDecorationsTheme = (ControlTheme)this.FindResource("NtildeWindowDecorationsTheme")!;
            // Regenerates every generated title bar item with its own visibility.
            RebuildTitleBar();
            ApplyFocusModeChrome();
        }

        internal void ToggleFocusMode()
        {
            if (_isFocusMode) LeaveFocusMode();
            else EnterFocusMode();
        }

        private void ApplyFocusModeChrome()
        {
            UpdateTabHeaderViewport();
            ApplyFocusModeToTitleBar();
            UpdateFocusStrip();
        }

        private void ApplyFocusModeToTitleBar()
        {
            var host = this.FindControl<StackPanel>("TitleBarItemsHost");
            var titleBar = this.FindControl<Grid>("TitleBar");
            if (host == null || titleBar == null) return;

            if (this.FindControl<MenuItem>("MenuCustomizeTitleBar") is { } customize)
                customize.IsVisible = !_isFocusMode;
            if (this.FindControl<MenuItem>("MenuTitleBarExitFocusMode") is { } exit)
                exit.IsVisible = _isFocusMode;

            var margin = titleBar.Margin;
            if (_isFocusMode)
            {
                // The agent light stays: it is a safety surface with no off switch.
                foreach (Control child in host.Children.OfType<Control>()
                             .Where(c => c.Name != "AgentObserveIndicator"))
                {
                    child.IsVisible = false;
                }

                titleBar.Height = FocusStripHeight;
                titleBar.Margin = new Thickness(margin.Left, 0, margin.Right, margin.Bottom);
            }
            else
            {
                var newTab = this.FindControl<Button>(Shell.TitleBar.TitleBarViewFactory.NewTabButtonName);
                if (newTab != null) newTab.IsVisible = true;

                titleBar.Height = 32;
                titleBar.Margin = new Thickness(margin.Left, 4, margin.Right, margin.Bottom);
            }
        }

        private void UpdateFocusStrip()
        {
            var strip = this.FindControl<Grid>("FocusStrip");
            var title = this.FindControl<TextBlock>("FocusStripTitle");
            if (strip == null || title == null) return;

            strip.IsVisible = _isFocusMode;
            if (!_isFocusMode) return;

            var tabs = this.FindControl<TabControl>("Tabs");
            title.Text = tabs?.SelectedItem is TabItem selected ? GetTabHeaderText(selected) : "";
            title.Foreground = TitleBarContrastForeground();

            double left = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? MacOsTrafficLightReserve : 10;
            double titleBarRight = this.FindControl<Grid>("TitleBar")?.Margin.Right ?? 0;
            // The extra 40 clears the agent light, which sits at the right end of the title bar.
            strip.Margin = new Thickness(left, 0, titleBarRight + 40, 0);
        }
    }
}
