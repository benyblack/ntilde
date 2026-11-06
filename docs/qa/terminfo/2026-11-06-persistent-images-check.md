# Persistent-session images check (#838)

#838's acceptance asks for a manual check with real programs in a persistent tab, versions
recorded. This note records one: Kitty graphics and iTerm2 inline images drawn by real programs in
a persistent pane, reattached, and compared with a direct pane.

It is a manual QA run. It does not change any published terminfo.dev observation or grade.

## Setup

| | |
|---|---|
| Ntilde under test | Debug build of `main` at `0d9911b0` (includes #881, #882 and #884) |
| Kitty graphics | notcurses `ncplayer` 3.0.17 (Homebrew), `-b pixel -k -s none`: direct mode, pixel blitter |
| iTerm2 inline images | `imgcat` from iTerm2 3.7.7 (`/Applications/iTerm.app/Contents/Resources/utilities/imgcat`), `-W 8` |
| Image | the app icon resized to a 96x96 PNG |
| Host | macOS 27.0.1, arm64, local PTY |

- **Isolation:** each run used its own `NTILDE_APPDATA_ROOT`, so it neither read the user's
  settings nor touched the user's multiplexer.
- **Profile:** the only profile ran [`run-images.sh`](persistent-images-check/run-images.sh). The
  script:
  - sends #838's probe (`ESC _G i=31,s=1,v=1,a=q,t=d,f=24;AAAA ESC \`) and records the reply
  - draws the image with `ncplayer`, which uses Kitty graphics when the terminal answers its query
  - draws it with `imgcat -l` (the classic `File=` sequence), then with `imgcat` as it is by
    default (the multipart form `MultipartFile`, then `FilePart` chunks, then `FileEnd`)
  - holds the screen
- **Capture:** each pane was captured with the MCP server's `ntilde.capture_screen` (render mode)
  and read with `ntilde.read_screen`, through *Agent access (observe)*.

## Results

| Run | Pane | Probe reply | `ncplayer` (Kitty) | `imgcat -l` (`File=`) | `imgcat` (multipart) |
|---|---|---|---|---|---|
| Released 0.13.0, measured in #838 (Windows, WSL) | persistent | `ESC _G i=31;ERR ESC \` | — | — | — |
| `persistent` | persistent (`KeepOnClose`, `NTILDE_MUX_SESSION` set) | `ESC _G i=31;OK ESC \` | drawn | drawn | not drawn |
| `reattached` | the same session, after the app was quit and started again | — | still drawn | still drawn | not drawn |
| `direct` | direct (`SessionPersistence` `Off`) | `ESC _G i=31;OK ESC \` | drawn | drawn | not drawn |

**What the captures showed:**

- **Persistent equals direct.** Both captures put the same images in the same cells: `ncplayer`'s at
  row 1, and `imgcat -l`'s at rows 2 to 5, eight columns wide. The text rows were identical, with
  no image after the multipart label. `ncplayer -k` leaves the cursor at its image's top-left
  corner, so the next label is printed under that image in both panes.
- **Reattach.** After the app was quit and started again, the pane reattached to the same session
  (same pane, and the script did not run again). It showed both images, restored from the daemon's
  snapshot alone: no program drew them again.
- **The multipart form is not supported in any pane.** Recent `imgcat` sends it by default, so a
  plain `imgcat` shows nothing in Ntilde, persistent or not. The coverage matrix already lists other
  OSC 1337 variants as unqualified; this is the one users meet first.
  It is supported since; see the [multipart check](2026-11-06-iterm2-multipart-check.md).

## Limits

- Two programs, on one platform (macOS), through Ntilde's local PTY. Windows (ConPTY), Linux, SSH
  and remote persistent sessions were not exercised by hand. Headless tests cover the daemon's
  replies with ConPTY filtering on (`tests/Ntilde.Mux.Tests/Headless/HeadlessImagePolicyTests.cs`).
- `kitty +kitten icat`, `timg` and `chafa` were not installed and were not run.
- The captures are render-mode re-renders of each pane's buffer. They show which cells hold which
  image, not how a window composited them on screen.

## Reproducing

1. **Build** `main` with `scripts/build.sh build Ntilde.sln`.
2. **Settings:** create an empty folder for `NTILDE_APPDATA_ROOT`. Keep its path short: on macOS
   the agent endpoint is a Unix socket inside it, and a socket path longer than 104 bytes does not
   bind. Add a `settings.json` with:
   - one local profile (`"Type": 0`) running `/bin/bash` with the arguments
     `<this folder>/persistent-images-check/run-images.sh <log path> <png path>`, as the
     `DefaultProfileId`
   - `"AgentAccessObserveEnabled": true`
   - `"SessionPersistence"`: `KeepOnClose` for the persistent run, `Off` for the direct one
3. **Run:** start `src/Ntilde.App/bin/Debug/net10.0/Ntilde` with that `NTILDE_APPDATA_ROOT`. The
   script writes `<log>.probe`, `<log>.env` and finally `<log>.done`.
   - The new window takes keyboard focus. Do not type elsewhere while it is open, or the keys land
     in its pane.
4. **Capture:** run `src/Ntilde.McpServer/bin/Debug/net10.0/Ntilde.McpServer` with the same
   `NTILDE_APPDATA_ROOT`, call `ntilde.list_sessions` for the pane id, then `ntilde.capture_screen`.
5. **Reattach:** quit the app and start it again with the same root, then capture again.
6. **Clean up:** close the app and stop the isolated multiplexer with
   `Ntilde mux kill-server --force` under the same `NTILDE_APPDATA_ROOT`.
