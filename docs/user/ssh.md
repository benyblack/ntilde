# SSH and file transfer

Ntilde keeps SSH connections as profiles, opens them in tabs and panes like local shells, and moves
files to and from the host.

## The Connection Manager

Open it with `Ctrl+Shift+K` (`Cmd+Shift+K` on macOS), the **Connections** button in the title bar,
or *Connections* in the palette. It is a window of its own, so it can stay open beside the
terminal.

It lists your SSH profiles by group, with a filter for favorites. For a profile you can:

- open it in the current pane, in a new tab (**+ Tab**), or in a split to the right or below;
- edit, delete, or mark it a favorite;
- **Copy launch command**, or see **Connection details**;
- show, clear or **Forget saved password**;
- pick a **Diagnostics level** for its connection log.

Each SSH profile also has a palette command, **New Connection (SSH): *name***.

## Creating a profile

**New** in the Connection Manager opens the editor. Its tabs:

| Tab | Fields |
|---|---|
| Basic | Name, HostName, User, Port, Backend (*Native* or *OpenSSH*), Accent color, Remote shell (*Auto*, bash, zsh, fish or PowerShell), Group (`Prod/DB` nests groups), Tags, Favorite, Notes |
| Auth | *Agent* or *Identity file*, with **Browse…** for the key |
| Jump Hosts | Hosts to go through first, in order: Host, User (blank uses the profile's user), Port |
| Port Forwards | Local, Remote and Dynamic (SOCKS) forwards |
| Reliability | Keepalive interval and count; connection multiplexing and **ControlPersist (sec)**; **Keep remote sessions running (ntilde-mux)** and **Install ntilde-mux on this host…** |
| Advanced | Extra SSH arguments (OpenSSH only); **Allow AI agent access to this connection** |

**Save** keeps the profile; **Connect** saves it and opens it. A missing key file is a warning, not
an error.

### Importing from `~/.ssh/config`

The Connection Manager's **Sync from ~/.ssh/config** button turns the hosts of your OpenSSH config
into profiles:

- It reads `Host`, `HostName`, `User`, `Port`, `IdentityFile` and `ProxyJump`. Hosts with `*` or `?`
  in their name are skipped, and `Include`, `Match` and other keywords are not read.
- New profiles are named `SSH <alias>`, in the group *Imported*, and use the OpenSSH backend. A
  `ProxyJump` host that has no `Host` block of its own becomes `SSH <host> (Stub)` in *Jump Hosts*.
- A profile already named `SSH <alias>` gets its host, user, port and key updated. Nothing is
  deleted.

Run it again whenever your config changes: profiles do not follow the file by themselves.

## SSH backends

Ntilde has two ways to connect:

- **Native** is an SSH client built into Ntilde, with its own list of trusted host keys. It is the
  **default for new profiles**. It never falls back to OpenSSH silently when it fails.
- **OpenSSH** runs your system's `ssh`. It is still selectable per profile, and remains the backend
  of profiles you created before Native became the default, and of imported ones.

Settings → SSH → **Native SSH backend** turns Native off globally: native profiles then refuse to
connect, and new profiles use OpenSSH.

| | Native | OpenSSH |
|---|---|---|
| Password, keyboard-interactive and two-factor prompts | yes | yes |
| Identity file, with passphrase prompt | yes | yes |
| SSH agent | yes: `SSH_AUTH_SOCK`; on Windows the OpenSSH agent, then Pageant | yes |
| Jump hosts | any number | any number (`ProxyJump`) |
| Local, remote and dynamic forwards | yes; a remote forward must name its port | yes |
| Connection multiplexing | yes | yes (`ControlMaster`) |
| Extra SSH arguments | ignored, with a warning | yes |
| Remote Files sidebar | yes | no |
| File transfer | built-in SFTP | the system `scp` |
| Trusted host keys | its own list | your `~/.ssh/known_hosts` |

> **The OpenSSH backend does not read your `~/.ssh/config`.** Ntilde writes a config of its own
> from the profile and runs `ssh -F` with it, so settings in `~/.ssh/config` and
> `/etc/ssh/ssh_config` do not apply. Put what you need in the profile, or in its *Extra SSH
> arguments* (`-o ForwardAgent=yes`, say).

## Signing in

**Auth** picks how the profile signs in: with your **Agent** (its keys only), or with an **Identity
file** (that key only, like `IdentitiesOnly`). Either way, when the server asks for a password, a
passphrase, or the answers of a keyboard-interactive login (a one-time code, say), Ntilde asks you
in a dialog. The native backend tries, in turn, the key file, the agent's keys, and then the
password or keyboard-interactive login.

**Saved passwords.** Tick **Remember password** in the password dialog to keep it in your system's
credential store: Windows Credential Manager, the macOS login Keychain, or the Secret Service
(GNOME Keyring, KWallet) on Linux. Passwords are never written to Ntilde's files, never put in a
backup, and never saved for a jump host. The Connection Manager can show, clear or forget a saved
password. Ntilde never types a password because of something the terminal printed.

Older settings files containing a profile password are migrated to your OS credential store, including
Ntilde's settings recovery copy. An existing credential in the store takes precedence. Ntilde verifies
a new credential before removing the old file entry. If the store is locked or unavailable, the old
file is kept and settings saves wait until migration can finish; unlock the store and restart Ntilde.

The native backend keeps credentials in pinned buffers and clears its owned copies after authentication,
on replacement, and when their session or remote host closes. This reduces retention; it does not make
passwords unrecoverable from process dumps or swap. Password dialogs, OpenSSH askpass, OS libraries and
SSH protocol libraries can still hold transient copies.

**Host keys.** The first connection to a host asks whether to trust its key ("The server host key
is not trusted yet. Continue only if you trust this host."); a key that changed asks again, with a
warning ("The server host key changed. Only continue if you trust this change."). Answering yes
replaces the old key.

- The native backend keeps one key per host and port in `ssh/native_known_hosts.json` in Ntilde's
  data folder. There is no list of them in the app yet: to forget a host, quit Ntilde and delete its
  entry from that file.
- The OpenSSH backend uses your `~/.ssh/known_hosts`, and refuses a changed key outright: remove the
  old key (`ssh-keygen -R host`) first.

## Connection multiplexing

**Connection multiplexing** (the profile's *Reliability* tab) works with both backends. With it on,
the first tab to a profile signs in, and every later tab or split of that profile opens on the same
connection: no new handshake and no second password or key prompt. The profile's port forwards
belong to that connection, so they are set up once and stay up while any of its tabs is open. A
forward whose local port is already taken is left out with a warning in each tab, as OpenSSH does,
rather than stopping the tabs from opening.

When the last tab closes, the connection closes too, or after **ControlPersist (sec)** when that is
above 0, so a tab you reopen within that time connects at once. One connection carries all of a
profile's tabs, so when it drops, they all drop together. Only tabs of the same profile share. On
the native backend, the profile's persistent tabs, its *Remote Files* listings, path completion and
SFTP transfers use the same connection too; a transfer or listing that finds no connection up signs
in on its own.

## Tabs that survive a disconnect

A profile can keep its shells running on the host across network drops, a sleeping laptop and
Ntilde closing, with either backend: tick **Keep remote sessions running (ntilde-mux)** on the
*Reliability* tab. See [Remote SSH tabs that keep running](persistent-sessions.md#remote-ssh-tabs-that-keep-running).

## Transferring files

### Remote Files sidebar

On a native SSH pane, *SFTP: Toggle Remote Files* (or the pane's right-click menu → SFTP → Remote
Files, or the title bar's **Remote Files**) opens a sidebar listing the remote folder, starting in
the shell's current folder when [shell integration](shell-integration.md) is installed on the host.
It shows the host, the current path and each entry's modified date.

- **Upload File** and **Upload Folder** pick something on your computer and upload it into the
  folder the sidebar shows.
- **Download Selected** downloads the selected file (to a name you choose) or folder (into a folder
  you choose).

The sidebar hides while a full-screen program runs.

### Palette commands

On any SSH pane:

- *SFTP: Upload File...* / *SFTP: Upload Folder...*
- *SFTP: Download File...* / *SFTP: Download Folder...*
- *SFTP: Show Transfers* shows the transfer list.

These open a dialog for the remote path, which completes paths when the profile already has a
native session open. They do nothing on a local pane.

### How transfers behave

- Native profiles transfer over SFTP, trusting the same host keys and never falling back to
  OpenSSH. They sign in with your key, agent or saved password; a host that needs a one-time code
  cannot be used for transfers, so use a key or the agent there.
- OpenSSH profiles transfer with your system's `scp`.
- A single file shows its progress in bytes when its size is known; a folder shows progress file by
  file.
- Native transfers can be cancelled from the transfer list.

## Agents and SSH

An AI agent with *Agent access (act)* can type into an SSH session only when that profile has
**Allow AI agent access to this connection** ticked ([Agents](agents.md)).

## Where profiles are kept

SSH profiles are in `ssh/profiles.json` in Ntilde's data folder ([File
locations](troubleshooting.md#file-locations)), separate from local profiles. You can edit the file
with Ntilde closed; inside it, choices such as the backend are numbers (`BackendKind` `0` OpenSSH,
`1` Native). A file Ntilde cannot read is moved aside as `profiles.json.corrupt.<date>.json` and
Ntilde starts with no SSH profiles; restore one from a [snapshot](backup.md#snapshots).
