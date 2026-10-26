# Backup and restore

Export your Ntilde configuration to a single `.ntildebackup` file and import it on another
computer, or roll back to an automatic snapshot.

Everything is on Settings → **Backup & Restore**. The palette's **Export configuration…**, **Import
configuration…** and **Restore from snapshot…** open that page, because export and import need a
file and a choice, and a restore needs your confirmation. The same operations are on the
[command line](cli.md#ntilde-backup).

## What a backup holds

Six categories, which you can pick one by one:

| Category | What |
|---|---|
| Settings | `settings.json`, local profiles included |
| Themes | Your imported and edited themes |
| Connections | SSH profiles and the native SSH backend's trusted host keys |
| Workspaces | Saved workspaces and templates |
| Policy | The workspace policy |
| Snippets | Command Assist snippets |

A backup is a zip file with a manifest, so you can look inside one before importing it.

> **Passwords are not in a backup.** They live in your system's credential store (Windows
> Credential Manager, macOS Keychain, Linux Secret Service), not in Ntilde's folder. After an
> import, type each SSH password once when you connect. A backup does carry each profile's
> *remember password* choice.

Command history, recordings, logs and running shells are not in a backup either.

## Exporting

**Export…** writes `ntilde-YYYY-MM-DD.ntildebackup` wherever you choose.

## Importing

**Import…** asks how:

- **Merge**: for each item in the backup, the backup's version wins; items you have that the backup
  does not are kept.
- **Replace**: each category in the backup becomes exactly what the backup holds.

Restart Ntilde afterwards to pick up every change. Backups from NovaTerminal (`.novabackup`) import
too.

## Snapshots

Ntilde keeps automatic snapshots of the same files, in the `backups` folder of its data folder:

- 30 seconds after your configuration stops changing (at most 5 minutes after the first change);
  a snapshot identical to the last one is not kept twice;
- just before every import and every restore, so both can be undone.

It keeps the newest 20, plus every snapshot from the last 7 days.

To roll back, pick a snapshot in the list and click **Restore selected**, confirm, and restart
Ntilde.

## Agents

An agent connected through [Agent Access](agents.md) can export a backup and list snapshots, but it
can never import or restore.
