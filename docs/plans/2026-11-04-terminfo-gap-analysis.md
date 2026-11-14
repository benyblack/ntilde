# terminfo.dev gap analysis — 2026-11-04

Planning input. It is based on `main` at `78b4082d`, a local macOS owned run of
0.13.0 (run `ece931da9c34fd8b0af603c4007d81c5`, 2026-11-04 13:46 UTC), the first
[Windows and Linux inline runs](../qa/terminfo/2026-11-04-windows-linux-inline.md),
the `terminfo:` issues #769–#776, and the ledgers in `docs/qa/terminfo/`
(`exact-query-audit.md`, `remaining-protocol-variants.md`,
`kitty-image-lifecycle.md`). It changes no grade, capability claim or frozen
evidence.

## 1. Where the measurement stands

| Run (macOS, owned) | Supported | Unsupported | No response | Insufficient evidence | Policy refused |
|---|---:|---:|---:|---:|---:|
| 2026-10-30 baseline | 64 | 11 | 25 | 152 | 3 |
| 2026-11-02 frozen triage | 78 | 0 | 22 | 152 | 3 |
| **2026-11-04 local run** | **82** | **0** | **18** | **152** | **3** |

- All 11 original *unsupported* items are fixed: the Kitty keyboard flags
  2/4/8/16, OSC 66, OSC 5522, the color stack, mode 1048, color-scheme
  reporting and DECSTR.
- **The 152 "insufficient evidence" results do not show missing features.**
  The stock collector cannot read cells or pixels. Most of them have since been
  qualified by Ntilde's own owned suites (#761–#768, all closed).
- `suiteComplete=false` comes only from the collector's `cursor.reverse-wrap`
  geometry bug, tracked in #776.
- On Windows (through WSL) and Linux, a direct tab gives the same outcome as
  macOS on all 50 probes the inline collector executes. There are no
  platform-specific differences at query level; see the
  [run summary](../qa/terminfo/2026-11-04-windows-linux-inline.md).

**Main finding:** the `terminfo:` issues track evidence and qualification work.
#772 marked about 45 protocol variants as *Unsupported*, but none of them had an
implementation issue or priority. The next section ranks them by how much real
applications are affected.

## 2. Implementation gaps ranked by real-app impact

### Tier 1: everyday impact, small to medium effort

| Gap | Who hits it | Status |
|---|---|---|
| **Alternate scroll (`?1007`)**: mouse wheel sends arrow keys on the alternate screen when mouse reporting is off | `less`, `man`, the `git log` pager and `htop` without mouse support, where the wheel did nothing. iTerm2, kitty, WezTerm, Ghostty, Windows Terminal and VTE do this by default. | #828, PR #836 open |
| **modifyOtherKeys (`CSI > 4 ; Pv m`), levels 1/2** | Vim 9's default `keyprotocol` maps `xterm*` to `mok2`. With `TERM=xterm-256color`, Vim never uses Kitty keys, so Ctrl+Shift, Ctrl+I vs Tab and similar combinations are lost. tmux `extended-keys` and Emacs xterm.el use it too. | #829, PR #837 open |
| **Title stack (`CSI 22 ; Ps t` / `CSI 23 ; Ps t`)** | Vim's `t_ST`/`t_RT` save the title on start and restore it on exit. | #830, merged (#835) |
| **DECSCNM reverse video (`?5`)** | The `xterm-256color` terminfo `flash` capability is `\E[?5h$<100/>\E[?5l`, used by Vim `visualbell`, readline and Emacs `visible-bell`. | #831, merged (#834) |

### Tier 2: feature parity, medium to large effort

| Gap | Who hits it | Status / tracking |
|---|---|---|
| **Kitty and iTerm2 images in persistent sessions** | Anyone with persistent tabs on. Only Sixel works there; the Kitty query replies `ERR`. | #838: decide whether to support it or document it |
| **Kitty Unicode placeholders (`U=1`)** | Images through tmux or other multiplexers (yazi, `kitty +kitten icat --unicode-placeholder`, timg) | `U=1` is accepted but does nothing. Tracked in #770, after basic pixels. |
| **OSC 8 complete** | Hyperlinks across scrollback, `id=` grouping, hover span | #95 |
| **XTGETTCAP positive answers** (`TN`, `Co`/`colors`, `RGB`, `Ms`, `Smulx`, `Setulc`) | Neovim and other modern TUIs probe these. Ntilde returns only `DCS 0 + r`. | "Unavailable" in #771. Needs a capability source of truth, ideally shared with the terminfo entry below. |
| **DECRQSS `m` (SGR) and `SP q` (cursor style)** | Apps that save and restore styling or cursor shape (notcurses, test suites) | Only `r` is implemented (#819). The state already exists. |
| **Opt-in `xterm-ntilde` terminfo entry** | Lets apps find `Smulx`/`Setulc` (styled underlines exist), `Ss`/`Se`, `Sync`, `Tc`/`RGB`, `fsl`/`tsl` and so on without relying on `xterm-256color` | Not started. There is no `.ti` source. `TERM` is `xterm-256color` everywhere (PTY and `rusty_ssh`). |
| **Left/right margins (`?69`, DECSLRM, SL/SR, DECIC/DECDC)** | tmux's `margins` terminal feature speeds up redraws in vertical splits; Vim can use it for vertically split scrolling. | Unsupported (#772 boundary 2). Large: affects every edit, scroll and reflow path, plus replay. |
| **Desktop notifications OSC 777 / OSC 99** | Tools that choose a notification channel by terminal (OSC 9 is already handled) | Implemented as a subset: OSC 777 `notify` (title, body) and kitty OSC 99 title/body payloads (chunking, base64, the `p=?` capability reply), routed to the same opt-in in-app toast as OSC 9. Not implemented: OSC 99 actions, close reports, icons, buttons, urgency, sounds, expiry; no OS desktop notification. |
| **OSC 133 `P` / prompt kinds; OSC 633** | fish 4, VS Code shell-integration scripts | OSC 133 handles only A/B/C/D. OSC 633 has no route. |
| **SGR-pixel mouse (`?1016`)** | Image TUIs and a few editors | Unsupported. Encoder only. |
| **XTSMGRAPHICS (`CSI ? Pi ; Pa ; Pv S`)** | notcurses and chafa use it to size Sixel output and query color registers | Unavailable (#771) |
| **OSC 22 pointer shape** | Some TUIs change the mouse cursor shape | Unsupported. Needs an App policy decision. |
| **Bold appearance with explicit RGB** | Measured negative in #782 (`HMWqg_` glyphs identical under SGR 1) | #822 paints bold glyphs with distinct atlas masks; #833 retained new bold measurements. |

### Tier 3: niche or legacy; keep the documented Unsupported policy

DECCOLM (`?3`), DECSCLM (`?4`), UTF-8 mouse `?1005`, urxvt mouse `?1015`,
rectangle operations (DECFRA/DECCRA/DECERA/DECSERA/DECCARA/DECRARA/DECSACE,
DECRQCRA), DECSCA protected cells (DECSED/DECSEL are currently aliases),
XTPUSHSGR/XTPOPSGR, XTPUSHCOLORS/XTPOPCOLORS/XTREPORTCOLORS, XTSAVE/XTRESTORE,
XTGETXRES, OSC 5/21/176/440/555/666/701/702/710/720/776/3008/7770/7777, OSC 1337
`RequestCapabilities`/`ReportCellSize`, and Kitty animation. Their main cost is
the terminfo.dev score. Revisit one when a named application needs it.

## 3. Evidence and qualification gaps

| Issue | What is still missing | Notes |
|---|---|---|
| #769 keypad/mouse input | Live generated-input qualification for X10 and button-event mouse | Foundations #801/#806/#809/#813/#823/#826 are merged; #833 retained complete generated-input measurements. |
| #770 graphics | Owned Sixel and iTerm2 pixels, Kitty source crops/extended layout/deletion scopes, animation, placeholders | Lifecycle and ACKs (#808/#818/#825) and aspect fit (#827/#840) are merged. Static images: PR #841. |
| #771 queries | Fresh owned receipts for DA3, `18t` and DECRQSS `r`; any newly implemented variants | 16 query spellings are formally marked "unavailable". |
| #772 variants | Turning dispositions into implementation decisions (section 2) | Tier 1 is now tracked as #828–#831. |
| #774 clipboard | Live qualification of the three OSC 52 probes | The private fixture foundation (#820) is merged. |
| #775 Windows/Linux | **No owned (behavioral) measurement on Windows or Linux** | Inline query runs now exist for both. The Linux process/PTY provider is PR #842; it is not yet connected to the owned runner. Native Windows collection is blocked upstream (see the run summary). |
| #776 ledger | Root cause of the collector's reverse-wrap diagnostic (`suiteComplete=false`) | Likely needs an upstream terminfo.dev fix or a documented adapter limitation. |

Gaps with no issue yet:

- **Real-application matrix:** an editor with diagnostic underlines, a Kitty
  keyboard event viewer, Sixel output, `icat`, and persistent-session reconnect.
- **MIME clipboard (OSC 5522) in persistent sessions.** Turned off by design
  because the daemon cannot reach the desktop clipboard, but not in the
  user-facing limitations (noted in #838).
- **Per-pane broadcast encoding for Kitty keyboard and modifyOtherKeys
  receivers.** Check it as part of #829.
- **The inline runner on native Windows.** It prepares a run directory and
  installs dependencies before the collector refuses. It could fail early and
  point to the WSL steps instead.

## 4. Suggested planning cut

1. **Land the open Tier 1 PRs:** #836 (alternate scroll) and #837
   (modifyOtherKeys, with per-pane broadcast encoding).
2. **Decide #838:** support Kitty and iTerm2 images in persistent sessions, or
   document them as Sixel-only.
3. **Discovery:** an opt-in `xterm-ntilde` terminfo entry plus XTGETTCAP served
   from the same capability table, and DECRQSS `m` and `SP q`. Needs an issue.
4. **Graphics:** finish #770 basics, then Kitty Unicode placeholders, then
   XTSMGRAPHICS.
5. **Platform evidence:** connect the Linux provider (#842) to the owned runner
   and run it on a real Linux desktop, then Windows (#775).
6. **Large and optional:** left/right margins as a single contract, if tmux or
   vertical-split performance matters.
7. Keep Tier 3 as documented Unsupported.
