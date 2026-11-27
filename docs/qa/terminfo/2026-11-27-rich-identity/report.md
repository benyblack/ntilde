# OSC 176 app ID and OSC 666 terminal properties

| Rich TUI case | Exact pinned App verdict | Owned property observation |
| --- | --- | --- |
| `extensions.osc176-app-id` | Inconclusive | Native Wayland app ID is inapplicable to the measured XWayland window. Sway readback worked on the native foot control. |
| `extensions.osc666-termprop` | Inconclusive | The exact payload is inapplicable to VTE property mutation. The separate registered property set/reset is inconclusive on Ntilde because its observer exposes no corresponding typed property state. |

The `terminfo.dev@4.0.0` terminal callbacks in the [pinned bundle](../2026-11-16-shell-integration-aggregates/terminfo-dev-4.0.0.bundle.mjs.gz) each write `CSI 1;1 H` and `CSI 2 K`, send the BEL-terminated OSC, and query cursor position. They have no Termless callback. The exact payloads are `176;terminfo-test` and `666;test-prop=value`. Both returned `CSI 1;1 R` from Ntilde. Replaying the unchanged callbacks yields `pass: false` with `outcome: inconclusive` and `reason: insufficient-evidence` in both cases. Even a missing cursor reply leaves the callback inconclusive, with reason `no-response`. The frozen stock run also reports both inconclusive.

## Native Wayland app ID

The foot contract sets the window's Wayland app ID and resets it to the configured default on an empty value. The documented framing uses ST. The owned criterion `ntilde-wayland-osc176-app-id-v1` requires the same owned native top-level to report its distinguishable baseline ID, `terminfo-test` after the request and its baseline ID after reset. The exact pinned BEL form and the documented ST form were observed separately. [foot 1.21.0 control-sequence manual](https://sources.debian.org/src/foot/1.21.0-2/doc/foot-ctlseqs.7.scd/#L768)

The unprivileged, private Sway session associated foot PID 16 with native top-level 5. Sway reported `qa.foot.baseline → terminfo-test → qa.foot.baseline` for BEL and repeated the same set/reset with ST. The same compositor associated Ntilde PID 54 with top-level 6 and XID `0x400013`. X11 property readback reported that PID and `WM_CLASS` of `"Ntilde", "Ntilde"` throughout. The compositor reported shell `xwayland` and a null `app_id` at baseline, after the pinned BEL and documented ST requests, and after reset. A native `xdg_toplevel` app ID was unavailable for this window. The X11 class was a separate stable observation. The Linux backend selection is documented in the App source. [Avalonia backend setup](../../../src/Ntilde.App/Program.cs#L322)

## VTE terminal properties

The [GNOME VTE 0.80.1 contract](https://gitlab.gnome.org/GNOME/vte/-/blob/0.80.1/src/vtegtk.cc#L3117) uses ST and semicolon-delimited statements. `key=value` sets a registered writable typed property, a bare `key` clears it, and a trailing dot resets a matching prefix. `key!` signals a registered valueless property. `key?` is reserved for a query and in release builds produces an OSC 666 reply with zero statements, without a value readback. An invalid value for a known settable property acts as reset. An unregistered set is ignored. Extension registrations use the `vte.ext.` namespace. [Registry and types](https://gitlab.gnome.org/GNOME/vte/-/blob/0.80.1/src/vtegtk.cc#L3001) · [Termprop identifiers](https://gitlab.gnome.org/GNOME/vte/-/blob/0.80.1/src/vte/vteglobals.h) · [Parser](https://gitlab.gnome.org/GNOME/vte/-/blob/0.80.1/src/vteseq.cc#L1969)

The registry includes container name, runtime and UID, shell precmd and preexec signals, ephemeral shell postexec status, and icon color. Cwd, current file, xterm title and progress hint/value have dedicated sources and are protected from OSC 666 assignment. The icon image slot is registered and cannot be set with an OSC 666 string in this version. The full observed registry is in the machine-readable report. `test-prop` is absent, and VTE's parser rejects BEL for this protocol before reading any key.

The owned criterion `ntilde-vte-osc666-termprop-v1` requires typed state for a registered key with a distinguishable value and a reset to unset. A GTK VTE 0.80.1 widget on the private Wayland session accepted the documented ST-terminated `666;vte.container.name=ntilde-qa` and bare-key reset through its public feed API. Its typed getter observed unset, `ntilde-qa`, then unset, and each transition produced a property-change signal. The exact BEL fixture, its ST-only variant, and the registered setter with BEL left the control unset. The widget feed control establishes parser and observer behavior in VTE.

Ntilde received both the unregistered ST form and the same registered ST set/reset through its direct child PTY. Its private pane observer exposes no typed VTE termprop state, and the release VTE query grammar offers no independent property value readback. The run verifies silence beyond the expected delivery and state replies, and unchanged cursor, foreground, OSC title, tab text and measured X11 window properties. It leaves the VTE property effect inconclusive. The existing silent-ignore policy and its parser/buffer evidence remain in place. [Ntilde pane observer](../../../src/Ntilde.App/MainWindow.AgentMetadata.cs) · [Policy tests from PR #1075](../../../tests/Ntilde.VT.Tests/IdentityPropertyOscPolicyTests.cs)

## Delivery, custody and reproduction

Each App exchange retained the actual bytes written and every child read chunk in a bounded raw PTY window. A primary device attributes query after the CPR provided an independent delivery fence. The separate state control changed cursor from `3;5` to `4;7`, foreground from `#123456` to `#654321`, and the observed OSC title and tab text from `identity-baseline` to `identity-control`, then restored the prepared baseline. Target exchanges preserved those baseline observations. The host verified container removal before releasing its shared desktop lease. Inside the container, both direct children restored and read back their original termios attributes. Their PTYs, worker sockets, windows, compositor sockets and private fixture directory were checked after cleanup.

Run `09460fd214874ef8c50f7e8064ee7f19` ran on Debian 13 arm64 under Docker Desktop, with Sway 1.10.1, Xwayland 24.1.6, foot 1.21.0 and VTE 0.80.1. Ntilde `0.14.0+7824244ebd715d9f14ade754ce3fbb352ec7643d` was published from a Git source archive using .NET SDK 10.0.403. Measured collector revision `59e082e1cbf96dfe16e329b4f313530cfa114348` and its captured source hashes are separate from the App publish-tree hashes. The archive retains the full Sway trees, X11 and pane observations, process and PTY identities, binary hashes, reference outputs, and lease lifecycle. The host pathname was omitted from the published lease copies.

Two earlier attempts are retained under `attempts/` in the archive. The first used the wrong result wrapper for the App metadata call. The second used the wrong GI signal callback signature and sampled the App PTY path before its master closed. Both containers were removed and their leases released. The publication uses run `09460fd214874ef8c50f7e8064ee7f19` exclusively.

Re-derive without a desktop using `node scripts/terminfo/rich-identity-results.mjs --check` and `node --test scripts/terminfo/rich-identity-publication.test.mjs`. The two cases add no graded inventory claim or change to the frozen stock, Core, Modern, or common-harness results. [Raw owned run](owned-run.zip) · [Manifest](manifest.json) · [Re-derived report](report.json)

For a fresh collection, use a Linux arm64 build environment with .NET SDK 10 and the native Rust build prerequisites. Use a private absolute stage directory and keep the App source archive and sidecar tar there. Run the host launcher with Node 24 and Docker at the measured collector revision.

~~~sh
git archive 7824244ebd715d9f14ade754ce3fbb352ec7643d > "$stage/app-source.tar"
# In the extracted App source tree
scripts/build.sh publish src/Ntilde.App -c Release -r linux-arm64 --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:SkipCliShim=true -p:SourceRevisionId=7824244ebd715d9f14ade754ce3fbb352ec7643d -o "$stage/publish"
tar -cf "$stage/sidecar.tar" -C "$stage/publish" .
# In the collector worktree
docker build -t ntilde-rich-osc-runtime:trixie scripts/terminfo/rich-identity
node scripts/terminfo-rich-identity-owned.mjs "$stage" ntilde-rich-osc-runtime:trixie
node scripts/terminfo/rich-identity-results.mjs --verify-run "$stage/$run_id"
~~~
