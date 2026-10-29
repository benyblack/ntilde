using Ntilde.Platform;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Ntilde.Shell
{
    /// <summary>
    /// Dropping files and text on the terminal.
    /// </summary>
    public partial class TerminalView
    {
        public ShellOverride ShellOverride { get; set; } = ShellOverride.Auto;

        public class TextFileDroppedEventArgs : EventArgs
        {
            public string FilePath { get; set; } = string.Empty;
            public string EscapedPath { get; set; } = string.Empty;

            /// A DropRouter message that applies to this same drop, or null.
            ///
            /// Carried here rather than raised as a separate DropNotice because both render
            /// into the pane's single toast panel: raising both would mean one immediately
            /// overwrites the other, and whichever won, information would be lost. The
            /// reachable case is a single text file dropped on a WSL session where path
            /// mapping fell back to the Windows path - the smart-paste prompt is actionable
            /// and must survive, so the warning rides along with it.
            public string? Notice { get; set; }
        }

        public event EventHandler<TextFileDroppedEventArgs>? TextFileDropped;

        /// Raised with a human-readable explanation when a file drop is refused, or is
        /// accepted with a caveat. DropRouter has always produced these messages
        /// (ToastMessage) but nothing consumed them, so all three - secure-input block,
        /// shell-metacharacter block, and WSL-mapping fallback - were discarded and the drop
        /// appeared to do nothing at all (#182).
        ///
        /// Deliberately a plain message rather than a typed severity: two of the three are
        /// refusals and one is a warning about a successful drop, and the pane presents both
        /// the same way. Splitting them would add a distinction the UI does not make.
        public event Action<string>? DropNotice;

        /// Whether a DropRouter outcome carries a message the user must see.
        ///
        /// Extracted so the contract can be tested against real DropRouter results without
        /// simulating a drag-and-drop gesture. The subtlety worth pinning is that it depends
        /// only on the message: the original code surfaced nothing when a message arrived
        /// *alongside* TextToSend, which is exactly the WSL-mapping-fallback shape, so that
        /// third message was lost even though the other two were at least reachable via the
        /// block branch.
        internal static bool ShouldRaiseDropNotice(string? toastMessage) =>
            !string.IsNullOrEmpty(toastMessage);

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private async Task OnDropAsync(object? sender, DragEventArgs e)
        {
            if (_session == null) return;

            var files = e.DataTransfer.TryGetFiles();
            if (files != null)
            {
                var paths = new List<string>();
                foreach (var item in files)
                {
                    if (item is IStorageItem storage && storage.Path.IsFile)
                    {
                        paths.Add(storage.Path.LocalPath);
                    }
                }

                if (paths.Count > 0)
                {
                    bool isAlt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

                    bool isWsl = _session.ShellCommand?.Contains("wsl", StringComparison.OrdinalIgnoreCase) ?? false;
                    string? distroName = null;
                    if (isWsl && !string.IsNullOrWhiteSpace(_session.ShellArguments))
                    {
                        var args = _session.ShellArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < args.Length - 1; i++)
                        {
                            if (args[i] == "-d" || args[i] == "--distribution")
                            {
                                distroName = args[i + 1];
                                break;
                            }
                        }
                    }

                    var ctx = new SessionContext
                    {
                        DetectedShell = DetectShellFromCommand(_session.ShellCommand),
                        IsEchoEnabled = _buffer?.Modes.IsEchoEnabled ?? true,
                        IsWslSession = isWsl,
                        WslDistroName = distroName,
                        IsAltScreen = _buffer?.IsAltScreenActive ?? false,
                        ShellOverride = this.ShellOverride
                    };

                    var mapper = new Ntilde.Platform.Paths.WslPathMapper(new Ntilde.Platform.Execution.DefaultProcessRunner(), distroName);
                    var result = await DropRouter.HandleDropAsync(ctx, paths, isAlt, mapper);
                    if (result.Handled)
                    {
                        // 1) First check if DropRouter explicitly blocked the input for security
                        if (ShouldRaiseDropNotice(result.ToastMessage) && string.IsNullOrEmpty(result.TextToSend))
                        {
                            // Terminal session echo is disabled and Alt was not held.
                            // Do not send anything - but do tell the user why nothing
                            // happened. This used to be a bare `return`, so a blocked drop
                            // was indistinguishable from a drop that silently did nothing
                            // (#182).
                            DropNotice?.Invoke(result.ToastMessage!);
                            return;
                        }

                        // Fire smart action event if only 1 text file was dropped
                        if (paths.Count == 1 && Ntilde.Platform.Input.TextFileDetector.IsTextFile(paths[0]))
                        {
                            var args = new TextFileDroppedEventArgs
                            {
                                FilePath = paths[0],
                                EscapedPath = result.TextToSend ?? string.Empty,
                                // This branch returns, so a DropNotice raised below would
                                // never fire for a single text file - the WSL mapping-failure
                                // warning was still being lost here.
                                Notice = ShouldRaiseDropNotice(result.ToastMessage)
                                    ? result.ToastMessage
                                    : null
                            };
                            TextFileDropped?.Invoke(this, args);
                            return; // Do NOT insert path automatically, wait for user to click Toast
                        }

                        if (!string.IsNullOrEmpty(result.TextToSend))
                        {
                            SendUserInput(result.TextToSend);
                            PasteObserved?.Invoke(result.TextToSend);
                        }

                        // A message can accompany a *successful* drop too: DropRouter sets
                        // one alongside TextToSend when WSL path mapping failed and it fell
                        // back to the Windows path. The path is inserted either way, so this
                        // is a warning rather than a block - and it was the third message
                        // being dropped on the floor here, not just the two blocks above.
                        if (ShouldRaiseDropNotice(result.ToastMessage))
                        {
                            DropNotice?.Invoke(result.ToastMessage!);
                        }

                        return;
                    }
                }
            }

            if (e.DataTransfer.TryGetText() is string text && !string.IsNullOrWhiteSpace(text))
            {
                SendUserInput(text);
                PasteObserved?.Invoke(text);
                e.Handled = true;
            }
        }

        private static DetectedShell DetectShellFromCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return DetectedShell.Unknown;
            if (command.Contains("pwsh", StringComparison.OrdinalIgnoreCase) || command.Contains("powershell", StringComparison.OrdinalIgnoreCase)) return DetectedShell.Pwsh;
            if (command.Contains("cmd", StringComparison.OrdinalIgnoreCase)) return DetectedShell.Cmd;
            if (command.Contains("bash", StringComparison.OrdinalIgnoreCase) || command.Contains("zsh", StringComparison.OrdinalIgnoreCase) || command.Contains("sh", StringComparison.OrdinalIgnoreCase)) return DetectedShell.PosixSh;
            return DetectedShell.Unknown;
        }
    }
}
