# Owned exact-query observations — 2026-11-05

The completed disposable macOS collection retained **40 exact query contracts**:
38 matched the original v1 expectations and two mismatched. These are contract
comparisons, not 38 supported features. Nineteen matched replies are valid
negative replies, and sixteen matched transactions are silence. Neither proves
support. The remaining three matched transactions are positive replies.

The two mismatches are original DECRQSS SGR and cursor-style negative expectations.
The measured App includes [#853](https://github.com/benyblack/ntilde/pull/853),
which added positive replies for those settings. The original expectations and
outcomes remain unchanged; these differences do not establish terminal defects.

The [machine-readable publication](2026-11-05-query-observations.json) retains the
full query report, original ownership proof, context, complete receipt, fixed
source inventory and all 16 suite binding/count summaries. The
[complete evidence ZIP](2026-11-05-query-observations-evidence.zip) preserves all
266 original files, all 16 full reports and journals, 137 captured PNGs, three
source PNGs and the remaining immutable image inputs. Earlier publications and
their original grades remain unchanged.

| Measurement | Value |
|---|---|
| App build and adapter source | `630fe12606f24a0cf482b8756da32cf02c46ae72` |
| Supplied version | `0.13.0`; operator supplied, not independently attested |
| Runtime / grid | macOS arm64, Node `v24.22.1`, 40×140 cells |
| Owned run ID | `34412a050e77b76da29514b3f483246f` |
| Distinct stock run ID | `f11ba90e3cad6138fa0ee2ee6bcf9c7f` |
| Source scope | Fixed 74 files, graph policy v4 |
| Full receipt | Schema 11, all 16 suite bindings, fixture removed |
| Query plan | 44 admitted actions, 40 exact contracts, 44 raw captures |
| Read-only state observers | 48 + 42 captures, paired before/after exposed state |
| Global raw-byte controls | 110 prior input/image challenge pairs + 44 fresh query pairs |
| Cleanup / source removal / receipt | `08:09:50.742Z` / `08:09:51.170Z` / `08:09:55.181Z` |
| Complete archive | 266 files, 1,055,814 bytes |
| Archive SHA-256 | `38b81ecf9592e5eed3df554f4a5a750eb4711e2a994ea443d9a04e90849ffc5b` |
| Query report SHA-256 | `19e17e763d5194bd331f0466692f145848d37e428720b1c5b1a48eb72b82d003` |

## Exact spellings and observations

The fixed plan covers 18 Q07 and 22 Q12 spellings. Each original packet is sent
unchanged; no mode query is appended to it. Two bounded state observers record
full exposed lines, attributes, cell details, cursor, buffer modes, margins and
history before and after each action. Four source-selected fixture controls bind
the nonce, prepare absolute margins, restore full margins and restore the original
cursor-visible boolean after RIS. The final exposed state matches the initial
state exactly.

Independent owner checks surround all 90 state captures. Raw byte readers use
fresh paired ordering challenges after the exact packet; the 154 input/image/query
pairs are globally unique. All 44 query attempts completed with a healthy disposed
seal. An ordering reply is a control, never substitute evidence for the requested
query or an independent App-state readback.

| Exact contract | Original expected bytes | Actual retained bytes | Result |
|---|---|---|---|
| `q12.decrqss.sgr` | `1b503024721b5c` (`DCS 0$r ST`) | `1b50312472306d1b5c` (`DCS 1$r0m ST`) | Mismatched |
| `q12.decrqss.cursor-style` | `1b503024721b5c` (`DCS 0$r ST`) | `1b503124723020711b5c` (`DCS 1$r0 q ST`) | Mismatched |

DA3, window-size and vertical-margin positive replies match their own exact
contracts. Each malformed, negative and no-reply spelling remains individually
recorded; a successful member never substitutes for a whole family. The report
uses `matched`/`mismatched` rather than support scores. No expectation was changed
in response to the observed packets. A future expectation policy can qualify the
new SGR/cursor-style replies separately without rewriting this archive.

`reverseVideo` is explicitly listed as unobserved: the state API does not expose
it. No hidden reverse-video preservation or appearance claim is made. Observed
pane/process/state facts are point-in-time checks, not an atomic PTY lease or a
reconnect guarantee. See the [bounded query model](owned-query-model.md).

## Complete bound outcomes

| Suite | Passed | Failed | Inconclusive |
|---|---:|---:|---:|
| ASCII cells | 8 | 0 | 0 |
| SGR indices | 17 | 0 | 0 |
| RGB / underline | 20 | 0 | 0 |
| Rendition / charsets | 19 | 0 | 0 |
| Cursor modes | 8 | 1 | 0 |
| Text pixels | 24 | 1 | 0 |
| Runtime colors | 10 | 0 | 0 |
| Erase | 13 | 0 | 0 |
| Cell editing | 22 | 0 | 0 |
| History / editing | 30 | 0 | 0 |
| Resize | 3 | 0 | 0 |
| Saved state / reset | 9 | 0 | 0 |
| Metadata / private UI | 10 | 1 | 2 |
| Generated input | 39 | 0 | 0 |
| Static images | 21 | 1 | 1 |

The separate query suite has **38 matched / 2 mismatched** contracts.

The cursor and text-pixel negatives retain the original `CSI 0 q`
`isDefault=false` expectation. The measured #853 source instead preserves host
default intent for this spelling. These original negatives are retained as model
expectation differences, not reclassified as visual defects or silently promoted.
The calibrated SIXEL native-raster negative and its causally inconclusive clear
stage remain separate from query matching. The 39 input results qualify declared
Avalonia events routed into the owned local child, not physical keyboard input.

Metadata remains 10 passed / 2 inconclusive / 1 negative. Raw title clearing
succeeds while the intentional profile/cwd display/tab fallback remains nonempty;
the original literal-empty-label expectation stays negative. Shell-P variants
remain inconclusive. Private OSC9 UI witnesses do not establish operating-system
notification or broad focus-delivery support.

Stock output is **85 supported / 0 unsupported / 170 inconclusive**, with
`suiteComplete=false` and the ungraded reverse-wrap geometry diagnostic retained.
Relative to the [published v10 run](2026-11-04-image-observations.md), only
`device.decrqss` changes grade, from inconclusive to supported. This stock family
grade is separate from the two mismatched exact v1 owned contracts. The frozen
78/0/177 baseline and all earlier measurements remain unchanged.

## Retention and limits

The public context omits only its top-level executable path, following the prior
publication convention. The complete original context, proofs, source bytes and
recorded binary hashes remain in the archive. Source commits, operator-supplied
versions and matching binary hashes are not binary-to-source attestation.

Portable guards pin the publication and every ZIP member, decode all 140 PNGs,
rederive all 16 reports and the full receipt in private copies, and reject
coherent query-result promotion, missing prior fences, forged unavailable markers
and receipt downgrade. Future live-graph growth cannot reinterpret this frozen
74-file graph. Historical v8/v9/v10 guards remain in the same CI lane.

This local macOS run does not qualify hardware input, Linux/Windows desktop
runtime, SSH/mux, hidden state, live rendering cadence or exhaustive protocol
coverage. Conservative retained-import checks reject unsupported syntax but are
not a complete JavaScript parser, sandbox or computed-dependency proof. Remaining
query coverage and support-policy work in [#771](https://github.com/benyblack/ntilde/issues/771)
remain open.
