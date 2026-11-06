# iTerm2 multipart images check

The [persistent-images check](2026-11-06-persistent-images-check.md) found that plain `imgcat`
draws nothing in Ntilde: iTerm2 3.7's `imgcat` sends the multipart form (`MultipartFile`, then
`FilePart` chunks of 200 base64 characters, then `FileEnd`) unless given `-l`. This note checks the
parser support added for that form, in the same way as that check.

It is a manual QA run. It does not change any published terminfo.dev observation or grade.

## Setup

| | |
|---|---|
| Ntilde under test | Debug build of the branch adding multipart support, on `main` at `7a6929e3` |
| Program | `imgcat` from iTerm2 3.7.7 (`/Applications/iTerm.app/Contents/Resources/utilities/imgcat`), `-W 8` |
| Image | the app icon resized to a 96x96 PNG (14,425 bytes; 73 `FilePart`s) |
| Host | macOS 27.0.1, arm64, local PTY |

- **Isolation:** each run used its own short `NTILDE_APPDATA_ROOT` under the home folder. A root
  under a long temporary path leaves no agent endpoint, because the agent socket's path then exceeds
  the macOS socket limit.
- **Profile:** the only profile ran [`run-multipart.sh`](persistent-images-check/run-multipart.sh),
  which draws the image with plain `imgcat`, then with `imgcat -l`, and holds the screen.
- **Capture:** `ntilde.read_screen` and `ntilde.capture_screen` (render mode, scale 2) over
  `Ntilde.McpServer`, with *Agent access (observe)* on.

## Results

| Run | Pane | `imgcat` (multipart) | `imgcat -l` (`File=`) |
|---|---|---|---|
| `persistent` | persistent (`KeepOnClose`, `NTILDE_MUX_SESSION` set, image policy 2) | drawn, rows 1 to 4 | drawn, rows 6 to 9 |
| `reattached` | the same pane, after the app was quit and started again | still drawn | still drawn |
| `direct` | direct (`SessionPersistence` `Off`) | drawn, rows 1 to 4 | drawn, rows 6 to 9 |

- The two forms put the same image in the same number of cells (eight columns, four rows), and the
  text after each resumed at column 0 of the next row.
- The three captures were byte-identical (2388x1518 px, 63,308 bytes).
- The persistent and direct runs were repeated on the final build, which puts multipart behind image
  policy 2, with the same captures.

## Limits

- One program, on one platform (macOS), through the local PTY. Windows (ConPTY), Linux, SSH, tmux
  passthrough and remote persistent sessions were not exercised by hand.
- A client attaching between parts is covered by a headless test, not by hand
  (`tests/Ntilde.Mux.Tests/Headless/HeadlessImagePolicyTests.cs`).
