# Kitty file transport (`t=f`) in persistent sessions (#887)

Status: design, 2026-11-19. Follow-up to [#838](2026-11-06-persistent-session-images-design.md), section 4.

## Problem

In a persistent session the headless daemon parses the child's output with the authoritative parser, and every attached client runs the same
parser over the same bytes (image policy, `PersistentImagePolicy`). A `t=f` transmit names a file **on the daemon's host**; the image header in
that file decides placement and the Kitty reply, so every side must act on the same bytes. Today the file reader is null there: `a=q,t=f` answers
`ERR` and a `t=f` transmit is skipped, so programs fall back to `t=d`.

## Design

**Only the daemon reads, clients are told.** The daemon parser's `ReadFileBytes` reads the file under the direct pane's confinement. The result of
every `t=f` read in a chunk (the bytes, or "unavailable") is forwarded to every subscriber in a new frame that **precedes the output frame of the
chunk** that completed the command. Clients never read a file and never guess.

- **Frame.** `FileTransfer` (`0x14`, server to client): `id[16] + seq i64 + flags u8 + pathLength i32 + path utf8 + bytes`. `seq` is the stream
  offset of the output frame that follows (the chunk the daemon was parsing when the read happened). `flags` bit 0: bytes present; clear means the
  daemon could not read the file (refused path, missing, too large), and the client must act on that too. The path is carried so a client can check the
  entry is for the command it is parsing.
- **Ordering.** `HeadlessTerminalSession.ProcessChunk` parses the chunk, then broadcasts its `FileTransfer` frames in command order, then the
  `Output` frame. Attach and detach run as work items between chunks, so a client that attaches right after a chunk gets the snapshot (which carries the
  image like any retained source) and never sees that chunk's frames; one that is already subscribed gets frames and output together.
- **Client.** `MuxClientSession.DeliverFile` keeps the entries of the next output frame. While `OnOutputReceived` runs (parsing is synchronous in the
  pane and in the text client), the parser's `ReadFileBytes` takes the first entry whose path equals the requested one (bytes, or null for
  "unavailable"); an unclaimed entry is dropped when the output frame has been delivered, and an entry for any offset but the next expected one (the output frame it precedes) is a protocol error, so a peer cannot park file bytes at offsets that never arrive.
  A path with no entry is "unavailable": the client does what the daemon's refusal did, so a mismatch can only cost an image, never place a different one.
- **Retention.** The parser keeps the bytes as the image's source (`ImageSourceBudget`), so snapshots, reattach and a second client restore a `t=f`
  image exactly like a `t=d` one. The raw output recording holds the command (a path) and not the file, as in a direct pane: replay still cannot show it.
- **Confinement and caps.** One reader (`KittyTransportFile`, owned by `Ntilde.Mux` and used by the App's direct panes too): absolute paths only,
  resolved through every symbolic link component by component (a loop is refused) and then required to lie under the user's temp directory (case rules of the
  platform), the **resolved** path is what is opened, regular files only (on Unix the open is non-blocking and a stream that cannot seek is refused, so a FIFO with no
  writer cannot block the parse thread), and a 64 MiB cap re-checked on the opened handle. A program that can create links in the user's temp directory could still swap
  one between the check and the open; it already runs as that user, and the daemon reads for the same user.
- **Bounds per chunk.** The daemon stages at most 32 reads and `MaxFileTransferBytes` bytes in all for one output chunk, each file within what is left; a read past a bound is
  unavailable for everyone, so a small chunk naming one big file again and again cannot hold gigabytes. A path longer than a frame carries is unavailable without an entry
  (a client asked for it finds none, the same answer). A client refuses (a protocol error) more entries or bytes for one offset than a daemon stages. In a persistent session
  the file must also fit a frame (`MaxFrameBytes` minus the header and a 4 KiB path), otherwise it is "unavailable" for everyone.
- **Accounting.** A `FileTransfer` frame is accounted as a snapshot is (the bulk account), never against the stream budget that pending output is held to, so a large file does
  not make the small output frame behind it read as a slow client.
- **Policy 6.** `PersistentImagePolicy.FileTransportImagesVersion = 6` (`AllowsFileTransport`). The version is fixed per session at spawn like the others;
  a peer that predates it is refused (`version_mismatch`), never downgraded, and below 6 the reader stays null so `t=f` answers `ERR` as today.
- **Not changed.** `t=t` (the file is deleted after reading) stays unsupported everywhere (the parser skips it): deleting a file on the daemon's host
  from a program's escape sequence is a decision for a separate issue. SSH panes still have no reader (their files are remote).

## Tests

- The reader: relative paths, outside temp, a traversal form, a symlink out of temp, a directory, an oversized file, a file that grows past the cap, a frame-sized limit.
- Frame codec: round trip, truncated, bad flags, a path that is not UTF-8 or longer than the cap.
- Daemon and clients: one `t=f` transmit gives the same reply, the same cells and the same retained image on the daemon and on every client, including a
  client that attaches right after the command and one attached throughout; an unavailable file and a refused path give the same `EINVAL:Image payload unavailable` everywhere.
- Snapshot and reattach restore the image. A policy-5 session refuses `t=f` as before; negotiation refuses an older peer.
