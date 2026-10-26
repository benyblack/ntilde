# Command Assist

Command Assist suggests commands as you type, searches your history, proposes a fix when a command
fails, and keeps snippets. It is anchored to what the shell reports through shell-integration marks
(OSC 133) rather than to a guess about where the prompt ends, and it reads the live command line
from the terminal's screen rather than from a copy of your keystrokes, so it stays correct through
re-prompts, wrapped lines and edits.

Everything runs locally. There is no network call.

**It is off by default.** Turn it on with Settings → Command Assist → **Command assistant**
([Settings](settings.md#command-assist)).

## Using it

| Action | Windows / Linux | macOS |
|---|---|---|
| Open or close | `Ctrl+Space` | `Ctrl+Space` |
| Search history | `Ctrl+R` | `Cmd+R` |
| Help for the command you are typing | `Ctrl+Shift+H` | `Cmd+Shift+H` |
| Pin or unpin the selection as a snippet | `Ctrl+Shift+S` | `Cmd+Shift+S` |

While its list is open, `Up` / `Down` browse, `Enter` accepts the row and runs it, `Ctrl+Enter`
inserts it without running, and `Escape` closes the list. Accepting a history row replaces what you
had typed.

History, Help and Pin take their key only when there is something to act on, so `Ctrl+R` reaches
your shell's own history search while Command Assist has nothing to show. On macOS the history key
is `Cmd+R`, which leaves `Ctrl+R` to the shell.

**The suggestion bubble.** After two characters, the best match from your history appears above the
prompt; `Escape` hides it. Its hint strip shows your current keys, so it stays right after you
rebind them. Turn the bubble off with **Suggestion bubble while typing** to keep Command Assist
silent until you open it.

Command Assist hides itself while a fullscreen program (`vim`, `less`, `htop`) owns the screen.

## Fixing a failed command

When a command fails, Command Assist can propose a correction worked out from that command's own
output, not from a generic rule. It keeps the failing command's output for this purpose.

## History, knowledge and snippets

- **History** is recorded locally, in `command-assist/history.jsonl` in Ntilde's data folder, one
  command per line, along with where you ran it. Arguments that look like secrets (`--password`,
  tokens, bearer headers) are redacted. Turn recording off with **Remember commands**, or delete it
  with **Clear history**; the limit is 5000 commands (`CommandAssistMaxHistoryEntries`).
- **Descriptions** of common commands and their usual invocations come from a catalogue built from
  [tldr](https://tldr.sh) pages, shipped with Ntilde.
- **Snippets.** Pin any suggestion or history row with `Ctrl+Shift+S` to keep it as a snippet.
  Settings → Command Assist → **Saved snippets** lists them and lets you add and edit them. They are
  kept in `command-assist/snippets.json`.

## Shell integration

Command Assist works best with shell integration on, so it knows where the prompt is, what you
typed and how each command ended. For local shells Ntilde sets it up by itself; on an SSH host you
install it once. See [Shell integration](shell-integration.md).

Without any marks, Command Assist still records the commands you type straight through. You lose
the anchoring, not the feature. Path suggestions are local only and stay off in SSH sessions.
