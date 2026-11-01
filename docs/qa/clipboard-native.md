# Native clipboard qualification

The dedicated `Native clipboard qualification` workflow runs production
`KittyClipboardSession` and `AvaloniaKittyClipboard` in a separate, visible
Avalonia process on disposable macOS, Windows and Linux CI desktops. Independent
peers use AppKit/CoreServices, Win32 and GTK4 respectively. Linux uses a private
Xvfb display. None of these programs runs on a personal desktop by default:
both host and launcher require `CI=true`, and the host also requires the explicit
`--isolated-desktop` argument. Do not set these switches on your everyday desktop.

## What the fixture proves

1. The production service writes Unicode text, PNG, HTML, binary data containing
   NULs and a MIME alias. The native peer independently reads and compares them.
2. The native peer replaces every value with different bytes/pixels, then the
   production service reads it using a one-use paste token. Identical fixtures
   would allow an unchanged old clipboard to produce a false pass.
3. A data read with consent denied emits EPERM and no DATA.

The host sends a real `walias` packet. Text and HTML results match exact bytes.
Binary fixtures carry a four-byte big-endian length and compare exactly that
payload, excluding Win32 HGLOBAL allocation padding. Native PNG containers are
validated through IEND and checked by dimensions/pixel: compression and metadata
can change during lossless bitmap conversion. Supplementary-plane Unicode is
included in both directional text fixtures.

Windows uses registered `PNG` and `HTML Format` formats. The adapter constructs
and validates [CF_HTML byte offsets](https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format) and returns only the HTML fragment to OSC 5522
clients. Long headers and contextless fragments are supported; malformed native
HTML falls through to another format, while budget violations still fail.
Arbitrary MIME aliases remain custom formats. macOS maps MIME names to
UTIs; Linux advertises MIME targets. A bitmap fallback is preferred only when no
raw platform representation supplies the requested PNG. Avalonia's Win32 OLE
provider requires a bitmap behind PNG: the writer checks dimensions before
creating it, includes its pixel budget in the native representation limit, and
owns/disposes it with the transferred clipboard object. An extra native-only
format retains a length-framed copy of the original encoded PNG bytes for Ntilde
MIME reads and aliases. The validated logical length excludes HGLOBAL padding
before applying the public read budget; legitimate trailing payload bytes remain;
its name cannot be supplied as an OSC MIME type. Native size refusal reports
EFBIG. The owned transfer implements both synchronous and asynchronous contracts
so Win32 does not block on an asynchronous provider.

## Run on an isolated CI desktop

Build through `scripts/build.sh build tests/Ntilde.Clipboard.NativeHost -c Release`
(or the PowerShell wrapper). The project references the production App and its
CLI build dependency. The workflow compiles the AppKit peer with `swiftc`, or
installs GTK4/PyGObject for the Linux peer. Win32 uses Python's standard library.
The workflow is the authoritative invocation for each OS. The host's 55-second
peer-write handoff covers the runner's 25-second peer-read plus 25-second writer
allowances; native reply waits retain their separate 20-second deadline.

The Python launcher creates a private temporary fixture, uses only generated
data, supervises its own child processes and terminates them on success/failure.
Only SHA-256 hashes, platform details and a result are retained in the uploaded
`clipboard-native-<os>/result.json`; failures can also retain bounded fixture-only
logs. The host and production App DLL digests, CI source revision/run, and both
directional fixture digests are recorded.

Runner self-tests, which do not open a clipboard, run with:

```sh
python3 scripts/clipboard-native/test_runner.py
```

## Limits

This is native data-format interchange with independent peers. It does not
exercise a PTY, the permission dialog's buttons, manual paste into arbitrary
editors, rich-text rendering, Wayland, or primary selection. It is separate from
the terminfo.dev recognition measurement; it does not change any of that suite's
inconclusive grades. Qualification requires actual successful workflow results,
not merely the existence of this harness.

Avalonia's provider allocation and native-operation cancellation limitations
remain as described in the [OSC 5522 report](terminfo/2026-11-01-clipboard.md).
