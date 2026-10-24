# Command line

The Ntilde program is also a command-line tool. Run it as `ntilde` (`Ntilde.exe`, or
`/Applications/Ntilde.app/Contents/MacOS/Ntilde` on macOS when it is not on your `PATH`).

| Command | What it does |
|---|---|
| `ntilde` | Opens a window |
| [`ntilde mux …`](#ntilde-mux) | Lists, attaches to and ends the shells of persistent sessions |
| [`ntilde backup …`](#ntilde-backup) | Exports, imports and restores your configuration |
| [`ntilde --replay <file>`](#ntilde---replay) | Prints the final screen of a recording |
| [`ntilde --vt-report`](#ntilde---vt-report) | Prints what escape sequences Ntilde supports |
| [`ntilde-mcp`](#ntilde-mcp) | The MCP server for AI agents, a separate program |

Opening a window takes no options: there is no option yet to choose the profile, the folder or a
command to run, and options Ntilde does not know are ignored.

**Windows.** Typing `ntilde` in PowerShell or cmd runs `ntilde.com`, a small launcher next to
`Ntilde.exe` that waits for a command-line mode to finish and returns its exit code, so
`$LASTEXITCODE` works ([`ntilde.com` on Windows](persistent-sessions.md#ntildecom-on-windows)). The
installer puts it on your `PATH`; with the `.zip`, run it from the folder you extracted.

**Exit codes** are the same across the commands: `0` success, `1` the operation failed, `2` the
command line was wrong.

## `ntilde mux`

Works with the multiplexer that keeps persistent shells running
([Persistent sessions](persistent-sessions.md)).

| Command | What it does |
|---|---|
| `ntilde mux ls [--json]` | Lists the shells: id, state (`running`, `running, detached`, `exited <code>` or `faulted`), attached windows, size and title |
| `ntilde mux ls --all [--json]` | Also lists the shells on the host of every SSH profile that keeps remote sessions |
| `ntilde mux attach <id\|prefix> [--read-only]` | Shows a shell in this terminal; detach with **Ctrl+\ then d** |
| `ntilde mux kill <id>` | Ends one shell |
| `ntilde mux kill-server [--force]` | Ends every shell and stops the multiplexer |

None of these starts a multiplexer: with none running they print "No multiplexer is running." and
exit with `1` (`attach` with `2`). [The command line](persistent-sessions.md#the-command-line) in
the persistent sessions guide covers each in full.

On a remote host, `ntilde-mux` has the same `ls`, `attach`, `kill` and `kill-server` commands for
the shells of persistent SSH tabs ([On the host](persistent-sessions.md#remote-ssh-tabs-that-keep-running)).

## `ntilde backup`

```text
ntilde backup export <path>
ntilde backup import <path> --merge | --replace
ntilde backup list
ntilde backup restore <id>
```

- `export` writes a `.ntildebackup` file with all six categories.
- `import` needs exactly one of `--merge` and `--replace`, after the path. A snapshot is taken first.
- `list` prints the automatic snapshots, one per line: id, why it was taken (`auto`, `pre-import`
  or `pre-restore`), when, and its size. With none, "No snapshots yet."
- `restore` restores a snapshot by the id `list` shows. A snapshot is taken first.

See [Backup and restore](backup.md). Restart Ntilde after an import or restore.

## `ntilde --replay`

```text
ntilde --replay <file.rec> [--attributes]
```

Plays a recording ([Recording and replay](terminal.md#recording-and-replay)) without a window and
prints its final screen as text; `--attributes` adds each cell's colors and styles. A file that ends
early still prints what was read, with a warning, and exits with `1`.

## `ntilde --vt-report`

```text
ntilde --vt-report [--json]
```

Prints a summary of the escape sequences Ntilde supports, counted as supported, partial,
experimental, not supported and won't support. `--json` prints the full report. Attach it to a bug
report about how a program draws in Ntilde.

## `ntilde-mcp`

The MCP server agents connect to. It takes no options; an agent starts it. See
[Connecting an agent](agents.md#connecting-an-agent).

## Environment variables

| Variable | Effect |
|---|---|
| `NTILDE_APPDATA_ROOT` | Uses this folder instead of Ntilde's data folder, for the app, the multiplexer and `ntilde-mcp` ([File locations](troubleshooting.md#file-locations)) |
| `NTILDE_LOG_LEVEL` | `debug`, `info` (the default), `warning` or `error`, for `logs/debug.log` |
| `NTILDE_SSH_FULL_ARGS` | `1` logs the full `ssh` command lines of OpenSSH profiles (identity file paths stay hidden) |
| `NTILDE_BOX_PRIMITIVES` | `0` draws box-drawing characters from the font instead of as lines ([Troubleshooting](troubleshooting.md#text-and-fonts)) |
| `NTILDE_REPO_ROOT` | A checkout of the Ntilde source, for `ntilde-mcp`'s project-documentation tools |

Ntilde sets these in the shells it starts:

| Variable | Value |
|---|---|
| `TERM` | `xterm-256color` |
| `COLORTERM` | `truecolor` |
| `TERM_PROGRAM` | `Ntilde` |
| `NTILDE_MUX_SESSION` | The shell's multiplexer session id, in persistent shells |
