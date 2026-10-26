# Shell integration

Shell integration is a small hook in your shell that tells Ntilde where each prompt starts, what you
typed, when a command finishes and with what exit status, and which folder you are in. It does so
with escape sequences the shell prints at each prompt, which are invisible on screen.

## What uses it

- **Command Assist** anchors its suggestions to the prompt, reads the command line, records history
  with the folder it ran in, and proposes fixes from a failed command's output
  ([Command Assist](command-assist.md)).
- **Long command notifications** (Settings → General) need it to know when a command finished.
- **The pane's folder**: the *Switch Tab* entries of the command palette show it, and the *Remote
  Files* sidebar of an SSH tab opens there.
- **Agents** get a precise session status (running, waiting for input, idle, how the last command
  ended) instead of a guess ([Agents](agents.md)).

Without it, all of these still work, less precisely: Command Assist still records the commands you
type straight through, and status falls back to watching the output.

## On this computer

Ntilde sets it up by itself for the shells it starts, when Settings → Command Assist → **Shell
integration** is on (the default). It changes none of your files: your own startup files still run,
first.

| Shell | How |
|---|---|
| bash | Started with `--rcfile` pointing at Ntilde's bootstrap, which sources your `~/.bashrc` first. Not done when the profile's arguments run a command (`-c`). |
| zsh | `ZDOTDIR` points at Ntilde's folder, whose startup files restore your own `ZDOTDIR` and source your `.zshenv`, `.zprofile`, `.zshrc` and `.zlogin`. |
| fish | `XDG_CONFIG_HOME` points at Ntilde's folder, whose `config.fish` sources your `~/.config/fish/config.fish` first. Not done with `-c`, `--no-config` or `-N`. |
| PowerShell | Started with `-NoLogo -NoExit -EncodedCommand`, so your execution policy does not block it. Not done when the profile's arguments already have `-File`, `-Command` or `-EncodedCommand`. |

The bootstrap files live in `command-assist/` in Ntilde's data folder. Turning the setting off
applies to shells started afterwards.

## On a remote host

Ntilde cannot install anything over SSH by itself, so for an SSH host you install the hook once:

1. In Settings → Command Assist → **Remote shell integration**, pick the host's shell: *bash / zsh*,
   *fish* or *PowerShell*.
2. Click **Copy installer** and paste it into a shell on the host. (**Copy plain snippet** copies
   the hook itself, if you would rather put it in place by hand.)
3. Open a new SSH tab.

What the installer writes:

| Shell | File | Loaded by |
|---|---|---|
| bash, zsh | `~/.ntilde-shell-integration.sh` | the line `[ -f ~/.ntilde-shell-integration.sh ] && . ~/.ntilde-shell-integration.sh` in `~/.bashrc` or `~/.zshrc` |
| fish | `~/.config/fish/conf.d/ntilde-shell-integration.fish` | fish, on its own |
| PowerShell | `~/.ntilde-shell-integration.ps1` | the line `. ~/.ntilde-shell-integration.ps1` in `$PROFILE` |

To remove it, delete the file and the loader line. If you used NovaTerminal on that host, remove
its old `~/.nova-shell-integration.sh` (or `.ps1`) loader line too.

Path suggestions stay off in SSH sessions, with or without the hook.

## The sequences

For anyone writing their own hook, or debugging one:

| Sequence | When |
|---|---|
| `OSC 7 ; file://host/path` | At each prompt: the current folder, URL-encoded |
| `OSC 133 ; A` | The prompt starts |
| `OSC 133 ; B` | The prompt ends and your input starts |
| `OSC 133 ; C ; base64 command` | You pressed Enter on a command |
| `OSC 133 ; D ; exit status ; duration in ms` | The command finished |

Extra parameters such as `aid=` are accepted. `OSC 133 ; A` also turns off mouse and focus
reporting that a crashed full-screen program left on.

Ntilde starts shells with `TERM=xterm-256color`, `COLORTERM=truecolor` and `TERM_PROGRAM=Ntilde`,
so a startup file can tell it is running in Ntilde.
