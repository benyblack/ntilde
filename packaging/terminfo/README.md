# `xterm-ntilde` terminfo entry

`xterm-ntilde.terminfo` is the optional terminfo entry for Ntilde: `xterm-256color` plus what
Ntilde adds. Ntilde still sets `TERM=xterm-256color` everywhere; users opt in themselves. See
[TERM and the `xterm-ntilde` entry](../../docs/user/terminal.md#term-and-the-xterm-ntilde-entry)
for installing it locally and on remote hosts.

## Where it comes from

The file is generated, not written by hand. `TerminfoSource` (`src/Ntilde.VT/TerminfoSource.cs`)
builds it from `TerminalCapabilities` (`src/Ntilde.VT/TerminalCapabilities.cs`), the same table
XTGETTCAP answers from (#844). Every table entry is written except those with a
`TerminfoOmission` reason:

- `Co`: the termcap name for `colors`, which the entry carries.
- `RGB`: in a terminfo entry, ncurses reads it as `setaf`/`setab` taking direct colors, which a
  256-color entry does not do. Truecolor is `Tc` with `setrgbf`/`setrgbb`.
- `Ms`: whether OSC 52 writes work depends on a setting and on the session type, which a static
  file can't express. The entry inherits whatever `xterm-256color` says.

`TN` is not in the table: a terminfo entry has no terminal-name capability, and XTGETTCAP does not
answer it because the honest value is the `TERM` the session uses, which VT doesn't know.

## Changing it

Change the table, then regenerate the file:

```bash
NTILDE_UPDATE_TERMINFO=1 scripts/build.sh test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~TerminfoSourceTests"
```

`TerminfoSourceTests` fails while the checked-in file differs from the generated one. On macOS
and Linux it also compiles the file with `tic -x` (no warnings allowed) and reads it back with
`infocmp -x`. Every capability the table writes must come back with the table's value, and every
other capability must equal `xterm-256color`'s.
