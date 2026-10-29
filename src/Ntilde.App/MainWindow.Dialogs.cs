using Avalonia.Controls;
using Ntilde.Shell;
using Ntilde.Shell.Theming;
using Avalonia;
using Avalonia.Media;
using Avalonia.Layout;

namespace Ntilde
{
    /// <summary>
    /// Modal dialogs shared by the window's features (text prompts, confirmations, simple messages), themed
    /// like the window.
    /// </summary>
    public partial class MainWindow
    {
        private async Task<string?> ShowTextPromptAsync(string title, string prompt, string defaultValue)
        {
            string? result = null;
            var dialog = CreateThemedDialogWindow(title, 520, 190, canResize: false);

            var input = new TextBox
            {
                Text = defaultValue,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var cancelButton = new Button { Content = "Cancel", Width = 92 };
            cancelButton.Click += (_, __) => dialog.Close();

            var applyButton = new Button { Content = "Apply", Width = 92 };
            applyButton.Click += (_, __) =>
            {
                result = input.Text;
                dialog.Close();
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = prompt },
                        input,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, applyButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return result;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "The window's own dialog factory: its dialogs are built through the window, tests included.")]
        private Window CreateThemedDialogWindow(string title, double width, double height, bool canResize)
        {
            var dialog = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                CanResize = canResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            UiScale.FitWindow(dialog);

            ThemedWindowChrome.Apply(dialog);
            return dialog;
        }

        private Task<bool> ShowRunningProcessCloseConfirmationAsync(string message) =>
            ShowConfirmationDialogAsync("Close Running Pane", "A process is still running.", message, "Close Pane", 110);

        /// <summary>The update path's question (spec §9): the pane-close wording would misname what the button does.</summary>
        internal Task<bool> ShowUpdateSessionLossConfirmationAsync(string message) =>
            ShowConfirmationDialogAsync("Apply Update", "Multiplexed sessions are still running.", message, "Close sessions and update", 190);

        /// <summary>A modal Cancel / confirm question; true only when the confirm button was pressed.</summary>
        private async Task<bool> ShowConfirmationDialogAsync(string title, string heading, string message, string confirmText, double confirmWidth)
        {
            bool confirmed = false;

            var dialog = CreateThemedDialogWindow(title, 460, 190, canResize: false);

            var messageBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            };

            var cancelButton = new Button
            {
                Content = "Cancel",
                Width = 92
            };
            cancelButton.Click += (_, __) =>
            {
                confirmed = false;
                dialog.Close();
            };

            var closeButton = new Button
            {
                Content = confirmText,
                Width = confirmWidth
            };
            closeButton.Click += (_, __) =>
            {
                confirmed = true;
                dialog.Close();
            };

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = heading,
                            FontWeight = FontWeight.SemiBold
                        },
                        messageBlock,
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(this);
            return confirmed;
        }

        private async Task ShowSimpleMessageDialogAsync(string title, string message, Window? owner = null)
        {
            var dialog = CreateThemedDialogWindow(title, 520, 220, canResize: false);

            var closeButton = new Button { Content = "Close", Width = 92 };
            closeButton.Click += (_, __) => dialog.Close();

            dialog.Content = new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new StackPanel
                        {
                            Orientation = Avalonia.Layout.Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Children = { closeButton }
                        }
                    }
                }
            };

            await dialog.ShowDialog(owner ?? this);
        }
    }
}
