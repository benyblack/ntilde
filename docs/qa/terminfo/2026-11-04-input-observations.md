# Owned generated input — 2026-11-04

The completed disposable macOS run records **39 passing generated-input cases**:
30 emitted byte sequences and nine suppressed events, with 81 paired complete
state frames, 39 App action records and 80 raw child-input challenges. The
[machine-readable publication](2026-11-04-input-observations.json) retains the
complete input report and receipt. The [176-file archive](2026-11-04-input-observations-evidence.zip)
contains every original journal, all 75 PNGs and all 60 captured source/input files.

These are declared Avalonia events routed through the real disposable App into
its direct-local child. They qualify the tested generated-event path, including
exact high-bit legacy mouse bytes; they do not qualify hardware keyboard/pointer
input, native NumLock remapping, SSH/mux delivery or Windows/Linux runtime input.

| Measurement | Value |
|---|---|
| App build and adapter source | `9648f5171b2d383f4954e2bcf6170c2b92e3f98e` |
| Supplied version | `0.13.0`; operator supplied, not independently attested |
| Runtime and observed grid | macOS arm64, Node `v24.22.1`, 40 × 140 cells |
| Run ID | `b33fa9682aae73016ec9e37f543a3e85` |
| Complete source scope | policy v2, fixed 60-file graph captured before measured imports |
| Receipt | schema 9; all 14 suite bindings; fixture removed |
| Cleanup / source removal / receipt | `13:46:39.883Z` / `13:46:40.357Z` / `13:46:42.262Z` |
| Complete evidence ZIP | 642,621 bytes; SHA-256 `d967c6553d985d5722ed1274dc6e66b4cdf5c8a59c54fddbc786356c2d650541` |
| Input report binding | SHA-256 `5f8b55e363a9fd1dcef3ab85795e7f69221be7daa161e4e851d64f6d11ffb89a` |

The fixed plan covers three normal keypad key/text/up cases, seventeen
application-keypad key-downs plus key-up, and eighteen mouse cases: DECSET 9
press-only, legacy/SGR coordinates, modifiers, release/motion/wheel suppression,
normal/button/any-event tracking and repeated-hover coalescing. Before dispatch,
independent DECRQM/Kitty replies prove the requested modes. The parent chooses
exactly one source-declared event and rechecks process, worker, private-profile,
pane/session and geometry facts before and after the action. App `handled` or
`inputSubmitted` fields are retained observations, not child acknowledgements.

The exclusive child reader retains Buffer bytes before decoding. After each
action it chooses a fresh pair of distinct unknown private modes and observes
their exact replies through the same local FIFO. Only those challenge bytes
are removed from the event observation. Silence alone never earns a suppressed
result. Final restoration and a healthy disposed-reader seal complete the input
suite; App/worker/socket/fixture teardown and captured-source removal precede
the final receipt. See the [fixture contract](owned-input.md).

## First attempt retained

The first run, `de23505351409f092affa047887367e1`, failed on its first action.
Its App recorded dispatch and unchanged ownership facts, but the adapter compared
serialized object property order: the producer wrote `modifiers, kind, key,
paneId`, while validation expected `paneId, modifiers, kind, key`. It rejected
equal fields before requesting that action's raw-byte fence. No case grade or
completed receipt is claimed for this attempt.

[PR #826](https://github.com/benyblack/ntilde/pull/826) fixes exact request-field
comparison while preserving archival byte hashing. Reordering only the retained
request's keys reproduced the old defect; strict value/missing/extra-field and
production-wrapper regressions cover the correction. The [entire failed attempt](2026-11-04-input-attempt-1-failure-evidence.zip)
is retained separately: 163 files, 500,953 bytes, SHA-256
`425c747ba17b4fca7df7c2d677663bbf9afc8764a3ea4dbdc55c11cf08e45cb8`.
Its original failure/partial action/raw attempt and source bytes remain unchanged.

## Other observations and limits

The same completed run records **25 passing text-pixel comparisons**, including
explicit-RGB bold under the recorded JetBrains Mono NL 14 configuration after
[the font-weight correction](bold-glyphs.md). The older [24-pass/one-negative run](2026-11-03-pixels.md)
and its PNGs remain frozen. Offscreen synthetic blink phases do not measure
desktop cadence, all fonts or image-protocol pixels. Basic SGR stays Partial.

All other bound suites retain their exact outcomes. Metadata has nine passes,
three inconclusive cases and the literal-empty visible-title negative. Raw OSC 2
clearing and the intentional profile/cwd display fallback are separate from
that older literal-empty-label expectation. Both P variants and focused-window
notification suppression remain explicit. The reset suite has nine passes.

Stock output remains **82 supported / 0 unsupported / 173 inconclusive**, with
the same `16a690f34c0e` probe hash and 255 graded identities as the preceding
[reset publication](2026-11-04-reset-observations.md). `suiteComplete=false` and
the ungraded reverse-wrap geometry diagnostic remain. The original **78/0/177**
baseline, all earlier negatives and every historical archive remain unchanged.
Kitty acknowledgements continue to prove replies only.

The public context omits only the executable path; original context, process
proofs, supplied-version limits and all binary/source hashes are in the archive.
An observed build commit is not binary-to-source attestation. Both original
attempt directories have byte-verified complete copies in the primary checkout.
For independent reproduction, extract to a private copy, verify the full member
manifest, rederive the reports and invoke the complete finalizer without changing
originals; its receipt differs only in completion time. The portable publication
guards perform that full 14-suite reproduction as well as exact archive/JSON pins.
