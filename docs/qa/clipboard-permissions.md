# OSC 5522 clipboard read permissions

Direct desktop and SSH terminals ask before sending clipboard data to a program.
The dialog shows the terminal context separately from the program-supplied name.
Choose **Deny**, **Allow once**, or, when the request supplies a visible name and
nonempty password, **Allow this session**.

A session approval allows later reads with the same full name and password on
that terminal. Any program sharing that terminal and those credentials can use
it; the name is not an independently verified application identity. Generate a
new random password for each program launch, as recommended by the
[Kitty protocol](https://sw.kovidgoyal.net/kitty/clipboard/#avoiding-repeated-permission-prompts).

Approvals live only in that terminal's App service. It retains SHA-256 hashes of
the full name and password, with at most 32 remembered grants; admitting a 33rd
forgets the oldest. Nothing is written to settings, the credential vault, disk,
or terminal snapshots. Resetting the terminal (RIS), replacing its parser,
process exit, or closing the pane revokes the grants. A cancelled prompt cannot
restore permission after a reset. Raw client OSC packets may still occur in
recorded PTY output or the parser's incomplete input accumulator.

Allow once and Deny never create a reusable grant. A password without a visible
name cannot be remembered. MIME listings still require no prompt. Read approval
does not override `AllowOsc52ClipboardWrite`, enable OSC 52 reads or enable clipboard I/O in mux/headless hosts. Linux/X11
[primary selection](clipboard-primary.md) requires a separate approval for that
location; an approval for the regular clipboard cannot be reused for PRIMARY.

User paste tokens remain separate: they authorize one read of values captured
at that paste gesture, for 15 seconds on that terminal. Using a paste token does
not create a session approval or authorize a newer clipboard value. In contrast,
an explicitly remembered read approval can read later clipboard contents.

`KittyClipboardSessionTests` covers credential and TTY scope, changed clipboard
contents, allow-once/denial, missing identity, bounded eviction, reset and late
approval, write-policy separation, primary refusal, and the existing one-use
paste-token regressions. Native format interchange is covered separately by the
[native clipboard qualification workflow](clipboard-native.md).
