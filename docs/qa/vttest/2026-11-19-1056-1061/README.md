# vttest regression rerun for #1056–#1061

All six issue reproductions passed on 2026-11-19, including both pane modes for #1061. The macOS Release build came from merged `main` at [`9f832ecdc`](https://github.com/benyblack/ntilde/commit/9f832ecdce8d09237f60e5e161e31373a44aa53f), which includes the S8C1T reply fix in [PR #1071](https://github.com/benyblack/ntilde/pull/1071).

The rerun used Homebrew vttest 2.7 (20251205) on macOS 27.0.1 (arm64). Each case started in a fresh local Ntilde pane verified at 80 columns by 24 rows and ran `vttest -l vttest.log 24x80.80`. The app startup log confirmed the tested commit in every pane.

## Results

| Issue | vttest menu | Pane | Passing observation | Screenshots |
| --- | --- | --- | --- | --- |
| [#1056](https://github.com/benyblack/ntilde/issues/1056) | `11 → 6 → 5` | Direct | Reverse Index preserved the active blue background across the viewport. The second page had a black background with color confined to the asterisk border. | [Blue page](1056-blue.png), [black page](1056-black.png) |
| [#1057](https://github.com/benyblack/ntilde/issues/1057) | `5 → 9` | Direct | Ctrl+A registered as SOH (`01`) and Ctrl+V as SYN (`16`). | [Ctrl+A](1057-ctrl-a.png), [Ctrl+V](1057-ctrl-v.png) |
| [#1058](https://github.com/benyblack/ntilde/issues/1058) | `6 → 2` | Direct | Return sent `0D 0A` with ANSI New Line Mode set and `0D` after reset. vttest accepted both. | [New Line Mode](1058-new-line-mode.png) |
| [#1059](https://github.com/benyblack/ntilde/issues/1059) | `11 → 3 → 4 → 1` | Direct | Backspace sent `08` with DECBKM set and `7F` after reset. vttest accepted both. | [Backarrow Key Mode](1059-backarrow-key.png) |
| [#1060](https://github.com/benyblack/ntilde/issues/1060) | `11 → 1 → 3` | Direct | The cursor position report was `9B 31 3B 31 52` after S8C1T and `1B 5B 31 3B 31 52` after S7C1T. vttest accepted both, and no stray `F` or `G` appeared. | [C1 control width](1060-c1-width.png) |
| [#1061](https://github.com/benyblack/ntilde/issues/1061) | `11 → 2 → 1 → 2 → 1` | Direct | `11` appeared with PTY echo off. `22` appeared with PTY echo on. | [Direct SRM](1061-srm-direct.png) |
| [#1061](https://github.com/benyblack/ntilde/issues/1061) | `11 → 2 → 1 → 2 → 1` | Persistent | `11` appeared with PTY echo off. `22` appeared with PTY echo on. | [Persistent SRM](1061-srm-persistent.png) |

For #1061, `stty -a` showed `-echo` after vttest sent `CSI 12 l` and `echo` after `CSI 12 h` in each pane. Both vttest logs recorded exactly two `1` keystrokes followed by two `2` keystrokes. The persistent run used a daemon-backed session, confirmed by the presence of `NTILDE_MUX_SESSION` in the child.
