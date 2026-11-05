# Passive private-QA native child-wait fact

Raw AgentHost `readNativeChildWait` is a read-only observation available only in
the existing explicit private-QA process startup mode. It does not launch or
stop a child, perform a native wait, operate on the clipboard, or attest process
ownership. Linux desktop/full owned collection qualification remains separate;
Windows desktop is deferred and cannot produce this Unix source.

The request contains `paneId`, matching 32-hex `runId`, positive
`expectedChildPid`, and optional `expectedNativeSessionId`. The first observation
can discover the native generation; the caller must bind that returned tuple to
its independently admitted original child identity and pin the generation in
later requests. This is stateless observation, not a cross-call ownership lease.

Result version1 contains `paneId`, `nativeSessionId`, `originalChildPid`,
`source="unix-native-child-try-wait"`, `nativeWaitObserved` and `exitCode`.
False with an explicit null code means unknown, never running. A true flag and
code, including genuine zero, come only from the existing native child.try_wait
Some result. On the supported Unix implementation that wait has reaped/cached
the App's own child. Generic EOF/OnExit/ExitCode, fallback zero, read errors and
handle disposal cannot create this flag.

The PID is captured once using the existing pre-thread native spawn query and
the existing immutable Rust session Id supplies the generation. The new getter
only reads cached fields and disposal state; it never invokes the existing
active status probe. Disposed, replaced, wrapped, remote, mux, windowless,
background, split, hidden and minimized targets are unavailable. Normal and
maximized visible windows are eligible under the same identity gates. The current original
exited session remains readable when its pane is retained, for example by the
existing private runner choice ShellExitPolicy=Never. Product exit behavior is
unchanged.

The existing explicit process and pane `PrivateClipboardQaMode` latch is only
a privacy gate. No clipboard fixture is begun/read/restored/closed by this
method, no provider is resolved and no grant is changed. Ordinary and
owner-marker-only processes refuse before observer access. The endpoint remains
under observe permission; it does not need act permission. The UI bridge checks
the same registration/buffer/session identities around its passive read and
honors the existing two-second responsive-UI deadline/caller cancellation.
Read attention and ordinary journal entries contain only fixed method/target
metadata; they omit the PID, native generation, exit code and run marker.

The caller still needs its original pidfd identity, genuine owned-launch proof,
same-connection App credentials, positive launcher-parent reaping and the other
restoration/resource checks before whole-run completion. Endpoint probes,
generic status and this DTO alone cannot supply those missing facts. Native ABI,
existing/default DTO bytes, settings, MCP tools, Node graph and receipts are
unchanged by this foundation.
