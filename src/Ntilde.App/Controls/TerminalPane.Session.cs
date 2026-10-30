using Ntilde.Shell;
using Avalonia.Threading;
using Ntilde.VT;
using Ntilde.CommandAssist.ShellIntegration.Contracts;
using Ntilde.Pty;
using Ntilde.Mux;
using Ntilde.Shell.Mux;

namespace Ntilde.Controls
{
    /// <summary>
    /// Starting the pane's session: choosing the shell, creating the parser and the session, wiring their
    /// streams and lifecycle, and the files the kitty graphics protocol may read.
    /// </summary>
    public partial class TerminalPane
    {
        internal static string DetermineShellKind(string? shellCommand)
        {
            if (string.IsNullOrWhiteSpace(shellCommand))
            {
                return "unknown";
            }

            // Order matters: bash/zsh/fish must be matched before the generic
            // `sh` fallback because each contains "sh" as a substring.
            if (shellCommand.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
                shellCommand.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return "pwsh";
            }

            if (shellCommand.Contains("cmd", StringComparison.OrdinalIgnoreCase))
            {
                return "cmd";
            }

            if (shellCommand.Contains("bash", StringComparison.OrdinalIgnoreCase))
            {
                return "bash";
            }

            if (shellCommand.Contains("zsh", StringComparison.OrdinalIgnoreCase))
            {
                return "zsh";
            }

            if (shellCommand.Contains("fish", StringComparison.OrdinalIgnoreCase))
            {
                return "fish";
            }

            if (shellCommand.Contains("sh", StringComparison.OrdinalIgnoreCase))
            {
                return "sh";
            }

            return "unknown";
        }

        private (float? Width, float? Height) InitialMuxCellSize()
        {
            var presentation = BuildMuxPresentation();
            if (float.IsFinite(presentation.CellWidthPx) && float.IsFinite(presentation.CellHeightPx) &&
                presentation.CellWidthPx > 0 && presentation.CellHeightPx > 0)
                return (presentation.CellWidthPx, presentation.CellHeightPx);
            return (null, null);
        }

        private void SyncLocalSixelCellSize()
        {
            if (Parser is not { } parser || Session is MuxClientSession) return;
            double scale = TermView.EffectiveRenderScaling;
            if (!double.IsFinite(scale) || scale <= 0) scale = 1;
            float width = (float)(TermView.Metrics.CellWidth * scale);
            float height = (float)(TermView.Metrics.CellHeight * scale);
            if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0) return;
            parser.SixelCellSizeOverride = new SixelCellSize(width, height);
        }

        private void InitializeSession(string? shell, TerminalProfile? profile, int cols, int rows, string? explicitArgs = null)
        {
            if (Session != null || Buffer == null) return;

            if (cols <= 0 || rows <= 0) return;

            // Update buffer to match view exactly before starting PTY
            Buffer.Resize(cols, rows);
            CreateAndWireParser();

            // Sync initial metrics
            float cw = TermView.Metrics.CellWidth;
            float ch = TermView.Metrics.CellHeight;
            if (cw > 0) Parser!.CellWidth = cw;
            if (ch > 0) Parser!.CellHeight = ch;
            SyncLocalSixelCellSize();

            // Setup Session.
            //
            // Deliberately IsNullOrWhiteSpace, not `??`: an EMPTY command is just as
            // unspawnable as a null one, and it is the value that actually reaches here in
            // practice. `ShellCommand` starts out as string.Empty and is fed straight back
            // into this method by the OnAttachedToVisualTree fallback and by Reconnect(),
            // and a restored pane whose persisted Command was empty arrives the same way.
            // With `??` all of those spawned "", which the native layer resolved to the
            // first %PATH% *directory* and reported as "Access is denied" against a path the
            // user never typed. The default shell is always spawnable, so preferring it over
            // a blank is strictly better than failing.
            string effectiveShell = string.IsNullOrWhiteSpace(shell) ? ShellHelper.GetDefaultShell() : shell;
            string args = explicitArgs ?? profile?.Arguments ?? "";
            InitializeSessionCore(effectiveShell, args, profile, cols, rows);
        }

        /// <summary>
        /// Replaces <see cref="Parser"/> with a fresh <see cref="AnsiParser"/> and attaches this
        /// pane's handlers to it.
        /// </summary>
        /// <remarks>
        /// The handlers attached here are deliberately never removed, and that is safe for exactly
        /// one reason: a <b>new</b> parser is created on every call, so each one starts with an empty
        /// handler list and the previous parser becomes garbage along with its handlers. #102 read
        /// the missing <c>-=</c> calls as an accumulation bug on <c>Reconnect()</c>; they are not,
        /// because of this line.
        ///
        /// That makes the assignment load-bearing. Hoisting the parser out to reuse it across
        /// sessions — a reasonable-looking change — would silently double all of these on every
        /// reconnect, producing the duplicate bell/title symptom #102 describes, with no <c>-=</c>
        /// anywhere to fall back on. Split into its own method so that invariant can be asserted
        /// without spinning up a shell; see <c>PaneParserWiringTests</c>.
        /// </remarks>
        internal void CreateAndWireParser(bool? forceConPtyFiltering = null, bool muxBacked = false)
        {
            if (Buffer == null) return;

            // A mux-backed pane shares the daemon's SIXEL geometry even though only the GUI
            // decodes the bitmap. Other image protocols keep their existing mux restriction.
            Parser = new AnsiParser(Buffer, forceConPtyFiltering);

            // The GUI decodes SIXEL pixels while the headless parser applies the same geometry.
            // Other bitmap protocols retain their existing mux restriction.
            Parser.ImageDecoder = CreateImageDecoder(muxBacked);
            Parser.AllowNonSixelImages = !muxBacked;

            // A device reply is text on the PTY that the keyboard path never produced: DA1, a DSR
            // cursor report, an answerback. Nothing here can promise the shell's line editor was not
            // reading when the query arrived, so history capture stands down for the rest of the
            // line - but the accumulator is *not* poisoned outright, because a local pane is sent one
            // of these before the user has touched the keyboard (ConPTY and Clink both probe the
            // terminal while the first prompt is drawn) and a poison there disabled suggestion
            // insertion for the whole session. See MarklessSubmissionAccumulator's
            // _deviceReplyObserved for the full argument and the measurement. Subscribed here rather
            // than beside the SendInput handler in InitializeSessionCore so it exists whenever a
            // parser does, session or not.
            Parser.OnResponse += _ => _marklessSubmission.ObserveDeviceReply();

            // OSC 10/11 (fg/bg color query) answers come from the active theme; without this,
            // a freshly-created parser would fall back to AnsiParser's hardcoded defaults until
            // the next ApplySettings call (#265). Must use the same profile-merged theme
            // resolution as ApplySettings (via BuildEffectiveSettings) — using the global
            // _settings.ActiveTheme directly would clobber a per-profile theme override,
            // since this method runs after ApplySettings on pane init and on every
            // Reconnect().
            if (_settings != null)
            {
                var effectiveTheme = BuildEffectiveSettings(_settings).ActiveTheme;
                Parser.DefaultForeground = effectiveTheme.Foreground;
                Parser.DefaultBackground = effectiveTheme.Background;
            }

            // Kill switch (Blocker 2, #277 review): a freshly-created parser must start with
            // the query-reply gate in the same state TerminalView's encoder gate will be in,
            // same reasoning as the DefaultForeground wiring above.
            Parser.KittyKeyboardEnabled = _settings?.EnableKittyKeyboardProtocol ?? true;
            Buffer?.Modes.KittyKeyboard.SetSupportedFlags(muxBacked ? KittyKeyboardState.FlagDisambiguateEscapeCodes : KittyKeyboardState.SupportedFlags);

            // Native kitty graphics on Windows (ConPTY pass-through opt-in): a freshly-created
            // parser must match what ApplySettings will set, same reasoning as above.
            Parser.AllowNativeKittyGraphics = AllowsNativeKittyGraphics(muxBacked);

            // Kitty t=f (file transport) reads happen through this delegate so the VT layer
            // never touches disk itself. Confinement: absolute paths only, must resolve under
            // the user's temp directory (where clients like terminal-browser stage raw frames),
            // with a hard size cap - the path arrives from the remote stream, so it is
            // attacker-controlled input, not a filename to trust.
            //
            // Local sessions only: on an SSH pane the path names a file on the REMOTE host, and
            // reading a same-named local file would fabricate frames (or just fail). Leaving
            // the delegate null makes t=f probes answer ERR, so remote clients fall back to
            // inline payloads, which are self-contained.
            // A mux-backed pane never decodes images (above), so it has nothing to read either.
            Parser.ReadFileBytes = ResolveKittyFileReader(muxBacked);

            Parser.OnBell += () =>
            {
                this.Dispatcher.Post(() =>
                {
                    TermView.TriggerBell();
                    BellReceived?.Invoke(this);
                });
            };
            // OSC 52 clipboard write (issue #268). AnsiParser always raises this event
            // (VT stays policy-free); the settings gate lives here, checked at invocation
            // time so a live ApplySettings toggle takes effect immediately without needing
            // to re-wire the parser. Read (query) is handled entirely inside the parser via
            // an OnResponse denial reply and never reaches this handler.
            Parser.OnClipboardWrite += (target, data) =>
            {
                // Resolved through BuildEffectiveSettings rather than off raw _settings, matching
                // the #277 precedent below in ApplySettings: BuildEffectiveSettings copies this
                // bool straight through today, so the two are equivalent, but the moment a
                // per-profile / SSH-scoped override lands, reading the global would silently gate
                // on the wrong value (PR #280 review). Still read at invocation time so a live
                // ApplySettings toggle takes effect without re-wiring.
                // A null _settings means "not configured yet" and defaults to allow, consistent
                // with TerminalSettings.AllowOsc52ClipboardWrite's own default.
                bool allowed = _settings == null || BuildEffectiveSettings(_settings).AllowOsc52ClipboardWrite;
                if (!allowed) return;

                // Lossy by design: invalid UTF-8 in the payload becomes U+FFFD rather than being
                // rejected, matching how other terminals treat OSC 52 as text. GetString does not
                // throw for malformed input, so there is nothing to catch here - if this should
                // ever reject non-UTF-8 instead, it needs a throwing decoder, not a try/catch.
                string text = System.Text.Encoding.UTF8.GetString(data);

                // Bumped only once the gate above has passed, i.e. exactly when this handler is
                // actually about to reach the clipboard. Gives PaneParserWiringTests a synchronous
                // seam to assert "setting off -> clipboard not touched" without needing a real
                // UI-thread TopLevel/Clipboard in tests. Deliberately a plain non-atomic increment
                // on the PTY thread: it is a single-writer test seam, not a metric, so don't read
                // it as one.
                _clipboardWriteAttemptsForTest++;
                this.Dispatcher.Post(() =>
                {
                    _ = TermView.SetClipboardTextAsync(text);
                });
            };
            Parser.OnWorkingDirectoryChanged += cwd =>
            {
                _shellLifecycleTracker?.HandleWorkingDirectoryChanged(cwd);
                this.Dispatcher.Post(() =>
                {
                    HandleWorkingDirectoryChanged(cwd);
                });
            };
            Parser.OnTitleChanged += title =>
            {
                this.Dispatcher.Post(() =>
                {
                    CurrentOscTitle = title;
                    TitleChanged?.Invoke(this, title);
                });
            };
            Parser.OnDesktopNotification += text =>
            {
                this.Dispatcher.Post(() =>
                {
                    OscNotificationReceived?.Invoke(this, text);
                });
            };
            Parser.OnProgressReported += (state, percent) =>
            {
                // State 0 is the ONLY withdrawal edge (Codex review, PR #500). An
                // unknown/future state is ignored rather than mapped to a withdrawal:
                // clearing a live indicator because the client speaks a dialect this
                // build doesn't know would throw away real progress. FromOsc returns
                // null for both, so the distinction is made here, before the post.
                if (state == 0)
                {
                    this.Dispatcher.Post(() => ProgressReported?.Invoke(this, null));
                    return;
                }

                if (TerminalProgressReport.FromOsc(state, percent) is not { } report)
                {
                    return;
                }

                this.Dispatcher.Post(() =>
                {
                    ProgressReported?.Invoke(this, report);
                });
            };
            Parser.OnPromptReady += () =>
            {
                NoteShellIntegrationMarkObserved();
                _shellLifecycleTracker?.HandlePromptReady();
                _agentRegistration?.StatusMachine.NotifyPromptReady();
            };
            Parser.OnCommandAccepted += commandText =>
            {
                NoteShellIntegrationMarkObserved();


                // OSC 133;C is the only moment the output region's *start* can be established: the
                // input line is still on screen and the mark that describes it is still live, so
                // "the row after the last row of the input" is answerable. One frame later the
                // shell has echoed a newline and started printing, and nothing on the grid says
                // where that began. Runs here on the parse thread, synchronously, for the same
                // reason - a UI-thread hop would read a grid the parser has moved on from.
                CaptureCommandOutputRegionStart();

                // Only overwritten when the mark carried text. A bare `133;C` (legal FinalTerm, and
                // what several third-party remote snippets emit) arrives with null, and clearing
                // here would throw away the command the grid/heuristic path already read at Enter -
                // which on those shells is the only source Fix mode has.
                //
                // Gated on the setting for the same reason the latch above is: with shell
                // integration off, a C payload written by the far end of an SSH connection (or by a
                // local `cat` of a crafted file) must not become the command Fix mode and the
                // long-command notification talk about.
                if (!string.IsNullOrWhiteSpace(commandText) && IsShellIntegrationConsumptionEnabled)
                {
                    _lastRelevantCommandText = commandText.Trim();
                }

                _shellLifecycleTracker?.HandleCommandAccepted(commandText);
                // OSC 133;C is the execution-start edge: the line editor is closed and the
                // shell is about to run the command. (OSC 133;B, below, only says the prompt
                // finished printing -- the shell is idle waiting for input at that point, so
                // driving "running" off B would report every idle prompt as a busy session.)
                //
                // Status machine is notified synchronously on the parser path so
                // command-lifecycle signals keep their emission order relative to
                // OnCommandStarted/OnPromptReady (the UI post below would let a
                // snapshot briefly see AwaitingInput with CurrentCommand set).
                _agentRegistration?.StatusMachine.NotifyCommandAccepted(commandText);
                _agentRegistration?.StatusMachine.NotifyCommandStarted();
                _lastCommandStartedAtUtc = DateTimeOffset.UtcNow;
                this.Dispatcher.Post(() =>
                {
                    LastExitCode = null;
                    CommandStarted?.Invoke(this);
                });
            };
            Parser.OnCommandStarted += mark =>
            {
                // OSC 133;B == prompt end / start of user input. The mark position is the
                // anchor Command Assist uses to read the live command line out of the grid.
                // B rides inside the prompt string, so it is re-emitted on every prompt the
                // shell *prints* (a new prompt, clear, zle reset-prompt) with fresh
                // coordinates: keep the newest one rather than the first. It is NOT re-emitted
                // on a window resize - PSReadLine (measured on 2.3) repaints the input line
                // without re-running the prompt function, and zsh/fish behave the same unless
                // the user wired a resize hook. That is why the mark has to survive the reflow
                // a resize triggers rather than waiting to be replaced; see
                // TerminalBuffer.CommandStartMark, which is where this now lives.
                NoteShellIntegrationMarkObserved();

                // The mark is not written here any more: AnsiParser publishes it into the buffer
                // together with the command-input window, before this callback runs, so the pair can
                // never be observed half-updated (Codex P2 on #448). LatestCommandStartMark remains
                // as the pane's reader of it.
                _shellLifecycleTracker?.HandleCommandStarted(new ShellMarkPosition(
                    Row: mark.Row,
                    Column: mark.Column,
                    AbsoluteRow: mark.AbsoluteRow,
                    IsAltScreen: mark.IsAltScreen,
                    Generation: mark.Generation));
            };
            Parser.OnCommandFinished += exitCode =>
            {
                _agentRegistration?.StatusMachine.NotifyCommandFinished(exitCode);

                // The Agent Output panel's final read must happen here, on the parse thread: D is
                // the last instant the grid still holds the command's output, and a debounced
                // background read would race the next prompt painting over the tail. Bounded by
                // the same budget as every other read. OnCommandFinishedDetailed (below) clears
                // the tracked marks after this.
                _agentOutputTracker?.NotifyCommandFinished();

                // Captured here, on the parse thread, and only for a failure. By the time the UI
                // post below runs, the shell has painted the next prompt over the last rows of the
                // output and may already be running the next command; D is the last instant at
                // which the region is still on the grid. The success path pays nothing - no read,
                // no redaction - because there is nothing for Fix mode to say about it.
                string? outputTail = TryCaptureFailureOutputTail(exitCode);
                _lastFailureOutputTailForTest = outputTail;

                this.Dispatcher.Post(() =>
                {
                    if (exitCode.HasValue)
                    {
                        LastExitCode = exitCode.Value;
                    }

                    CommandFinished?.Invoke(this, exitCode);
                    _ = HandleCommandAssistCompletionAsync(exitCode, outputTail);
                });
            };
            Parser.OnCommandFinishedDetailed += (exitCode, durationMs) =>
            {
                // OSC 133;D == the command finished, so the B mark that anchored its input line
                // no longer points at an input line: everything from it down is command output.
                // Dropping it here closes the window in which the grid reader would happily
                // return output as "the live command line" -- until the next prompt re-emits B,
                // there is nothing truthful to read, and "no mark" is the honest answer.
                //
                // D rather than C (CommandExecuted) deliberately: in one B -> C -> D cycle, C
                // fires the instant the user submits, while the input line is still on screen and
                // still exactly what the mark describes. Clearing on C would blind the reader for
                // the whole run of the command, including the submission edge that Phase 1c reads
                // the final command text on. GridQueryReader.MaxSpanRows stays as a backstop for
                // shells that emit B without a matching D, but it is no longer the only guard.
                NoteShellIntegrationMarkObserved();

                // The output region ends here too. OnCommandFinished (above, same OSC 133;D)
                // has already read it; holding the mark past this point would let the *next*
                // failure read a region that starts inside this command's output.
                Buffer?.ClearTrackedShellMarks();

                _shellLifecycleTracker?.HandleCommandFinished(exitCode, durationMs);

                // Long-command completion (A2 PR4): the pane only applies the
                // mechanical threshold; opt-in + focus policy lives in the window.
                var startedAt = _lastCommandStartedAtUtc;
                _lastCommandStartedAtUtc = null;
                TimeSpan? duration = durationMs.HasValue
                    ? TimeSpan.FromMilliseconds(durationMs.Value)
                    : startedAt.HasValue ? DateTimeOffset.UtcNow - startedAt.Value : null;
                if (duration is { } d && LongCommandNotificationPolicy.QualifiesAsLong(d))
                {
                    var commandText = _lastRelevantCommandText;
                    this.Dispatcher.Post(() => LongCommandCompleted?.Invoke(this, commandText, exitCode, d));
                }
            };
        }

        private static Ntilde.Rendering.SkiaImageDecoder CreateImageDecoder(bool deferSixel) => new() { DeferSixelDecoding = deferSixel };

        private bool AllowsNativeKittyGraphics(bool muxBacked) =>
            !muxBacked && (_settings?.AllowNativeKittyGraphics ?? true);

        private Func<string, byte[]?>? ResolveKittyFileReader(bool muxBacked) =>
            muxBacked || Profile is { Type: ConnectionType.SSH }
                ? null
                : ReadKittyTransportFile;

        /// Spawns the session and wires the handlers that depend on it. Split out of
        /// <c>InitializeSession</c> alongside <see cref="CreateAndWireParser"/>; no behaviour change.
        internal void InitializeSessionCore(string effectiveShell, string args, TerminalProfile? profile, int cols, int rows)
        {
            _shellLifecycleTracker = null;
            _isShellIntegrationActive = false;
            _hasObservedShellIntegrationMark = false;
            _shellIntegrationEnvOverrides = null;
            // The Agent Output tracker's heuristic region mark belongs to the shell that is going
            // away; drop it with the rest of the per-session state.
            _agentOutputTracker?.Reset();
            // A restart or a profile switch reaches here; the pending line belonged to the shell
            // that is going away.
            _marklessSubmission.Reset();
            Buffer?.ClearTrackedShellMarks();

            _lastFailureOutputTailForTest = null;

            // Update SFTP Menu Visibility
            // If it's not an SSH session, detach the context menu entirely to avoid "tiny empty box" artifacts
            if (profile == null || profile.Type != ConnectionType.SSH)
            {
                RootGrid.ContextMenu = null;
            }

            UpdateRemoteFilesSidebarEntryPointState();

            string startingDir = profile?.StartingDirectory ?? "";
            // Whatever comes next registers on its own attach (a reattach of the same session too).
            UnregisterRemoteMuxSession();
            Session = null;
            // MuxEndpoint is reset below, once it is known whether a pending id stays pending (a plain
            // SSH pane's does, and the endpoint must go on naming it: Phase 4 spec §5).
            // Set again below only once a session is wired; every early return (a spawn that threw,
            // a failed SSH connect) must leave the view resizing its own buffer, not waiting for a
            // stream resize from a mux session that is gone.
            TermView.DefersBufferResizeToSession = false;
            // Reset for every session, not only mux ones: a reconnect can land on a local fallback,
            // and a stale flag would make every Enter reconnect.
            _muxConnectionLost = false;
            _muxReconnecting = false;
            _muxReleasedForRestart = false;
            _remoteInputHintShown = false;
            // A remote result still on its way belongs to the spawn this one replaces (Phase 4 spec §7.4).
            _remoteConnecting = false;
            _remoteGen++;
            bool muxPreviousLost = false;
            _agentRegistration?.SetLifecycle(null);
            try
            {
                // A combined command carries its arguments inline ("zsh -l"). The split lives in
                // ShellHelper because ResolveExecutableOrDefault has to make the same call when it
                // decides whether a command is runnable, and when the two split differently they
                // disagreed about what was being launched: this code cut at the first literal space,
                // so a quoted executable whose path contains spaces
                // ("C:\Program Files\PowerShell\7\pwsh.exe" -NoLogo) passed the resolver's check and
                // was then spawned here as `"C:\Program`.
                if (ShellHelper.TrySplitCommandLine(effectiveShell, out string cmdPart, out string argPart))
                {
                    effectiveShell = cmdPart;
                    args = (argPart + " " + args).Trim();
                }

                ShellCommand = effectiveShell;
                ShellArgs = args;

                if (profile == null || profile.Type != ConnectionType.SSH)
                {
                    // Like the launch plan below, a launch-time detail: applied after ShellArgs is
                    // captured so the persisted arguments stay what the user configured.
                    args = ShellHelper.ApplyLoginShellConvention(effectiveShell, args, OperatingSystem.IsMacOS());
                    ApplyShellIntegrationLaunchPlan(profile, ref effectiveShell, ref args, startingDir);

                    // ShellCommand/ShellArgs are deliberately NOT updated with the merged
                    // command line. SessionManager persists them, and a launch plan written
                    // back to disk is a trap: on the next launch the provider sees its own
                    // -File / -EncodedCommand in the incoming arguments, takes the "the user
                    // supplied a script" bail-out, and passes the stale line through — so
                    // integration silently stops and the old bootstrap is launched forever.
                    // Every launch re-saves it, so it never recovers on its own.
                    //
                    // They hold what the USER configured; the merge is a launch-time detail
                    // that `args` carries into the session below. A relaunch re-runs the plan
                    // from the user's arguments, which is what makes it self-correcting.
                }
                else
                {
                    ArmRemoteShellIntegrationTracker(profile);
                }

                bool isSsh = profile != null && profile.Type == ConnectionType.SSH;
                var initialCellSize = InitialMuxCellSize();
                var request = new TerminalSessionRequest(
                    Command: effectiveShell,
                    Arguments: args,
                    StartingDirectory: startingDir,
                    Cols: cols,
                    Rows: rows,
                    EnvironmentOverrides: _shellIntegrationEnvOverrides,
                    SkipPowerShellPostLaunchInit: _isShellIntegrationActive,
                    Ssh: isSsh
                        ? new SshSessionDescriptor(
                            ProfileId: profile!.Id,
                            DiagnosticsLevel: (int)_sshDiagnosticsLevel,
                            InteractionHandler: SshPromptsForNewConnection(),
                            NativeSshEnabled: _settings?.ExperimentalNativeSshEnabled ?? false)
                        {
                            Interactive = !OpenedByAgent,
                        }
                        : null)
                {
                    KittyKeyboardSupportedFlags = (_settings?.EnableKittyKeyboardProtocol ?? true) ? KittyKeyboardState.SupportedFlags : 0,
                    CellWidthPx = initialCellSize.Width,
                    CellHeightPx = initialCellSize.Height,
                };

                // Phase 4 spec §7.4: an SSH pane whose profile persists its remote sessions goes to that
                // profile's remote daemon, and takes its pending id as a local pane does. Any other SSH
                // pane is plain SSH and leaves the id pending.
                MuxTerminalSessionFactory? remote = isSsh && SessionFactory is MuxTerminalSessionFactory persistent && persistent.RoutesRemote(request)
                    ? persistent
                    : null;
                bool plainSsh = isSsh && remote is null;
                if (plainSsh && _muxReattachId is Guid dropped)
                {
                    // A remote pane's dropped session, now respawning as plain SSH (its profile stopped persisting):
                    // the id stays pending where the session file reads it - a share's with its flag, and so let go below.
                    if (MuxSessionIdToRestore is null)
                    {
                        MuxSessionIdToRestore = dropped;
                        MuxAttachSharedToRestore = _muxReattachShared;
                    }

                    _muxReattachId = null;
                    _muxReattachShared = false;
                }

                if (plainSsh) LetGoOfPendingShareOnPlainSsh();

                bool attachShared = TakeMuxAttachShared(plainSsh); // before TakeMuxSessionIdToRestore clears the id
                Guid? existingMuxSessionId = TakeMuxSessionIdToRestore(plainSsh);
                request = request with
                {
                    ExistingMuxSessionId = existingMuxSessionId,
                    AttachShared = attachShared,
                    // Its own dropped session: its dead connection may still hold it, so take it back shared.
                    ReattachAfterDrop = remote is not null && existingMuxSessionId is not null && _muxReattachAfterDrop,
                };

                // The endpoint names a pending id while one is left; otherwise the new session sets it.
                if (MuxSessionIdToRestore is null) MuxEndpoint = null;
                if (remote is null)
                {
                    _muxReattachAfterDrop = false;
                    FollowRemoteHost(null);
                }
                else
                {
                    BeginRemoteSession(remote, request, profile!);
                    return;
                }

                if (isSsh)
                {
                    try
                    {
                        // Plain SSH, decided above: a mux factory is not asked to route it again here, on the UI thread.
                        Session = SessionFactory is MuxTerminalSessionFactory muxFactory
                            ? muxFactory.CreateNotPersistent(request)
                            : SessionFactory.Create(request);
                        ShellCommand = Session.ShellCommand;
                        ShellArgs = string.Empty;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TerminalPane] SSH connection failed for '{profile!.Name}': {ex.Message}");
                        WriteBanner($"\r\n[ERROR] SSH Connection Failed: {SanitizeBannerValue(ex.Message)}\r\n");

                        // Fail loudly: Do not fall back to RustPtySession with missing arguments.
                        return;
                    }
                }
                else
                {
                    Session = CreateLocalSession(request, out muxPreviousLost);
                    if (Session is null)
                    {
                        // DaemonUnreachable: the shell this pane reopens is still in the daemon.
                        // CreateLocalSession kept its id and wrote the retry banner; nothing to wire.
                        // ShareEnded: the chosen shell is gone; the window closes this pane.
                        return;
                    }
                }

                WireSessionLifecycle(Session, profile);
            }
            catch (Exception ex)
            {
                // Graceful failure: Log and show in terminal
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Failed to spawn session: {ex.Message}");
                WriteBanner($"\r\n[ERROR] Failed to spawn process: {SanitizeBannerValue(effectiveShell)}\r\n[DETAILS] {SanitizeBannerValue(ex.Message)}\r\n");
                return;
            }

            if (Session is not { } spawned) return; // every path above that leaves none returned already
            WireSessionStream(spawned, muxPreviousLost);
        }

        /// <summary>
        /// UI thread, right after <paramref name="session"/> became <see cref="Session"/>: the view sends
        /// to it, its exit reaches <see cref="HandleSessionExit"/>, and the registries hear about it.
        /// </summary>
        private void WireSessionLifecycle(ITerminalSession session, TerminalProfile? profile)
        {
            TermView.SetSession(session);
            session.OnExit += code =>
            {
                this.Dispatcher.Post(() =>
                {
                    HandleSessionExit(session, code);
                });
            };
            // A session on a remote daemon registers once its attach is through, with its host's password scope
            // (RegisterRemoteMuxSession, Phase 5 spec R8); a local daemon's never does.
            if (session is not MuxClientSession) RegisterActiveSshSession(session, profile);
            UpdateCommandAssistContext();

            // Publish the PTY lifecycle to the registration (the endpoint's
            // sweep probes only this published reference, never the pane).
            if (_agentRegistration is { } agentReg)
            {
                agentReg.SetLifecycle(session);

                // Seed the first child-process sample, but OFF the UI thread:
                // ProbeHasActiveChildProcesses() is a full OS process-table scan
                // (CreateToolhelp32Snapshot on Windows, a pgrep spawn elsewhere) —
                // blocking I/O that would jank tab creation. It is thread-safe, so
                // run the probe on a background thread and post only the Sweep back
                // to the UI thread. The endpoint's 1 s sweep corrects it regardless.
                Task.Run(() =>
                {
                    var hasChildren = agentReg.ProbeHasActiveChildProcesses();
                    this.Dispatcher.Post(
                        () => agentReg.StatusMachine.Sweep(hasChildren),
                        DispatcherPriority.Background);
                });
            }
        }

        /// <summary>
        /// UI thread, after <see cref="WireSessionLifecycle"/>: the session's output reaches the parser, the
        /// parser's replies reach the session (unless it answers them itself), and a mux session is wired
        /// and attached, last (spec §8).
        /// </summary>
        private void WireSessionStream(ITerminalSession session, bool muxPreviousLost)
        {
            // Wire up Output. CreateAndWireParser, which session start runs first, sets Parser, and nothing
            // sets it back to null.
            session.OnOutputReceived += text =>
            {
                Parser!.Process(text);

                // After Parse, not before: the flag's contract is "the grid may be behind the
                // keyboard", so it may only be cleared once these bytes are actually painted.
                NoteSessionOutputApplied();
                QueueOutputUiRefresh();
            };

            // Wire up Parser responses (e.g. DA1). The accumulator invalidation for these lives in
            // CreateAndWireParser, next to the parser's other observers.
            //
            // Skipped for a session that answers device queries itself: a multiplexer-attached
            // pane runs a second, downstream parser over a stream whose authoritative parser is
            // in the daemon, and both replying means the child gets two answers to one query.
            // The ObserveDeviceReply observer in CreateAndWireParser deliberately stays wired
            // either way - it only invalidates a local heuristic and writes nothing to the child.
            if (!SessionAnswersDeviceQueries(Session))
            {
                Parser!.OnResponse += response =>
                {
                    Session?.SendInput(response);
                };
            }

            WireReusedTermViewHandlers();
            WireStreamOrderedSession(muxPreviousLost);
        }

        /// <summary>
        /// The non-SSH spawn: through the persistent (mux) factory when there is one, else a plain
        /// session. <paramref name="previousLost"/> is true when a mux session was started in place
        /// of one that is gone; the notice is raised once it has attached.
        /// </summary>
        /// <remarks>
        /// These notices are raised as <see cref="PersistenceNotice"/> (a window toast), never written
        /// into the buffer: the new shell's ConPTY - or the daemon's parser - owns that screen and
        /// paints its first frame over any local text.
        /// </remarks>
        private ITerminalSession? CreateLocalSession(TerminalSessionRequest request, out bool previousLost)
        {
            previousLost = false;
            if (SessionFactory is not IPersistentSessionFactory persistent)
            {
                return SessionFactory.Create(request);
            }

            return ApplyPersistentResult(request, persistent.CreatePersistent(request), out previousLost);
        }



        /// <summary>
        /// Merges global settings with this pane's profile overrides (font, theme, cursor).
        /// Shared by <see cref="ApplySettings"/> (which needs every merged field for
        /// <see cref="TermView"/>) and <see cref="CreateAndWireParser"/> (which only needs
        /// the resulting <see cref="TerminalSettings.ActiveTheme"/> colors for OSC 10/11
        /// answers), so the two call sites can never resolve the profile-effective theme
        /// differently — see the #265 wiring-bug follow-up.
        /// </summary>
        /// <summary>
        /// Hard cap on a single kitty <c>t=f</c> transport read. A 2000×2000 RGBA frame — the
        /// parser's pixel guardrail — is 16 MB, so 64 MB leaves generous headroom for legitimate
        /// staged frames while keeping a hostile path from pulling an arbitrary file into memory.
        /// </summary>
        internal const long KittyTransportMaxFileBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Disk reader behind <see cref="AnsiParser.ReadFileBytes"/> for kitty graphics file
        /// transport. The path comes from the remote byte stream, so it is treated as hostile:
        /// absolute paths only, confined to the user's temp directory (where clients like
        /// terminal-browser stage raw RGBA frames), size-capped, and any failure degrades to a
        /// null the parser logs and skips. Internal + static so the confinement rules are unit
        /// testable without a pane.
        /// </summary>
        internal static byte[]? ReadKittyTransportFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path))
            {
                return null;
            }

            string candidate;
            try
            {
                candidate = System.IO.Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }

            string tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            // Casing follows the platform's filesystem rules: ignore-case is only correct where
            // the filesystem itself is case-insensitive - on Unix, /TMP/frame.rgba is a
            // different directory from /tmp/frame.rgba, and accepting it as "under temp" would
            // let a case-variant path escape the confinement boundary.
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(tempRoot, pathComparison))
            {
                return null;
            }

            try
            {
                var info = new System.IO.FileInfo(candidate);
                if (!info.Exists || info.Length > KittyTransportMaxFileBytes)
                {
                    return null;
                }

                // The writer (e.g. terminal-browser's frame ring) keeps the file open while it
                // cycles frames, so a plain File.ReadAllBytes' FileShare.Read gets rejected with
                // a sharing violation. Open tolerating concurrent writers: the path is only sent
                // after the frame write completes, so the bytes read are the finished frame.
                using var stream = new System.IO.FileStream(
                    candidate,
                    System.IO.FileMode.Open,
                    System.IO.FileAccess.Read,
                    System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);

                // Re-check the cap on the OPENED handle: the file can grow between the
                // FileInfo probe above and this open, and the allocation below trusts the
                // length blindly.
                if (stream.Length > KittyTransportMaxFileBytes)
                {
                    return null;
                }

                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }

                return read == bytes.Length ? bytes : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
