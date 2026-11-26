# OSC 633 command-line and Cwd qualification

Issue [#983](https://github.com/benyblack/ntilde/issues/983). The disposable direct-local macOS App reported the command line from E for one A–D cycle and accepted two distinct reported-directory updates from P/Cwd. The [A–D implementation qualification](../2026-11-16-osc633-implementation-qualification.md) remains the lifecycle evidence.

| Exact case | Pinned termless | Pinned terminal | Owned effect | Historical stock |
| --- | --- | --- | --- | --- |
| [extensions.osc633-e](https://terminfo.dev/extensions/osc633-e-set-commandline) | Inconclusive | Inconclusive | Supported | Inconclusive |
| [extensions.osc633-p](https://terminfo.dev/extensions/osc633-p-properties) | Inconclusive | Inconclusive | Supported for Cwd | Inconclusive |

The SHA-256-pinned terminfo.dev 4.0.0 bundle runs unchanged callbacks. The E fixture writes BEL-terminated ESC ] 633;E;ls -la;nonce123 followed by X. The P fixture writes BEL-terminated ESC ] 633;P;Cwd=/tmp followed by X. Each termless callback reads cell (0,0). The terminal callbacks first home and erase row 1, then write the same fixture and request a cursor position report with CSI 6 n. Each case observed X in the first cell and received CSI 1;2 R. The shared helper can only return inconclusive, even with both consumption checks satisfied. The [stock baseline](../2026-11-09-stock-baseline.json) is carried forward. [Pinned results and exact bytes](report.json)

## Owned metadata observations

The E fixture wrote grid text “grid command” and a separate ST-terminated E record. The reported command remained empty at the prompt after E. At C, the App reported “echo reported;\part” and switched to running. D cleared the command. A second A–D cycle with the same visible grid text and no E stayed empty at C. A separate OSC 133 C payload reported “echo control” through the same observer.

For P, an OSC 7 control first changed the prepared directory to a distinct path. P/Cwd then reported an escaped path ending in “/east;one” and a literal Windows path ending in “\x64;west=two”. A record containing an escaped NUL and a separate unknown property each left the Windows path intact. The report observes shell-supplied directory metadata. The child process’s actual working directory is outside the measured effect.

## Input and trust policy

E is scoped to the next OSC 633 C following B. A fresh prompt, input marker, completion, reset or imported parser state abandons a pending report. OSC 133 lifecycle markers also invalidate any pending OSC 633 report. The App exposes E only through its session status, with the separate OSC 133 command capture and Command Assist path preserved. The parser caps the encoded metadata field plus any nonce at 8,192 UTF-16 code units. It decodes doubled backslashes and ASCII hex escapes, accepts literal spaces for compatibility with the pinned E fixture, and rejects malformed escapes and disallowed controls. The unit tests also cover split sequences, delimiter escaping, oversized records and stale metadata. [VS Code protocol](https://code.visualstudio.com/docs/terminal/shell-integration#_supported-escape-sequences)

A missing nonce and a single opaque printable ASCII nonce up to 128 characters both produce unverified metadata. The App issues no OSC 633 nonce, discards the supplied token, and assigns no command trust based on its presence. For P/Cwd, the whole value after Cwd= is the directory field, including literal semicolons. The parser preserves standard literal Windows drive and UNC paths, while accepting the doubled backslashes and encoded delimiters the VS Code shell scripts emit. Empty values, controls and oversized values are rejected. The App sends the resulting path through its existing directory notification. [VS Code Bash serializer](https://github.com/microsoft/vscode/blob/main/src/vs/workbench/contrib/terminal/common/scripts/shellIntegration-bash.sh) Other P properties, OSC 133 P, command execution and the aggregate integration grades sit outside this batch.

## Evidence and reproduction

The measurement manifest identifies the App and collector revisions. [Measurement manifest](manifest.json) The archive binds the executable and assembly digests, captured collector sources, paired App observations, exact writes and per-stage child PTY ordering replies. The run restored the prepared OSC 7 directory, the initial blank viewport and the kernel PTY mode. The parent verified removal of the disposable fixture and the shared desktop lease was released after cleanup. Earlier runs passed both effects on superseded binaries. This measurement uses the rebuilt App after review and includes the literal Windows path regression.

Reissue the receipt and rederive the exact and owned verdicts with Node 24:

~~~sh
node --test scripts/terminfo/osc633-metadata-publication.test.mjs
~~~

For a fresh run, use the measured revision from the manifest with a private absolute output directory. The runner acquires the shared desktop lock.

~~~sh
scripts/build.sh publish src/Ntilde.App -c Release -r osx-arm64 --self-contained false \
  -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false \
  -p:SkipCliShim=true -o /absolute/private/sidecar
node scripts/terminfo-owned.mjs --executable /absolute/private/sidecar/Ntilde \
  --app-version 0.14.0+a3319b62029f434b3420ed24fad646d76e163aaa --scope osc633-metadata
~~~

The [owned archive](owned-run.zip) and [machine-readable report](report.json) cover two exact IDs in the [frozen Rich TUI catalogue](../2026-11-26-rich-tui/report.md). The two supported effects add inventory claims. The pinned and stock verdicts remain inconclusive.
