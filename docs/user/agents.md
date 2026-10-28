# Agents

Two features help when you run AI coding agents: the **Agent Output panel**, which renders an
agent's markdown properly beside the pane, and **Agent Access**, which lets an agent read, and if
you allow it, drive your terminal sessions over the Model Context Protocol (MCP).

## Agent Output panel

Coding agents print a lot of markdown into a terminal, where it shows as raw `##` and backticks. The
Agent Output panel renders that output beside the pane while it streams.

**Opening it.** An **MD** button fades in at the top right of a pane when its recent output looks
like markdown; click it to open the panel. If you open the panel with nothing tracked yet, it shows
the latest response already on screen, with prompt lines trimmed.

The panel belongs to its pane. It stays hidden while a fullscreen program owns the screen and comes
back by itself when that program exits.

**The header:**

- **Render** shows fenced blocks as formatted documents. Turn it off to see them as plain code.
  Diff fences are shown as diffs either way.
- **Copy** copies the raw markdown, not the rendered text.
- **✕** closes the panel.

## Agent Access (MCP)

Ntilde can expose your live terminal sessions to AI agents through a local
[Model Context Protocol](https://modelcontextprotocol.io) server. **All of it is off by default.**
With it off there is no live endpoint at all, and the server answers only its offline tools
(validators and terminal reference).

### Turning it on

Settings → **Agent Access** has three switches, each building on the one before
([Settings](settings.md#agent-access)):

- **Agent access (observe)** lets an agent list sessions, read the screen and scrollback, ask a
  session's status, wait for events, and capture a pane as a PNG (`capture_screen`; the picture
  shows everything drawn in the pane, inline images included).
- **Agent replay export** also lets an agent save a session's recent output as a replay file.
  Exports hold output and window resizes only, never anything you type.
- **Agent access (act)** also lets an agent type into, open and close sessions. Every action is
  recorded. An SSH connection must also be allowed one by one: tick **Allow AI agent access to this
  connection** in its profile ([SSH](ssh.md)).

### Connecting an agent

Ntilde ships its MCP server as the program `ntilde-mcp` (`ntilde-mcp.exe` on Windows), which
needs nothing else installed. For **Claude Code**:

```sh
claude mcp add ntilde -- ntilde-mcp
```

`ntilde-mcp` is on your `PATH` after installing with the Windows installer (open a new terminal
first), the `.deb` (`/usr/bin/ntilde-mcp`), the Arch package or the Homebrew cask. Otherwise give its full path:

| Installed from | `ntilde-mcp` is at |
|---|---|
| Windows installer | `%LOCALAPPDATA%\NtildeApp\current\ntilde-mcp.exe` |
| Windows `.zip`, Linux `.tar.gz` | next to `Ntilde.exe` / `Ntilde` |
| macOS `.pkg` or `.zip` | `/Applications/Ntilde.app/Contents/MacOS/ntilde-mcp` |

The AppImage carries it too, but inside the image, where an agent cannot start it: use the `.deb`
or the tarball for agent access. For **Claude Desktop** or **VS Code**, give the same path as the
server's `command`, with no arguments.

### What an agent can do

| Tool | Needs | What it does |
|---|---|---|
| `list_sessions` | observe | Lists the window's sessions (and, with persistent sessions, the multiplexer's windowless ones) |
| `read_screen`, `read_scrollback` | observe | Reads a session's text |
| `get_session_status` | observe | Whether it is running, waiting for input, idle or exited, with the command in flight and its exit code. Precise with [shell integration](shell-integration.md), a guess without it. |
| `wait_for_events` | observe | Waits for status changes, finished commands (with exit code and duration), bells, stalls, and sessions opening or closing |
| `capture_screen` | observe | A PNG of the pane |
| `export_replay` | replay export | Saves recent output as a replay file |
| `send_input`, `spawn_session`, `close_session` | act | Types into, opens and closes sessions |

The server also has offline tools that need no switch: JSON validators for settings, themes and SSH
profiles, and explanations of escape sequences.

With persistent sessions on, an agent also sees the shells the multiplexer keeps that no pane of the
window shows ([Agents and windowless sessions](persistent-sessions.md#agents-and-windowless-sessions)).

### SSH sessions an agent opens

An agent can open an SSH profile only if you allowlisted it. On the native SSH backend, such a
session never shows a host-key or password dialog: it would open over the shell you are typing in.
It signs in only with what needs no question: a host key you already trust, a password saved for
the profile, or, with connection multiplexing, a connection one of your tabs already has open. If
the server asks for anything else (a host key you have not trusted, a password that is not saved,
a key passphrase, a one-time code), the session ends. Its tab says what the server asked, and
pressing Enter there connects as you, with the usual dialogs. The agent is told why its session
ended.

So before you allowlist a profile, connect to it once yourself, trust its host key, and save its
password. With the OpenSSH backend, ssh asks in the terminal itself, in the agent's tab.

### Seeing what an agent did

A pane shows a live indicator while an agent reads, captures or acts on it, and its tab is marked
too ([Indicators](tabs-and-panes.md#indicators)). Every acting call, allowed or refused, is recorded
in the **agent activity journal**, as is every read of a windowless session. Open it from the title
bar's … menu → **Agent Activity...**, or put its button in the title bar from Settings → Tabs &
title bar.

To see a session as an agent saw it, open the replay file it exported with **Open Recording...**
([Recording](terminal.md#recording-and-replay)).
