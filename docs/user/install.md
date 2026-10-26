# Installing and updating

Ntilde runs on Windows (x64), macOS (Apple silicon) and Linux (x64 and arm64). Every download is on
the [releases page](https://github.com/benyblack/ntilde/releases/latest).

## Windows

**Installer (recommended):** download `ntilde-Setup-win-x64-<version>.exe` and run it.

- It installs for your user only, with no administrator prompt, into `%LOCALAPPDATA%\NtildeApp`.
- It adds Start menu and desktop shortcuts, and puts `ntilde` on your `PATH` for terminals opened
  afterwards ([Command line](cli.md)).
- It [updates itself](#updates).

**Portable:** download `ntilde-win-x64-<version>.zip` and extract it anywhere. It does not update
itself and adds nothing to `PATH`.

Ntilde is not code-signed yet, so SmartScreen warns the first time: choose **More info → Run
anyway**.

## macOS

**Installer (recommended):** download `ntilde-Setup-osx-arm64-<version>.pkg` and open it. It
installs `Ntilde.app` in `/Applications` (or `~/Applications`) and [updates itself](#updates).

**Zip:** download `ntilde-osx-arm64-<version>.zip`, open it and drag `Ntilde.app` to Applications.

Intel Macs are not supported.

Release builds are signed and notarized. If macOS still blocks the first launch ("cannot be
opened"), open **System Settings → Privacy & Security**, scroll down and click **Open Anyway**, or run
`xattr -cr /Applications/Ntilde.app` in a terminal.

## Linux

Ntilde needs glibc 2.35 or newer: Ubuntu 22.04, Debian 12, Fedora 36 or later, or a current rolling
distribution. Debian 11 and RHEL 8 and 9 are not supported. In the file names below, use `arm64`
instead of `x64` (or `amd64` for the `.deb`) on ARM computers.

**AppImage (recommended, updates itself):**

```sh
curl -LO https://github.com/benyblack/ntilde/releases/download/<version>/ntilde-linux-x64-<version>.AppImage
chmod +x ntilde-linux-x64-<version>.AppImage
mkdir -p ~/Applications && mv ntilde-linux-x64-<version>.AppImage ~/Applications/
~/Applications/ntilde-linux-x64-<version>.AppImage
```

Keep it in a folder you can write to, such as `~/Applications`: it updates by replacing itself. It
needs FUSE 2: on Ubuntu, `sudo apt install fuse` (`libfuse2` alone is not enough), or start it with
`--appimage-extract-and-run`. The AppImage cannot be used by AI agents ([Agents](agents.md)): use the
`.deb` or the tarball for that.

**Debian and Ubuntu package:** copy the exact `.deb` file name from the releases page (it does not
follow the pattern of the other files), then:

```sh
curl -LO https://github.com/benyblack/ntilde/releases/download/<version>/<file>.deb
sudo apt install ./<file>.deb
```

It installs `ntilde` and `ntilde-mcp` on your `PATH`, an application menu entry and `man ntilde`.
Update it with your package manager; it does not update itself.

**Arch Linux:** download `PKGBUILD-<version>` from the release, rename it to `PKGBUILD`, and run
`makepkg -si`. It installs the `ntilde-bin` package. Repeat for each new version.

**Tarball:** `tar -xzf ntilde-linux-x64-<version>.tar.gz && ./Ntilde`. No integration, no updates.

Ntilde does not make itself your default terminal. To do so after installing the `.deb`, see
`man ntilde`.

## First run

Ntilde opens a tab with your default shell ([Shells & Profiles](settings.md#shells--profiles)).
Some things to do next:

- Press `Ctrl+Shift+P` (`Cmd+Shift+P` on macOS) for the [command palette](shortcuts.md#the-command-palette):
  every feature is there.
- Pick a theme and font in Settings (`Ctrl+,`): [Themes and fonts](themes-and-fonts.md).
- Add your SSH hosts in the Connection Manager (`Ctrl+Shift+K`), or import them from
  `~/.ssh/config`: [SSH](ssh.md).
- Your shells keep running when you close the window, and come back when you reopen it:
  [Persistent sessions](persistent-sessions.md).

**Coming from NovaTerminal?** Ntilde is NovaTerminal renamed. On its first launch it copies your
settings, themes, connection profiles, workspaces, snippets, backups and history into its own data
folder, and leaves the old folder alone. If a file cannot be copied, Ntilde starts anyway and tries
again at the next launch; with `NTILDE_APPDATA_ROOT` set, it copies nothing. Keep the old folder
until you have checked that what you need came across. Saved passwords do not carry
over: type each one once. Re-run the remote shell-integration installer on your hosts
([Shell integration](shell-integration.md#on-a-remote-host)). The command is now `ntilde` (was
`nova`), and `NOVATERM_*` environment variables are now `NTILDE_*`.

## Updates

| Installed with | Updates |
|---|---|
| Windows installer, macOS `.pkg` | By themselves, in the background |
| AppImage | By themselves, by replacing the AppImage |
| `.deb`, Arch package | With your package manager |
| Windows `.zip`, macOS `.zip`, tarball | Download the new version yourself |

Where Ntilde updates itself, it checks GitHub about 10 seconds after it starts and downloads a new
version quietly. Nothing restarts by surprise: a notification offers to restart, and
*Update: Restart to apply version* in the palette does the same. *Update: Check for updates* checks
now. On macOS, an app in `/Applications` asks for your password once per update. Turn automatic
checks off with Settings → General → **Automatic update checks**.

Your shells survive an update when the new version can keep the running multiplexer; otherwise
Ntilde asks before closing them ([Updates](persistent-sessions.md#updates)).

Messages you may see:

- "Updates unavailable … This build was not installed by the Ntilde installer, so it cannot update
  itself." You are running a zip, tarball or package install.
- "Could not reach GitHub. See the debug log for details."
- "Automatic update checks are switched off in settings."

## Uninstalling

- **Windows installer:** *Settings → Apps* (or *Add or remove programs*) → Ntilde → Uninstall. This
  stops the multiplexer, ending its shells, and removes Ntilde from your `PATH`.
- **macOS:** quit Ntilde, run `ntilde mux kill-server` if you use persistent sessions, and drag
  `Ntilde.app` to the Trash.
- **`.deb`:** `sudo apt remove ntilde`. **Arch:** `sudo pacman -R ntilde-bin`.
- **AppImage, zip, tarball:** delete the file or folder.

None of these removes your settings. To remove them too, delete Ntilde's data folder
([File locations](troubleshooting.md#file-locations)), and the saved SSH passwords in your system's
credential store (entries named `Ntilde` in the macOS Keychain, `Ntilde:SSH:…` in Windows Credential
Manager).
