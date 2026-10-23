# Ask for the password once, and once more when refused: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On keyboard-interactive-only SSH servers the native backend asks for the password once, in the normal
password window, and on every server a refused password gets one more window that is never filled in.

**Architecture:** rusty_ssh asks the server's methods with a `none` request and tracks the latest method list. When
the server takes keyboard-interactive but not `password`, a shared keyboard-interactive loop raises the exchange's
first password question as an ordinary `PasswordPrompt` (sessions) or answers it with the hop's password
(transfers). A retry prompt carries a `followsRefusal` flag, which the app honours by filling it from no store. A
`canceled` flag on responses lets a cancel stop sign-in instead of sending an empty answer.

**Tech Stack:** Rust (russh 0.59.0, serde, zeroize) in `src/Ntilde.App/native/rusty_ssh`; C# .NET 10 (Ntilde.Platform,
Ntilde.App), xUnit v3, Avalonia headless (`[AvaloniaFact]`), Docker E2E fixture.

**Spec:** `docs/superpowers/specs/2026-10-23-kbd-interactive-password-once-design.md`

## Global Constraints

- russh stays at 0.59.0. No new crates or NuGet packages.
- The FFI contract changes only additively: `followsRefusal` (bool) on the text prompt payload, `canceled` (bool) on
  the text and keyboard-interactive response payloads. Absent means false. The event and response kinds are unchanged.
- The .NET side omits `canceled` when false, so existing payloads stay byte-identical.
- Every secret Rust holds stays in `Zeroizing` (as `prompt_text` and `wait_keyboard_responses` do today).
- The password-question rule is the text half of `SshAskPassCommand.IsTargetPasswordPrompt`: one question,
  `echo == false`, trimmed text ends with `password:` (case-insensitive) or is exactly `Password for <account>:`
  (case-insensitive lead, account non-empty with no whitespace).
- Only the first password question of each keyboard-interactive exchange is converted.
- One retry, never more. Transfers never retry.
- Build and test .NET only through `scripts/build.ps1` (PowerShell tool). Rust: `cargo test --manifest-path
  src/Ntilde.App/native/rusty_ssh/Cargo.toml <filter>` from the worktree root.
- App.Tests targeted runs end their filter with `&Lane!=PlatformBoot`, pass `--blame-hang-timeout 5m`, log to a file
  in the scratchpad, and never run concurrently with another test run. `$scratch` below is the session's scratchpad
  directory.
- Docker E2E runs need `$env:NTILDE_ENABLE_DOCKER_E2E = "1"`. Without it they skip with exit 0, so read the log for
  `Passed!` or `Failed!`.
- Files are CRLF: edit with the Edit tool, never `sed -i`.
- Commits: `fix(ssh): ...` subject, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Comments match the surrounding code: doc comments say why, and name the review or spec item when there is one.

## Review Focus

1. A keyboard-interactive-only **jump host**: the converted prompt must say `isJumpHop: true`, so the app never fills
   it with the target's saved password. Test: Task 5, step 1 (`a_converted_prompt_names_its_hop`).
2. An **informational round with no questions before** the password question: the password question is still the
   exchange's first password question and is converted. Test: Task 5 (server mode `KeyboardEmptyRoundFirst`).
3. A password accepted with **partial success** (`AuthenticationMethods password,keyboard-interactive`): not a
   refusal, so no retry window, and the code question follows. Test: Task 6 (server mode `PasswordThenCode`).
4. A **localized or echoed** password question (`Passwort:`, or echo on): not converted; the keyboard-interactive
   window as today. Test: Task 1 rule table.
5. **Cancel on the retry window**: sign-in stops, and the server never receives a second password. Test: Task 6.

---

### Task 1: The password-question rule (Rust)

**Files:**
- Modify: `src/Ntilde.App/native/rusty_ssh/src/lib.rs` (new functions next to `authenticate_keyboard_interactive`;
  tests in `mod tests`)

**Interfaces:**
- Produces: `fn is_password_text(text: &str) -> bool` and
  `fn password_question(prompts: &[client::Prompt]) -> Option<&str>` (the question's text when the round is exactly
  one non-echoed password question).

- [ ] **Step 1: Write the failing tests** in `mod tests`:

```rust
#[test]
fn password_text_follows_the_askpass_rule() {
    for text in ["Password:", "Password: ", "  password:  ", "alice@host's password:",
                 "Enter PASSWORD:", "Password for alice@example.com:", "password for alice@EXAMPLE.COM:"] {
        assert!(is_password_text(text), "{text:?} asks for the password");
    }
    for text in ["Verification code:", "Passcode or option (1-3):", "Password or code from your phone?",
                 "Password for:", "Password for two words:", "Passwort:", "New password", ""] {
        assert!(!is_password_text(text), "{text:?} is not a password question");
    }
}

#[test]
fn only_a_round_of_one_hidden_password_question_is_the_password() {
    let ask = |prompt: &str, echo: bool| client::Prompt { prompt: prompt.to_owned(), echo };
    assert_eq!(Some("Password: "), password_question(&[ask("Password: ", false)]));
    assert_eq!(None, password_question(&[ask("Password: ", true)]));
    assert_eq!(None, password_question(&[ask("Password: ", false), ask("Verification code:", false)]));
    assert_eq!(None, password_question(&[]));
}
```

Note `"New password"` (no colon) is rejected while `"New password:"` would match: the rule matches the text, and
"only the first one is converted" (Task 5) keeps an expired password's change questions out.

- [ ] **Step 2: Run them to see them fail**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml password_`
Expected: compile error, `is_password_text` / `password_question` not found.

- [ ] **Step 3: Implement both functions.** Doc comment on `is_password_text` names
  `SshAskPassCommand.IsTargetPasswordPrompt` as the rule's other copy and says why the `(user@host) ` prefix is absent
  here (the native layer knows the server from the hop).

- [ ] **Step 4: Run the tests to see them pass.** Same command. Expected: 2 passed.

- [ ] **Step 5: Commit** `fix(ssh): rusty_ssh recognizes a keyboard-interactive password question`

---

### Task 2: Cancel stops sign-in (Rust and .NET)

**Files:**
- Modify: `src/Ntilde.App/native/rusty_ssh/src/lib.rs` (`TextResponse`, `KeyboardInteractiveResponse`,
  `prompt_text`, `wait_keyboard_responses`)
- Modify: `src/Ntilde.Platform/Ssh/Native/NativeSshInteractionJson.cs` (`NativeTextResponsePayload`,
  `NativeKeyboardInteractiveResponsePayload`, `BuildResponsePayload`)
- Modify: `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxInteractionHandler.cs`. In `Attempt.HandleAsync`, the comment
  "rusty_ssh would send the cancel as an empty password" stops being true; the abort itself stays. The last line
  answers a keyboard-interactive round with no questions with `SshInteractionResponse.Cancel()`. That becomes
  `SshInteractionResponse.FromKeyboardResponses()`, because a cancel now stops sign-in.
- Test: `mod tests` in lib.rs; `tests/Ntilde.Platform.Tests/Ssh/NativeSshInteractionJsonTests.cs`;
  `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxInteractionHandlerTests.cs`

**Interfaces:**
- Produces (Rust): `TextResponse { text: String, #[serde(default)] canceled: bool }`,
  `KeyboardInteractiveResponse { responses: Vec<String>, #[serde(default)] canceled: bool }`. `prompt_text` returns
  `Err` with message `SSH prompt canceled` when `canceled`; `wait_keyboard_responses` returns `Err` with
  `Keyboard-interactive prompt canceled`. Both messages already map to `NativeSshFailureKind.Authentication`.
- Produces (.NET): `NativeTextResponsePayload.Canceled` and `NativeKeyboardInteractiveResponsePayload.Canceled`,
  `bool`, `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]`; `BuildResponsePayload` sets them from
  `response.IsCanceled`.

- [ ] **Step 1: Write the failing Rust tests.** Each queues a response on a fresh `SharedState`
  (`shared.queue_response(QueuedResponse { kind, payload })`) and then calls the waiter:

```rust
fn test_hop() -> AuthHop<'static> {
    AuthHop { host: "target.internal", port: 22, user: "nova", is_jump_hop: false }
}

fn answer(shared: &Arc<SharedState>, kind: NovaSshResponseKind, json: &str) {
    shared.queue_response(QueuedResponse { kind, payload: json.as_bytes().to_vec() });
}

#[test]
fn a_canceled_text_answer_stops_sign_in() {
    let shared = Arc::new(SharedState::new());
    answer(&shared, NovaSshResponseKind::Password, r#"{"text":"","canceled":true}"#);
    let error = prompt_text(&shared, NovaSshEventKind::PasswordPrompt, "Password:", NovaSshResponseKind::Password, test_hop())
        .err()
        .expect("a cancel must not become an empty password");
    assert_eq!("SSH prompt canceled", error.to_string());
}

#[test]
fn an_empty_password_is_still_a_password() {
    let shared = Arc::new(SharedState::new());
    answer(&shared, NovaSshResponseKind::Password, r#"{"text":""}"#);
    let password = prompt_text(&shared, NovaSshEventKind::PasswordPrompt, "Password:", NovaSshResponseKind::Password, test_hop())
        .ok()
        .expect("an empty password is an answer");
    assert_eq!("", password.as_str());
}

#[test]
fn a_canceled_keyboard_answer_stops_sign_in() {
    let shared = Arc::new(SharedState::new());
    answer(&shared, NovaSshResponseKind::KeyboardInteractive, r#"{"responses":[],"canceled":true}"#);
    let error = wait_keyboard_responses(&shared).err().expect("a cancel must not become zero answers");
    assert_eq!("Keyboard-interactive prompt canceled", error.to_string());
}
```

`prompt_text` gains its `follows_refusal: bool` parameter in Task 5, which updates these calls.
`an_empty_password_is_still_a_password` passes before the change too: it guards that the fix does not swallow real
empty passwords.

In `RemoteMuxInteractionHandlerTests.A_passphrase_an_empty_keyboard_round_and_an_untrusted_host_key_are_still_answered`,
add `Assert.False(keyboard.IsCanceled);` next to `Assert.Empty(keyboard.KeyboardResponses);`.

- [ ] **Step 2: Write the failing .NET tests** in `NativeSshInteractionJsonTests`:

```csharp
[Fact]
public void BuildResponsePayload_ACancelledPasswordSaysSo()
    => Assert.Equal("""{"text":"","canceled":true}""",
        Encoding.UTF8.GetString(NativeSshInteractionJson.BuildResponsePayload(NativeSshResponseKind.Password, SshInteractionResponse.Cancel())));

[Fact]
public void BuildResponsePayload_ATypedPasswordCarriesNoCancelField()
    => Assert.Equal("""{"text":"hunter2"}""",
        Encoding.UTF8.GetString(NativeSshInteractionJson.BuildResponsePayload(NativeSshResponseKind.Password, SshInteractionResponse.FromSecret("hunter2"))));

[Fact]
public void BuildResponsePayload_ACancelledKeyboardRoundSaysSo()
    => Assert.Equal("""{"responses":[],"canceled":true}""",
        Encoding.UTF8.GetString(NativeSshInteractionJson.BuildResponsePayload(NativeSshResponseKind.KeyboardInteractive, SshInteractionResponse.Cancel())));
```

The existing `BuildResponsePayload_SerializesKeyboardInteractiveResponsesDeterministically` must stay green unchanged.

- [ ] **Step 3: Run both to see them fail**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml canceled`
Expected: the canceled tests fail (`Ok` returned).
Run (PowerShell): `./scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~NativeSshInteractionJsonTests"`
Expected: the 2 cancel tests fail.
Run (PowerShell): `./scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "FullyQualifiedName~RemoteMuxInteractionHandlerTests&Lane!=PlatformBoot" *> $scratch/task2-mux.log`
Expected: the empty-round test fails (`IsCanceled` is true).

- [ ] **Step 4: Implement** the fields and checks listed under Interfaces, and the mux's empty-round answer. Update
  the mux comment to say a password cancel would stop sign-in, and the abort keeps an automatic attempt from looking
  like the user's cancel.

- [ ] **Step 5: Run all three again to see them pass.** Same commands. Expected: all pass.

- [ ] **Step 6: Commit** `fix(ssh): cancelling a sign-in window stops sign-in instead of sending an empty answer`

---

### Task 3: The app honours a retry prompt (.NET)

**Files:**
- Modify: `src/Ntilde.Platform/Ssh/Interactions/SshInteractionRequest.cs` (new property; copy it in
  `WithoutVaultPasswordReuse`)
- Modify: `src/Ntilde.Platform/Ssh/Native/NativeSshInteractionJson.cs` (`NativeTextPromptPayload.FollowsRefusal`,
  `CreateTextPromptRequest`)
- Modify: `src/Ntilde.Platform/Ssh/Native/NativeSshPromptResponder.cs` (`WithProfileContext` copies it)
- Modify: `src/Ntilde.App/Services/Ssh/SshInteractionService.cs` (`TryHandlePasswordFromVault`,
  `CreatePasswordMessage`)
- Modify: `src/Ntilde.App/Shell/Mux/Remote/RemoteMuxInteractionHandler.cs` (`Attempt.HandleAsync`; class comment
  paragraph "The native layer asks for a password once and then falls to keyboard-interactive")
- Test: `tests/Ntilde.Platform.Tests/Ssh/NativeSshInteractionJsonTests.cs`,
  `tests/Ntilde.Platform.Tests/Ssh/NativeSshPromptResponderTests.cs`,
  `tests/Ntilde.App.Tests/Ssh/SshInteractionServiceTests.cs`,
  `tests/Ntilde.App.Tests/Shell/Mux/Remote/RemoteMuxInteractionHandlerTests.cs`

**Interfaces:**
- Produces: `SshInteractionRequest.FollowsRefusal` (`bool`, init). Doc comment: the server refused the password
  this server was just sent, so this prompt is answered by the user, never from the vault, the session's runtime
  store, or the mux's memory.
- Consumes: JSON field `followsRefusal` on `PasswordPrompt` payloads (emitted by Task 5 and Task 6).

- [ ] **Step 1: Write the failing tests.**
  - `NativeSshInteractionJsonTests.ParseRequest_PasswordPrompt_CarriesFollowsRefusal`: payload
    `{"prompt":"Password: ","host":"h","port":22,"user":"u","isJumpHop":false,"followsRefusal":true}` gives
    `FollowsRefusal == true`; the existing payloads without the field give `false` (add that assert to
    `ParseRequest_PasswordPrompt_FromTheTarget_IsNotAJumpHop`).
  - `NativeSshPromptResponderTests.A_retry_prompt_keeps_its_flag_through_the_profile_context`: a `PasswordPrompt`
    event with `followsRefusal:true` reaches the handler with `FollowsRefusal == true`, `ProfileId` set, and
    `AllowVaultPasswordReuse == false` even though it is the connection's first password prompt. Use the file's
    existing fake interop.
  - `SshInteractionServiceTests` (`[AvaloniaFact]`, following `PasswordAnswers_SayWhetherTheyWereFilledFromAStoreOrTyped`;
    give `TargetPasswordRequest` an optional `bool followsRefusal = false`):
    - `RetryPrompt_IsNeverFilledFromTheRuntimeStoreOrTheVault`: vault and runtime store both hold a value;
      `TargetPasswordRequest(profile, sessionId, allowVaultPasswordReuse: true)` with `FollowsRefusal = true`
      shows the dialog once and returns `"typed-secret"` with `FilledFromStore == false`.
    - `RetryPrompt_OffersRemember_AndReplacesTheStoredPasswords`: the presenter ticks remember and answers
      `"new-secret"`; afterwards `vault.GetSshPasswordForProfile(profile) == "new-secret"` and the registry's
      runtime password for the target is `"new-secret"`. Assert `CanRememberPassword` on the presented view model.
    - `RetryPrompt_SaysTheServerRefusedThePassword`: the presented view model's `Message` is
      `"The server refused that password. Enter the SSH password for alice@example.internal to continue."`
  - `RemoteMuxInteractionHandlerTests.A_retry_prompt_is_never_answered_from_memory`: remember `"old"` via
    `RememberAsync`; an automatic attempt's retry-flagged `Password` prompt throws
    `RemoteMuxPromptAbortedException`; an interactive attempt's retry-flagged prompt asks the `ScriptedUser` and
    returns its answer, not `"old"`.

- [ ] **Step 2: Run them to see them fail**

Run (PowerShell): `./scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~NativeSshInteractionJsonTests|FullyQualifiedName~NativeSshPromptResponderTests"`
Expected: compile error (`FollowsRefusal` missing).

- [ ] **Step 3: Implement.**
  - `TryHandlePasswordFromVault` returns false for `request.FollowsRefusal` before either store is read.
  - `CreatePasswordMessage` prefixes `"The server refused that password. "` when `FollowsRefusal`. That covers the
    jump-host wording too.
  - `Attempt.HandleAsync`: for a `Password` prompt with `FollowsRefusal`, skip `Recall` and `TakeSavedPassword`.
    With no user, `throw Abort(kind)`. With a user, ask as today. `Advance` already supersedes the refused answer.
  - Rewrite the mux class-comment sentence: the native layer now asks once more after a refusal with a prompt that
    says so (`FollowsRefusal`), or falls to keyboard-interactive. Either way a refusal still shows as another auth
    prompt after it. Fix the matching sentence in the `RemoteMuxInteractionHandlerTests` class comment.

- [ ] **Step 4: Run the tests to see them pass**

Run: the Platform.Tests command above, then (PowerShell)
`./scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --filter "(FullyQualifiedName~SshInteractionServiceTests|FullyQualifiedName~RemoteMuxInteractionHandlerTests)&Lane!=PlatformBoot" *> $scratch/task3-app.log`
Expected: `Passed!`, 0 failed.

- [ ] **Step 5: Commit** `fix(ssh): a password prompt after a refusal is never filled in`

---

### Task 4: Ask the server first, and track its methods (Rust)

**Files:**
- Modify: `src/Ntilde.App/native/rusty_ssh/src/lib.rs`: `authenticate`, `try_agent_auth`, `try_one_agent`, and new
  `ServerMethods`, `NoneProbe` and `ask_server_methods`. Test server in `mod exec_session_e2e_tests`.

**Interfaces:**
- Produces:
  - `struct ServerMethods(Option<russh::MethodSet>)`, `Default` = unknown.
    - `fn note(&mut self, result: &AuthResult)`: a `Failure` with a non-empty list replaces the list.
    - `fn note_remaining(&mut self, remaining: &MethodSet)`: same, for keyboard-interactive failures.
    - `fn offers(&self, kind: MethodKind) -> bool`: known and listed.
    - `fn password_through_keyboard_interactive(&self) -> bool`: known, no `Password`, has `KeyboardInteractive`.
  - `enum NoneProbe { Authenticated, Methods(ServerMethods) }`.
  - `async fn ask_server_methods<H: client::Handler + Send + 'static>(user: &str, session: &mut client::Handle<H>) -> NoneProbe`.
    `Success` gives `Authenticated`. A failure gives its list. An `Err` gives unknown methods: the next step reports
    the dropped connection.
  - `async fn try_agent_auth<H>(user: &str, session: &mut client::Handle<H>, methods: &mut ServerMethods) -> bool`.
    `try_one_agent` takes the same extra parameter and notes every refusal.
- Produces (test module): `enum ServerAuth` and `fn start_server_with(auth: ServerAuth) -> (u16, SeenLog)`.
  `start_server()` stays as `start_server_with(ServerAuth::Password)`. The server `Config` sets
  `auth_rejection_time: Duration::from_millis(1)` and `auth_rejection_time_initial: Some(Duration::ZERO)`, and
  `methods` per mode. `ServerSeen` gains `password_attempts: Vec<String>` and `keyboard_answers: Vec<Vec<String>>`.
  Also `fn drive_with(handle, answer: impl FnMut(&Recorded) -> Option<(NovaSshResponseKind, Vec<u8>)>, on_event) -> Vec<Recorded>`:
  host keys are still accepted automatically, and `answer` replies to everything else. `drive` becomes
  `drive_with(handle, <answers PasswordPrompt with PASSWORD>, on_event)`.

`ServerAuth` modes (Tasks 5 and 6 add the remaining ones):

| Mode | Advertised methods | Behaviour |
|------|--------------------|-----------|
| `Password` | password | today's `auth_password` |
| `NoneAccepted` | none, password | `auth_none` accepts |

- [ ] **Step 1: Write the failing tests**
  - `ServerMethods` unit tests in `mod tests`: unknown by default (`offers` false,
    `password_through_keyboard_interactive` false); a failure listing `[PublicKey, KeyboardInteractive]` makes
    `password_through_keyboard_interactive` true; a later partial-success failure listing `[Password]` replaces it
    (false again); an empty list changes nothing.
  - `a_server_that_takes_none_connects_without_any_prompt` (`exec_session_e2e_tests`): `NoneAccepted`, shell
    session via `nova_ssh_connect`, `drive`; no `PasswordPrompt` in the events and `password_attempts` empty.
  - The three existing tests in that module must still pass. They now go through a `none` refusal first.

- [ ] **Step 2: Run them to see them fail**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml server_methods none`
Expected: compile errors, then `a_server_that_takes_none...` fails with a `PasswordPrompt` seen.

- [ ] **Step 3: Implement.** `authenticate` calls `ask_server_methods` first and returns on `Authenticated`, then
  notes the identity-file `AuthResult` and passes `&mut methods` to `try_agent_auth`. The rest of `authenticate` is
  unchanged in this task. `authenticate_transfer`'s `try_agent_auth` call passes a local
  `&mut ServerMethods::default()` until Task 7.

- [ ] **Step 4: Run the whole rusty_ssh suite to see it pass**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml`
Expected: `test result: ok` for the lib and `ffi_contract`.

- [ ] **Step 5: Commit** `fix(ssh): rusty_ssh asks the server which sign-in methods it takes`

---

### Task 5: Sessions take the password through keyboard-interactive (Rust)

**Files:**
- Modify: `src/Ntilde.App/native/rusty_ssh/src/lib.rs`: `TextPromptPayload`, `prompt_text`, `authenticate`;
  replace `authenticate_keyboard_interactive` with the shared loop.
- Test: `mod tests` and `mod exec_session_e2e_tests`.

**Interfaces:**
- Consumes: `password_question` (Task 1), `ServerMethods` and `ask_server_methods` (Task 4), the `canceled` checks
  (Task 2).
- Produces:
  - `TextPromptPayload` gains `#[serde(rename = "followsRefusal")] follows_refusal: bool`.
  - `fn prompt_text(shared, event_kind, prompt, response_kind, hop, follows_refusal: bool) -> anyhow::Result<Zeroizing<String>>`.
    The passphrase prompt passes `false`.
  - The `KeyboardRounds` trait, which answers the questions of one keyboard-interactive exchange:
    ```rust
    trait KeyboardRounds {
        /// The exchange's first password question (`password_question`), when the exchange converts it.
        fn password(&mut self, prompt: &str) -> anyhow::Result<Zeroizing<String>>;
        /// Any other round that has questions.
        fn round(&mut self, name: String, instructions: String, prompts: Vec<client::Prompt>)
            -> anyhow::Result<Zeroizing<Vec<String>>>;
    }
    ```
  - `enum KeyboardExchange { Success, Failure { remaining_methods: MethodSet, partial_success: bool, password_sent: bool } }`.
  - `async fn run_keyboard_exchange<H, R>(user: &str, session: &mut client::Handle<H>, convert_password: bool, rounds: &mut R) -> anyhow::Result<KeyboardExchange>`,
    where `H: client::Handler + Send + 'static` and `R: KeyboardRounds + Send`. A round with no questions is answered
    with zero answers and calls neither method. With `convert_password`, the first round that `password_question`
    accepts goes to `rounds.password` and its answer is sent as the round's single answer. Every other round with
    questions goes to `rounds.round`.
  - `struct SessionRounds<'a> { shared: &'a Arc<SharedState>, hop: AuthHop<'a>, follows_refusal: bool }`. `password`
    is `prompt_text(PasswordPrompt, <server's text>, Password, hop, follows_refusal)`. `round` is today's
    `KeyboardInteractivePrompt` event plus `wait_keyboard_responses`.

The new route in `authenticate`, after the agent (spec, "Terminal sessions", path 3):

```
if methods.password_through_keyboard_interactive():
    exchange(convert, follows_refusal = false)
    Success -> Ok
    Failure { password_sent: true, partial_success: false, remaining } where remaining has KeyboardInteractive:
        exchange(convert, follows_refusal = true); Success -> Ok
    otherwise -> bail "SSH authentication failed"
```

The `password` route stays as today in this task. Its keyboard-interactive fallback becomes
`run_keyboard_exchange(.., convert_password = false, SessionRounds { follows_refusal: false, .. })`.

New `ServerAuth` modes, all advertising `keyboard-interactive` only (`publickey` too where noted):

| Mode | Rounds of one exchange |
|------|------------------------|
| `KeyboardOnly` | `Password: ` (hidden); right answer, then an empty round, then accept. Wrong answer: reject (proceed with keyboard-interactive). |
| `KeyboardEmptyRoundFirst` | an empty round, then as `KeyboardOnly` |
| `KeyboardExpiredPassword` | as `KeyboardOnly`, but after the right password `New password:` (hidden), then accept on any answer |
| `KeyboardSecondFactor` | `Verification code:` (hidden); accept `123456` |

Every answer is recorded in `keyboard_answers`.

- [ ] **Step 1: Write the failing tests**
  - `mod tests`: `a_converted_prompt_names_its_hop`. `SessionRounds` for `AuthHop::jump(..)` with
    `follows_refusal: true`. Queue a `Password` response, call `password("Password: ")`, read the queued event: kind
    `PasswordPrompt`, payload has `prompt == "Password: "`, `isJumpHop == true`, `followsRefusal == true`.
  - `exec_session_e2e_tests`, each a shell session driven to `Closed`:
    - `a_keyboard_only_server_asks_for_the_password_once`: `KeyboardOnly`. Exactly one `PasswordPrompt`, whose
      payload has `"followsRefusal":false` and prompt `"Password: "`. No `KeyboardInteractivePrompt`. No `Error`.
      `keyboard_answers == [[PASSWORD], []]`.
    - `an_empty_round_before_the_password_does_not_stop_the_conversion`: `KeyboardEmptyRoundFirst`, same
      assertions, with answers `[[], [PASSWORD], []]`.
    - `a_refused_keyboard_password_gets_one_retry_that_says_so`: `KeyboardOnly`, answering `wrong` and then
      `PASSWORD`. Two `PasswordPrompt`s, the second with `followsRefusal:true`, then no `Error`.
    - `a_keyboard_password_refused_twice_ends_sign_in`: answering `wrong` both times. Exactly two `PasswordPrompt`s,
      and an `Error` containing `SSH authentication failed`.
    - `an_expired_passwords_change_question_keeps_the_keyboard_window`: `KeyboardExpiredPassword`. One
      `PasswordPrompt`, then one `KeyboardInteractivePrompt` whose prompt is `New password:`.
    - `a_second_factor_keeps_the_keyboard_window`: `KeyboardSecondFactor`. No `PasswordPrompt`, and one
      `KeyboardInteractivePrompt` answered `{"responses":["123456"]}`.
    - `cancelling_the_converted_prompt_sends_nothing`: `KeyboardOnly`, answering `{"text":"","canceled":true}`. An
      `Error` with `prompt canceled`, and `keyboard_answers` empty.
  - The `TextPromptPayload` serialization test (`prompt: "Password:"`, `AuthHop::target`) also asserts
    `payload["followsRefusal"] == false`.

- [ ] **Step 2: Run them to see them fail**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml exec_session_e2e_tests`
Expected: the new tests fail (a `PasswordPrompt` then a `KeyboardInteractivePrompt` on `KeyboardOnly`).

- [ ] **Step 3: Implement** the Interfaces above and the route. Delete `authenticate_keyboard_interactive`.

- [ ] **Step 4: Run the whole rusty_ssh suite to see it pass**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml`
Expected: `test result: ok`.

- [ ] **Step 5: Commit** `fix(ssh): a keyboard-interactive-only server asks for the password once`

---

### Task 6: A refused password gets one retry on password servers (Rust)

**Files:**
- Modify: `src/Ntilde.App/native/rusty_ssh/src/lib.rs` (`authenticate`'s `password` route)
- Test: `mod exec_session_e2e_tests`

**Interfaces:**
- Consumes: `prompt_text(.., follows_refusal)` (Task 5), `ServerMethods::note` (Task 4).

The `password` route (spec, path 2). Retry when the first `authenticate_password` gives
`Failure { partial_success: false, remaining_methods }` and `remaining_methods` lists `Password`. Then
`prompt_text(.., follows_refusal = true)` and `authenticate_password` once more. If that is refused, or the server
gave partial success or no longer lists `password`, the keyboard-interactive fallback runs as today. Note every
result in `methods`.

New `ServerAuth` modes:

| Mode | Advertised methods | Behaviour |
|------|--------------------|-----------|
| `PasswordAndKeyboard` | password, keyboard-interactive | `auth_password` as `Password`; keyboard-interactive as `KeyboardOnly` |
| `PasswordThenCode` | password, keyboard-interactive | right password: `Reject { proceed_with_methods: Some([KeyboardInteractive]), partial_success: true }`; then `Verification code:`, accept `123456` |

- [ ] **Step 1: Write the failing tests**
  - `a_refused_password_gets_one_retry_that_says_so`: `Password`, answering `wrong` then `PASSWORD`. Two
    `PasswordPrompt`s, the second with `followsRefusal:true`. No `Error`. `password_attempts == ["wrong", PASSWORD]`.
  - `after_the_retry_the_keyboard_fallback_runs_as_before`: `PasswordAndKeyboard`, answering every password prompt
    `wrong` and the `KeyboardInteractivePrompt` with `PASSWORD`. Two `PasswordPrompt`s, then one
    `KeyboardInteractivePrompt`, then no `Error`.
  - `a_partially_accepted_password_is_not_retried`: `PasswordThenCode`. One `PasswordPrompt` with
    `followsRefusal:false`, then a `KeyboardInteractivePrompt` asking `Verification code:`. No retry prompt.
  - `cancelling_the_retry_stops_sign_in`: `Password`, answering `wrong` and then `{"text":"","canceled":true}`. An
    `Error` with `prompt canceled`, and `password_attempts == ["wrong"]`.

- [ ] **Step 2: Run them to see them fail**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml retry`
Expected: the first, second and fourth tests fail (one `PasswordPrompt`, then `Error` or a keyboard prompt).
`a_partially_accepted_password_is_not_retried` passes before the change: it guards the partial-success exception.

- [ ] **Step 3: Implement** the retry in the `password` route.

- [ ] **Step 4: Run the whole rusty_ssh suite to see it pass**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml`
Expected: `test result: ok`.

- [ ] **Step 5: Commit** `fix(ssh): a refused password gets one more window, never filled in`

---

### Task 7: Transfers log in through keyboard-interactive (Rust)

**Files:**
- Modify: `src/Ntilde.App/native/rusty_ssh/src/lib.rs` (`authenticate_transfer`; new `TransferRounds`)
- Test: `mod exec_session_e2e_tests` (it owns the test server)

**Interfaces:**
- Consumes: `ask_server_methods`, `ServerMethods`, `try_agent_auth(.., &mut methods)` (Task 4);
  `run_keyboard_exchange`, `KeyboardRounds`, `KeyboardExchange` (Task 5).
- Produces: `struct TransferRounds<'a> { password: &'a str }`. Its `password` returns a `Zeroizing` copy, and its
  `round` returns `Err(anyhow!(TRANSFER_NEEDS_MORE_THAN_A_PASSWORD))`, where
  `const TRANSFER_NEEDS_MORE_THAN_A_PASSWORD: &str = "The server asks for more than a password at sign-in, which file transfers cannot answer. Use an SSH key or the SSH agent for this host's transfers.";`

The route in `authenticate_transfer` (spec, "Transfers"), after `ask_server_methods` (return `Ok(())` on
`Authenticated`), the identity file (note its result) and the agent:

```
no password for this hop                          -> today's errors, unchanged
methods.password_through_keyboard_interactive():
    run_keyboard_exchange(convert = true, TransferRounds { password })
    Success                                         -> Ok
    Failure { password_sent: true, partial_success: false, .. }
                                                    -> NativeSftpTransferError(AuthenticationFailed, "Authentication failed.")
    Failure { password_sent: true, partial_success: true, .. }
                                                    -> bail TRANSFER_NEEDS_MORE_THAN_A_PASSWORD
    Failure { password_sent: false, .. }            -> bail "Authentication failed." (plain, not the auth-failed kind)
    Err from TransferRounds::round                  -> propagates (plain error)
otherwise                                         -> today's password method, unchanged
```

- [ ] **Step 1: Write the failing tests.** Each is a `#[tokio::test]` that serves a `ServerAuth` mode with
  `start_server_with`. It connects with `client::connect(Arc::new(client::Config::default()), ("127.0.0.1", port), AcceptAnyKey)`,
  where `struct AcceptAnyKey` is a test `client::Handler` whose `check_server_key` returns `Ok(true)`. Then it calls
  `authenticate_transfer("tester", TransferHop::Target, &auth, &mut session)` with
  `TransferAuthConfig { target_password: Some(Zeroizing::new(<pw>.into())), jump_hop_passwords: vec![], identity_file: None, use_agent: false }`.
  - `a_transfer_signs_in_to_a_keyboard_only_server_with_its_password`: `KeyboardOnly`, `PASSWORD`. `Ok(())`, and
    `keyboard_answers == [[PASSWORD], []]`.
  - `a_refused_keyboard_password_is_a_transfer_auth_failure`: `KeyboardOnly`, `"wrong"`. The error downcasts to
    `NativeSftpTransferError` with kind `AuthenticationFailed`.
  - `a_transfer_cannot_answer_a_second_factor`: `KeyboardSecondFactor`. The error message equals
    `TRANSFER_NEEDS_MORE_THAN_A_PASSWORD`, and it does not downcast to `NativeSftpTransferError`.
  - `a_transfer_does_not_feed_its_password_to_a_password_change`: `KeyboardExpiredPassword`. The error is
    `TRANSFER_NEEDS_MORE_THAN_A_PASSWORD`, and `keyboard_answers == [[PASSWORD]]` (the `New password:` round got
    nothing).
  - `a_transfer_to_a_password_server_is_unchanged`: `Password`, `PASSWORD`. `Ok(())`, and
    `password_attempts == [PASSWORD]`.

- [ ] **Step 2: Run them to see them fail**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml transfer`
Expected: `a_transfer_signs_in...`, `a_transfer_cannot_answer...` and `a_transfer_does_not_feed...` fail with
`Authentication failed.` (the password method, refused). `a_refused_keyboard_password_is_a_transfer_auth_failure` and
`a_transfer_to_a_password_server_is_unchanged` pass before the change: they guard that the new route keeps the
`auth-failed` kind and leaves password servers alone.

- [ ] **Step 3: Implement** the route.

- [ ] **Step 4: Run the whole rusty_ssh suite to see it pass**

Run: `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml`
Expected: `test result: ok`.

- [ ] **Step 5: Commit** `fix(ssh): file transfers sign in to keyboard-interactive password servers`

---

### Task 8: Docker E2E against a real sshd (Platform.Tests)

**Files:**
- Modify: `tests/Ntilde.Platform.Tests/Ssh/NativeSshTestInteractionHandler.cs`
- Modify: `tests/Ntilde.Platform.Tests/Ssh/NativeSshDockerAuthE2eTests.cs`
- Modify: `tests/Ntilde.Platform.Tests/Ssh/NativeSshDockerE2eTests.cs`

**Interfaces:**
- Produces: a `NativeSshTestInteractionHandler` constructor parameter `string? firstPassword = null`, which answers
  the first `Password` request only. Later ones get `password` (or `passwordsByUser`).

- [ ] **Step 1: Write the tests**
  - `KeyboardInteractiveAuth_ForAUserThatAcceptsNothingElse_Authenticates`: replace the comment about the
    unconditional password request. Assert exactly one `Password` request (with `FollowsRefusal == false`) and no
    `KeyboardInteractive` request.
  - `KeyboardInteractiveAuth_AfterAWrongPassword_AsksOnceMoreAndGetsIn`: `kbdnova`,
    `firstPassword: "wrong-password"`. Shell prompt reached; two `Password` requests, the second with
    `FollowsRefusal == true`.
  - `PasswordAuth_AfterAWrongPassword_AsksOnceMoreAndGetsIn`: `nova`, the same assertions.
  - `NativeSftp_KeyboardInteractiveUser_CanDownloadFile`: as `NativeSftp_CanDownloadFile`, with
    `User = fixture.KeyboardInteractiveUserName` and `Password = fixture.KeyboardInteractivePassword`. Write the file
    where `kbdnova` can read it (`/tmp`).
  - `NativeSftp_KeyboardInteractiveUser_BadPassword_ReturnsAuthenticationFailed`: as
    `NativeSftp_BadPassword_ReturnsAuthenticationFailed` for `kbdnova`.

- [ ] **Step 2: Run them**

Run (PowerShell): `$env:NTILDE_ENABLE_DOCKER_E2E = "1"; ./scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~NativeSshDocker" *> $scratch/task8-docker.log`
Expected: `Passed!`, 0 failed, 0 skipped. Grep the log with the Grep tool: no `[SKIP]` lines. These tests exercise
code from Tasks 2-7, so they pass on first run. If one fails, the failure is a real finding about sshd's behaviour:
report it rather than editing the assertion.

- [ ] **Step 3: Commit** `test(ssh): keyboard-interactive and retry sign-in against a real sshd`

---

### Task 9: Gate, then PR

- [ ] **Step 1: Rust.** `cargo test --manifest-path src/Ntilde.App/native/rusty_ssh/Cargo.toml`. Expected: `ok`.
- [ ] **Step 2: Platform.Tests, whole project, with Docker.** As in Task 8 Step 2, without the filter. Expected:
  `Passed!`.
- [ ] **Step 3: App.Tests main lane** (PowerShell):
  `./scripts/build.ps1 test tests/Ntilde.App.Tests --blame-hang-timeout 5m --blame-hang-dump-type mini --filter "Category!=Replay&Category!=RenderMetrics&Category!=PtySmoke&Category!=Stress&Category!=GoldenSharedPng&Lane!=PlatformBoot" *> $scratch/gate-main.log`.
  Expected: `Failed: 0`. Then the boot lane, `--filter "Lane=PlatformBoot&Category!=GoldenSharedPng"`, to
  `$scratch/gate-boot.log`. Expected: `Failed: 0`. Run them one after the other, never together.
- [ ] **Step 4: Format**, as ci.yml's "Format (whitespace)" job checks it:
  `./scripts/build.ps1 format whitespace --verify-no-changes`. Expected: exit 0.
- [ ] **Step 5: Commit the spec and plan** (`docs/superpowers/specs/2026-10-23-kbd-interactive-password-once-design.md`,
  `docs/superpowers/plans/2026-10-23-kbd-interactive-password-once.md`) as `docs(ssh): design and plan for asking the password once`.
- [ ] **Step 6: Push and open the PR** against `main`. The title is
  `fix(ssh): ask for the password once on keyboard-interactive servers, and once more when refused`. The body has
  what changed for the user, the FFI fields added, the tests, and a "Not in this PR" section (OpenSSH backend,
  transfer second factors). It ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`. Note that
  PR #580 also edits `SshInteractionService.cs`.
