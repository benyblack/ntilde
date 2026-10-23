# Native SSH: ask for the password once, and once more when it is refused

Date: 2026-10-23. Status: draft, awaiting approval.

## Why

Some servers turn the `password` login method off and ask for the password through
`keyboard-interactive` instead (OpenSSH with `PasswordAuthentication no`, `KbdInteractiveAuthentication yes`,
`UsePAM yes`: PAM sends one round with the question `Password: `). The user's server `sv249049012`
(OpenSSH 9.6p1 Ubuntu, reached over Tailscale) is one: it offers only `publickey,keyboard-interactive`.

With the native backend (rusty_ssh) the user saw, on that server:

1. a normal password window;
2. a second "Keyboard Interactive" window asking for the password again;
3. the session failing with `Unable to receive more messages from the channel`.

Step 3 was a separate bug (the answer to a one-question round was sent with zero answers) and is fixed by
PR #580. Steps 1 and 2 are this design. The user's words: they want "to enter the password only once", and
ticking "remember" should make that server connect next time without asking, like their password hosts do.

Today the first window is wasted: `authenticate()` in `src/Ntilde.App/native/rusty_ssh/src/lib.rs` always asks for
a password and tries the `password` method, whatever the server offers. The server refuses it outright, and the
real password question then comes through `keyboard-interactive`, in a different window that has no "remember",
is never filled from the vault, and does not feed the reconnect logic.

Native file transfers, remote directory listings and remote path autocomplete (`authenticate_transfer`, same file)
have the matching gap: they only ever try the `password` method, so on such a server they fail with
"Authentication failed" even with the right saved password.

Fixing the first gap exposes a second one. Once the saved password is what the server actually checks, a stale
saved password (changed on the server since) is refused, and nothing gives the user a window to type the new one.
Every reconnect fills in the same stale value again. Plain password servers have this dead end today: nothing on the
terminal side forgets a refused saved password, and `NativeSshPromptResponder` re-arms its one-shot vault reuse for
every new session. The user chose to fix it on both kinds of server.

The OpenSSH backend already handles keyboard-interactive password servers: `SshAskPassCommand.IsTargetPasswordPrompt`
treats a keyboard-interactive question that asks for the password as the password. This design brings the native
backend to the same place.

## Goals

- On a server that does not offer `password` but offers `keyboard-interactive`, the user enters the password once,
  in the normal password window, with "remember".
- That answer behaves like any password: filled from the vault when saved, kept for reconnects
  (`ActiveSshSessionRegistry`), saved to the vault when "remember" is ticked, and available to the mux's
  automatic reconnect.
- On both kinds of server, a refused password gets one more password window, never filled in, with "remember".
  Ticking it replaces the stale saved password.
- File transfers, directory listings and path autocomplete log in to keyboard-interactive password servers with the
  password they are already given today (saved or typed earlier in the session).

## Non-goals

- Second factors (codes, push approvals, several questions in one round). They keep the keyboard-interactive
  window and are never filled from the vault. Transfers still cannot answer them.
- Skipping public key or agent attempts when the server does not offer `publickey`.
- The OpenSSH backend. It already handles these servers and has its own retry (ssh asks three times).
- Retries for transfers. They have no window to ask in. A refused password keeps today's `AuthenticationFailed`.
- More than one retry.

## Anti-goals (failure even if it "works")

- A saved password sent to any server other than the one it was saved for (a jump hop, or a question that is not
  a password question). Today's hop scoping (`AuthHop`, `TransferAuthConfig::password_for`, `IsJumpHop`) must hold.
- A second-factor question, or an expired password's `New password:`, filled with the saved password or saved to the
  vault as the password.
- The retry window filled in from any store, so a refused value is sent twice without asking.
- A server that connects today failing after this change.

## Behavior

### Step 0: ask the server what it accepts

Before any credential, the client sends a `none` authentication request (`Handle::authenticate_none(user)`,
russh 0.59). OpenSSH does the same, and sshd does not count that first `none` against `MaxAuthTries`. The reply is
one of:

- **Success**: the server needs no credentials (e.g. Tailscale SSH with a permissive policy). Authentication is
  done.
- **Failure with a method list**: the server's methods.
- **An error, or an empty list**: russh reports a dropped connection as an empty list and ends the connection, so
  sign-in fails with the error the next step reports, as it would today.

The method list then follows the server's latest answer, as OpenSSH's does: every failure from a later attempt (a
refused or partially accepted key, a refused password) replaces it. A server set to
`AuthenticationMethods publickey,keyboard-interactive` lists only `publickey` at first and `keyboard-interactive`
after the key is partially accepted, so it then takes path 3 below.

This happens for every server of a chain (jump hops and target), in both `authenticate()` and
`authenticate_transfer()`.

### Terminal sessions and exec channels (`authenticate()`)

1. Identity file, then agent: unchanged.
2. **The methods include `password`:**
   1. Password window (filled from the runtime store or the vault, as today), `password` method.
   2. Refused, and the server still offers `password`: **one more password window, marked as a retry** (below),
      `password` method again.
   3. Refused again: the keyboard-interactive fallback with its own window, as today.
3. **The methods do not include `password` but include `keyboard-interactive`:** no password window up front.
   Go straight to keyboard-interactive. Within one keyboard-interactive exchange, each round from the server is
   handled as follows:
   - **No questions**: answered with zero answers, with no event to the app. (PAM sends this round after a correct
     password. PR #580 makes the app answer it without a window; answering in Rust means it never leaves the native
     layer.)
   - **The exchange's first password question** (rule below): raised as a normal `PasswordPrompt` event carrying the
     server's question text and the hop, and answered with a `Password` response. The text is sent back as the
     round's single answer. The app sees an ordinary password prompt, so vault fill, the one-shot vault reuse in
     `NativeSshPromptResponder`, the runtime password, "remember" and the mux's handling all apply unchanged.
   - **Anything else**, including later password questions in the same exchange: the keyboard-interactive window,
     as today. This covers an expired password, where PAM asks `Current password:`, `New password:` and
     `Retype new password:` in the same exchange after accepting the password.

   If the exchange fails after the converted password was sent, and the server still offers
   `keyboard-interactive`: **one more exchange, whose first password question is raised as a password window marked
   as a retry.** Refused again: sign-in ends with `SSH authentication failed`.
4. **The methods include neither:** unchanged (today's code tries the password and then keyboard-interactive, and the
   server refuses both).

**The retry window.** The `PasswordPrompt` event gains an additive field that says it follows a refusal. The app
then never fills it, neither from the runtime store (`ActiveSshSessionRegistry`, which `SshInteractionService`
consults first today) nor from the vault. It shows a normal password window with "remember". The typed answer
replaces the runtime password, and with "remember" ticked it replaces the vault entry. The mux already reads a
second password prompt on the same hop as a refusal of the first, which this is. An automatic (no-user) mux
reconnect cannot show the window, so it cancels, and sign-in stops.

**Cancel.** Cancelling a password window stops sign-in without sending anything. Today a cancel is sent as an
empty password (`{"text":""}` from `NativeSshInteractionJson.BuildResponsePayload`), which the server counts as a
failed login before sign-in carries on to the next method. The text response gains an additive `canceled` field.
Rust's `prompt_text` stops with `SSH prompt canceled` when it is set, which the app already classifies as an
authentication failure. A real empty password (OK on an empty field) is still sent. The same field on the
keyboard-interactive response makes cancelling that window stop sign-in too, instead of sending zero answers and
ending in the cryptic disconnect. This applies to every password, passphrase and keyboard-interactive window.

### Transfers, listings and autocomplete (`authenticate_transfer()`)

1. Identity file, then agent: unchanged.
2. If there is no password for this hop: unchanged errors.
3. If the methods include `password`: unchanged (the `password` method only).
4. If the methods do not include `password` but include `keyboard-interactive`: keyboard-interactive, answered
   without any window:
   - **No questions**: zero answers.
   - **The exchange's first password question**: the hop's own password (`password_for(hop)`), as the single answer.
   - **Anything else**, including a later password question: stop, with a plain error (not the
     `AuthenticationFailed` kind) that says the server asks for more than a password at sign-in, which transfers
     cannot answer.
   - **Refused** after the password was sent: the same `AuthenticationFailed` error kind the `password` method
     returns, so the app forgets a stale runtime password exactly as it does today (release hardening item 5). A
     failure before any password was sent is not `AuthenticationFailed`.
5. If the methods include neither: unchanged.

### The password-question rule

A keyboard-interactive round is a password question when it has exactly one question, that question is not echoed
(`echo == false`), and its text, trimmed of whitespace:

- ends with `password:` (case-insensitive), or
- is exactly `Password for <account>:` (case-insensitive lead, an account with no whitespace in it): FreeBSD's
  pam_unix and Kerberos.

This is the text part of `SshAskPassCommand.IsTargetPasswordPrompt`. The `(user@host) ` prefix that rule also
checks is written by the OpenSSH client and does not exist here: the native layer knows the server from the hop it
is authenticating. The round's name and instructions are not consulted.

Text that only mentions a password somewhere ("Password or code from your phone?") does not match. Second-factor
questions in practice are worded as codes (`Verification code:`, `Passcode or option (1-3):`) and do not match
either. A round that asks for a password and a code together has two questions and does not match. Using the
OpenSSH backend's rule exactly is the point: both backends then agree about which questions are passwords.

## Decided by the user

- Enter the password once; "remember" works on these servers like on password servers.
- Transfers to these servers log in with the saved password as part of this change.
- A refused password gets one more window, never filled in, on both kinds of server.

## My calls (the user can overrule)

- On servers offering both methods, `password` stays first, as today.
- The method list follows the server's latest answer, not just the first `none` reply.
- Only the first password question of each keyboard-interactive exchange becomes a password window. Later ones
  (an expired password's change questions) keep the keyboard-interactive window.
- Cancelling any password, passphrase or keyboard-interactive window stops sign-in without sending anything. This
  also fixes the cryptic error after cancelling a keyboard-interactive window, which PR #580 listed as a follow-up.
- The retry is one more window, not OpenSSH's three tries.
- The rule and the conversion live in Rust, once, for both sessions and transfers. The alternative, converting in
  .NET (`NativeSshPromptResponder`), would need a second copy in Rust for transfers anyway.
- Zero-question rounds are answered in Rust in both paths. The app-side handling from PR #580 stays as a backstop.

## Constraints

- The FFI contract changes only additively: a retry flag on the password prompt payload and a `canceled` flag on
  the text and keyboard-interactive response payloads. Both default to false when absent, and the event and
  response kinds are unchanged. The .NET changes are parsing the retry flag into `SshInteractionRequest`, skipping
  both stores for it in `SshInteractionService`, and writing `canceled`.
- Secrets stay wrapped (`Zeroizing`) as in `prompt_text` and `wait_keyboard_responses`.
- Builds via `scripts/build.ps1`. App.Tests run as two lanes (see repo memory). Docker E2E needs
  `NTILDE_ENABLE_DOCKER_E2E=1`, or it skips with exit 0.

## Testing

- **Rust, in-process**: `exec_session_e2e_tests` in `lib.rs` already runs a russh server (`TestServer`). Give it a
  keyboard-interactive-only mode, an accepts-none mode, and a mode that asks a second password question in the same
  exchange. Pin, through the FFI poll loop:
  - exactly one `PasswordPrompt` and no `KeyboardInteractivePrompt` on a keyboard-interactive-only server;
  - a refused answer there gets one retry-flagged `PasswordPrompt`, then sign-in fails;
  - on a password server, a refused password gets one retry-flagged `PasswordPrompt`;
  - a second password question in one exchange, and a second-factor round, are raised as
    `KeyboardInteractivePrompt`;
  - a zero-question round is answered without an event;
  - `none` success connects with no prompt;
  - a `canceled` response stops sign-in without the server receiving an answer;
  - the rule's accept and reject table.
- **Rust, transfers**: `authenticate_transfer` against the same server modes: a password question is answered with
  the hop's password; a second-factor round, or a second password question, fails with the non-auth error; a
  refusal gives `AuthenticationFailed`.
- **.NET**: `SshInteractionService` never fills a retry-flagged request from the runtime store or the vault, shows
  "remember" on it, and stores what was typed. `NativeSshInteractionJson` parses the retry flag and writes
  `canceled`.
- **Docker E2E (Platform.Tests)**: the fixture's `kbdnova` user is exactly this server's setup.
  - Session: one `Password` request, no `KeyboardInteractive` request, then a shell. Update the comment and assertion
    in `KeyboardInteractiveAuth_ForAUserThatAcceptsNothingElse_Authenticates`, which documents today's double prompt.
  - Session with a wrong first answer: a retry-flagged `Password` request, whose right answer reaches the shell.
    The same for the password user.
  - Transfer: a download as `kbdnova` with the password succeeds. With a wrong password it returns
    `AuthenticationFailed`, mirroring `NativeSftp_BadPassword_ReturnsAuthenticationFailed`.
- **Existing**: the password-user Docker tests and the jump-chain tests must stay green.

## Left to the builder

- How the method list is threaded through `authenticate()`, `authenticate_transfer()` and the key and agent
  helpers (`try_agent_auth` returns a bare `bool` today), and whether the keyboard-interactive loop is shared between
  the session and transfer paths.
- The names of the new JSON fields, and the error wording for the transfer second-factor case.
- Whether PR #580 has merged first. This branch is cut from `main` without it. Both change
  `SshInteractionService.cs`, in different methods, so whichever lands second rebases.
