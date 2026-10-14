# ntilde Multiplexer Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the multiplexer core (`Ntilde.Mux.Contracts` + `Ntilde.Mux`) as a library exercised entirely in-process: a headless authoritative session per PTY, a server that streams snapshot-then-ordered-events to attached clients, and a client session that implements `ITerminalSession` — with zero user-visible behaviour change.

**Architecture:** `Ntilde.Pty` gains an optional raw-byte tap (`ITerminalByteOutput` + `RawOutputTap`). `Ntilde.Mux.Contracts` (leaf) defines framing, frame kinds, binary payload codecs and source-generated JSON DTOs. `Ntilde.Mux` holds `HeadlessTerminalSession` (one dedicated parse thread per session; control items run between `Process()` calls), `MuxServer` (per-connection reader/sender threads, byte-budgeted send queue), `MuxClient` + `MuxClientSession` (reader thread = in-order delivery thread), and an in-memory duplex transport. Tests drive everything through a scripted fake session.

**Tech Stack:** .NET 10, C# (nullable enabled), xUnit v3, `System.Text.Json` source generation (Native AOT), `System.Buffers.ArrayPool`, `BlockingCollection<T>` on dedicated threads.

**Spec:** `docs/superpowers/specs/2026-09-23-ntilde-mux-phase1.md` — read it first; this plan argues from it (frame catalogue §6, threading §5, resource policy §7, deliberate deviations §9).

## Global Constraints

Every task's requirements implicitly include this section.

- **Never call raw `dotnet build` / `dotnet test`.** Use `scripts/build.ps1` (PowerShell) or `scripts/build.sh` (Git Bash). Run test projects **one at a time**.
- **`tests/Ntilde.App.Tests`** always with `--blame-hang-timeout 5m`, two lanes, run separately, never concurrently:
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot" --blame-hang-timeout 5m`
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane=PlatformBoot" --blame-hang-timeout 5m`
- **Baselines at branch point `34df035`:** VT.Tests 725/0/0 · Architecture 85/0 · Platform.Tests 203/0/30 skipped · Rendering 100 · McpServer 198 · App.Tests `Lane!=PlatformBoot` 3734/0/15 skipped · `Lane=PlatformBoot` 54/0/4.
- **Layering:** `Ntilde.VT` stays a leaf. `Ntilde.Pty` must not reference `Ntilde.VT`. `Ntilde.Mux.Contracts` has **zero** project references. `Ntilde.Mux` references exactly Pty, VT, Replay, Mux.Contracts — never Platform, App, Avalonia, SkiaSharp.
- **Both new libraries:** `<IsAotCompatible>true</IsAotCompatible>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. Fix analyzer findings; do not blanket-suppress. If a CA rule fires on a wire DTO shape (e.g. CA1819 on `byte[]`), suppress it on that member with `[SuppressMessage(..., Justification = "wire DTO")]`.
- **JSON:** only through `MuxJsonContext` / `TerminalStateJsonContext` (source-generated). No reflection overloads.
- **No thread-pool work on the output path** (tap → parse thread → send queue → stream). Dedicated `IsBackground` named threads only. Test code may use `Task.Run`.
- **Additive only.** No `TerminalSettings` fields. No App changes except the test project reference in Task 9.
- **Tests (xUnit v3):** pass `TestContext.Current.CancellationToken` to every cancellable call (`Task.Delay`, `WaitAsync`, client RPCs) — `xUnit1051` is an error under `TreatWarningsAsErrors`.
- **Line endings:** the repo's `.editorconfig` requires CRLF and CI gates on it. Before **every** commit run `scripts/build.ps1 format whitespace --no-restore` and stage what it changes. Never edit files with `sed`.
- **Branch:** `feat/mux-phase1-core` (cut from `origin/dev-mux` @ `34df035`), checked out in the worktree `D:\projects\nova2\.claude\worktrees\feat+mux-phase0-seams` (directory name is historical). Its upstream is `origin/dev-mux` — **push with `git push -u origin feat/mux-phase1-core`**, never a bare `git push`. Do not touch `claude/ntilde-multiplexer-mbxx9b`. Verify `git rev-parse --abbrev-ref HEAD` before each commit.
- Commit messages end with:
  ```
  Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>
  ```

---

## File Structure

### Created

| File | Responsibility |
|---|---|
| `src/Ntilde.Pty/ITerminalByteOutput.cs` | Optional raw-output interface. |
| `src/Ntilde.Pty/RawOutputTap.cs` | Fan-out with first-subscriber replay; shared by Pty and Platform sessions. |
| `src/Ntilde.Mux.Contracts/Ntilde.Mux.Contracts.csproj` | Leaf project. |
| `src/Ntilde.Mux.Contracts/MuxProtocol.cs` | Constants, error codes, method names, frame kinds, version negotiation, `MuxProtocolException`. |
| `src/Ntilde.Mux.Contracts/MuxFrameCodec.cs` | 5-byte header write/validate. |
| `src/Ntilde.Mux.Contracts/MuxFrameReader.cs` | `MuxInboundFrame` (pooled) + blocking `MuxFrameReader.Read`. |
| `src/Ntilde.Mux.Contracts/MuxOutboundFrame.cs` | Ref-counted pooled outbound frame. |
| `src/Ntilde.Mux.Contracts/MuxFrames.cs` | Frame builders / binary payload parsers / JSON helpers. |
| `src/Ntilde.Mux.Contracts/MuxMessages.cs` | JSON DTO records. |
| `src/Ntilde.Mux.Contracts/MuxJsonContext.cs` | Source-generated context. |
| `src/Ntilde.Mux/Ntilde.Mux.csproj` | Library project. |
| `src/Ntilde.Mux/Transport/IMuxListener.cs`, `InMemoryDuplexPipe.cs`, `InMemoryMuxListener.cs` | Transport abstraction + in-memory implementation. |
| `src/Ntilde.Mux/IMuxFrameSink.cs` | Internal: where a session offers frames. |
| `src/Ntilde.Mux/HeadlessSessionOptions.cs`, `HeadlessTerminalSession.cs` | Authoritative session. |
| `src/Ntilde.Mux/MuxServerOptions.cs`, `MuxServer.cs`, `MuxServerConnection.cs`, `MuxRequestException.cs` | Server. |
| `src/Ntilde.Mux/MuxClientOptions.cs`, `MuxClient.cs`, `MuxClientSession.cs` | Client. |
| `tests/Ntilde.Mux.Tests/**` | New test project (Support/, Contracts/, Transport/, Headless/, Server/, Client/, Scenarios/). |
| `tests/Ntilde.VT.Tests/StateTransfer/TerminalStateAssert.cs` | Equality comparison extracted from `ParityHarness`; linked into Mux.Tests. |
| `tests/Ntilde.VT.Tests/StateTransfer/TerminalStateAssertTests.cs` | Vacuity guard for the comparison. |
| `tests/Ntilde.Platform.Tests/Pty/RawOutputTapTests.cs` | Tap unit tests. |
| `tests/Ntilde.App.Tests/PtyRawOutputTapTests.cs` | RustPtySession tap tests (scripted reads, real handle). |
| `tests/Ntilde.App.Tests/MuxRealShellSmokeTests.cs` | The one PtySmoke test with the real factory. |

### Modified

`src/Ntilde.Pty/RustPtySession.cs`, `src/Ntilde.Platform/Ssh/Sessions/{NativeSshSession,OpenSshSession,SshSession}.cs`, `tests/Ntilde.VT.Tests/StateTransfer/ParityHarness.cs`, `tests/Ntilde.Platform.Tests/Ssh/NativeSshSessionTests.cs`, `tests/Ntilde.Architecture.Tests/{LayeringTests,ProjectFileLayeringTests,NamespaceAlignmentTests}.cs` + its csproj, `tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj`, `Ntilde.sln`, `.github/workflows/ci.yml`, `docs/ARCHITECTURE.md`, `docs/MODULE_OWNERSHIP.md`.

---

### Task 1: Raw byte tap on sessions

**Files:**
- Create: `src/Ntilde.Pty/ITerminalByteOutput.cs`, `src/Ntilde.Pty/RawOutputTap.cs`
- Modify: `src/Ntilde.Pty/RustPtySession.cs` (class decl ~line 13; fields ~line 256; `OnOutputReceived.add` ~line 294; `ReadLoop` ~line 1015)
- Modify: `src/Ntilde.Platform/Ssh/Sessions/NativeSshSession.cs`, `OpenSshSession.cs`, `SshSession.cs`
- Test: `tests/Ntilde.Platform.Tests/Pty/RawOutputTapTests.cs`, `tests/Ntilde.Platform.Tests/Ssh/NativeSshSessionTests.cs` (add tests), `tests/Ntilde.App.Tests/PtyRawOutputTapTests.cs`

**Interfaces:**
- Produces: `Ntilde.Pty.ITerminalByteOutput { event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived; }`; `Ntilde.Pty.RawOutputTap { void Subscribe(Action<ReadOnlyMemory<byte>>?); void Unsubscribe(...); void StopRetaining(); void Publish(ReadOnlySpan<byte>); }`. `RustPtySession`, `NativeSshSession`, `OpenSshSession`, `SshSession` implement `ITerminalByteOutput`.

- [ ] **Step 1: Write the failing tap unit tests** — `tests/Ntilde.Platform.Tests/Pty/RawOutputTapTests.cs`:

```csharp
using System.Text;
using Ntilde.Pty;

namespace Ntilde.Platform.Tests.Pty;

public sealed class RawOutputTapTests
{
    private static string Ascii(ReadOnlyMemory<byte> m) => Encoding.ASCII.GetString(m.Span);

    [Fact]
    public void Chunks_published_before_the_first_subscriber_are_replayed_to_it_in_order()
    {
        var tap = new RawOutputTap();
        tap.Publish("ab"u8);
        tap.Publish("cd"u8);
        var seen = new List<string>();

        tap.Subscribe(m => seen.Add(Ascii(m)));
        tap.Publish("ef"u8);

        Assert.Equal(["ab", "cd", "ef"], seen);
    }

    [Fact]
    public void A_second_subscriber_gets_no_replay()
    {
        var tap = new RawOutputTap();
        tap.Publish("ab"u8);
        tap.Subscribe(_ => { });
        var second = new List<string>();

        tap.Subscribe(m => second.Add(Ascii(m)));
        tap.Publish("cd"u8);

        Assert.Equal(["cd"], second);
    }

    [Fact]
    public void StopRetaining_drops_what_was_retained_and_retains_nothing_afterwards()
    {
        var tap = new RawOutputTap();
        tap.Publish("ab"u8);
        tap.StopRetaining();
        tap.Publish("cd"u8);
        var seen = new List<string>();

        tap.Subscribe(m => seen.Add(Ascii(m)));
        tap.Publish("ef"u8);

        Assert.Equal(["ef"], seen);
    }

    [Fact]
    public void Each_chunk_is_a_copy_so_the_producer_may_reuse_its_buffer()
    {
        var tap = new RawOutputTap();
        ReadOnlyMemory<byte> got = default;
        tap.Subscribe(m => got = m);
        byte[] buffer = "xy"u8.ToArray();

        tap.Publish(buffer);
        buffer[0] = (byte)'Z';

        Assert.Equal("xy", Ascii(got));
    }

    [Fact]
    public void A_throwing_subscriber_does_not_escape_Publish_and_later_chunks_still_flow()
    {
        var tap = new RawOutputTap();
        int calls = 0;
        tap.Subscribe(_ => { calls++; throw new InvalidOperationException("boom"); });

        tap.Publish("a"u8);
        tap.Publish("b"u8);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void Empty_chunks_are_not_published()
    {
        var tap = new RawOutputTap();
        int calls = 0;
        tap.Subscribe(_ => calls++);

        tap.Publish(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0, calls);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~RawOutputTapTests"`
Expected: build FAILS — `RawOutputTap` does not exist.

- [ ] **Step 3: Implement the interface and the tap**

`src/Ntilde.Pty/ITerminalByteOutput.cs`:

```csharp
using System;

namespace Ntilde.Pty
{
    /// <summary>
    /// Optional companion to <see cref="ITerminalSession"/>: the session's output as raw bytes,
    /// before any UTF-8 decoding.
    /// </summary>
    /// <remarks>
    /// Exists for the multiplexer, whose headless session runs its own <see cref="Utf8ChunkDecoder"/>
    /// over these bytes so that its <c>ConsumedBytes</c>/<c>PendingTail</c> are the stream position a
    /// snapshot is tagged with. Raised on the session's producer thread, in the same order as the
    /// recorders' <c>RecordChunk</c> and as <see cref="ITerminalIO.OnOutputReceived"/>. Every chunk is
    /// a fresh array the producer never touches again; with more than one subscriber they share it,
    /// so treat it as read-only. Implemented through <see cref="RawOutputTap"/>, which replays chunks
    /// published before the first subscriber (see there for when that retention stops).
    /// </remarks>
    public interface ITerminalByteOutput
    {
        event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived;
    }
}
```

`src/Ntilde.Pty/RawOutputTap.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Ntilde.Pty
{
    /// <summary>
    /// The shared implementation behind <see cref="ITerminalByteOutput"/>: fans raw output chunks out
    /// to subscribers, with first-subscriber replay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replay exists because <c>RustPtySession</c> starts reading in its constructor, so bytes can
    /// arrive before anyone had a chance to subscribe - the same reason the string path buffers for
    /// its first subscriber.
    /// </para>
    /// <para>
    /// Retention is not unconditional: a session whose string event gets a subscriber first (every
    /// GUI pane) calls <see cref="StopRetaining"/>, which drops what was kept and keeps nothing more.
    /// From then on a chunk nobody taps costs a lock and nothing else - no copy, no allocation.
    /// </para>
    /// <para>
    /// Handlers run under this tap's lock, on the producer thread, so they must not block for long
    /// (a bounded-queue <c>Add</c> that exerts back-pressure is fine; waiting on the thread that is
    /// unsubscribing is not). A throwing handler is logged and contained: this runs inside a read
    /// loop whose catch-all would otherwise end the session.
    /// </para>
    /// </remarks>
    public sealed class RawOutputTap
    {
        private readonly object _gate = new();
        private Action<ReadOnlyMemory<byte>>? _handler;
        private List<byte[]>? _retained = new();

        public void Subscribe(Action<ReadOnlyMemory<byte>>? handler)
        {
            if (handler is null) return;
            lock (_gate)
            {
                List<byte[]>? replay = _retained;
                _retained = null;
                _handler += handler;
                if (replay is null) return;
                foreach (byte[] chunk in replay)
                {
                    Invoke(handler, chunk);
                }
            }
        }

        public void Unsubscribe(Action<ReadOnlyMemory<byte>>? handler)
        {
            if (handler is null) return;
            lock (_gate)
            {
                _handler -= handler;
            }
        }

        /// <summary>Drops retained chunks and stops retaining. Idempotent.</summary>
        public void StopRetaining()
        {
            lock (_gate)
            {
                _retained = null;
            }
        }

        /// <summary>Publishes one chunk. Copies it only when someone will see it.</summary>
        public void Publish(ReadOnlySpan<byte> chunk)
        {
            if (chunk.IsEmpty) return;
            lock (_gate)
            {
                if (_handler is { } handler)
                {
                    Invoke(handler, chunk.ToArray());
                    return;
                }

                _retained?.Add(chunk.ToArray());
            }
        }

        private static void Invoke(Action<ReadOnlyMemory<byte>> handler, byte[] chunk)
        {
            try
            {
                handler(chunk);
            }
            catch (Exception ex)
            {
                PtyLogger.Error($"[RawOutputTap] A raw output subscriber threw; the chunk is dropped for it: {ex}");
            }
        }
    }
}
```

- [ ] **Step 4: Run the tap tests** — same command. Expected: 6 PASS.

- [ ] **Step 5: Write the failing RustPtySession test** — `tests/Ntilde.App.Tests/PtyRawOutputTapTests.cs`. It substitutes `pty_read` through the existing internal seam (see `PtyChildStatusProbeFailureTests.NewSession` for the constructor call) and gates each scripted read so ordering is deterministic:

```csharp
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// RustPtySession's raw-byte tap: the bytes are the ones pty_read returned, in read order, and
    /// first-subscriber replay stops once a string subscriber exists.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class PtyRawOutputTapTests
    {
        /// Returns chunk i once gate i is released; signals Entered[i] when read i begins, which
        /// proves read i-1 was fully processed (published) - the loop is single-threaded.
        private sealed class ScriptedReads
        {
            private readonly byte[][] _chunks;
            private int _next;

            public ScriptedReads(params byte[][] chunks)
            {
                _chunks = chunks;
                Gates = chunks.Select(_ => new SemaphoreSlim(0)).ToArray();
                Entered = Enumerable.Range(0, chunks.Length + 1).Select(_ => new SemaphoreSlim(0)).ToArray();
            }

            public SemaphoreSlim[] Gates { get; }
            public SemaphoreSlim[] Entered { get; }

            public int Read(RustPtySession.PtySafeHandle handle, byte[] buffer, int length)
            {
                int i = _next++;
                Entered[Math.Min(i, Entered.Length - 1)].Release();
                if (i >= _chunks.Length)
                {
                    Thread.Sleep(50);
                    return 0; // EOF once the script is spent
                }

                Gates[i].Wait(TimeSpan.FromSeconds(30));
                _chunks[i].CopyTo(buffer, 0);
                return _chunks[i].Length;
            }
        }

        private static RustPtySession NewSession(ScriptedReads reads) =>
            new RustPtySession(
                ShellHelper.GetDefaultShell(), 80, 24, args: null, cwd: null,
                skipPowerShellPostLaunchInit: true, environmentOverrides: null,
                readFromPty: reads.Read);

        private static async Task WaitAsync(SemaphoreSlim s) =>
            Assert.True(await s.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "scripted read never started");

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task Bytes_read_before_the_first_raw_subscriber_are_replayed_then_live_bytes_follow_in_order()
        {
            var reads = new ScriptedReads("AB"u8.ToArray(), [0xC3], [0xA9, (byte)'C', (byte)'D']);
            using RustPtySession session = NewSession(reads);
            var raw = new ConcurrentQueue<byte[]>();

            reads.Gates[0].Release();
            await WaitAsync(reads.Entered[1]); // chunk 0 published - into retention, nobody subscribed
            session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
            reads.Gates[1].Release();
            reads.Gates[2].Release();
            await WaitAsync(reads.Entered[3]);

            Assert.Equal(
                new[] { "AB"u8.ToArray(), new byte[] { 0xC3 }, new byte[] { 0xA9, (byte)'C', (byte)'D' } },
                raw.ToArray());
        }

        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_string_subscriber_that_arrives_first_ends_raw_retention()
        {
            var reads = new ScriptedReads("early"u8.ToArray(), "late"u8.ToArray());
            using RustPtySession session = NewSession(reads);
            var raw = new ConcurrentQueue<byte[]>();
            var text = new ConcurrentQueue<string>();

            session.OnOutputReceived += text.Enqueue;
            reads.Gates[0].Release();
            await WaitAsync(reads.Entered[1]);
            session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
            reads.Gates[1].Release();
            await WaitAsync(reads.Entered[2]);

            Assert.Equal(new[] { "late"u8.ToArray() }, raw.ToArray());
        }
    }
}
```

- [ ] **Step 6: Run to verify it fails**

Run: `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~PtyRawOutputTapTests" --blame-hang-timeout 5m`
Expected: build FAILS — `OnRawOutputReceived` not found on `RustPtySession`.

- [ ] **Step 7: Wire the tap into RustPtySession**

1. Class declaration: `public class RustPtySession : ITerminalSession, ITerminalByteOutput`.
2. Directly below the `_utf8Decoder` field add:

```csharp
        // Raw-byte tap for the multiplexer (ITerminalByteOutput). Published from ReadLoop in read
        // order, right after the recorders, so its stream is exactly the bytes RecordChunk sees.
        // When nothing taps it (every GUI pane), it costs one lock per chunk and allocates nothing:
        // the first string subscriber below calls StopRetaining.
        private readonly RawOutputTap _rawOutput = new();

        public event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived
        {
            add => _rawOutput.Subscribe(value);
            remove => _rawOutput.Unsubscribe(value);
        }
```

3. In `OnOutputReceived.add`, inside `if (!_hasOutputSubscriberEver) {`, directly after `_hasOutputSubscriberEver = true;` add `_rawOutput.StopRetaining();`.
4. In `ReadLoop`, directly after `_flightRecorder?.RecordChunk(buffer, read);` add `_rawOutput.Publish(buffer.AsSpan(0, read));`.

- [ ] **Step 8: Run the RustPtySession tests** — same command as Step 6. Expected: 2 PASS.

- [ ] **Step 9: Write the failing NativeSshSession tests** — append to `NativeSshSessionTests` (it already has `FakeNativeSshInterop` with `Enqueue`, `CreateProfile`, `WaitUntilAsync`):

```csharp
    [Fact]
    public async Task RawOutputCarriesTheWireBytesInOrder_IncludingSplitCodePoints()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(NativeSshEvent.Data(new byte[] { 0xE2, 0x82 }));
        interop.Enqueue(NativeSshEvent.Data(new byte[] { 0xAC, (byte)'!' }));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed(Array.Empty<byte>()));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var raw = new ConcurrentQueue<byte[]>();
        session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += code => exit.TrySetResult(code);

        await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 0xE2, 0x82, 0xAC, (byte)'!' }, raw.SelectMany(b => b).ToArray());
    }

    [Fact]
    public async Task RawOutputCarriesSessionWrittenText_SoItMatchesTheStringStream()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(new NativeSshEvent(NativeSshEventKind.Error, Encoding.UTF8.GetBytes("auth failed"), statusCode: 5));

        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var raw = new ConcurrentQueue<byte[]>();
        session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnExit += code => exit.TrySetResult(code);

        await exit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal("auth failed" + Environment.NewLine, Encoding.UTF8.GetString(raw.SelectMany(b => b).ToArray()));
    }

    [Fact]
    public async Task LateRawSubscriberAfterAStringSubscriber_GetsNoRetainedBytes()
    {
        var interop = new FakeNativeSshInterop();
        using var session = new NativeSshSession(CreateProfile(), interop: interop);
        var text = new ConcurrentQueue<string>();
        session.OnOutputReceived += text.Enqueue;

        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("early")));
        await WaitUntilAsync(() => string.Concat(text) == "early");
        var raw = new ConcurrentQueue<byte[]>();
        session.OnRawOutputReceived += m => raw.Enqueue(m.ToArray());
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("late")));
        await WaitUntilAsync(() => string.Concat(text) == "earlylate");

        Assert.Equal("late", Encoding.UTF8.GetString(raw.SelectMany(b => b).ToArray()));
    }
```

(Add `using System.Linq;` if the file lacks it. If `WaitUntilAsync` here has a different signature, use the file's own.)

- [ ] **Step 10: Run to verify they fail** — `scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~NativeSshSessionTests"`. Expected: build FAILS.

- [ ] **Step 11: Wire the tap into the SSH sessions**

`NativeSshSession.cs`:
1. `public sealed class NativeSshSession : ITerminalSession, ITerminalByteOutput`.
2. Add the same `_rawOutput` field + `OnRawOutputReceived` event as in RustPtySession (comment: "published from the poll loop in wire order, right after the recorders").
3. In `OnOutputReceived.add`, after `_hasOutputSubscriberEver = true;` add `_rawOutput.StopRetaining();`.
4. In `EmitOutput`, after `_flightRecorder?.RecordChunk(payload, payload.Length);` add `_rawOutput.Publish(payload);`.
5. Add below `EmitText`:

```csharp
    // Text this session writes itself (warnings, failure banners) rather than bytes off the wire.
    // Published to the raw tap as UTF-8 too, so a byte subscriber sees everything a string
    // subscriber does. It bypasses the wire decoder, so a partial code point pending in
    // _utf8Decoder is not flushed first - these arrive at connect or at failure, not mid-stream.
    private void EmitSessionText(string text)
    {
        _rawOutput.Publish(Encoding.UTF8.GetBytes(text));
        EmitText(text);
    }
```

6. Replace every `EmitText(` call **except the one inside `EmitOutput`** with `EmitSessionText(` (today: the two constructor warnings, the `warn:` lambda, the poll-loop failure banner, `EmitErrorAndExit`). Verify: `rtk grep -n "EmitText(" src/Ntilde.Platform/Ssh/Sessions/NativeSshSession.cs` shows only the definition, the `EmitOutput` call, and the call inside `EmitSessionText`.

`OpenSshSession.cs` and `SshSession.cs` (both wrap `_inner`): add `ITerminalByteOutput` to the declaration and:

```csharp
    // Forwarded, and loud when the inner session cannot tap: a multiplexer that subscribed and
    // silently got nothing would show an empty screen with no clue why.
    public event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived
    {
        add => InnerBytes.OnRawOutputReceived += value;
        remove => InnerBytes.OnRawOutputReceived -= value;
    }

    private ITerminalByteOutput InnerBytes => _inner as ITerminalByteOutput
        ?? throw new InvalidOperationException($"{_inner.GetType().Name} does not expose raw output.");
```

- [ ] **Step 12: Run all affected suites**

- `scripts/build.ps1 test tests/Ntilde.Platform.Tests` → expected 212 passed (203 + 6 + 3), 30 skipped.
- `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` → 85 passed (`Pty_must_not_depend_on_Vt` still green).
- `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~Pty" --blame-hang-timeout 5m` → all pass.

- [ ] **Step 13: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Pty/ITerminalByteOutput.cs src/Ntilde.Pty/RawOutputTap.cs src/Ntilde.Pty/RustPtySession.cs src/Ntilde.Platform/Ssh/Sessions tests/Ntilde.Platform.Tests tests/Ntilde.App.Tests/PtyRawOutputTapTests.cs
git commit -m "feat(pty): raw output byte tap for the multiplexer"
```

---

### Task 2: Extract `TerminalStateAssert` from the parity harness

The mux tests need the Phase 0 equality specification without the `ParityRun` type. This is a mechanical move guarded by the 108 existing parity cases, plus a vacuity guard so a future edit cannot make it pass everything.

**Files:**
- Create: `tests/Ntilde.VT.Tests/StateTransfer/TerminalStateAssert.cs`, `tests/Ntilde.VT.Tests/StateTransfer/TerminalStateAssertTests.cs`
- Modify: `tests/Ntilde.VT.Tests/StateTransfer/ParityHarness.cs`

**Interfaces:**
- Produces: `internal static class Ntilde.VT.Tests.StateTransfer.TerminalStateAssert` with
  `public static void AssertEquivalent(string where, TerminalBuffer expectedBuffer, AnsiParser expectedParser, TerminalBuffer actualBuffer, AnsiParser actualParser)` and
  `public static void AssertLinesEqual(string what, string[] expected, string[] actual)`. Task 5 links this file into `Ntilde.Mux.Tests`.

- [ ] **Step 1: Write the vacuity-guard tests** — `TerminalStateAssertTests.cs`:

```csharp
using Ntilde.VT;
using Xunit.Sdk;

namespace Ntilde.VT.Tests.StateTransfer;

public sealed class TerminalStateAssertTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser) Terminal(string input)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        parser.Process(input);
        return (buffer, parser);
    }

    [Fact]
    public void Identical_terminals_are_equivalent()
    {
        var a = Terminal("hello\r\n\x1b[1mworld");
        var b = Terminal("hello\r\n\x1b[1mworld");

        TerminalStateAssert.AssertEquivalent("[same]", a.Buffer, a.Parser, b.Buffer, b.Parser);
    }

    [Fact]
    public void One_different_cell_is_caught()
    {
        var a = Terminal("hello");
        var b = Terminal("hellO");

        Assert.ThrowsAny<XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("[cell]", a.Buffer, a.Parser, b.Buffer, b.Parser));
    }

    [Fact]
    public void A_parser_left_mid_escape_is_caught_even_when_the_screens_match()
    {
        var a = Terminal("hi");
        var b = Terminal("hi\x1b[3");

        Assert.ThrowsAny<XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("[parser]", a.Buffer, a.Parser, b.Buffer, b.Parser));
    }
}
```

- [ ] **Step 2: Run to verify it fails** — `scripts/build.ps1 test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~TerminalStateAssertTests"`. Expected: build FAILS (`TerminalStateAssert` missing).

- [ ] **Step 3: Move the comparison**

Create `TerminalStateAssert.cs` (`namespace Ntilde.VT.Tests.StateTransfer;`, usings as `ParityHarness.cs`) containing `internal static class TerminalStateAssert` with a doc comment "The specification of what 'same terminal state' means - every field of the exported state, compared. A field left out here is a field the snapshot is free to get wrong. Shared by the Phase 0 parity suite and the Phase 1 mux suite." Then:

1. `public static void AssertEquivalent(string where, TerminalBuffer expectedBuffer, AnsiParser expectedParser, TerminalBuffer actualBuffer, AnsiParser actualParser)` whose body is today's `ParityHarness.AssertEquivalent` body from the `BufferSnapshot sa = ...` line through the `parser state` assertion, with `a.Buffer`→`expectedBuffer`, `c.Buffer`→`actualBuffer`, `a.Parser`→`expectedParser`, `c.Parser`→`actualParser`. Drop the `string where = ...` line (it is now the parameter) and drop the `device replies` line.
2. Move `AssertScreenEqual`, `AssertEqual<T>`, `AssertLinesEqual`, and every `Describe*`/`DescribeMap`/`DescribeCodeUnits` helper verbatim. Make `AssertLinesEqual` and `AssertEqual<T>` `public`; the rest stay `private`.
3. In the two failure messages change the labels `continuous:` → `expected:` and `restored:` → `actual:`.

Replace `ParityHarness.AssertEquivalent` with:

```csharp
    private static void AssertEquivalent(
        string name, int cut, ParityRun a, List<string> responsesAfterCut, ParityRun c)
    {
        string where = $"[{name} @ cut {cut}]";
        TerminalStateAssert.AssertEquivalent(where, a.Buffer, a.Parser, c.Buffer, c.Parser);
        TerminalStateAssert.AssertLinesEqual(
            $"{where} device replies", responsesAfterCut.ToArray(), c.Responses.ToArray());
    }
```

and delete the moved helpers from `ParityHarness`. Keep `ParityHarness`'s remark about why `AssertEquivalent` compares through `ExportState` by moving it onto `TerminalStateAssert.AssertEquivalent`.

- [ ] **Step 4: Run the whole VT suite** — `scripts/build.ps1 test tests/Ntilde.VT.Tests`. Expected: 728 passed (725 + 3), 0 failed.

- [ ] **Step 5: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add tests/Ntilde.VT.Tests/StateTransfer
git commit -m "test(vt): extract the terminal-state equality spec so the mux suite can share it"
```

---

### Task 3: `Ntilde.Mux.Contracts` + the `Ntilde.Mux.Tests` project

**Files:**
- Create: `src/Ntilde.Mux.Contracts/{Ntilde.Mux.Contracts.csproj, MuxProtocol.cs, MuxFrameCodec.cs, MuxFrameReader.cs, MuxOutboundFrame.cs, MuxFrames.cs, MuxMessages.cs, MuxJsonContext.cs}`
- Create: `tests/Ntilde.Mux.Tests/Ntilde.Mux.Tests.csproj`, `tests/Ntilde.Mux.Tests/Contracts/{MuxFrameCodecTests.cs, MuxFramesTests.cs, MuxJsonTests.cs, MuxVersionNegotiationTests.cs}`
- Modify: `Ntilde.sln`, `.github/workflows/ci.yml`, `tests/Ntilde.Architecture.Tests/{Ntilde.Architecture.Tests.csproj, LayeringTests.cs, ProjectFileLayeringTests.cs, NamespaceAlignmentTests.cs}`

**Interfaces:**
- Produces (namespace `Ntilde.Mux.Contracts`): `MuxProtocol { MinSupportedVersion=1, MaxSupportedVersion=1, MaxFrameBytes=64 MiB, FrameHeaderBytes=5, static int? NegotiateVersion(int serverMin,int serverMax,int clientMin,int clientMax) }`; `MuxErrorCodes`; `MuxMethods`; `enum MuxFrameKind : byte`; `MuxProtocolException(string code, string message)` with `Code`; `MuxFrameCodec.WriteHeader/ReadHeader`; `MuxInboundFrame : IDisposable { Kind, Length, Payload }`; `MuxFrameReader.Read(Stream, int maxFrameBytes = MaxFrameBytes) → MuxInboundFrame?`; `MuxOutboundFrame { Rent(kind, payloadLength), Kind, PayloadLength, Length, Payload (Span), Bytes, AddRef(), Release(), WriteTo(Stream) }`; `MuxFrames` builders (`Request/Response/Notification/Json/Input/Output/ResizeEvent/Snapshot`), parsers (`TryParseInput/TryParseOutput/TryParseResizeEvent/TryParseSnapshot`), `ParseJson<T>`, `ParseParams<T>`, `ToElement<T>`, constants `SessionIdBytes=16`, `SnapshotHeaderBytes=32`; all DTOs in `MuxMessages.cs`; `MuxJsonContext`.

- [ ] **Step 1: Create the projects and wire them in**

`src/Ntilde.Mux.Contracts/Ntilde.Mux.Contracts.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Leaf wire-protocol library for the multiplexer
    (docs/superpowers/specs/2026-09-23-ntilde-mux-phase1.md §6). Shared by Ntilde.Mux today and
    by any future out-of-process client. Must have zero project references; enforced by
    Ntilde.Architecture.Tests (MuxContracts_csproj_must_have_no_project_references and the IL-level
    MuxContracts_must_be_a_leaf_assembly).
  -->

  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

</Project>
```

`tests/Ntilde.Mux.Tests/Ntilde.Mux.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- CA1861 (constant array arguments) is noise in assertions over literal byte sequences. -->
    <NoWarn>$(NoWarn);CA1861</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Ntilde.Mux.Contracts\Ntilde.Mux.Contracts.csproj" />
  </ItemGroup>
</Project>
```

Add both to the solution: `scripts/build.ps1 sln Ntilde.sln add src/Ntilde.Mux.Contracts/Ntilde.Mux.Contracts.csproj tests/Ntilde.Mux.Tests/Ntilde.Mux.Tests.csproj`. Check `git diff Ntilde.sln`: both must nest under the **existing** `src` / `tests` solution folders, with no duplicate folder created.

`.github/workflows/ci.yml` — three edits (without them every solution-wide `--no-build` test job fails with "argument invalid"):
1. The build-artifact `path:` list (next to `tests/Ntilde.McpServer.Tests/bin/${{ env.CONFIGURATION }}`): add `tests/Ntilde.Mux.Tests/bin/${{ env.CONFIGURATION }}`.
2. The gating unit loop: `for proj in VT Rendering Architecture Platform McpServer Mux; do`.
3. The coverage loop: `for proj in VT Rendering Platform McpServer Mux; do`.

- [ ] **Step 2: Write the failing contract tests**

`tests/Ntilde.Mux.Tests/Contracts/MuxVersionNegotiationTests.cs`:

```csharp
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxVersionNegotiationTests
{
    [Theory]
    [InlineData(1, 3, 2, 5, 3)]
    [InlineData(1, 1, 1, 1, 1)]
    [InlineData(2, 6, 1, 4, 4)]
    public void Overlapping_ranges_pick_the_highest_common_version(int sMin, int sMax, int cMin, int cMax, int expected)
        => Assert.Equal(expected, MuxProtocol.NegotiateVersion(sMin, sMax, cMin, cMax));

    [Theory]
    [InlineData(1, 1, 2, 3)]
    [InlineData(4, 5, 1, 3)]
    [InlineData(3, 1, 1, 3)] // inverted server range
    [InlineData(1, 3, 3, 1)] // inverted client range
    public void Disjoint_or_inverted_ranges_have_no_version(int sMin, int sMax, int cMin, int cMax)
        => Assert.Null(MuxProtocol.NegotiateVersion(sMin, sMax, cMin, cMax));
}
```

`tests/Ntilde.Mux.Tests/Contracts/MuxFrameCodecTests.cs`:

```csharp
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxFrameCodecTests
{
    [Fact]
    public void Header_is_kind_then_little_endian_u32_length()
    {
        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Output, 0x01020304);
        Assert.Equal(new byte[] { 0x11, 0x04, 0x03, 0x02, 0x01 }, header);
    }

    [Fact]
    public void A_length_above_the_ceiling_is_frame_too_large()
    {
        byte[] header = [0x01, 0, 0, 0, 0];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, 101);
        var ex = Assert.Throws<MuxProtocolException>(() => MuxFrameCodec.ReadHeader(header, maxFrameBytes: 100));
        Assert.Equal(MuxErrorCodes.FrameTooLarge, ex.Code);
    }

    [Fact]
    public void An_unknown_kind_is_a_protocol_error()
    {
        byte[] header = [0x7F, 1, 0, 0, 0];
        var ex = Assert.Throws<MuxProtocolException>(() => MuxFrameCodec.ReadHeader(header, MuxProtocol.MaxFrameBytes));
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
    }

    [Fact]
    public void Reader_returns_null_on_a_clean_end_of_stream()
        => Assert.Null(MuxFrameReader.Read(new MemoryStream()));

    [Fact]
    public void Reader_round_trips_a_frame()
    {
        var stream = new MemoryStream();
        MuxOutboundFrame outbound = MuxFrames.Output(Guid.NewGuid(), 7, "hi"u8);
        outbound.WriteTo(stream);
        outbound.Release();
        stream.Position = 0;

        using MuxInboundFrame? frame = MuxFrameReader.Read(stream);

        Assert.NotNull(frame);
        Assert.Equal(MuxFrameKind.Output, frame!.Kind);
        Assert.Equal(MuxFrames.SessionIdBytes + sizeof(long) + 2, frame.Length);
    }

    [Fact]
    public void A_truncated_header_or_payload_is_end_of_stream()
    {
        Assert.Throws<EndOfStreamException>(() => MuxFrameReader.Read(new MemoryStream([0x01, 0x05])));
        Assert.Throws<EndOfStreamException>(() => MuxFrameReader.Read(new MemoryStream([0x01, 0x05, 0, 0, 0, (byte)'{'])));
    }

    [Fact]
    public void An_oversize_header_is_refused_before_any_payload_is_read()
    {
        var stream = new MemoryStream();
        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, 1000);
        stream.Write(header);
        stream.Write(new byte[1000]);
        stream.Position = 0;

        var ex = Assert.Throws<MuxProtocolException>(() => MuxFrameReader.Read(stream, maxFrameBytes: 999));

        Assert.Equal(MuxErrorCodes.FrameTooLarge, ex.Code);
        Assert.Equal(MuxProtocol.FrameHeaderBytes, stream.Position);
    }

    [Fact]
    public void Outbound_frames_are_reference_counted()
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Output, 4);
        frame.AddRef();
        frame.Release();
        Assert.Equal(MuxProtocol.FrameHeaderBytes + 4, frame.Bytes.Length); // still alive: one ref left
        frame.Release();
        Assert.Throws<ObjectDisposedException>(() => frame.AddRef());
        Assert.Throws<InvalidOperationException>(() => frame.Release());
    }
}
```

`tests/Ntilde.Mux.Tests/Contracts/MuxFramesTests.cs`:

```csharp
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxFramesTests
{
    private static readonly Guid Id = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private static byte[] PayloadOf(MuxOutboundFrame frame)
    {
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        frame.Release();
        return payload;
    }

    [Fact]
    public void Output_round_trips_session_seq_and_bytes()
    {
        byte[] payload = PayloadOf(MuxFrames.Output(Id, 123_456_789_012, "abc"u8));
        Assert.True(MuxFrames.TryParseOutput(payload, out Guid id, out long seq, out ReadOnlySpan<byte> data));
        Assert.Equal(Id, id);
        Assert.Equal(123_456_789_012, seq);
        Assert.Equal("abc"u8.ToArray(), data.ToArray());
    }

    [Fact]
    public void Input_carries_utf8_bytes_verbatim()
    {
        byte[] payload = PayloadOf(MuxFrames.Input(Id, "é\r"u8));
        Assert.True(MuxFrames.TryParseInput(payload, out Guid id, out ReadOnlySpan<byte> utf8));
        Assert.Equal(Id, id);
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0x0D }, utf8.ToArray());
    }

    [Fact]
    public void ResizeEvent_is_exactly_32_bytes_and_round_trips()
    {
        byte[] payload = PayloadOf(MuxFrames.ResizeEvent(Id, 42, 132, 43));
        Assert.Equal(32, payload.Length);
        Assert.True(MuxFrames.TryParseResizeEvent(payload, out Guid id, out long seq, out int cols, out int rows));
        Assert.Equal((Id, 42L, 132, 43), (id, seq, cols, rows));
    }

    [Fact]
    public void Snapshot_round_trips()
    {
        byte[] payload = PayloadOf(MuxFrames.Snapshot(9, Id, 77, "{}"u8));
        Assert.True(MuxFrames.TryParseSnapshot(payload, out long requestId, out Guid id, out long seq, out ReadOnlySpan<byte> json));
        Assert.Equal((9L, Id, 77L), (requestId, id, seq));
        Assert.Equal("{}"u8.ToArray(), json.ToArray());
    }

    [Fact]
    public void Short_payloads_are_rejected_not_sliced()
    {
        Assert.False(MuxFrames.TryParseInput(new byte[15], out _, out _));
        Assert.False(MuxFrames.TryParseOutput(new byte[23], out _, out _, out _));
        Assert.False(MuxFrames.TryParseResizeEvent(new byte[31], out _, out _, out _, out _));
        Assert.False(MuxFrames.TryParseResizeEvent(new byte[33], out _, out _, out _, out _));
        Assert.False(MuxFrames.TryParseSnapshot(new byte[31], out _, out _, out _, out _));
    }
}
```

`tests/Ntilde.Mux.Tests/Contracts/MuxJsonTests.cs`:

```csharp
using System.Text;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Contracts;

public sealed class MuxJsonTests
{
    [Fact]
    public void Requests_round_trip_with_camel_case_params()
    {
        var attach = new AttachParams
        {
            SessionId = Guid.NewGuid(),
            MaxScrollbackRows = 500,
            Presentation = new MuxPresentation { Cols = 80, Rows = 24, CellWidthPx = 9, CellHeightPx = 18, DefaultBg = 0xFF102030 },
        };
        MuxOutboundFrame frame = MuxFrames.Request(new MuxRequest
        {
            Id = 3, Method = MuxMethods.Attach, Params = MuxFrames.ToElement(attach, MuxJsonContext.Default.AttachParams),
        });
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        frame.Release();

        string json = Encoding.UTF8.GetString(payload);
        Assert.Contains("\"maxScrollbackRows\":500", json, StringComparison.Ordinal);
        Assert.Contains("\"cellWidthPx\":9", json, StringComparison.Ordinal);

        MuxRequest request = MuxFrames.ParseJson(payload, MuxJsonContext.Default.MuxRequest);
        AttachParams back = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.AttachParams);
        Assert.Equal(attach, back);
    }

    [Fact]
    public void Malformed_json_is_a_protocol_error()
    {
        var ex = Assert.Throws<MuxProtocolException>(() =>
            MuxFrames.ParseJson("{nope"u8, MuxJsonContext.Default.MuxRequest));
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
    }

    [Fact]
    public void A_missing_required_member_is_a_protocol_error()
    {
        MuxRequest request = MuxFrames.ParseJson(
            "{\"id\":1,\"method\":\"attach\",\"params\":{\"sessionId\":\"00000000-0000-0000-0000-000000000001\"}}"u8,
            MuxJsonContext.Default.MuxRequest);
        var ex = Assert.Throws<MuxProtocolException>(() =>
            MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.AttachParams));
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
    }

    [Fact]
    public void Missing_params_are_a_protocol_error()
    {
        var ex = Assert.Throws<MuxProtocolException>(() =>
            MuxFrames.ParseParams(null, MuxJsonContext.Default.SessionIdParams));
        Assert.Equal(MuxErrorCodes.ProtocolError, ex.Code);
    }
}
```

- [ ] **Step 3: Run to verify they fail** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests`. Expected: build FAILS (types missing).

- [ ] **Step 4: Implement the protocol constants** — `src/Ntilde.Mux.Contracts/MuxProtocol.cs`:

```csharp
namespace Ntilde.Mux.Contracts;

/// <summary>
/// Protocol constants for the multiplexer wire (spec §6). Frame = <c>u8 kind</c>,
/// <c>u32 payloadLength</c> little-endian, payload.
/// </summary>
public static class MuxProtocol
{
    public const int MinSupportedVersion = 1;
    public const int MaxSupportedVersion = 1;

    /// <summary>Largest payload a frame may announce. A 10k-row 80x24 snapshot measured 12.9 MB in Phase 0.</summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    public const int FrameHeaderBytes = 5;

    /// <summary>
    /// Real range negotiation (client and daemon builds drift): the highest version both sides
    /// speak, or null when the ranges do not overlap or either is inverted.
    /// </summary>
    public static int? NegotiateVersion(int serverMin, int serverMax, int clientMin, int clientMax)
    {
        if (serverMin > serverMax || clientMin > clientMax) return null;
        int high = Math.Min(serverMax, clientMax);
        int low = Math.Max(serverMin, clientMin);
        return high >= low ? high : null;
    }
}

public static class MuxErrorCodes
{
    public const string VersionMismatch = "version_mismatch";
    public const string UnknownSession = "unknown_session";
    public const string SessionExited = "session_exited";
    public const string FrameTooLarge = "frame_too_large";
    public const string SnapshotTooLarge = "snapshot_too_large";
    public const string ClientTooSlow = "client_too_slow";
    public const string ProtocolError = "protocol_error";
    public const string SpawnFailed = "spawn_failed";
    public const string Internal = "internal_error";
}

public static class MuxMethods
{
    public const string Hello = "hello";
    public const string ListSessions = "listSessions";
    public const string Spawn = "spawn";
    public const string Attach = "attach";
    public const string Detach = "detach";
    public const string Kill = "kill";
    public const string Resize = "resize";
    public const string SessionInfo = "sessionInfo";
    public const string StartRecording = "startRecording";
    public const string StopRecording = "stopRecording";
    public const string EnableFlightRecording = "enableFlightRecording";
    public const string DisableFlightRecording = "disableFlightRecording";
    public const string ExportFlight = "exportFlight";
    public const string Ping = "ping";

    /// <summary>Notification (server → client).</summary>
    public const string Exited = "exited";
}

public enum MuxFrameKind : byte
{
    Request = 0x01,
    Response = 0x02,
    Notification = 0x03,
    Input = 0x10,
    Output = 0x11,
    ResizeEvent = 0x12,
    Snapshot = 0x13,
}

/// <summary>A protocol-level failure carrying one of <see cref="MuxErrorCodes"/>.</summary>
public sealed class MuxProtocolException : Exception
{
    public MuxProtocolException() : this(MuxErrorCodes.ProtocolError, "Multiplexer protocol error.") { }
    public MuxProtocolException(string message) : this(MuxErrorCodes.ProtocolError, message) { }
    public MuxProtocolException(string message, Exception innerException) : this(MuxErrorCodes.ProtocolError, message, innerException) { }
    public MuxProtocolException(string code, string message) : base(message) => Code = code;
    public MuxProtocolException(string code, string message, Exception innerException) : base(message, innerException) => Code = code;

    public string Code { get; }
}
```

- [ ] **Step 5: Implement the codec, reader, outbound frame** —

`MuxFrameCodec.cs`:

```csharp
using System.Buffers.Binary;

namespace Ntilde.Mux.Contracts;

public static class MuxFrameCodec
{
    public static bool IsKnownKind(byte kind) => kind is 0x01 or 0x02 or 0x03 or 0x10 or 0x11 or 0x12 or 0x13;

    public static void WriteHeader(Span<byte> destination, MuxFrameKind kind, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        destination[0] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(1, 4), (uint)payloadLength);
    }

    /// <summary>
    /// Validates a received header. Length is checked before kind so an oversize announcement is
    /// always reported as such.
    /// </summary>
    public static (MuxFrameKind Kind, int PayloadLength) ReadHeader(ReadOnlySpan<byte> header, int maxFrameBytes)
    {
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(1, 4));
        if (length > (uint)maxFrameBytes)
        {
            throw new MuxProtocolException(MuxErrorCodes.FrameTooLarge,
                $"Frame announces {length} payload bytes; the limit is {maxFrameBytes}.");
        }

        if (!IsKnownKind(header[0]))
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Unknown frame kind 0x{header[0]:X2}.");
        }

        return ((MuxFrameKind)header[0], (int)length);
    }
}
```

`MuxFrameReader.cs`:

```csharp
using System.Buffers;

namespace Ntilde.Mux.Contracts;

/// <summary>A received frame whose payload lives in a pooled buffer until <see cref="Dispose"/>.</summary>
public sealed class MuxInboundFrame : IDisposable
{
    private byte[]? _rented;

    internal MuxInboundFrame(MuxFrameKind kind, byte[] rented, int length)
    {
        Kind = kind;
        _rented = rented;
        Length = length;
    }

    public MuxFrameKind Kind { get; }
    public int Length { get; }

    public ReadOnlySpan<byte> Payload =>
        (_rented ?? throw new ObjectDisposedException(nameof(MuxInboundFrame))).AsSpan(0, Length);

    public void Dispose()
    {
        byte[]? rented = Interlocked.Exchange(ref _rented, null);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}

public static class MuxFrameReader
{
    /// <summary>
    /// Blocking read of one frame, for dedicated reader threads. Returns null on a clean end of
    /// stream at a frame boundary; throws <see cref="EndOfStreamException"/> mid-frame and
    /// <see cref="MuxProtocolException"/> for a header that fails validation (before any payload
    /// byte is read, so an oversize announcement costs nothing).
    /// </summary>
    public static MuxInboundFrame? Read(Stream stream, int maxFrameBytes = MuxProtocol.MaxFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Span<byte> header = stackalloc byte[MuxProtocol.FrameHeaderBytes];
        int got = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (got == 0) return null;
        if (got < header.Length) throw new EndOfStreamException("Stream ended inside a frame header.");

        (MuxFrameKind kind, int length) = MuxFrameCodec.ReadHeader(header, maxFrameBytes);
        byte[] rented = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            stream.ReadExactly(rented, 0, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }

        return new MuxInboundFrame(kind, rented, length);
    }
}
```

`MuxOutboundFrame.cs`:

```csharp
using System.Buffers;

namespace Ntilde.Mux.Contracts;

/// <summary>
/// A frame ready to write - header and payload in one <see cref="ArrayPool{T}"/> buffer - shared by
/// reference count: a session builds one <c>Output</c> frame per chunk and every attached client's
/// sender holds a reference until it has written it. The creator owns the first reference.
/// </summary>
public sealed class MuxOutboundFrame
{
    private byte[]? _buffer;
    private int _refCount = 1;

    private MuxOutboundFrame(MuxFrameKind kind, byte[] buffer, int payloadLength)
    {
        Kind = kind;
        _buffer = buffer;
        PayloadLength = payloadLength;
    }

    public MuxFrameKind Kind { get; }
    public int PayloadLength { get; }
    public int Length => MuxProtocol.FrameHeaderBytes + PayloadLength;

    /// <summary>Writable payload area; for the creator, before the frame is shared.</summary>
    public Span<byte> Payload => Buffer.AsSpan(MuxProtocol.FrameHeaderBytes, PayloadLength);

    public ReadOnlySpan<byte> Bytes => Buffer.AsSpan(0, Length);

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(MuxOutboundFrame));

    public static MuxOutboundFrame Rent(MuxFrameKind kind, int payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        if (payloadLength > MuxProtocol.MaxFrameBytes)
        {
            throw new MuxProtocolException(MuxErrorCodes.FrameTooLarge,
                $"A {payloadLength}-byte payload exceeds the {MuxProtocol.MaxFrameBytes}-byte frame limit.");
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(MuxProtocol.FrameHeaderBytes + payloadLength);
        MuxFrameCodec.WriteHeader(buffer, kind, payloadLength);
        return new MuxOutboundFrame(kind, buffer, payloadLength);
    }

    public void AddRef()
    {
        if (Interlocked.Increment(ref _refCount) <= 1)
        {
            throw new ObjectDisposedException(nameof(MuxOutboundFrame), "AddRef on a frame already returned to the pool.");
        }
    }

    public void Release()
    {
        int remaining = Interlocked.Decrement(ref _refCount);
        if (remaining == 0)
        {
            byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer);
        }
        else if (remaining < 0)
        {
            throw new InvalidOperationException("MuxOutboundFrame released more times than it was referenced.");
        }
    }

    public void WriteTo(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(Bytes);
    }
}
```

- [ ] **Step 6: Implement the DTOs and context** —

`MuxMessages.cs`:

```csharp
using System.Text.Json;

namespace Ntilde.Mux.Contracts;

public sealed record MuxRequest
{
    /// <summary>0 = no response wanted.</summary>
    public long Id { get; init; }
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}

public sealed record MuxResponse
{
    /// <summary>0 with an <see cref="Error"/> = connection-level, sent just before the server closes.</summary>
    public long Id { get; init; }
    public JsonElement? Result { get; init; }
    public MuxError? Error { get; init; }
}

public sealed record MuxNotification
{
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
}

public sealed record MuxError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record MuxEmpty;

public sealed record HelloParams
{
    public int MinVersion { get; init; }
    public int MaxVersion { get; init; }
    public string ClientKind { get; init; } = string.Empty;
}

public sealed record WelcomeResult
{
    public int Version { get; init; }
    public bool ForceConPtyFiltering { get; init; }
}

/// <summary>What the client looks like: applied to the mux parser so its replies describe it.</summary>
public sealed record MuxPresentation
{
    public int Cols { get; init; }
    public int Rows { get; init; }
    public float CellWidthPx { get; init; }
    public float CellHeightPx { get; init; }
    /// <summary>ARGB as <c>TermColor.ToUint()</c>; null = parser default.</summary>
    public uint? DefaultFg { get; init; }
    public uint? DefaultBg { get; init; }
    public bool KittyKeyboardEnabled { get; init; } = true;
}

/// <summary>The local fields of <c>TerminalSessionRequest</c>; SSH is not spawnable through the mux (spec §9.2).</summary>
public sealed record SpawnParams
{
    public required string Command { get; init; }
    public string Arguments { get; init; } = string.Empty;
    public string StartingDirectory { get; init; } = string.Empty;
    public int Cols { get; init; } = 80;
    public int Rows { get; init; } = 24;
    public IReadOnlyDictionary<string, string>? EnvironmentOverrides { get; init; }
    public bool SkipPowerShellPostLaunchInit { get; init; }
    public string Title { get; init; } = string.Empty;
}

public sealed record SpawnResult { public Guid SessionId { get; init; } }

public sealed record SessionSummary
{
    public Guid SessionId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string? Arguments { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
    public bool Running { get; init; }
    public int? ExitCode { get; init; }
    public int AttachedClients { get; init; }
    public bool Faulted { get; init; }
}

public sealed record ListSessionsResult { public IReadOnlyList<SessionSummary> Sessions { get; init; } = []; }

public sealed record AttachParams
{
    public Guid SessionId { get; init; }
    public int MaxScrollbackRows { get; init; }
    public required MuxPresentation Presentation { get; init; }
}

public sealed record SessionIdParams { public Guid SessionId { get; init; } }

public sealed record ResizeParams
{
    public Guid SessionId { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
    public MuxPresentation? Presentation { get; init; }
}

public sealed record SessionInfoResult
{
    public bool Running { get; init; }
    public int? ExitCode { get; init; }
    public bool HasActiveChildProcesses { get; init; }
    public int? Pid { get; init; }
}

public sealed record StartRecordingParams
{
    public Guid SessionId { get; init; }
    public required string Path { get; init; }
}

public sealed record EnableFlightRecordingParams
{
    public Guid SessionId { get; init; }
    public long MaxBytes { get; init; }
}

/// <summary>Bytes, not a path, so a remote daemon (Phase 4) needs no change.</summary>
public sealed record ExportFlightResult
{
    public bool Exported { get; init; }
    public byte[]? Bytes { get; init; }
    public int EventCount { get; init; }
    public long FirstEventMs { get; init; }
    public long LastEventMs { get; init; }
    public bool TruncatedAtStart { get; init; }
}

public sealed record ExitedNotification
{
    public Guid SessionId { get; init; }
    public int ExitCode { get; init; }
}
```

`MuxJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Ntilde.Mux.Contracts;

/// <summary>
/// Source-generated JSON context for every control-frame type. Both ends serialize exclusively
/// through it: reflection-free (Native AOT) and one wire shape.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MuxRequest))]
[JsonSerializable(typeof(MuxResponse))]
[JsonSerializable(typeof(MuxNotification))]
[JsonSerializable(typeof(MuxError))]
[JsonSerializable(typeof(MuxEmpty))]
[JsonSerializable(typeof(HelloParams))]
[JsonSerializable(typeof(WelcomeResult))]
[JsonSerializable(typeof(MuxPresentation))]
[JsonSerializable(typeof(SpawnParams))]
[JsonSerializable(typeof(SpawnResult))]
[JsonSerializable(typeof(SessionSummary))]
[JsonSerializable(typeof(ListSessionsResult))]
[JsonSerializable(typeof(AttachParams))]
[JsonSerializable(typeof(SessionIdParams))]
[JsonSerializable(typeof(ResizeParams))]
[JsonSerializable(typeof(SessionInfoResult))]
[JsonSerializable(typeof(StartRecordingParams))]
[JsonSerializable(typeof(EnableFlightRecordingParams))]
[JsonSerializable(typeof(ExportFlightResult))]
[JsonSerializable(typeof(ExitedNotification))]
public sealed partial class MuxJsonContext : JsonSerializerContext
{
}
```

- [ ] **Step 7: Implement `MuxFrames`** — `MuxFrames.cs`:

```csharp
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Ntilde.Mux.Contracts;

/// <summary>
/// Builders and parsers for every frame payload. Binary layouts (spec §6), all little-endian,
/// Guids in <see cref="Guid.TryWriteBytes(Span{byte})"/> layout:
/// Input = id[16] + utf8; Output = id[16] + seq i64 + bytes; ResizeEvent = id[16] + seq i64 +
/// cols i32 + rows i32; Snapshot = requestId i64 + id[16] + seq i64 + json.
/// Parsers length-check before slicing: every byte here came from a peer.
/// </summary>
public static class MuxFrames
{
    public const int SessionIdBytes = 16;
    public const int OutputHeaderBytes = SessionIdBytes + sizeof(long);
    public const int ResizeEventPayloadBytes = SessionIdBytes + sizeof(long) + (2 * sizeof(int));
    public const int SnapshotHeaderBytes = sizeof(long) + SessionIdBytes + sizeof(long);

    public static MuxOutboundFrame Request(MuxRequest request) =>
        Json(MuxFrameKind.Request, request, MuxJsonContext.Default.MuxRequest);

    public static MuxOutboundFrame Response(MuxResponse response) =>
        Json(MuxFrameKind.Response, response, MuxJsonContext.Default.MuxResponse);

    public static MuxOutboundFrame Notification(MuxNotification notification) =>
        Json(MuxFrameKind.Notification, notification, MuxJsonContext.Default.MuxNotification);

    public static MuxOutboundFrame Json<T>(MuxFrameKind kind, T value, JsonTypeInfo<T> typeInfo)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(kind, json.Length);
        json.CopyTo(frame.Payload);
        return frame;
    }

    public static MuxOutboundFrame Input(Guid sessionId, ReadOnlySpan<byte> utf8)
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Input, SessionIdBytes + utf8.Length);
        Span<byte> p = frame.Payload;
        WriteGuid(p, sessionId);
        utf8.CopyTo(p[SessionIdBytes..]);
        return frame;
    }

    public static MuxOutboundFrame Output(Guid sessionId, long seq, ReadOnlySpan<byte> data)
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Output, OutputHeaderBytes + data.Length);
        Span<byte> p = frame.Payload;
        WriteGuid(p, sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(p.Slice(SessionIdBytes, 8), seq);
        data.CopyTo(p[OutputHeaderBytes..]);
        return frame;
    }

    public static MuxOutboundFrame ResizeEvent(Guid sessionId, long seq, int cols, int rows)
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.ResizeEvent, ResizeEventPayloadBytes);
        Span<byte> p = frame.Payload;
        WriteGuid(p, sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(p.Slice(16, 8), seq);
        BinaryPrimitives.WriteInt32LittleEndian(p.Slice(24, 4), cols);
        BinaryPrimitives.WriteInt32LittleEndian(p.Slice(28, 4), rows);
        return frame;
    }

    public static MuxOutboundFrame Snapshot(long requestId, Guid sessionId, long seq, ReadOnlySpan<byte> snapshotJson)
    {
        MuxOutboundFrame frame = MuxOutboundFrame.Rent(MuxFrameKind.Snapshot, SnapshotHeaderBytes + snapshotJson.Length);
        Span<byte> p = frame.Payload;
        BinaryPrimitives.WriteInt64LittleEndian(p[..8], requestId);
        WriteGuid(p.Slice(8, SessionIdBytes), sessionId);
        BinaryPrimitives.WriteInt64LittleEndian(p.Slice(24, 8), seq);
        snapshotJson.CopyTo(p[SnapshotHeaderBytes..]);
        return frame;
    }

    public static bool TryParseInput(ReadOnlySpan<byte> payload, out Guid sessionId, out ReadOnlySpan<byte> utf8)
    {
        if (payload.Length < SessionIdBytes) { sessionId = default; utf8 = default; return false; }
        sessionId = new Guid(payload[..SessionIdBytes]);
        utf8 = payload[SessionIdBytes..];
        return true;
    }

    public static bool TryParseOutput(ReadOnlySpan<byte> payload, out Guid sessionId, out long seq, out ReadOnlySpan<byte> data)
    {
        if (payload.Length < OutputHeaderBytes) { sessionId = default; seq = 0; data = default; return false; }
        sessionId = new Guid(payload[..SessionIdBytes]);
        seq = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(SessionIdBytes, 8));
        data = payload[OutputHeaderBytes..];
        return true;
    }

    public static bool TryParseResizeEvent(ReadOnlySpan<byte> payload, out Guid sessionId, out long seq, out int cols, out int rows)
    {
        if (payload.Length != ResizeEventPayloadBytes) { sessionId = default; seq = 0; cols = 0; rows = 0; return false; }
        sessionId = new Guid(payload[..SessionIdBytes]);
        seq = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(16, 8));
        cols = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, 4));
        rows = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(28, 4));
        return true;
    }

    public static bool TryParseSnapshot(ReadOnlySpan<byte> payload, out long requestId, out Guid sessionId, out long seq, out ReadOnlySpan<byte> snapshotJson)
    {
        if (payload.Length < SnapshotHeaderBytes) { requestId = 0; sessionId = default; seq = 0; snapshotJson = default; return false; }
        requestId = BinaryPrimitives.ReadInt64LittleEndian(payload[..8]);
        sessionId = new Guid(payload.Slice(8, SessionIdBytes));
        seq = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(24, 8));
        snapshotJson = payload[SnapshotHeaderBytes..];
        return true;
    }

    /// <summary>Parses a JSON payload; malformed, null or incomplete (missing <c>required</c>) → protocol_error.</summary>
    public static T ParseJson<T>(ReadOnlySpan<byte> payload, JsonTypeInfo<T> typeInfo) where T : class
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize(payload, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Malformed {typeof(T).Name}: {ex.Message}", ex);
        }

        return value ?? throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"{typeof(T).Name} payload was null.");
    }

    /// <summary>Parses request params / response results with the same rules as <see cref="ParseJson{T}"/>.</summary>
    public static T ParseParams<T>(JsonElement? element, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (element is not { } e)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"{typeof(T).Name} is missing.");
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(e, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Malformed {typeof(T).Name}: {ex.Message}", ex);
        }

        return value ?? throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"{typeof(T).Name} was null.");
    }

    public static JsonElement ToElement<T>(T value, JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToElement(value, typeInfo);

    private static void WriteGuid(Span<byte> destination, Guid id)
    {
        if (!id.TryWriteBytes(destination))
        {
            throw new InvalidOperationException("Destination too small for a Guid.");
        }
    }
}
```

- [ ] **Step 8: Run the contract tests** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests`. Expected: all PASS (≈ 28). Also `scripts/build.ps1 build src/Ntilde.Mux.Contracts -c Release` → 0 warnings (AOT analyzers are on).

- [ ] **Step 9: Add the architecture tests for the contracts leaf**

`tests/Ntilde.Architecture.Tests/Ntilde.Architecture.Tests.csproj`: add `<ProjectReference Include="..\..\src\Ntilde.Mux.Contracts\Ntilde.Mux.Contracts.csproj" />`.

`LayeringTests.cs` — add the accessor and test; add `MuxContracts` to the `No_production_assembly_references_test_assemblies` array:

```csharp
    private static Assembly MuxContracts => typeof(global::Ntilde.Mux.Contracts.MuxProtocol).Assembly;

    /// <summary>
    /// The IL sibling of <c>MuxContracts_csproj_must_have_no_project_references</c>. "Ntilde.Mux" is
    /// deliberately not in the list: it is this assembly's own namespace root, and Mux referencing
    /// Contracts (not the reverse) is enforced by the project-file test.
    /// </summary>
    [Fact]
    public void MuxContracts_must_be_a_leaf_assembly()
    {
        var result = Types.InAssembly(MuxContracts)
            .Should()
            .NotHaveDependencyOnAny(
                "Ntilde.VT", "Ntilde.Replay", "Ntilde.Rendering", "Ntilde.Pty", "Ntilde.Platform",
                "Ntilde.App", "Ntilde.AgentHost", "Avalonia", "SkiaSharp")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Mux.Contracts is a wire-contract leaf. Offenders: {Join(result.FailingTypeNames)}");
    }
```

`ProjectFileLayeringTests.cs`:

```csharp
    [Fact]
    public void MuxContracts_csproj_must_have_no_project_references()
    {
        var refs = ProjectReferences("src/Ntilde.Mux.Contracts/Ntilde.Mux.Contracts.csproj");
        Assert.Empty(refs);
    }
```

`NamespaceAlignmentTests.cs`: add `"Ntilde.Mux.Contracts"` to `LeafAssemblies` and `[InlineData("Ntilde.Mux.Contracts")]` to the theory.

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` → 88 passed (85 + two facts + one theory row).

- [ ] **Step 10: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux.Contracts tests/Ntilde.Mux.Tests tests/Ntilde.Architecture.Tests Ntilde.sln .github/workflows/ci.yml
git commit -m "feat(mux): wire-protocol contracts leaf and the Mux test project"
```

---

### Task 4: `Ntilde.Mux` project + in-memory transport

**Files:**
- Create: `src/Ntilde.Mux/Ntilde.Mux.csproj`, `src/Ntilde.Mux/Transport/{IMuxListener.cs, InMemoryDuplexPipe.cs, InMemoryMuxListener.cs}`
- Create: `tests/Ntilde.Mux.Tests/Transport/InMemoryTransportTests.cs`
- Modify: `tests/Ntilde.Mux.Tests/Ntilde.Mux.Tests.csproj`, `Ntilde.sln`, architecture tests (+ csproj)

**Interfaces:**
- Produces (namespace `Ntilde.Mux.Transport`): `IMuxListener : IDisposable { Stream? Accept(CancellationToken) }` (blocking; null once closed); `InMemoryDuplexPipe.Create(int capacityBytes) → (Stream A, Stream B)`; `InMemoryMuxListener(int pipeCapacityBytes = 1 << 20) { Stream Connect(); }`.

- [ ] **Step 1: Create the project**

`src/Ntilde.Mux/Ntilde.Mux.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Multiplexer core (docs/superpowers/specs/2026-09-23-ntilde-mux-phase1.md). References exactly
    Pty, VT, Replay and Mux.Contracts - enforced by Ntilde.Architecture.Tests
    (Mux_only_references_Pty_Vt_Replay_and_MuxContracts, Mux_must_not_depend_on_ui_platform_or_app).
    The headless session lives here rather than in Pty because it needs VT, and Pty must not.
  -->

  <PropertyGroup>
    <IsAotCompatible>true</IsAotCompatible>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Ntilde.Mux.Tests" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Ntilde.Pty\Ntilde.Pty.csproj" />
    <ProjectReference Include="..\Ntilde.VT\Ntilde.VT.csproj" />
    <ProjectReference Include="..\Ntilde.Replay\Ntilde.Replay.csproj" />
    <ProjectReference Include="..\Ntilde.Mux.Contracts\Ntilde.Mux.Contracts.csproj" />
  </ItemGroup>

</Project>
```

`scripts/build.ps1 sln Ntilde.sln add src/Ntilde.Mux/Ntilde.Mux.csproj` (check it nests under `src`). In `Ntilde.Mux.Tests.csproj` add project references to `Ntilde.Mux`, `Ntilde.VT`, `Ntilde.Pty`, `Ntilde.Replay`.

- [ ] **Step 2: Write the failing transport tests** — `tests/Ntilde.Mux.Tests/Transport/InMemoryTransportTests.cs`:

```csharp
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Transport;

public sealed class InMemoryTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Bytes_arrive_in_order_across_ring_wrap_around()
    {
        (Stream a, Stream b) = InMemoryDuplexPipe.Create(capacityBytes: 8);
        byte[] sent = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();

        Task writer = Task.Run(() => a.Write(sent), Ct);
        byte[] received = new byte[sent.Length];
        await Task.Run(() => b.ReadExactly(received), Ct).WaitAsync(Timeout, Ct);
        await writer.WaitAsync(Timeout, Ct);

        Assert.Equal(sent, received);
    }

    [Fact]
    public async Task Read_returns_zero_after_the_peer_closes_and_the_pipe_drains()
    {
        (Stream a, Stream b) = InMemoryDuplexPipe.Create(64);
        a.Write("xy"u8);
        a.Dispose();

        byte[] buffer = new byte[16];
        Assert.Equal(2, b.Read(buffer));
        Assert.Equal(0, await Task.Run(() => b.Read(buffer), Ct).WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task Disposing_an_end_unblocks_its_own_blocked_read()
    {
        (Stream a, _) = InMemoryDuplexPipe.Create(64);
        Task<int> read = Task.Run(() => a.Read(new byte[4]), Ct);
        await Task.Delay(50, Ct);

        a.Dispose();

        Assert.Equal(0, await read.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task Disposing_an_end_unblocks_its_own_blocked_write()
    {
        (Stream a, _) = InMemoryDuplexPipe.Create(4);
        Task write = Task.Run(() => a.Write(new byte[64]), Ct);
        await Task.Delay(50, Ct);

        a.Dispose();

        await Assert.ThrowsAsync<IOException>(() => write.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public void Writing_to_a_peer_that_closed_throws()
    {
        (Stream a, Stream b) = InMemoryDuplexPipe.Create(64);
        b.Dispose();
        Assert.Throws<IOException>(() => a.Write("x"u8));
    }

    [Fact]
    public async Task Listener_pairs_Connect_with_Accept_and_bytes_flow_both_ways()
    {
        using var listener = new InMemoryMuxListener();
        using Stream client = listener.Connect();
        using Stream server = (await Task.Run(() => listener.Accept(Ct), Ct).WaitAsync(Timeout, Ct))!;

        client.Write("ping"u8);
        byte[] got = new byte[4];
        server.ReadExactly(got);
        server.Write("pong"u8);
        byte[] back = new byte[4];
        client.ReadExactly(back);

        Assert.Equal("ping"u8.ToArray(), got);
        Assert.Equal("pong"u8.ToArray(), back);
    }

    [Fact]
    public async Task Accept_returns_null_once_the_listener_is_disposed()
    {
        var listener = new InMemoryMuxListener();
        Task<Stream?> accept = Task.Run(() => listener.Accept(Ct), Ct);
        await Task.Delay(50, Ct);

        listener.Dispose();

        Assert.Null(await accept.WaitAsync(Timeout, Ct));
    }
}
```

- [ ] **Step 3: Run to verify it fails** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~InMemoryTransportTests"`. Expected: build FAILS.

- [ ] **Step 4: Implement the transport**

`Transport/IMuxListener.cs`:

```csharp
namespace Ntilde.Mux.Transport;

/// <summary>
/// Where a <c>MuxServer</c> gets connections. Phase 1 has only <see cref="InMemoryMuxListener"/>;
/// Phase 2 plugs a named-pipe / Unix-socket listener in here, which is why the server only ever
/// sees a <see cref="Stream"/>.
/// </summary>
public interface IMuxListener : IDisposable
{
    /// <summary>Blocks until a client connects. Returns null once the listener is closed or cancelled.</summary>
    Stream? Accept(CancellationToken cancellationToken);
}
```

`Transport/InMemoryDuplexPipe.cs`:

```csharp
namespace Ntilde.Mux.Transport;

/// <summary>
/// A connected pair of blocking, bounded, in-memory streams. Synchronous by design: the mux reads
/// and writes on dedicated threads, and a bounded pipe is what lets a test stand in for a client
/// that stops reading. Disposing an end closes both of its directions and unblocks any thread
/// parked in that end's Read or Write.
/// </summary>
public static class InMemoryDuplexPipe
{
    public static (Stream A, Stream B) Create(int capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        var aToB = new ByteRingPipe(capacityBytes);
        var bToA = new ByteRingPipe(capacityBytes);
        return (new DuplexEnd(read: bToA, write: aToB), new DuplexEnd(read: aToB, write: bToA));
    }

    private sealed class ByteRingPipe
    {
        private readonly object _gate = new();
        private readonly byte[] _ring;
        private int _head;
        private int _count;
        private bool _writerClosed;
        private bool _readerClosed;

        public ByteRingPipe(int capacity) => _ring = new byte[capacity];

        public int Read(Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;
            lock (_gate)
            {
                while (_count == 0)
                {
                    if (_writerClosed || _readerClosed) return 0;
                    Monitor.Wait(_gate);
                }

                if (_readerClosed) return 0;
                int n = Math.Min(destination.Length, _count);
                int first = Math.Min(n, _ring.Length - _head);
                _ring.AsSpan(_head, first).CopyTo(destination);
                if (n > first) _ring.AsSpan(0, n - first).CopyTo(destination[first..]);
                _head = (_head + n) % _ring.Length;
                _count -= n;
                Monitor.PulseAll(_gate);
                return n;
            }
        }

        public void Write(ReadOnlySpan<byte> source)
        {
            while (!source.IsEmpty)
            {
                lock (_gate)
                {
                    while (_count == _ring.Length && !_readerClosed && !_writerClosed)
                    {
                        Monitor.Wait(_gate);
                    }

                    if (_readerClosed || _writerClosed) throw new IOException("The in-memory pipe is closed.");
                    int tail = (_head + _count) % _ring.Length;
                    int n = Math.Min(source.Length, _ring.Length - _count);
                    int first = Math.Min(n, _ring.Length - tail);
                    source[..first].CopyTo(_ring.AsSpan(tail));
                    if (n > first) source[first..n].CopyTo(_ring);
                    _count += n;
                    source = source[n..];
                    Monitor.PulseAll(_gate);
                }
            }
        }

        public void CloseWriter() { lock (_gate) { _writerClosed = true; Monitor.PulseAll(_gate); } }

        public void CloseReader() { lock (_gate) { _readerClosed = true; Monitor.PulseAll(_gate); } }
    }

    private sealed class DuplexEnd : Stream
    {
        private readonly ByteRingPipe _read;
        private readonly ByteRingPipe _write;
        private int _disposed;

        public DuplexEnd(ByteRingPipe read, ByteRingPipe write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer) => _read.Read(buffer);

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            Write(buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new IOException("The in-memory pipe end is disposed.");
            _write.Write(buffer);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _write.CloseWriter();
                _read.CloseReader();
            }

            base.Dispose(disposing);
        }
    }
}
```

`Transport/InMemoryMuxListener.cs`:

```csharp
using System.Collections.Concurrent;

namespace Ntilde.Mux.Transport;

/// <summary>In-process listener: <see cref="Connect"/> hands the client end back and queues the server end for <see cref="Accept"/>.</summary>
public sealed class InMemoryMuxListener : IMuxListener
{
    private readonly BlockingCollection<Stream> _pending = new();
    private readonly int _pipeCapacityBytes;

    public InMemoryMuxListener(int pipeCapacityBytes = 1 << 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pipeCapacityBytes);
        _pipeCapacityBytes = pipeCapacityBytes;
    }

    public Stream Connect()
    {
        (Stream client, Stream server) = InMemoryDuplexPipe.Create(_pipeCapacityBytes);
        _pending.Add(server);
        return client;
    }

    public Stream? Accept(CancellationToken cancellationToken)
    {
        try
        {
            return _pending.Take(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null; // CompleteAdding: listener closed
        }
    }

    public void Dispose()
    {
        _pending.CompleteAdding();
        while (_pending.TryTake(out Stream? orphan)) orphan.Dispose();
    }
}
```

(`BlockingCollection` itself is intentionally not disposed: a concurrent `Accept` may still be inside `Take`. If CA2213 fires, dispose it after `CompleteAdding` and catch `ObjectDisposedException` in `Accept` as a closed listener.)

- [ ] **Step 5: Run** — same filter. Expected: 7 PASS.

- [ ] **Step 6: Architecture tests for `Ntilde.Mux`**

Architecture.Tests csproj: add `<ProjectReference Include="..\..\src\Ntilde.Mux\Ntilde.Mux.csproj" />`.

`LayeringTests.cs`:

```csharp
    private static Assembly Mux => typeof(global::Ntilde.Mux.Transport.InMemoryMuxListener).Assembly;

    // Hoisted for CA1861.
    private static readonly string[] MuxApprovedNtildeReferences =
        ["Ntilde.Pty", "Ntilde.VT", "Ntilde.Replay", "Ntilde.Mux.Contracts"];

    /// <summary>
    /// The mux is a headless library a daemon will host: no UI toolkit, no renderer, no App, and no
    /// Platform (SSH/process plumbing stays behind the ITerminalSessionFactory the host injects).
    /// </summary>
    [Fact]
    public void Mux_must_not_depend_on_ui_platform_or_app()
    {
        var result = Types.InAssembly(Mux)
            .Should()
            .NotHaveDependencyOnAny(
                "Avalonia", "SkiaSharp", "Ntilde.Platform", "Ntilde.Rendering", "Ntilde.Shell",
                "Ntilde.Controls", "Ntilde.CommandAssist", "Ntilde.AgentHost")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Mux must stay headless. Offenders: {Join(result.FailingTypeNames)}");
    }

    /// <summary>The emitted-reference edge, which catches a dependency no type names yet.</summary>
    [Fact]
    public void Mux_references_only_approved_ntilde_assemblies()
    {
        string[] offenders = Mux.GetReferencedAssemblies()
            .Select(r => r.Name ?? string.Empty)
            .Where(n => n.StartsWith("Ntilde", StringComparison.Ordinal))
            .Where(n => !MuxApprovedNtildeReferences.Contains(n))
            .ToArray();

        Assert.True(offenders.Length == 0, $"Mux references unapproved assemblies: {Join(offenders)}");
    }
```

Add `Mux` to the `No_production_assembly_references_test_assemblies` array.

`ProjectFileLayeringTests.cs`:

```csharp
    // Same CA1861 reasoning as VtOnly. Order matches the csproj.
    private static readonly string[] MuxDependencies =
        ["Ntilde.Pty", "Ntilde.VT", "Ntilde.Replay", "Ntilde.Mux.Contracts"];

    [Fact]
    public void Mux_only_references_Pty_Vt_Replay_and_MuxContracts()
    {
        var refs = ProjectReferences("src/Ntilde.Mux/Ntilde.Mux.csproj");
        Assert.Equal(MuxDependencies, refs);
    }
```

`NamespaceAlignmentTests.cs`:
1. Add `"Ntilde.Mux"` to `LeafAssemblies` and `[InlineData("Ntilde.Mux")]` to the theory.
2. In `No_two_assemblies_share_a_namespace_prefix`, directly after `if (label == owner) continue;` add:

```csharp
                // A child assembly owns a sub-namespace of its parent's prefix by construction:
                // Ntilde.Mux.Contracts lives under "Ntilde.Mux". The reverse direction - the parent
                // reaching into the child's namespace - is asserted by
                // Mux_does_not_use_the_MuxContracts_namespace below.
                if (asmName.StartsWith(owner + ".", StringComparison.Ordinal)) continue;
```

3. Add:

```csharp
    [Fact]
    public void Mux_does_not_use_the_MuxContracts_namespace()
    {
        var result = Types.InAssembly(LoadByName("Ntilde.Mux"))
            .That().ArePublic()
            .Should()
            .NotResideInNamespaceStartingWith("Ntilde.Mux.Contracts")
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Wire types belong in Ntilde.Mux.Contracts. Offenders: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
```

Run: `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` → 93 passed (88 + four facts + one theory row).

- [ ] **Step 7: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux tests/Ntilde.Mux.Tests tests/Ntilde.Architecture.Tests Ntilde.sln
git commit -m "feat(mux): Ntilde.Mux project with an in-memory duplex transport"
```

---

### Task 5: `HeadlessTerminalSession`

**Files:**
- Create: `src/Ntilde.Mux/IMuxFrameSink.cs`, `src/Ntilde.Mux/HeadlessSessionOptions.cs`, `src/Ntilde.Mux/HeadlessTerminalSession.cs`
- Create: `tests/Ntilde.Mux.Tests/Support/{ScriptedTerminalSession.cs, ScriptedSessionFactory.cs, NoTapTerminalSession.cs, RecordingFrameSink.cs, TestWait.cs, StreamCuts.cs}`, `tests/Ntilde.Mux.Tests/Headless/HeadlessTerminalSessionTests.cs`
- Modify: `tests/Ntilde.Mux.Tests/Ntilde.Mux.Tests.csproj` (link shared files)

**Interfaces:**
- Consumes: `ITerminalByteOutput` (Task 1); `MuxFrames`, `MuxOutboundFrame`, DTOs (Task 3); `TerminalStateAssert` (Task 2).
- Produces (namespace `Ntilde.Mux`):
  - `internal interface IMuxFrameSink { bool TryEnqueue(MuxOutboundFrame frame); }`
  - `public sealed class HeadlessSessionOptions { Title, Command, Arguments, Cols=80, Rows=24, ForceConPtyFiltering=OperatingSystem.IsWindows(), Log }`
  - `public sealed class HeadlessTerminalSession : IDisposable` — ctor `(Guid id, ITerminalSession session, HeadlessSessionOptions options)`; `Id, Title, Command, Arguments, ForceConPtyFiltering, Cols, Rows, AttachedClients, IsExited, ExitCode (int?), IsFaulted, StreamPosition (long)`; `SendInput(string)`, `PostResize(int cols, int rows, MuxPresentation? presentation)`, `Kill()`, `Dispose()`; internal `Inner`, `Buffer`, `Parser`, `PostAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes)`, `PostDetach(IMuxFrameSink)`, `Task<T> InvokeAsync<T>(Func<T>)`, `Task FlushAsync()`, `TerminalStateSnapshot CaptureSnapshot(int maxScrollbackRows)` (parse thread only), `ExportFlightResult ExportFlight()`, `int QueuedDataCount`.
  - Test support used by Tasks 6-8: `ScriptedTerminalSession`, `ScriptedSessionFactory`, `NoTapTerminalSession`, `RecordingFrameSink`/`RecordedFrame`, `TestWait.UntilAsync`, `StreamCuts.Interesting(byte[])`.

- [ ] **Step 1: Link the shared Phase 0 test files** into `Ntilde.Mux.Tests.csproj`:

```xml
  <ItemGroup>
    <!-- One definition of "same terminal state" and one corpus, shared with the Phase 0 parity
         suite: a fix to either is a fix for both. -->
    <Compile Include="..\Ntilde.VT.Tests\StateTransfer\TerminalStateAssert.cs" Link="Shared\TerminalStateAssert.cs" />
    <Compile Include="..\Ntilde.VT.Tests\StateTransfer\ParityCorpus.cs" Link="Shared\ParityCorpus.cs" />
    <Content Include="..\Ntilde.App.Tests\Fixtures\Replay\*.rec">
      <Link>Fixtures/Replay/%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 2: Write the test support**

`Support/ScriptedTerminalSession.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;
using Ntilde.Pty;
using Ntilde.Replay;

namespace Ntilde.Mux.Tests.Support;

/// <summary>
/// A deterministic ITerminalSession + ITerminalByteOutput: emits exactly the chunks a test gives
/// it, synchronously on the caller's thread, and records SendInput / Resize. Flight recording is a
/// real FlightRecordingBuffer so exports replay through the real ReplayRunner.
/// </summary>
internal sealed class ScriptedTerminalSession : ITerminalSession, ITerminalByteOutput
{
    private readonly object _gate = new();
    private Action<ReadOnlyMemory<byte>>? _raw;
    private FlightRecordingBuffer? _flight;
    private int _exited;

    public ScriptedTerminalSession(TerminalSessionRequest request)
    {
        Request = request;
        Cols = request.Cols;
        Rows = request.Rows;
    }

    public TerminalSessionRequest Request { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand => Request.Command;
    public string? ShellArguments => Request.Arguments;
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public ConcurrentQueue<string> SentInput { get; } = new();
    public ConcurrentQueue<(int Cols, int Rows)> Resizes { get; } = new();
    public bool ThrowOnSendInput { get; set; }
    public Action<int, int>? OnResizeCalled { get; set; }
    public bool Disposed { get; private set; }

    public event Action<string>? OnOutputReceived { add { } remove { } }
    public event Action<int>? OnExit;

    public event Action<ReadOnlyMemory<byte>>? OnRawOutputReceived
    {
        add { lock (_gate) _raw += value; }
        remove { lock (_gate) _raw -= value; }
    }

    public void Emit(byte[] chunk)
    {
        Action<ReadOnlyMemory<byte>>? handler;
        lock (_gate)
        {
            handler = _raw;
            _flight?.RecordChunk(chunk, chunk.Length);
        }

        handler?.Invoke(chunk.ToArray());
    }

    public void Emit(string text) => Emit(Encoding.UTF8.GetBytes(text));

    public void EmitInChunks(ReadOnlySpan<byte> bytes, int chunkSize)
    {
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            Emit(bytes.Slice(i, Math.Min(chunkSize, bytes.Length - i)).ToArray());
        }
    }

    public void Exit(int code)
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0) return;
        ExitCode = code;
        OnExit?.Invoke(code);
    }

    public bool IsProcessRunning => Volatile.Read(ref _exited) == 0;
    public bool HasActiveChildProcesses { get; set; }
    public int? ExitCode { get; private set; }

    public void SendInput(string input)
    {
        if (ThrowOnSendInput) throw new InvalidOperationException("scripted SendInput failure");
        SentInput.Enqueue(input);
    }

    public void Resize(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        Resizes.Enqueue((cols, rows));
        OnResizeCalled?.Invoke(cols, rows);
        lock (_gate) _flight?.RecordResize(cols, rows);
    }

    public bool IsRecording { get; private set; }
    public string? RecordingPath { get; private set; }
    public void StartRecording(string filePath) { IsRecording = true; RecordingPath = filePath; }
    public void StopRecording() => IsRecording = false;

    public bool IsFlightRecording { get { lock (_gate) return _flight is not null; } }
    public void EnableFlightRecording(long maxTotalBytes) { lock (_gate) _flight ??= new FlightRecordingBuffer(maxTotalBytes, Cols, Rows); }
    public void DisableFlightRecording() { lock (_gate) _flight = null; }

    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
    {
        FlightRecordingBuffer? flight;
        lock (_gate) flight = _flight;
        if (flight is null) { info = default; return false; }
        info = flight.ExportTo(filePath, ShellCommand);
        return true;
    }

    public void Dispose()
    {
        Disposed = true;
        Exit(-1);
    }
}
```

`Support/ScriptedSessionFactory.cs`:

```csharp
using System.Collections.Concurrent;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Support;

internal sealed class ScriptedSessionFactory : ITerminalSessionFactory
{
    public ConcurrentQueue<TerminalSessionRequest> Requests { get; } = new();
    public bool FailNext { get; set; }
    public bool ProduceSessionsWithoutByteTap { get; set; }
    public NoTapTerminalSession? LastNoTapSession { get; private set; }

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        Requests.Enqueue(request);
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("scripted spawn failure");
        }

        if (ProduceSessionsWithoutByteTap)
        {
            return LastNoTapSession = new NoTapTerminalSession();
        }

        return new ScriptedTerminalSession(request);
    }
}
```

`Support/NoTapTerminalSession.cs`:

```csharp
using Ntilde.Pty;
using Ntilde.Replay;

namespace Ntilde.Mux.Tests.Support;

/// <summary>A session with no raw-byte tap: the mux must refuse to host it.</summary>
internal sealed class NoTapTerminalSession : ITerminalSession
{
    public bool Disposed { get; private set; }
    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand => "no-tap";
    public string? ShellArguments => null;
    public bool IsProcessRunning => !Disposed;
    public bool HasActiveChildProcesses => false;
    public int? ExitCode => null;
    public bool IsRecording => false;
    public bool IsFlightRecording => false;
    public event Action<string>? OnOutputReceived { add { } remove { } }
    public event Action<int>? OnExit { add { } remove { } }
    public void SendInput(string input) { }
    public void Resize(int cols, int rows) { }
    public void StartRecording(string filePath) { }
    public void StopRecording() { }
    public void EnableFlightRecording(long maxTotalBytes) { }
    public void DisableFlightRecording() { }
    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info) { info = default; return false; }
    public void Dispose() => Disposed = true;
}
```

`Support/RecordingFrameSink.cs`:

```csharp
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Support;

/// <summary>An IMuxFrameSink that copies every frame it accepts (so it takes no reference).</summary>
internal sealed class RecordingFrameSink : IMuxFrameSink
{
    private readonly object _gate = new();
    private readonly List<RecordedFrame> _frames = new();

    public bool Accept { get; set; } = true;

    public IReadOnlyList<RecordedFrame> Frames { get { lock (_gate) return _frames.ToArray(); } }

    public IReadOnlyList<string> Described => Frames.Select(f => f.Describe()).ToArray();

    public bool TryEnqueue(MuxOutboundFrame frame)
    {
        if (!Accept) return false;
        byte[] payload = frame.Bytes[MuxProtocol.FrameHeaderBytes..].ToArray();
        lock (_gate) _frames.Add(new RecordedFrame(frame.Kind, payload));
        return true;
    }
}

internal sealed record RecordedFrame(MuxFrameKind Kind, byte[] Payload)
{
    /// <summary>"Output@0:abc", "Resize@3:40x10", "Snapshot@5+1", "Exited:7", "Error:snapshot_too_large".</summary>
    public string Describe()
    {
        switch (Kind)
        {
            case MuxFrameKind.Output:
                MuxFrames.TryParseOutput(Payload, out _, out long seq, out ReadOnlySpan<byte> data);
                return string.Create(CultureInfo.InvariantCulture, $"Output@{seq}:{Encoding.UTF8.GetString(data)}");
            case MuxFrameKind.ResizeEvent:
                MuxFrames.TryParseResizeEvent(Payload, out _, out long rseq, out int cols, out int rows);
                return string.Create(CultureInfo.InvariantCulture, $"Resize@{rseq}:{cols}x{rows}");
            case MuxFrameKind.Snapshot:
                MuxFrames.TryParseSnapshot(Payload, out _, out _, out long sseq, out _);
                return string.Create(CultureInfo.InvariantCulture, $"Snapshot@{sseq}");
            case MuxFrameKind.Notification:
                MuxNotification n = MuxFrames.ParseJson(Payload, MuxJsonContext.Default.MuxNotification);
                ExitedNotification e = MuxFrames.ParseParams(n.Params, MuxJsonContext.Default.ExitedNotification);
                return string.Create(CultureInfo.InvariantCulture, $"Exited:{e.ExitCode}");
            case MuxFrameKind.Response:
                MuxResponse r = MuxFrames.ParseJson(Payload, MuxJsonContext.Default.MuxResponse);
                return r.Error is { } err ? $"Error:{err.Code}" : "Response";
            default:
                return Kind.ToString();
        }
    }
}
```

`Support/TestWait.cs`:

```csharp
namespace Ntilde.Mux.Tests.Support;

internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting until {because}.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
```

`Support/StreamCuts.cs`:

```csharp
namespace Ntilde.Mux.Tests.Support;

internal static class StreamCuts
{
    /// <summary>
    /// Cut points that stress an attach: one byte into the first escape sequence, at the first
    /// UTF-8 continuation byte (mid code point, so the decoder holds a tail), and the midpoint.
    /// </summary>
    public static IReadOnlyList<int> Interesting(byte[] bytes)
    {
        var cuts = new SortedSet<int>();
        int esc = Array.IndexOf(bytes, (byte)0x1B);
        if (esc >= 0 && esc + 1 < bytes.Length) cuts.Add(esc + 1);
        for (int i = 1; i < bytes.Length; i++)
        {
            if ((bytes[i] & 0xC0) == 0x80) { cuts.Add(i); break; }
        }

        if (bytes.Length > 1) cuts.Add(bytes.Length / 2);
        return cuts.ToArray();
    }
}
```

- [ ] **Step 3: Write the failing session tests** — `tests/Ntilde.Mux.Tests/Headless/HeadlessTerminalSessionTests.cs`:

```csharp
using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.VT;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Mux.Tests.Headless;

public sealed class HeadlessTerminalSessionTests
{
    private static readonly MuxPresentation Presentation80x24 = new() { Cols = 80, Rows = 24, CellWidthPx = 10, CellHeightPx = 20 };

    private static (HeadlessTerminalSession Mux, ScriptedTerminalSession Fake) NewSession(int cols = 80, int rows = 24)
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest(
            "scripted", string.Empty, string.Empty, cols, rows, null, false, null));
        var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted", Title = "t", Cols = cols, Rows = rows, ForceConPtyFiltering = false,
        });
        return (mux, fake);
    }

    private static string ScreenText(TerminalBuffer buffer) =>
        string.Join('\n', BufferSnapshot.Capture(buffer).Lines).TrimEnd();

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach ((string name, _) in ParityCorpus.All()) data.Add(name);
        return data;
    }

    [Fact]
    public async Task Parses_the_tapped_bytes_on_its_own_dedicated_thread()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Emit("hi");
            await mux.FlushAsync();

            Assert.StartsWith("hi", ScreenText(mux.Buffer), StringComparison.Ordinal);
            (string? name, bool pool) = await mux.InvokeAsync(() => (Thread.CurrentThread.Name, Thread.CurrentThread.IsThreadPoolThread));
            Assert.StartsWith("MuxParse-", name, StringComparison.Ordinal);
            Assert.False(pool);
            Assert.Equal(2, mux.StreamPosition);
        }
    }

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public async Task A_captured_snapshot_plus_the_tail_equals_the_mux(string name)
    {
        byte[] corpus = ParityCorpus.All().Single(c => c.Name == name).Bytes;
        foreach (int cut in StreamCuts.Interesting(corpus))
        {
            (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
            using (mux)
            {
                fake.EmitInChunks(corpus.AsSpan(0, cut), chunkSize: 5);
                TerminalStateSnapshot snapshot = await mux.InvokeAsync(() => mux.CaptureSnapshot(int.MaxValue));
                Assert.Equal(cut, snapshot.StreamSeq + (snapshot.DecoderTail?.Length ?? 0));

                // An independent client: restore, then decode tail + rest with its own decoder.
                var buffer = new TerminalBuffer(80, 24);
                var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
                TerminalStateTransfer.Restore(buffer, parser, snapshot);
                var decoder = new Utf8ChunkDecoder();
                char[] chars = new char[Utf8ChunkDecoder.GetMaxCharCount(corpus.Length + 4)];
                decoder.Decode(snapshot.DecoderTail ?? [], chars);
                int n = decoder.Decode(corpus.AsSpan(cut), chars);
                if (n > 0) parser.Process(new string(chars, 0, n));

                fake.EmitInChunks(corpus.AsSpan(cut), chunkSize: 7);
                await mux.FlushAsync();

                TerminalStateAssert.AssertEquivalent($"[{name} @ {cut}]", mux.Buffer, mux.Parser, buffer, parser);
            }
        }
    }

    [Fact]
    public async Task Device_queries_are_answered_by_writing_to_the_session()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Emit("\x1b[c");
            await mux.FlushAsync();

            string reply = Assert.Single(fake.SentInput);
            Assert.StartsWith("\x1b[?", reply, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_resize_is_recorded_at_the_current_offset_and_reaches_the_child_after_the_buffer()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            int colsWhenChildResized = -1;
            fake.OnResizeCalled = (_, _) => colsWhenChildResized = mux.Buffer.Cols; // runs on the parse thread
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, requestId: 1, maxScrollbackRows: 100, Presentation80x24, MuxProtocol.MaxFrameBytes);

            fake.Emit("abc");
            await mux.FlushAsync();          // control items outrank data: without this the resize could run first
            mux.PostResize(40, 10, presentation: null);
            await mux.InvokeAsync(() => 0);  // the resize has run
            fake.Emit("d");
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0", "Output@0:abc", "Resize@3:40x10", "Output@3:d"], sink.Described);
            Assert.Equal([(40, 10)], fake.Resizes.ToArray());
            Assert.Equal(40, colsWhenChildResized);
            Assert.Equal((40, 10), (mux.Cols, mux.Rows));
        }
    }

    [Fact]
    public async Task A_resize_to_the_current_size_emits_no_event()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            mux.PostResize(80, 24, presentation: null);
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0"], sink.Described);
            Assert.Empty(fake.Resizes);
        }
    }

    [Fact]
    public async Task Resize_sends_the_in_band_report_when_mode_2048_is_on()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Emit("\x1b[?2048h");
            await mux.FlushAsync(); // mode on before the resize is dequeued
            mux.PostResize(100, 30, Presentation80x24 with { Cols = 100, Rows = 30, CellWidthPx = 8, CellHeightPx = 16 });
            await mux.FlushAsync();

            Assert.Contains("\x1b[48;30;100;480;800t", fake.SentInput);
        }
    }

    [Fact]
    public async Task Presentation_reaches_the_parser_so_CSI_14_t_describes_the_client()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            mux.PostResize(90, 20, new MuxPresentation { Cols = 90, Rows = 20, CellWidthPx = 9, CellHeightPx = 18 });
            fake.Emit("\x1b[14t");
            await mux.FlushAsync();

            Assert.Contains("\x1b[4;360;810t", fake.SentInput);
        }
    }

    [Fact]
    public async Task Exit_is_delivered_after_every_byte_already_tapped()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            fake.Emit("tail");
            fake.Exit(7);
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0", "Output@0:tail", "Exited:7"], sink.Described);
            Assert.True(mux.IsExited);
            Assert.Equal(7, mux.ExitCode);
        }
    }

    [Fact]
    public async Task Attaching_to_an_exited_session_is_followed_by_its_exit()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.Exit(4);
            await mux.FlushAsync();
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            await mux.FlushAsync();

            Assert.Equal(["Snapshot@0", "Exited:4"], sink.Described);
        }
    }

    [Fact]
    public async Task An_oversize_snapshot_is_refused_and_the_sink_is_not_subscribed()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 5, 100, Presentation80x24, maxSnapshotBytes: 16);
            fake.Emit("x");
            await mux.FlushAsync();

            Assert.Equal(["Error:snapshot_too_large"], sink.Described);
            Assert.Equal(0, mux.AttachedClients);
        }
    }

    [Fact]
    public async Task A_sink_that_refuses_a_frame_is_dropped()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            await mux.FlushAsync();
            Assert.Equal(1, mux.AttachedClients);

            sink.Accept = false;
            fake.Emit("x");
            await mux.FlushAsync();

            Assert.Equal(0, mux.AttachedClients);
        }
    }

    [Fact]
    public async Task A_throwing_parser_marks_the_session_faulted_instead_of_killing_the_host()
    {
        // OnResponse -> session.SendInput runs inside Process, so a throwing SendInput is a parser
        // failure as far as the parse thread can tell. (If AnsiParser ever starts containing
        // callback exceptions itself, this test fails and needs a different trigger.)
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.ThrowOnSendInput = true;
            fake.Emit("\x1b[c");
            await mux.FlushAsync();

            Assert.True(mux.IsFaulted);
            Assert.Equal(1, await mux.InvokeAsync(() => 1)); // the parse thread survived
            var sink = new RecordingFrameSink();
            mux.PostAttach(sink, 9, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
            await mux.FlushAsync();
            Assert.Equal(["Error:internal_error"], sink.Described);
        }
    }

    [Fact]
    public async Task Kill_disposes_the_child_and_announces_the_exit_once()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        var sink = new RecordingFrameSink();
        mux.PostAttach(sink, 1, 100, Presentation80x24, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();

        mux.Kill();
        await TestWait.UntilAsync(() => sink.Described.Contains("Exited:-1"), "the kill is announced");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(fake.Disposed);
        Assert.Single(sink.Described, d => d.StartsWith("Exited", StringComparison.Ordinal));
        Assert.True(mux.IsExited);
        mux.Dispose();
    }

    [Fact]
    public async Task Dispose_with_a_producer_blocked_on_a_full_queue_does_not_deadlock()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using var release = new ManualResetEventSlim();
        Task<int> parked = mux.InvokeAsync(() => { release.Wait(TimeSpan.FromSeconds(30)); return 0; });
        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < HeadlessTerminalSession.DataQueueCapacity + 50; i++) fake.Emit("x");
        }, TestContext.Current.CancellationToken);
        await TestWait.UntilAsync(() => mux.QueuedDataCount == HeadlessTerminalSession.DataQueueCapacity, "the data queue is full");

        Task dispose = Task.Run(mux.Dispose, TestContext.Current.CancellationToken);
        await producer.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await parked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_session_without_the_byte_tap_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new HeadlessTerminalSession(Guid.NewGuid(), new NoTapTerminalSession(), new HeadlessSessionOptions()));
    }

    [Fact]
    public async Task Flight_export_returns_replayable_bytes()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            fake.EnableFlightRecording(1 << 20);
            fake.Emit("hello\r\n");
            fake.Emit("world");
            ExportFlightResult result = mux.ExportFlight();

            Assert.True(result.Exported);
            string path = Path.Combine(Path.GetTempPath(), $"mux-flight-test-{Guid.NewGuid():N}.rec");
            try
            {
                await File.WriteAllBytesAsync(path, result.Bytes!, TestContext.Current.CancellationToken);
                var replayed = new List<byte>();
                await new ReplayRunner(path).RunAsync(data => { replayed.AddRange(data); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
                Assert.Equal("hello\r\nworld", Encoding.UTF8.GetString(replayed.ToArray()));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
```

- [ ] **Step 4: Run to verify it fails** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~HeadlessTerminalSessionTests"`. Expected: build FAILS.

- [ ] **Step 5: Implement the sink and options**

`IMuxFrameSink.cs`:

```csharp
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux;

/// <summary>Where a session offers frames: a server connection in production, a recorder in tests.</summary>
internal interface IMuxFrameSink
{
    /// <summary>
    /// Offers a frame. Called on a session's parse thread, so it must never block. A sink that keeps
    /// the frame takes its own reference (<see cref="MuxOutboundFrame.AddRef"/>); one that copies
    /// need not. Returning false means "drop me": the session unsubscribes the sink.
    /// </summary>
    bool TryEnqueue(MuxOutboundFrame frame);
}
```

`HeadlessSessionOptions.cs`:

```csharp
namespace Ntilde.Mux;

public sealed class HeadlessSessionOptions
{
    public string Title { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;
    public string? Arguments { get; init; }
    public int Cols { get; init; } = 80;
    public int Rows { get; init; } = 24;
    /// <summary>Explicit, reported to clients in Welcome, so both halves of a snapshot parse identically.</summary>
    public bool ForceConPtyFiltering { get; init; } = OperatingSystem.IsWindows();
    public Action<string>? Log { get; init; }
}
```

- [ ] **Step 6: Implement `HeadlessTerminalSession`** — `HeadlessTerminalSession.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>
/// The authoritative side of one multiplexed terminal: a real session plus the headless parser and
/// buffer its raw bytes drive. Clients are downstream copies kept equal by snapshot + ordered events.
/// </summary>
/// <remarks>
/// <para>
/// <b>One parse thread.</b> Everything that touches <see cref="Buffer"/> or <see cref="Parser"/> runs
/// on <c>MuxParse-{id}</c>, which drains two queues with <c>TakeFromAny(control, data)</c>. Control
/// items (resize, presentation, attach, detach, invoke) outrank data and therefore always run
/// <em>between</em> <c>Process()</c> calls; data items (chunks, exit, flush barriers) keep arrival
/// order. Never the thread pool: see the #81 history in RustPtySession.
/// </para>
/// <para>
/// <b>Stream offset.</b> <see cref="StreamPosition"/> counts raw bytes fed to the decoder. An Output
/// frame carries the offset before its chunk; a ResizeEvent the offset it applies at; a snapshot
/// <c>StreamSeq = ConsumedBytes</c> with <c>StreamSeq + DecoderTail.Length == StreamPosition</c>.
/// </para>
/// <para>
/// <b>The mux answers device queries</b>: <c>OnResponse</c> goes to the session and nowhere else.
/// </para>
/// </remarks>
public sealed class HeadlessTerminalSession : IDisposable
{
    internal const int DataQueueCapacity = 256;

    private readonly ITerminalSession _session;
    private readonly ITerminalByteOutput _byteOutput;
    private readonly TerminalBuffer _buffer;
    private readonly AnsiParser _parser;
    private readonly Utf8ChunkDecoder _decoder = new();
    private readonly BlockingCollection<WorkItem> _control = new();
    private readonly BlockingCollection<WorkItem> _data = new(DataQueueCapacity);
    private readonly BlockingCollection<WorkItem>[] _queues;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _parseThread;
    private readonly Action<ReadOnlyMemory<byte>> _onRawOutput;
    private readonly Action<string> _onStringOutput = static _ => { };
    private readonly Action<int> _onExit;
    private readonly Action<string>? _log;
    private readonly List<IMuxFrameSink> _subscribers = new(); // parse thread only
    private char[] _chars = new char[Utf8ChunkDecoder.GetMaxCharCount(4096)];
    private long _rawOffset;
    private string _title;
    private int _cols;
    private int _rows;
    private int _attached;
    private int _exited;
    private int _exitCode;
    private int _faulted;
    private int _disposed;
    private int _unsubscribed;

    public HeadlessTerminalSession(Guid id, ITerminalSession session, HeadlessSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        _byteOutput = session as ITerminalByteOutput ?? throw new ArgumentException(
            $"{session.GetType().Name} does not implement {nameof(ITerminalByteOutput)}; the mux needs the raw bytes.",
            nameof(session));

        Id = id;
        _session = session;
        Command = options.Command;
        Arguments = options.Arguments;
        ForceConPtyFiltering = options.ForceConPtyFiltering;
        _title = options.Title;
        _log = options.Log;
        _cols = options.Cols;
        _rows = options.Rows;
        _buffer = new TerminalBuffer(options.Cols, options.Rows);
        _parser = new AnsiParser(_buffer, options.ForceConPtyFiltering)
        {
            ImageDecoder = null,
            AllowNativeKittyGraphics = false,
        };
        _parser.OnResponse = reply => _session.SendInput(reply);
        _parser.OnTitleChanged = title => Volatile.Write(ref _title, title);
        _queues = [_control, _data];
        _onRawOutput = OnRawOutput;
        _onExit = code => TryEnqueue(_data, WorkItem.ForExit(code));

        // The thread first: subscribing replays whatever arrived since spawn straight into the
        // bounded data queue, which would block this constructor if nobody were draining it.
        _parseThread = new Thread(ParseLoop) { IsBackground = true, Name = $"MuxParse-{id:N}" };
        _parseThread.Start();

        // Raw FIRST, then string. The string subscription is a no-op whose only job is to release
        // the session's own string replay buffer (nothing else ever subscribes to it here, so it
        // would grow for the session's lifetime) - and subscribing it first would make
        // RawOutputTap drop the retained startup bytes.
        _byteOutput.OnRawOutputReceived += _onRawOutput;
        _session.OnOutputReceived += _onStringOutput;
        _session.OnExit += _onExit;
    }

    public Guid Id { get; }
    public string Title => Volatile.Read(ref _title);
    public string Command { get; }
    public string? Arguments { get; }
    public bool ForceConPtyFiltering { get; }
    public int Cols => Volatile.Read(ref _cols);
    public int Rows => Volatile.Read(ref _rows);
    public int AttachedClients => Volatile.Read(ref _attached);
    public bool IsExited => Volatile.Read(ref _exited) != 0;
    public int? ExitCode => IsExited ? Volatile.Read(ref _exitCode) : null;
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;
    public long StreamPosition => Interlocked.Read(ref _rawOffset);

    internal ITerminalSession Inner => _session;

    /// <summary>Tests only, and only while the parse thread is idle (after <see cref="FlushAsync"/>).</summary>
    internal TerminalBuffer Buffer => _buffer;

    /// <inheritdoc cref="Buffer"/>
    internal AnsiParser Parser => _parser;

    internal int QueuedDataCount => _data.Count;

    /// <summary>Thread-safe; goes straight to the session (input is independent of the output stream).</summary>
    public void SendInput(string text)
    {
        if (IsExited || Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(text)) return;
        _session.SendInput(text);
    }

    /// <summary>Latest request wins. Presentation (if any) is applied first, so an in-band report uses the new cell size.</summary>
    public void PostResize(int cols, int rows, MuxPresentation? presentation) =>
        EnqueueControl(() =>
        {
            if (presentation is not null) ApplyPresentation(presentation);
            ApplyResize(cols, rows);
        });

    internal void PostAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes) =>
        EnqueueControl(() => ExecuteAttach(sink, requestId, maxScrollbackRows, presentation, maxSnapshotBytes));

    internal void PostDetach(IMuxFrameSink sink) =>
        EnqueueControl(() =>
        {
            if (_subscribers.Remove(sink)) PublishAttachedCount();
        });

    internal Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = WorkItem.ForAction(
            () =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            onDropped: () => tcs.TrySetException(new ObjectDisposedException(nameof(HeadlessTerminalSession))));
        if (!TryEnqueue(_control, item)) item.OnDropped!();
        return tcs.Task;
    }

    /// <summary>Completes once every data item queued before it (chunks, exit) has been processed.</summary>
    internal Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = WorkItem.ForAction(() => tcs.TrySetResult(), onDropped: () => tcs.TrySetResult());
        if (!TryEnqueue(_data, item)) tcs.TrySetResult();
        return tcs.Task;
    }

    /// <summary>
    /// Ends the session. Clients learn it through Exited, in stream order after every byte already
    /// queued; then the parse thread stops and unsubscribes.
    /// </summary>
    public void Kill()
    {
        try { _session.Dispose(); }
        catch (Exception ex) { Log($"[Mux] session {Id}: disposing the child failed: {ex.Message}"); }
        TryEnqueue(_data, WorkItem.ForExit(null, terminal: true));
    }

    internal TerminalStateSnapshot CaptureSnapshot(int maxScrollbackRows)
    {
        if (Thread.CurrentThread != _parseThread)
        {
            throw new InvalidOperationException("CaptureSnapshot must run on the session's parse thread; post it with InvokeAsync.");
        }

        TerminalStateSnapshot snapshot = _buffer.ExportState(maxScrollbackRows);
        snapshot.Parser = _parser.ExportState();
        snapshot.DecoderTail = _decoder.PendingTail.ToArray();
        snapshot.StreamSeq = _decoder.ConsumedBytes;
        Debug.Assert(snapshot.StreamSeq + snapshot.DecoderTail.Length == Interlocked.Read(ref _rawOffset),
            "decoder position and raw offset disagree: some byte bypassed the decoder");
        return snapshot;
    }

    internal ExportFlightResult ExportFlight()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntilde-mux-flight-{Guid.NewGuid():N}.rec");
        try
        {
            if (!_session.TryExportFlightRecording(path, out FlightExportInfo info))
            {
                return new ExportFlightResult { Exported = false };
            }

            return new ExportFlightResult
            {
                Exported = true,
                Bytes = File.ReadAllBytes(path),
                EventCount = info.EventCount,
                FirstEventMs = info.FirstEventMs,
                LastEventMs = info.LastEventMs,
                TruncatedAtStart = info.TruncatedAtStart,
            };
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Cancel FIRST: a producer parked in _data.Add holds RawOutputTap's lock while it waits, and
        // the unsubscribe below needs that lock. Cancelling throws it out of Add.
        _cts.Cancel();
        Unsubscribe();
        if (Thread.CurrentThread != _parseThread) _parseThread.Join(TimeSpan.FromSeconds(5));
        DrainAfterStop();
        try { _session.Dispose(); }
        catch (Exception ex) { Log($"[Mux] session {Id}: disposing the child failed: {ex.Message}"); }
    }

    private void OnRawOutput(ReadOnlyMemory<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        // The tap hands over an array the producer never touches again (ITerminalByteOutput), so it
        // is queued as-is: the one copy this path makes is the tap's own.
        byte[] data = MemoryMarshal.TryGetArray(chunk, out ArraySegment<byte> segment)
            && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? segment.Array
            : chunk.ToArray();
        TryEnqueue(_data, WorkItem.ForData(data));
    }

    private void EnqueueControl(Action action) => TryEnqueue(_control, WorkItem.ForAction(action));

    private bool TryEnqueue(BlockingCollection<WorkItem> queue, WorkItem item)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        try
        {
            queue.Add(item, _cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    private void ParseLoop()
    {
        try
        {
            while (true)
            {
                BlockingCollection<WorkItem>.TakeFromAny(_queues, out WorkItem item, _cts.Token);
                Execute(item);
                if (item.Kind == WorkKind.Exit && item.Terminal) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _faulted, 1);
            Log($"[Mux] session {Id}: parse loop terminated: {ex}");
        }
        finally
        {
            DrainAfterStop();
        }
    }

    private void Execute(WorkItem item)
    {
        switch (item.Kind)
        {
            case WorkKind.Data:
                ProcessChunk(item.Data!);
                break;
            case WorkKind.Action:
                try { item.Action!(); }
                catch (Exception ex) { Log($"[Mux] session {Id}: control action failed: {ex}"); }
                break;
            case WorkKind.Exit:
                ProcessExit(item.ExitCode, item.Terminal);
                break;
        }
    }

    private void ProcessChunk(byte[] data)
    {
        long seq = Interlocked.Read(ref _rawOffset);
        Interlocked.Exchange(ref _rawOffset, seq + data.Length);

        // A faulted parser no longer tracks the child; feeding clients a stream the mux cannot
        // vouch for would only make them diverge from something that is itself wrong.
        if (IsFaulted) return;

        int maxChars = Utf8ChunkDecoder.GetMaxCharCount(data.Length);
        if (_chars.Length < maxChars) _chars = new char[maxChars];
        int charCount = _decoder.Decode(data, _chars);
        if (charCount > 0)
        {
            try
            {
                _parser.Process(new string(_chars, 0, charCount));
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _faulted, 1);
                Log($"[Mux] session {Id} faulted: the parser threw at stream offset {seq}: {ex}");
                return;
            }
        }

        if (_subscribers.Count > 0) Broadcast(MuxFrames.Output(Id, seq, data));
    }

    private void ProcessExit(int? code, bool terminal)
    {
        if (!IsExited)
        {
            Volatile.Write(ref _exitCode, code ?? _session.ExitCode ?? -1);
            Volatile.Write(ref _exited, 1);
            if (_subscribers.Count > 0) Broadcast(ExitedFrame());
        }

        if (terminal)
        {
            _subscribers.Clear();
            PublishAttachedCount();
            Unsubscribe();
            _cts.Cancel();
        }
    }

    private void ExecuteAttach(IMuxFrameSink sink, long requestId, int maxScrollbackRows, MuxPresentation presentation, int maxSnapshotBytes)
    {
        if (_subscribers.Remove(sink)) PublishAttachedCount();
        if (IsFaulted)
        {
            Reply(sink, requestId, MuxErrorCodes.Internal, $"Session {Id} is faulted; its state no longer tracks the child.");
            return;
        }

        // Attaching is a resize to the attaching client's size (latest wins). The other clients get
        // the ResizeEvent; this one's snapshot already reflects it.
        ApplyPresentation(presentation);
        ApplyResize(presentation.Cols, presentation.Rows);

        byte[] json;
        long seq;
        try
        {
            TerminalStateSnapshot snapshot = CaptureSnapshot(maxScrollbackRows);
            seq = snapshot.StreamSeq;
            json = TerminalStateSerializer.ToBytes(snapshot);
        }
        catch (Exception ex)
        {
            Reply(sink, requestId, MuxErrorCodes.Internal, $"Snapshot capture failed: {ex.Message}");
            return;
        }

        if (json.Length > maxSnapshotBytes)
        {
            Reply(sink, requestId, MuxErrorCodes.SnapshotTooLarge,
                $"Snapshot of session {Id} is {json.Length} bytes; the limit is {maxSnapshotBytes}.");
            return;
        }

        MuxOutboundFrame frame = MuxFrames.Snapshot(requestId, Id, seq, json);
        bool accepted;
        try { accepted = sink.TryEnqueue(frame); }
        finally { frame.Release(); }
        if (!accepted) return;

        _subscribers.Add(sink);
        PublishAttachedCount();
        if (IsExited) Offer(sink, ExitedFrame());
    }

    private void ApplyPresentation(MuxPresentation presentation)
    {
        if (presentation.CellWidthPx > 0) _parser.CellWidth = presentation.CellWidthPx;
        if (presentation.CellHeightPx > 0) _parser.CellHeight = presentation.CellHeightPx;
        _parser.DefaultForeground = presentation.DefaultFg is uint fg ? TermColor.FromUint(fg) : null;
        _parser.DefaultBackground = presentation.DefaultBg is uint bg ? TermColor.FromUint(bg) : null;
        _parser.KittyKeyboardEnabled = presentation.KittyKeyboardEnabled;
    }

    private void ApplyResize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || (cols == _buffer.Cols && rows == _buffer.Rows)) return;

        // Buffer, then event, then child: bytes the child writes after learning the new size sort
        // after the event every client applies it at.
        _buffer.Resize(cols, rows);
        Volatile.Write(ref _cols, cols);
        Volatile.Write(ref _rows, rows);
        if (_subscribers.Count > 0) Broadcast(MuxFrames.ResizeEvent(Id, Interlocked.Read(ref _rawOffset), cols, rows));
        if (IsExited) return;
        _session.Resize(cols, rows);
        if (_parser.InBandResizeReportsEnabled)
        {
            _parser.SendInBandResize(rows, cols,
                (int)Math.Round(cols * _parser.CellWidth), (int)Math.Round(rows * _parser.CellHeight));
        }
    }

    private void Broadcast(MuxOutboundFrame frame)
    {
        try
        {
            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].TryEnqueue(frame))
                {
                    _subscribers.RemoveAt(i);
                    PublishAttachedCount();
                }
            }
        }
        finally
        {
            frame.Release();
        }
    }

    private MuxOutboundFrame ExitedFrame() =>
        MuxFrames.Notification(new MuxNotification
        {
            Method = MuxMethods.Exited,
            Params = MuxFrames.ToElement(
                new ExitedNotification { SessionId = Id, ExitCode = Volatile.Read(ref _exitCode) },
                MuxJsonContext.Default.ExitedNotification),
        });

    private void Reply(IMuxFrameSink sink, long requestId, string code, string message) =>
        Offer(sink, MuxFrames.Response(new MuxResponse
        {
            Id = requestId,
            Error = new MuxError { Code = code, Message = message },
        }));

    private static void Offer(IMuxFrameSink sink, MuxOutboundFrame frame)
    {
        try { sink.TryEnqueue(frame); }
        finally { frame.Release(); }
    }

    private void PublishAttachedCount() => Volatile.Write(ref _attached, _subscribers.Count);

    private void Unsubscribe()
    {
        if (Interlocked.Exchange(ref _unsubscribed, 1) != 0) return;
        _byteOutput.OnRawOutputReceived -= _onRawOutput;
        _session.OnOutputReceived -= _onStringOutput;
        _session.OnExit -= _onExit;
    }

    /// <summary>Completes waiters on items that will never run (flush barriers, invokes).</summary>
    private void DrainAfterStop()
    {
        while (_control.TryTake(out WorkItem item) || _data.TryTake(out item))
        {
            item.OnDropped?.Invoke();
        }
    }

    private void Log(string message) => _log?.Invoke(message);

    private enum WorkKind : byte { Data, Action, Exit }

    private readonly struct WorkItem
    {
        private WorkItem(WorkKind kind, byte[]? data, Action? action, Action? onDropped, int? exitCode, bool terminal)
        {
            Kind = kind;
            Data = data;
            Action = action;
            OnDropped = onDropped;
            ExitCode = exitCode;
            Terminal = terminal;
        }

        public WorkKind Kind { get; }
        public byte[]? Data { get; }
        public Action? Action { get; }
        public Action? OnDropped { get; }
        public int? ExitCode { get; }
        public bool Terminal { get; }

        public static WorkItem ForData(byte[] data) => new(WorkKind.Data, data, null, null, null, false);
        public static WorkItem ForAction(Action action, Action? onDropped = null) => new(WorkKind.Action, null, action, onDropped, null, false);
        public static WorkItem ForExit(int? exitCode, bool terminal = false) => new(WorkKind.Exit, null, null, null, exitCode, terminal);
    }
}
```

The `BlockingCollection`s and `CancellationTokenSource` are intentionally not disposed: late producers (the tap handler, `OnExit`) may still touch them and `TryEnqueue` treats a cancelled token as closed. If CA2213 fires, dispose them at the end of `Dispose` (after the join and drain) — `TryEnqueue` already catches `ObjectDisposedException`.

- [ ] **Step 7: Run the session tests** — same command as Step 4. Expected: all PASS (the corpus theory is 36 cases). Then `scripts/build.ps1 test tests/Ntilde.Mux.Tests` (whole project) and `scripts/build.ps1 test tests/Ntilde.Architecture.Tests` (93) — both green. `Mux_references_only_approved_ntilde_assemblies` now sees Pty, VT, Replay, Contracts.

- [ ] **Step 8: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux tests/Ntilde.Mux.Tests
git commit -m "feat(mux): headless authoritative session with a single parse thread"
```

---

### Task 6: `MuxServer`

**Files:**
- Create: `src/Ntilde.Mux/{MuxServerOptions.cs, MuxServer.cs, MuxServerConnection.cs, MuxRequestException.cs}`
- Create: `tests/Ntilde.Mux.Tests/Support/{RawMuxConnection.cs, MuxTestHost.cs}`, `tests/Ntilde.Mux.Tests/Server/{MuxServerHandshakeTests.cs, MuxServerRequestTests.cs, MuxServerProtocolErrorTests.cs, MuxServerSendBudgetTests.cs}`

**Interfaces:**
- Consumes: `HeadlessTerminalSession` (Task 5), transport (Task 4), contracts (Task 3).
- Produces: `public sealed class MuxServerOptions { MinProtocolVersion, MaxProtocolVersion, ForceConPtyFiltering, MaxAttachScrollbackRows=20_000, ClientSendBudgetBytes=16 MiB, MaxSnapshotBytes=MaxFrameBytes-32, MaxInboundFrameBytes=MaxFrameBytes, Log }`; `public sealed class MuxServer : IDisposable { MuxServer(ITerminalSessionFactory, MuxServerOptions?); Options; ConnectionCount; SessionIds; void Start(IMuxListener) /* takes ownership */; void AcceptConnection(Stream); internal bool TryGetSession(Guid, out HeadlessTerminalSession); internal event Action<MuxServerConnection>? ConnectionClosed; }`; `internal sealed class MuxServerConnection : IMuxFrameSink { ConnectionId; CloseReason; ProtocolVersion; Start(); Abort(string reason); }`. Test support: `RawMuxConnection`, `MuxTestHost` (extended in Task 7).

- [ ] **Step 1: Write the test support**

`Support/RawMuxConnection.cs`:

```csharp
using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Tests.Support;

/// <summary>A hand-driven client: speaks raw frames so tests can misbehave on purpose.</summary>
internal sealed class RawMuxConnection : IDisposable
{
    private long _nextId;

    public RawMuxConnection(Stream stream) => Stream = stream;

    public Stream Stream { get; }

    public void Send(MuxOutboundFrame frame)
    {
        try { frame.WriteTo(Stream); }
        finally { frame.Release(); }
    }

    public long Request<T>(string method, T parameters, JsonTypeInfo<T> typeInfo, long? id = null)
    {
        long requestId = id ?? Interlocked.Increment(ref _nextId);
        Send(MuxFrames.Request(new MuxRequest { Id = requestId, Method = method, Params = MuxFrames.ToElement(parameters, typeInfo) }));
        return requestId;
    }

    public void WriteRaw(ReadOnlySpan<byte> bytes) => Stream.Write(bytes);

    public async Task<MuxInboundFrame?> ReadAsync(TimeSpan? timeout = null) =>
        await Task.Run(() => MuxFrameReader.Read(Stream), TestContext.Current.CancellationToken)
            .WaitAsync(timeout ?? TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    public async Task<MuxResponse> ReadResponseAsync()
    {
        using MuxInboundFrame frame = await ReadAsync() ?? throw new EndOfStreamException("The server closed the connection.");
        Assert.Equal(MuxFrameKind.Response, frame.Kind);
        return MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxResponse);
    }

    public async Task<WelcomeResult> HelloAsync(int min = 1, int max = 1)
    {
        Request(MuxMethods.Hello, new HelloParams { MinVersion = min, MaxVersion = max, ClientKind = "raw" }, MuxJsonContext.Default.HelloParams);
        MuxResponse response = await ReadResponseAsync();
        Assert.Null(response.Error);
        return MuxFrames.ParseParams(response.Result, MuxJsonContext.Default.WelcomeResult);
    }

    /// <summary>True when the peer closes before sending another frame.</summary>
    public async Task<bool> IsClosedByPeerAsync()
    {
        try
        {
            using MuxInboundFrame? frame = await ReadAsync();
            return frame is null;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public void Dispose() => Stream.Dispose();
}
```

`Support/MuxTestHost.cs`:

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Support;

/// <summary>A started server over an in-memory listener with a scripted session factory.</summary>
internal sealed class MuxTestHost : IDisposable
{
    public static readonly MuxPresentation DefaultPresentation = new() { Cols = 80, Rows = 24, CellWidthPx = 10, CellHeightPx = 20 };

    private readonly List<IDisposable> _owned = new();

    public MuxTestHost(MuxServerOptions? options = null, int pipeCapacityBytes = 1 << 20)
    {
        Factory = new ScriptedSessionFactory();
        Listener = new InMemoryMuxListener(pipeCapacityBytes);
        Server = new MuxServer(Factory, options ?? new MuxServerOptions { ForceConPtyFiltering = false });
        Server.Start(Listener);
    }

    public ScriptedSessionFactory Factory { get; }
    public InMemoryMuxListener Listener { get; }
    public MuxServer Server { get; }

    public RawMuxConnection ConnectRaw()
    {
        var raw = new RawMuxConnection(Listener.Connect());
        _owned.Add(raw);
        return raw;
    }

    public HeadlessTerminalSession Mux(Guid id) =>
        Server.TryGetSession(id, out HeadlessTerminalSession? session) ? session : throw new InvalidOperationException($"No session {id}.");

    public ScriptedTerminalSession Fake(Guid id) => (ScriptedTerminalSession)Mux(id).Inner;

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        Server.Dispose();
    }

    internal void Own(IDisposable disposable) => _owned.Add(disposable);
}
```

- [ ] **Step 2: Write the failing server tests**

`Server/MuxServerHandshakeTests.cs`:

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerHandshakeTests
{
    [Fact]
    public async Task Overlapping_ranges_pick_the_highest_common_version()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MinProtocolVersion = 1, MaxProtocolVersion = 3, ForceConPtyFiltering = true });
        RawMuxConnection raw = host.ConnectRaw();

        WelcomeResult welcome = await raw.HelloAsync(min: 2, max: 5);

        Assert.Equal(3, welcome.Version);
        Assert.True(welcome.ForceConPtyFiltering);
    }

    [Fact]
    public async Task Disjoint_ranges_are_refused_and_the_connection_closed()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();

        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 2, MaxVersion = 3 }, MuxJsonContext.Default.HelloParams);
        MuxResponse response = await raw.ReadResponseAsync();

        Assert.Equal(MuxErrorCodes.VersionMismatch, response.Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the server forgets the connection");
    }

    [Fact]
    public async Task The_first_frame_must_be_hello()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();

        raw.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        MuxResponse response = await raw.ReadResponseAsync();

        Assert.Equal(0, response.Id);
        Assert.Equal(MuxErrorCodes.ProtocolError, response.Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
    }

    [Fact]
    public async Task A_second_hello_is_a_protocol_error()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        raw.Request(MuxMethods.Hello, new HelloParams { MinVersion = 1, MaxVersion = 1 }, MuxJsonContext.Default.HelloParams);

        Assert.Equal(MuxErrorCodes.ProtocolError, (await raw.ReadResponseAsync()).Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
    }
}
```

`Server/MuxServerRequestTests.cs`:

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerRequestTests
{
    private static async Task<MuxResponse> CallAsync<T>(RawMuxConnection raw, string method, T p, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        long id = raw.Request(method, p, info);
        MuxResponse response = await raw.ReadResponseAsync();
        Assert.Equal(id, response.Id);
        return response;
    }

    private static async Task<Guid> SpawnAsync(RawMuxConnection raw, SpawnParams? p = null)
    {
        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, p ?? new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "one" }, MuxJsonContext.Default.SpawnParams);
        Assert.Null(r.Error);
        return MuxFrames.ParseParams(r.Result, MuxJsonContext.Default.SpawnResult).SessionId;
    }

    [Fact]
    public async Task Spawn_forwards_the_request_and_the_session_is_listed()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();

        Guid id = await SpawnAsync(raw, new SpawnParams
        {
            Command = "pwsh", Arguments = "-NoProfile", StartingDirectory = "C:\\w", Cols = 100, Rows = 30,
            EnvironmentOverrides = new Dictionary<string, string> { ["A"] = "1" }, SkipPowerShellPostLaunchInit = true, Title = "work",
        });
        MuxResponse list = await CallAsync(raw, MuxMethods.ListSessions, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

        var request = Assert.Single(host.Factory.Requests);
        Assert.Equal(("pwsh", "-NoProfile", "C:\\w", 100, 30, true), (request.Command, request.Arguments, request.StartingDirectory, request.Cols, request.Rows, request.SkipPowerShellPostLaunchInit));
        Assert.Equal("1", request.EnvironmentOverrides!["A"]);
        Assert.Null(request.Ssh);
        SessionSummary summary = Assert.Single(MuxFrames.ParseParams(list.Result, MuxJsonContext.Default.ListSessionsResult).Sessions);
        Assert.Equal((id, "work", "pwsh", 100, 30, true, 0), (summary.SessionId, summary.Title, summary.Command, summary.Cols, summary.Rows, summary.Running, summary.AttachedClients));
    }

    [Fact]
    public async Task A_failing_spawn_is_reported_and_the_connection_survives()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        host.Factory.FailNext = true;

        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "x" }, MuxJsonContext.Default.SpawnParams);

        Assert.Equal(MuxErrorCodes.SpawnFailed, r.Error?.Code);
        Assert.Null((await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error);
    }

    [Fact]
    public async Task A_session_without_a_byte_tap_is_refused_and_disposed()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        host.Factory.ProduceSessionsWithoutByteTap = true;

        MuxResponse r = await CallAsync(raw, MuxMethods.Spawn, new SpawnParams { Command = "x" }, MuxJsonContext.Default.SpawnParams);

        Assert.Equal(MuxErrorCodes.SpawnFailed, r.Error?.Code);
        Assert.True(host.Factory.LastNoTapSession!.Disposed);
    }

    [Fact]
    public async Task Unknown_sessions_and_methods_are_request_errors_not_disconnects()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        var nobody = new SessionIdParams { SessionId = Guid.NewGuid() };

        Assert.Equal(MuxErrorCodes.UnknownSession, (await CallAsync(raw, MuxMethods.Kill, nobody, MuxJsonContext.Default.SessionIdParams)).Error?.Code);
        Assert.Equal(MuxErrorCodes.UnknownSession, (await CallAsync(raw, MuxMethods.SessionInfo, nobody, MuxJsonContext.Default.SessionIdParams)).Error?.Code);
        Assert.Equal(MuxErrorCodes.UnknownSession, (await CallAsync(raw, MuxMethods.Attach,
            new AttachParams { SessionId = nobody.SessionId, Presentation = MuxTestHost.DefaultPresentation }, MuxJsonContext.Default.AttachParams)).Error?.Code);
        Assert.Equal(MuxErrorCodes.ProtocolError, (await CallAsync(raw, "noSuchMethod", new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error?.Code);
        Assert.Null((await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty)).Error);
    }

    [Fact]
    public async Task SessionInfo_reports_the_probe_and_the_exit()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        host.Fake(id).HasActiveChildProcesses = true;

        SessionInfoResult running = MuxFrames.ParseParams((await CallAsync(raw, MuxMethods.SessionInfo, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams)).Result, MuxJsonContext.Default.SessionInfoResult);
        host.Fake(id).Exit(3);
        await host.Mux(id).FlushAsync();
        SessionInfoResult exited = MuxFrames.ParseParams((await CallAsync(raw, MuxMethods.SessionInfo, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams)).Result, MuxJsonContext.Default.SessionInfoResult);

        Assert.Equal((true, (int?)null, true, (int?)null), (running.Running, running.ExitCode, running.HasActiveChildProcesses, running.Pid));
        Assert.Equal((false, (int?)3, false), (exited.Running, exited.ExitCode, exited.HasActiveChildProcesses));
    }

    [Fact]
    public async Task Kill_disposes_the_child_and_removes_the_session()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);
        ScriptedTerminalSession fake = host.Fake(id);

        Assert.Null((await CallAsync(raw, MuxMethods.Kill, new SessionIdParams { SessionId = id }, MuxJsonContext.Default.SessionIdParams)).Error);

        Assert.True(fake.Disposed);
        Assert.DoesNotContain(id, host.Server.SessionIds);
    }

    [Fact]
    public async Task A_resize_with_id_zero_gets_no_response()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);

        raw.Request(MuxMethods.Resize, new ResizeParams { SessionId = id, Cols = 90, Rows = 20 }, MuxJsonContext.Default.ResizeParams, id: 0);
        long ping = raw.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

        Assert.Equal(ping, (await raw.ReadResponseAsync()).Id); // the ping reply is the very next frame
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.Equal((90, 20), (host.Mux(id).Cols, host.Mux(id).Rows));
    }

    [Fact]
    public async Task Input_reaches_the_session_as_the_sent_string()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        Guid id = await SpawnAsync(raw);

        raw.Send(MuxFrames.Input(id, "é\r"u8));
        await CallAsync(raw, MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

        Assert.Equal("é\r", Assert.Single(host.Fake(id).SentInput));
    }
}
```

`Server/MuxServerProtocolErrorTests.cs`:

```csharp
using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerProtocolErrorTests
{
    private static async Task<RawMuxConnection> ReadyAsync(MuxTestHost host)
    {
        RawMuxConnection raw = host.ConnectRaw();
        await raw.HelloAsync();
        return raw;
    }

    private static async Task AssertClosedWithAsync(RawMuxConnection raw, string code)
    {
        MuxResponse response = await raw.ReadResponseAsync();
        Assert.Equal(0, response.Id);
        Assert.Equal(code, response.Error?.Code);
        Assert.True(await raw.IsClosedByPeerAsync());
    }

    [Fact]
    public async Task An_oversize_frame_header_is_refused_with_frame_too_large()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MaxInboundFrameBytes = 1024, ForceConPtyFiltering = false });
        RawMuxConnection raw = await ReadyAsync(host);
        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, 1025);

        raw.WriteRaw(header);

        await AssertClosedWithAsync(raw, MuxErrorCodes.FrameTooLarge);
    }

    [Fact]
    public async Task Malformed_json_closes_the_connection_without_touching_any_buffer()
    {
        using var host = new MuxTestHost();
        RawMuxConnection spawner = await ReadyAsync(host);
        spawner.Request(MuxMethods.Spawn, new SpawnParams { Command = "scripted" }, MuxJsonContext.Default.SpawnParams);
        Guid id = MuxFrames.ParseParams((await spawner.ReadResponseAsync()).Result, MuxJsonContext.Default.SpawnResult).SessionId;
        host.Fake(id).Emit("before");
        await host.Mux(id).FlushAsync();
        string before = await host.Mux(id).InvokeAsync(() => host.Mux(id).CaptureSnapshot(100).Main.CellsBase64!);

        RawMuxConnection raw = await ReadyAsync(host);
        byte[] junk = Encoding.UTF8.GetBytes("{nope");
        byte[] header = new byte[MuxProtocol.FrameHeaderBytes];
        MuxFrameCodec.WriteHeader(header, MuxFrameKind.Request, junk.Length);
        raw.WriteRaw(header);
        raw.WriteRaw(junk);

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
        string after = await host.Mux(id).InvokeAsync(() => host.Mux(id).CaptureSnapshot(100).Main.CellsBase64!);
        Assert.Equal(before, after);
        Assert.Equal(6, host.Mux(id).StreamPosition);
    }

    [Fact]
    public async Task A_missing_required_member_closes_the_connection()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = await ReadyAsync(host);
        raw.Send(MuxFrames.Request(new MuxRequest
        {
            Id = 5, Method = MuxMethods.Attach,
            Params = MuxFrames.ToElement(new SessionIdParams { SessionId = Guid.NewGuid() }, MuxJsonContext.Default.SessionIdParams),
        }));

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
    }

    [Fact]
    public async Task An_unknown_frame_kind_closes_the_connection()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = await ReadyAsync(host);

        raw.WriteRaw([0x7F, 0, 0, 0, 0]);

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
    }

    [Fact]
    public async Task A_client_may_not_send_Output()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = await ReadyAsync(host);

        raw.Send(MuxFrames.Output(Guid.NewGuid(), 0, "x"u8));

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
    }

    [Fact]
    public async Task A_truncated_Input_closes_the_connection()
    {
        using var host = new MuxTestHost();
        RawMuxConnection raw = await ReadyAsync(host);

        raw.WriteRaw([(byte)MuxFrameKind.Input, 3, 0, 0, 0, 1, 2, 3]);

        await AssertClosedWithAsync(raw, MuxErrorCodes.ProtocolError);
    }
}
```

(`using Ntilde.VT;` is only needed if `CaptureSnapshot`'s return type is not otherwise resolved; remove it if unused.)

`Server/MuxServerSendBudgetTests.cs`:

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerSendBudgetTests
{
    [Fact]
    public async Task Overflowing_the_budget_disconnects_as_client_too_slow_without_blocking_the_caller()
    {
        using var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ClientSendBudgetBytes = 64 * 1024 });
        (Stream clientEnd, Stream serverEnd) = InMemoryDuplexPipe.Create(4096); // the client never reads
        using var _ = clientEnd;
        var connection = new MuxServerConnection(server, serverEnd);
        connection.Start();

        int accepted = 0;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (accepted < 1000)
        {
            MuxOutboundFrame frame = MuxFrames.Output(Guid.NewGuid(), 0, new byte[1024]);
            bool ok = connection.TryEnqueue(frame);
            frame.Release();
            if (!ok) break;
            accepted++;
        }

        Assert.InRange(accepted, 60, 80); // 64 KiB budget + what fits in the 4 KiB pipe
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), "TryEnqueue must never block");
        await TestWait.UntilAsync(() => connection.CloseReason is not null, "the connection is closed");
        Assert.Equal(MuxErrorCodes.ClientTooSlow, connection.CloseReason);
    }

    [Fact]
    public void One_frame_larger_than_the_budget_is_accepted_when_nothing_is_queued()
    {
        using var server = new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions { ClientSendBudgetBytes = 1024 });
        (Stream clientEnd, Stream serverEnd) = InMemoryDuplexPipe.Create(4096);
        using var _ = clientEnd;
        var connection = new MuxServerConnection(server, serverEnd);
        connection.Start();

        MuxOutboundFrame big = MuxFrames.Output(Guid.NewGuid(), 0, new byte[10 * 1024]);
        MuxOutboundFrame second = MuxFrames.Output(Guid.NewGuid(), 0, new byte[10 * 1024]);
        try
        {
            Assert.True(connection.TryEnqueue(big));     // a snapshot on a fresh attach
            Assert.False(connection.TryEnqueue(second)); // the first is still in flight: over budget
        }
        finally
        {
            big.Release();
            second.Release();
        }
    }
}
```

- [ ] **Step 3: Run to verify they fail** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~Server"`. Expected: build FAILS.

- [ ] **Step 4: Implement options, request exception, server**

`MuxServerOptions.cs`:

```csharp
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux;

public sealed class MuxServerOptions
{
    public int MinProtocolVersion { get; init; } = MuxProtocol.MinSupportedVersion;
    public int MaxProtocolVersion { get; init; } = MuxProtocol.MaxSupportedVersion;

    /// <summary>Passed to every session's parser and reported in Welcome so clients parse identically.</summary>
    public bool ForceConPtyFiltering { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Server-side cap on an attach's <c>maxScrollbackRows</c> (spec §7).</summary>
    public int MaxAttachScrollbackRows { get; init; } = 20_000;

    /// <summary>Bytes queued-but-unwritten per client before it is disconnected as too slow.</summary>
    public long ClientSendBudgetBytes { get; init; } = 16L * 1024 * 1024;

    public int MaxSnapshotBytes { get; init; } = MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes;

    public int MaxInboundFrameBytes { get; init; } = MuxProtocol.MaxFrameBytes;

    public Action<string>? Log { get; init; }
}
```

`MuxRequestException.cs` (make it `public` only if CA1064 fires):

```csharp
namespace Ntilde.Mux;

/// <summary>A per-request failure: answered with an error Response; the connection stays open.</summary>
internal sealed class MuxRequestException : Exception
{
    public MuxRequestException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
```

`MuxServer.cs`:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;
using Ntilde.Pty;

namespace Ntilde.Mux;

/// <summary>
/// Hosts headless sessions and serves them to clients over any <see cref="Stream"/>. Phase 2 runs
/// this inside <c>ntilde mux serve</c> with a pipe/socket <see cref="IMuxListener"/>.
/// </summary>
public sealed class MuxServer : IDisposable
{
    private readonly ITerminalSessionFactory _factory;
    private readonly ConcurrentDictionary<Guid, HeadlessTerminalSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, MuxServerConnection> _connections = new();
    private readonly CancellationTokenSource _cts = new();
    private IMuxListener? _listener;
    private Thread? _acceptThread;
    private int _disposed;

    public MuxServer(ITerminalSessionFactory sessionFactory, MuxServerOptions? options = null)
    {
        _factory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        Options = options ?? new MuxServerOptions();
    }

    public MuxServerOptions Options { get; }
    public int ConnectionCount => _connections.Count;
    public IReadOnlyCollection<Guid> SessionIds => _sessions.Keys.ToArray();

    internal event Action<MuxServerConnection>? ConnectionClosed;

    /// <summary>Starts accepting on a dedicated thread. The server owns the listener from here on.</summary>
    public void Start(IMuxListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (Interlocked.CompareExchange(ref _listener, listener, null) is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "MuxAccept" };
        _acceptThread.Start();
    }

    public void AcceptConnection(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (Volatile.Read(ref _disposed) != 0)
        {
            stream.Dispose();
            return;
        }

        var connection = new MuxServerConnection(this, stream);
        _connections[connection.ConnectionId] = connection;
        connection.Start();
    }

    internal bool TryGetSession(Guid id, [NotNullWhen(true)] out HeadlessTerminalSession? session) =>
        _sessions.TryGetValue(id, out session);

    internal Guid Spawn(SpawnParams p)
    {
        if (p.Cols <= 0 || p.Rows <= 0)
        {
            throw new MuxRequestException(MuxErrorCodes.ProtocolError, "spawn needs positive cols and rows.");
        }

        var request = new TerminalSessionRequest(
            p.Command, p.Arguments, p.StartingDirectory, p.Cols, p.Rows,
            p.EnvironmentOverrides, p.SkipPowerShellPostLaunchInit, Ssh: null);

        ITerminalSession inner;
        try
        {
            inner = _factory.Create(request);
        }
        catch (Exception ex)
        {
            throw new MuxRequestException(MuxErrorCodes.SpawnFailed, ex.Message);
        }

        if (inner is not ITerminalByteOutput)
        {
            inner.Dispose();
            throw new MuxRequestException(MuxErrorCodes.SpawnFailed,
                $"{inner.GetType().Name} does not expose raw output, so it cannot be multiplexed.");
        }

        Guid id = Guid.NewGuid();
        _sessions[id] = new HeadlessTerminalSession(id, inner, new HeadlessSessionOptions
        {
            Title = string.IsNullOrEmpty(p.Title) ? p.Command : p.Title,
            Command = p.Command,
            Arguments = p.Arguments,
            Cols = p.Cols,
            Rows = p.Rows,
            ForceConPtyFiltering = Options.ForceConPtyFiltering,
            Log = Options.Log,
        });
        return id;
    }

    internal SessionSummary[] ListSessions() =>
        _sessions.Values.Select(s => new SessionSummary
        {
            SessionId = s.Id,
            Title = s.Title,
            Command = s.Command,
            Arguments = s.Arguments,
            Cols = s.Cols,
            Rows = s.Rows,
            Running = !s.IsExited,
            ExitCode = s.ExitCode,
            AttachedClients = s.AttachedClients,
            Faulted = s.IsFaulted,
        }).ToArray();

    internal void Kill(Guid id)
    {
        if (!_sessions.TryRemove(id, out HeadlessTerminalSession? session))
        {
            throw new MuxRequestException(MuxErrorCodes.UnknownSession, $"No session {id}.");
        }

        session.Kill();
    }

    internal void OnConnectionClosed(MuxServerConnection connection)
    {
        _connections.TryRemove(connection.ConnectionId, out _);
        ConnectionClosed?.Invoke(connection);
    }

    internal void Log(string message) => Options.Log?.Invoke(message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _listener?.Dispose();
        _acceptThread?.Join(TimeSpan.FromSeconds(5));
        foreach (MuxServerConnection connection in _connections.Values) connection.Abort("server_shutdown");
        foreach (HeadlessTerminalSession session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _cts.Dispose();
    }

    private void AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                Stream? stream = _listener!.Accept(_cts.Token);
                if (stream is null) return;
                AcceptConnection(stream);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"[MuxServer] accept loop terminated: {ex}");
        }
    }
}
```

- [ ] **Step 5: Implement the connection** — `MuxServerConnection.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;

namespace Ntilde.Mux;

/// <summary>
/// One client: a reader thread that parses and dispatches frames, and a sender thread that drains
/// a byte-budgeted queue. <see cref="TryEnqueue"/> is called from session parse threads and never
/// blocks - overflowing the budget disconnects this client instead (spec §7).
/// </summary>
internal sealed class MuxServerConnection : IMuxFrameSink
{
    private readonly MuxServer _server;
    private readonly Stream _stream;
    private readonly Thread _readerThread;
    private readonly Thread _senderThread;
    private readonly object _gate = new();
    private readonly Queue<MuxOutboundFrame> _queue = new();
    private readonly HashSet<Guid> _attached = new(); // reader thread only
    private long _queuedBytes;       // queued + in flight
    private bool _closed;            // stream closed or closing now; nothing more is accepted
    private bool _closeAfterFlush;   // the queue ends with a final frame; the sender closes after it
    private int _version;
    private string? _closeReason;
    private int _closedNotified;

    public MuxServerConnection(MuxServer server, Stream stream)
    {
        _server = server;
        _stream = stream;
        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = $"MuxConnRead-{ConnectionId:N}" };
        _senderThread = new Thread(SendLoop) { IsBackground = true, Name = $"MuxConnSend-{ConnectionId:N}" };
    }

    public Guid ConnectionId { get; } = Guid.NewGuid();
    public string? CloseReason => Volatile.Read(ref _closeReason);
    public int ProtocolVersion => Volatile.Read(ref _version);

    private bool IsClosing
    {
        get { lock (_gate) return _closed || _closeAfterFlush; }
    }

    public void Start()
    {
        _senderThread.Start();
        _readerThread.Start();
    }

    public bool TryEnqueue(MuxOutboundFrame frame)
    {
        lock (_gate)
        {
            if (_closed || _closeAfterFlush) return false;

            // An empty queue takes any frame (a snapshot bigger than the budget on a fresh attach);
            // otherwise the frame must fit.
            if (_queuedBytes == 0 || _queuedBytes + frame.Length <= _server.Options.ClientSendBudgetBytes)
            {
                frame.AddRef();
                _queue.Enqueue(frame);
                _queuedBytes += frame.Length;
                Monitor.Pulse(_gate);
                return true;
            }
        }

        Abort(MuxErrorCodes.ClientTooSlow);
        return false;
    }

    /// <summary>
    /// Ends the connection now: drops queued frames and closes the stream, which unblocks both
    /// threads. For <c>client_too_slow</c> the reason cannot be delivered - the peer is not reading -
    /// so it is recorded here and logged (spec §9.5).
    /// </summary>
    public void Abort(string reason)
    {
        MuxOutboundFrame[] dropped;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            Interlocked.CompareExchange(ref _closeReason, reason, null);
            dropped = _queue.ToArray();
            _queue.Clear();
            foreach (MuxOutboundFrame f in dropped) _queuedBytes -= f.Length;
            Monitor.PulseAll(_gate);
        }

        foreach (MuxOutboundFrame f in dropped) f.Release();
        if (reason == MuxErrorCodes.ClientTooSlow)
        {
            _server.Log($"[MuxServer] connection {ConnectionId} disconnected: {reason} (send budget {_server.Options.ClientSendBudgetBytes} bytes exceeded).");
        }

        try { _stream.Dispose(); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Sends one last frame (the peer is still reading and deserves the reason), then closes.</summary>
    private void CloseAfter(MuxOutboundFrame finalFrame, string reason)
    {
        MuxOutboundFrame[] dropped;
        lock (_gate)
        {
            if (_closed || _closeAfterFlush)
            {
                finalFrame.Release();
                return;
            }

            Interlocked.CompareExchange(ref _closeReason, reason, null);
            dropped = _queue.ToArray();
            _queue.Clear();
            foreach (MuxOutboundFrame f in dropped) _queuedBytes -= f.Length;
            _queue.Enqueue(finalFrame); // takes over the caller's reference
            _queuedBytes += finalFrame.Length;
            _closeAfterFlush = true;
            Monitor.PulseAll(_gate);
        }

        foreach (MuxOutboundFrame f in dropped) f.Release();
    }

    private void CloseWithError(long requestId, string code, string message) =>
        CloseAfter(MuxFrames.Response(new MuxResponse { Id = requestId, Error = new MuxError { Code = code, Message = message } }), code);

    private void SendLoop()
    {
        try
        {
            while (TryTakeNext(out MuxOutboundFrame? frame))
            {
                int length = frame.Length;
                try
                {
                    frame.WriteTo(_stream);
                }
                finally
                {
                    frame.Release();
                    lock (_gate) _queuedBytes -= length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
        }

        Abort(CloseReason ?? "disconnected");
    }

    private bool TryTakeNext([NotNullWhen(true)] out MuxOutboundFrame? frame)
    {
        lock (_gate)
        {
            while (_queue.Count == 0)
            {
                if (_closed || _closeAfterFlush)
                {
                    frame = null;
                    return false;
                }

                Monitor.Wait(_gate);
            }

            frame = _queue.Dequeue();
            return true;
        }
    }

    private void ReadLoop()
    {
        try
        {
            while (!IsClosing)
            {
                MuxInboundFrame? frame = MuxFrameReader.Read(_stream, _server.Options.MaxInboundFrameBytes);
                if (frame is null) break;
                using (frame)
                {
                    Dispatch(frame);
                }
            }
        }
        catch (MuxProtocolException ex)
        {
            CloseWithError(0, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _server.Log($"[MuxServer] connection {ConnectionId} reader failed: {ex}");
            CloseWithError(0, MuxErrorCodes.Internal, ex.Message);
        }
        finally
        {
            foreach (Guid id in _attached)
            {
                if (_server.TryGetSession(id, out HeadlessTerminalSession? session)) session.PostDetach(this);
            }

            _attached.Clear();
            if (!IsClosing) Abort("disconnected");
            if (Interlocked.Exchange(ref _closedNotified, 1) == 0) _server.OnConnectionClosed(this);
        }
    }

    private void Dispatch(MuxInboundFrame frame)
    {
        if (ProtocolVersion == 0)
        {
            if (frame.Kind != MuxFrameKind.Request)
            {
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "The first frame must be a hello request.");
            }

            MuxRequest hello = MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest);
            if (hello.Method != MuxMethods.Hello)
            {
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Expected hello, got '{hello.Method}'.");
            }

            HandleHello(hello);
            return;
        }

        switch (frame.Kind)
        {
            case MuxFrameKind.Request:
                HandleRequest(MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest));
                break;
            case MuxFrameKind.Input:
                if (!MuxFrames.TryParseInput(frame.Payload, out Guid id, out ReadOnlySpan<byte> utf8))
                {
                    throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "Input frame shorter than its session id.");
                }

                // One Input frame is one whole SendInput string, so decoding it alone is lossless.
                if (_server.TryGetSession(id, out HeadlessTerminalSession? session)) session.SendInput(Encoding.UTF8.GetString(utf8));
                break;
            default:
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"A client may not send {frame.Kind} frames.");
        }
    }

    private void HandleHello(MuxRequest request)
    {
        HelloParams p = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.HelloParams);
        MuxServerOptions o = _server.Options;
        if (MuxProtocol.NegotiateVersion(o.MinProtocolVersion, o.MaxProtocolVersion, p.MinVersion, p.MaxVersion) is not int chosen)
        {
            CloseWithError(request.Id, MuxErrorCodes.VersionMismatch,
                $"Server speaks {o.MinProtocolVersion}..{o.MaxProtocolVersion}; client offered {p.MinVersion}..{p.MaxVersion}.");
            return;
        }

        Volatile.Write(ref _version, chosen);
        Reply(request, new WelcomeResult { Version = chosen, ForceConPtyFiltering = o.ForceConPtyFiltering }, MuxJsonContext.Default.WelcomeResult);
    }

    private void HandleRequest(MuxRequest request)
    {
        try
        {
            switch (request.Method)
            {
                case MuxMethods.Ping:
                    ReplyEmpty(request);
                    break;
                case MuxMethods.ListSessions:
                    Reply(request, new ListSessionsResult { Sessions = _server.ListSessions() }, MuxJsonContext.Default.ListSessionsResult);
                    break;
                case MuxMethods.Spawn:
                    Guid spawned = _server.Spawn(Params(request, MuxJsonContext.Default.SpawnParams));
                    Reply(request, new SpawnResult { SessionId = spawned }, MuxJsonContext.Default.SpawnResult);
                    break;
                case MuxMethods.Attach:
                    HandleAttach(request);
                    break;
                case MuxMethods.Detach:
                {
                    SessionIdParams p = Params(request, MuxJsonContext.Default.SessionIdParams);
                    if (_attached.Remove(p.SessionId) && _server.TryGetSession(p.SessionId, out HeadlessTerminalSession? s)) s.PostDetach(this);
                    ReplyEmpty(request);
                    break;
                }

                case MuxMethods.Kill:
                {
                    SessionIdParams p = Params(request, MuxJsonContext.Default.SessionIdParams);
                    _attached.Remove(p.SessionId);
                    _server.Kill(p.SessionId);
                    ReplyEmpty(request);
                    break;
                }

                case MuxMethods.Resize:
                {
                    ResizeParams p = Params(request, MuxJsonContext.Default.ResizeParams);
                    RequireGeometry(p.Cols, p.Rows);
                    if (p.Presentation is { } presentation) RequireGeometry(presentation.Cols, presentation.Rows);
                    Session(p.SessionId).PostResize(p.Cols, p.Rows, p.Presentation);
                    ReplyEmpty(request);
                    break;
                }

                case MuxMethods.SessionInfo:
                {
                    HeadlessTerminalSession s = Session(Params(request, MuxJsonContext.Default.SessionIdParams).SessionId);
                    Reply(request, new SessionInfoResult
                    {
                        Running = !s.IsExited,
                        ExitCode = s.ExitCode,
                        HasActiveChildProcesses = !s.IsExited && s.Inner.HasActiveChildProcesses,
                        Pid = (s.Inner as RustPtySession)?.Pid,
                    }, MuxJsonContext.Default.SessionInfoResult);
                    break;
                }

                case MuxMethods.StartRecording:
                {
                    StartRecordingParams p = Params(request, MuxJsonContext.Default.StartRecordingParams);
                    LiveSession(p.SessionId).Inner.StartRecording(p.Path);
                    ReplyEmpty(request);
                    break;
                }

                case MuxMethods.StopRecording:
                    Session(Params(request, MuxJsonContext.Default.SessionIdParams).SessionId).Inner.StopRecording();
                    ReplyEmpty(request);
                    break;
                case MuxMethods.EnableFlightRecording:
                {
                    EnableFlightRecordingParams p = Params(request, MuxJsonContext.Default.EnableFlightRecordingParams);
                    if (p.MaxBytes <= 0) throw new MuxRequestException(MuxErrorCodes.ProtocolError, "maxBytes must be positive.");
                    LiveSession(p.SessionId).Inner.EnableFlightRecording(p.MaxBytes);
                    ReplyEmpty(request);
                    break;
                }

                case MuxMethods.DisableFlightRecording:
                    Session(Params(request, MuxJsonContext.Default.SessionIdParams).SessionId).Inner.DisableFlightRecording();
                    ReplyEmpty(request);
                    break;
                case MuxMethods.ExportFlight:
                    Reply(request, Session(Params(request, MuxJsonContext.Default.SessionIdParams).SessionId).ExportFlight(), MuxJsonContext.Default.ExportFlightResult);
                    break;
                case MuxMethods.Hello:
                    throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "hello may only be sent once.");
                default:
                    ReplyError(request, MuxErrorCodes.ProtocolError, $"Unknown method '{request.Method}'.");
                    break;
            }
        }
        catch (MuxRequestException ex)
        {
            ReplyError(request, ex.Code, ex.Message);
        }
    }

    private void HandleAttach(MuxRequest request)
    {
        if (request.Id == 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "attach needs a request id: its reply is the snapshot.");
        }

        AttachParams p = Params(request, MuxJsonContext.Default.AttachParams);
        RequireGeometry(p.Presentation.Cols, p.Presentation.Rows);
        HeadlessTerminalSession session = Session(p.SessionId);
        int rows = Math.Clamp(p.MaxScrollbackRows, 0, _server.Options.MaxAttachScrollbackRows);
        _attached.Add(p.SessionId);

        // The reply - the Snapshot frame, or an error - comes from the parse thread.
        session.PostAttach(this, request.Id, rows, p.Presentation, _server.Options.MaxSnapshotBytes);
    }

    private static T Params<T>(MuxRequest request, JsonTypeInfo<T> typeInfo) where T : class =>
        MuxFrames.ParseParams(request.Params, typeInfo); // malformed → MuxProtocolException → connection closed

    private static void RequireGeometry(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) throw new MuxRequestException(MuxErrorCodes.ProtocolError, $"Invalid geometry {cols}x{rows}.");
    }

    private HeadlessTerminalSession Session(Guid id) =>
        _server.TryGetSession(id, out HeadlessTerminalSession? s) ? s : throw new MuxRequestException(MuxErrorCodes.UnknownSession, $"No session {id}.");

    private HeadlessTerminalSession LiveSession(Guid id)
    {
        HeadlessTerminalSession s = Session(id);
        return s.IsExited ? throw new MuxRequestException(MuxErrorCodes.SessionExited, $"Session {id} has exited.") : s;
    }

    private void Reply<T>(MuxRequest request, T result, JsonTypeInfo<T> typeInfo)
    {
        if (request.Id == 0) return;
        MuxOutboundFrame frame;
        try
        {
            frame = MuxFrames.Response(new MuxResponse { Id = request.Id, Result = MuxFrames.ToElement(result, typeInfo) });
        }
        catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.FrameTooLarge)
        {
            ReplyError(request, ex.Code, ex.Message);
            return;
        }

        Send(frame);
    }

    private void ReplyEmpty(MuxRequest request) => Reply(request, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

    private void ReplyError(MuxRequest request, string code, string message)
    {
        if (request.Id == 0)
        {
            _server.Log($"[MuxServer] {request.Method} (no reply wanted) failed: {code}: {message}");
            return;
        }

        Send(MuxFrames.Response(new MuxResponse { Id = request.Id, Error = new MuxError { Code = code, Message = message } }));
    }

    private void Send(MuxOutboundFrame frame)
    {
        try { TryEnqueue(frame); }
        finally { frame.Release(); }
    }
}
```

- [ ] **Step 6: Run** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~Server"`, then the whole Mux.Tests project. Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux tests/Ntilde.Mux.Tests
git commit -m "feat(mux): server with handshake, session registry and byte-budgeted clients"
```

---

### Task 7: `MuxClient` + `MuxClientSession`

**Files:**
- Create: `src/Ntilde.Mux/{MuxClientOptions.cs, MuxClient.cs, MuxClientSession.cs}`
- Create: `tests/Ntilde.Mux.Tests/Support/{ClientPaneModel.cs, FakeMuxServerEnd.cs}`, `tests/Ntilde.Mux.Tests/Client/{MuxClientHandshakeTests.cs, MuxClientSessionTests.cs, MuxClientLimitsTests.cs}`
- Modify: `tests/Ntilde.Mux.Tests/Support/MuxTestHost.cs` (add client helpers)

**Interfaces:**
- Consumes: server (Task 6), contracts (Task 3), `TerminalStateSerializer` (Phase 0).
- Produces:
  - `MuxClientOptions { MinProtocolVersion, MaxProtocolVersion, ClientKind="ntilde", RequestTimeout=30s, AttachLimits, Log }`, `MuxAttachLimits { MaxSnapshotBytes=MaxFrameBytes, MaxCells=1_000_000, MaxScrollbackRows=50_000 }`.
  - `MuxClient : IDisposable` — `static Task<MuxClient> ConnectAsync(Stream, MuxClientOptions? = null, CancellationToken = default)`; `ProtocolVersion`, `ForceConPtyFiltering`, `IsConnected`, `DisconnectReason`, `event Action<string?>? Disconnected`; `ListSessionsAsync`, `SpawnAsync(SpawnParams)`, `KillAsync(Guid)`, `PingAsync`; `MuxClientSession OpenSession(Guid sessionId, string shellCommand = "", string? shellArguments = null)`.
  - `MuxClientSession : ITerminalSession, ITerminalSessionCapabilities` — `AttachAsync(int maxScrollbackRows, MuxPresentation, CancellationToken) → Task<long>` (the snapshot's `StreamSeq`); `UpdatePresentation(MuxPresentation)`; `RefreshSessionInfoAsync`; `ExportFlightRecordingAsync`; `KillAsync`/`Kill()`; `IsAttached`, `AttachedSeq`, `StreamPosition`, `ForceConPtyFiltering`; events `SnapshotReceived(TerminalStateSnapshot)`, `OnOutputReceived(string)`, `StreamResize(int cols, int rows)`, `OnExit(int)`, `Disconnected(string?)`; internal `LastSnapshotByteCount`. `Dispose()` = detach; the session lives on.
  - Test support: `ClientPaneModel`, `FakeMuxServerEnd`, `MuxTestHost.ConnectClientAsync/SpawnAsync/AttachPaneAsync/SettleAsync/AssertPaneMatchesMux`.

- [ ] **Step 1: Test support**

Append to `MuxTestHost`:

```csharp
    public async Task<MuxClient> ConnectClientAsync(MuxClientOptions? options = null)
    {
        MuxClient client = await MuxClient.ConnectAsync(Listener.Connect(), options, TestContext.Current.CancellationToken);
        Own(client);
        return client;
    }

    public static Task<Guid> SpawnAsync(MuxClient client, int cols = 80, int rows = 24) =>
        client.SpawnAsync(new SpawnParams { Command = "scripted", Cols = cols, Rows = rows, Title = "test" }, TestContext.Current.CancellationToken);

    public static async Task<ClientPaneModel> AttachPaneAsync(
        MuxClient client, Guid sessionId, MuxPresentation? presentation = null, int maxScrollbackRows = 10_000)
    {
        MuxClientSession session = client.OpenSession(sessionId, "scripted");
        var pane = new ClientPaneModel(session);
        await session.AttachAsync(maxScrollbackRows, presentation ?? DefaultPresentation, TestContext.Current.CancellationToken);
        return pane;
    }

    /// <summary>
    /// Quiesces one session: ping each client (the server has handled everything it sent), flush the
    /// parse thread (every byte and control item is processed and its frames enqueued), ping again
    /// (every frame enqueued before the pong has been delivered - the reader is sequential).
    /// </summary>
    public async Task SettleAsync(Guid sessionId, params MuxClient[] clients)
    {
        foreach (MuxClient c in clients) await c.PingAsync(TestContext.Current.CancellationToken);
        await Mux(sessionId).FlushAsync();
        foreach (MuxClient c in clients) await c.PingAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Call only after <see cref="SettleAsync"/>: reads both sides with their threads idle.</summary>
    public void AssertPaneMatchesMux(Guid sessionId, ClientPaneModel pane, string because)
    {
        HeadlessTerminalSession mux = Mux(sessionId);
        Assert.Equal(mux.StreamPosition, pane.Session.StreamPosition);
        Ntilde.VT.Tests.StateTransfer.TerminalStateAssert.AssertEquivalent(
            $"[{because}]", mux.Buffer, mux.Parser, pane.Buffer, pane.Parser);
    }
```

`Support/ClientPaneModel.cs`:

```csharp
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Support;

/// <summary>
/// What Phase 2's TerminalPane will do with a MuxClientSession: restore on SnapshotReceived, parse
/// OnOutputReceived, resize on StreamResize - and never send its own parser's replies.
/// </summary>
internal sealed class ClientPaneModel
{
    private readonly object _gate = new();
    private readonly List<string> _events = new();
    private readonly List<string> _responses = new();

    public ClientPaneModel(MuxClientSession session)
    {
        Session = session;
        Buffer = new TerminalBuffer(80, 24);
        Parser = new AnsiParser(Buffer, session.ForceConPtyFiltering) { ImageDecoder = null, AllowNativeKittyGraphics = false };
        Parser.OnResponse = reply => { lock (_gate) _responses.Add(reply); }; // parsed, never sent
        session.SnapshotReceived += OnSnapshot;
        session.OnOutputReceived += OnOutput;
        session.StreamResize += OnResize;
    }

    public MuxClientSession Session { get; }
    public TerminalBuffer Buffer { get; }
    public AnsiParser Parser { get; }
    public TerminalStateSnapshot? LastSnapshot { get; private set; }
    public IReadOnlyList<string> Events { get { lock (_gate) return _events.ToArray(); } }
    public IReadOnlyList<string> Responses { get { lock (_gate) return _responses.ToArray(); } }

    private void OnSnapshot(TerminalStateSnapshot snapshot)
    {
        TerminalStateTransfer.Restore(Buffer, Parser, snapshot);
        lock (_gate)
        {
            LastSnapshot = snapshot;
            _events.Add($"snapshot@{snapshot.StreamSeq}+{snapshot.DecoderTail?.Length ?? 0}");
        }
    }

    private void OnOutput(string text)
    {
        Parser.Process(text);
        lock (_gate) _events.Add("out:" + text);
    }

    private void OnResize(int cols, int rows)
    {
        Buffer.Resize(cols, rows);
        lock (_gate) _events.Add($"resize:{cols}x{rows}");
    }
}
```

`Support/FakeMuxServerEnd.cs` (a hand-driven server for client-side error paths):

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Support;

internal sealed class FakeMuxServerEnd : IDisposable
{
    private FakeMuxServerEnd(Stream clientEnd, Stream serverEnd)
    {
        ClientEnd = clientEnd;
        Raw = new RawMuxConnection(serverEnd);
    }

    public Stream ClientEnd { get; }
    public RawMuxConnection Raw { get; }

    public static FakeMuxServerEnd Create()
    {
        (Stream client, Stream server) = InMemoryDuplexPipe.Create(1 << 20);
        return new FakeMuxServerEnd(client, server);
    }

    public async Task<MuxRequest> ReadRequestAsync()
    {
        using MuxInboundFrame frame = await Raw.ReadAsync() ?? throw new EndOfStreamException();
        Assert.Equal(MuxFrameKind.Request, frame.Kind);
        return MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxRequest);
    }

    public void Reply<T>(long id, T result, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        Raw.Send(MuxFrames.Response(new MuxResponse { Id = id, Result = MuxFrames.ToElement(result, info) }));

    /// <summary>Answers the client's hello with <paramref name="version"/>.</summary>
    public async Task AcceptHelloAsync(int version = 1)
    {
        MuxRequest hello = await ReadRequestAsync();
        Reply(hello.Id, new WelcomeResult { Version = version }, MuxJsonContext.Default.WelcomeResult);
    }

    public static byte[] SnapshotJson(string screen = "", long streamSeq = 0)
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        parser.Process(screen);
        TerminalStateSnapshot s = buffer.ExportState(100);
        s.Parser = parser.ExportState();
        s.DecoderTail = [];
        s.StreamSeq = streamSeq;
        return TerminalStateSerializer.ToBytes(s);
    }

    public void Dispose()
    {
        Raw.Dispose();
        ClientEnd.Dispose();
    }
}
```

- [ ] **Step 2: Write the failing client tests**

`Client/MuxClientHandshakeTests.cs`:

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class MuxClientHandshakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Overlapping_ranges_negotiate_the_highest_common_version_and_report_filtering()
    {
        using var host = new MuxTestHost(new MuxServerOptions { MinProtocolVersion = 1, MaxProtocolVersion = 3, ForceConPtyFiltering = true });
        MuxClient client = await host.ConnectClientAsync(new MuxClientOptions { MinProtocolVersion = 2, MaxProtocolVersion = 5 });

        Assert.Equal(3, client.ProtocolVersion);
        Assert.True(client.ForceConPtyFiltering);
    }

    [Fact]
    public async Task Disjoint_ranges_throw_version_mismatch_and_the_server_drops_the_connection()
    {
        using var host = new MuxTestHost();
        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() =>
            MuxClient.ConnectAsync(host.Listener.Connect(), new MuxClientOptions { MinProtocolVersion = 2, MaxProtocolVersion = 3 }, Ct));

        Assert.Equal(MuxErrorCodes.VersionMismatch, ex.Code);
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the server drops the connection");
    }

    [Fact]
    public async Task A_welcome_outside_the_clients_range_is_refused()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync(version: 9);

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => connect);
        Assert.Equal(MuxErrorCodes.VersionMismatch, ex.Code);
    }
}
```

`Client/MuxClientSessionTests.cs`:

```csharp
using System.Text;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Client;

public sealed class MuxClientSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// A stream built to hurt: SGR, cursor moves, a code point split across chunks, the alt screen,
    /// an OSC 8 link split mid-sequence, a scroll region, and a DA query the mux must answer.
    private static readonly byte[][] HostileChunks =
    [
        "plain "u8.ToArray(), "\x1b[1;31mred\x1b[0m\r\n"u8.ToArray(), [0xE2, 0x82], [0xAC, (byte)'\r', (byte)'\n'],
        "\x1b[5;10Hmoved"u8.ToArray(), "\x1b]8;id=a;https://ex"u8.ToArray(), "ample.com\x1b\\link\x1b]8;;\x1b\\"u8.ToArray(),
        "\x1b[?1049h"u8.ToArray(), "alt screen\x1b[2J\x1b[H"u8.ToArray(), "\x1b[?1049l"u8.ToArray(), "\x1b[3;20r"u8.ToArray(),
        "\x1b[c"u8.ToArray(), "\x1b[r\x1b[999;999H"u8.ToArray(), "end"u8.ToArray(),
    ];

    [Fact]
    public async Task Spawn_attach_stream_the_client_equals_the_mux_after_every_chunk()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        ScriptedTerminalSession fake = host.Fake(id);

        for (int i = 0; i < HostileChunks.Length; i++)
        {
            fake.Emit(HostileChunks[i]);
            await host.SettleAsync(id, client);
            host.AssertPaneMatchesMux(id, pane, $"after chunk {i}");
        }
    }

    [Fact]
    public async Task Attach_reports_the_snapshot_seq_and_StreamPosition_follows_the_stream()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit([(byte)'a', 0xC3]); // ends mid code point: seq 1, tail 1
        await host.Mux(id).FlushAsync();

        MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        long seq = await session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        host.Fake(id).Emit([0xA9]);
        await host.SettleAsync(id, client);

        Assert.Equal(1, seq);
        Assert.Equal(["snapshot@1+1", "out:é"], pane.Events);
        Assert.Equal(3, session.StreamPosition);
    }

    [Fact]
    public async Task Input_round_trips_as_utf8()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");

        session.SendInput("é\r");
        await client.PingAsync(Ct);

        string sent = Assert.Single(host.Fake(id).SentInput);
        Assert.Equal(new byte[] { 0xC3, 0xA9, 0x0D }, Encoding.UTF8.GetBytes(sent));
    }

    [Fact]
    public async Task The_exit_code_surfaces_once()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        var exits = new List<int>();
        pane.Session.OnExit += exits.Add;

        host.Fake(id).Exit(3);
        await host.SettleAsync(id, client);

        Assert.Equal([3], exits);
        Assert.Equal(3, pane.Session.ExitCode);
        Assert.False(pane.Session.IsProcessRunning);
        SessionSummary summary = Assert.Single(await client.ListSessionsAsync(Ct));
        Assert.Equal((false, (int?)3), (summary.Running, summary.ExitCode));
    }

    [Fact]
    public async Task Kill_propagates_Exited_and_removes_the_session()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        ScriptedTerminalSession fake = host.Fake(id);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.Session.OnExit += code => exited.TrySetResult(code);

        await pane.Session.KillAsync(Ct);

        Assert.Equal(-1, await exited.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.True(fake.Disposed);
        Assert.Empty(await client.ListSessionsAsync(Ct));
    }

    [Fact]
    public async Task Capabilities_and_session_info()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");

        ITerminalSessionCapabilities caps = session;
        Assert.True(caps.AnswersDeviceQueries);
        Assert.True(caps.OrdersResizeInStream);
        // Set first: the read below also starts a background refresh, and a refresh that probed
        // before the flag flipped could land after the explicit one and overwrite it.
        host.Fake(id).HasActiveChildProcesses = true;
        Assert.False(session.HasActiveChildProcesses); // the cached initial value; never blocks

        SessionInfoResult info = await session.RefreshSessionInfoAsync(Ct);

        Assert.True(info.HasActiveChildProcesses);
        Assert.True(session.HasActiveChildProcesses);
    }

    [Fact]
    public async Task Resize_sends_a_request_and_raises_StreamResize_only_at_the_event()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("x");
        await host.SettleAsync(id, client);

        int resizeHandlerThread = -1;
        pane.Session.StreamResize += (_, _) => resizeHandlerThread = Environment.CurrentManagedThreadId;
        pane.Session.Resize(100, 30);
        await host.SettleAsync(id, client);

        // Exactly one resize, raised by the delivery thread (never by Resize() on the caller).
        Assert.Equal(["snapshot@0+0", "out:x", "resize:100x30"], pane.Events);
        Assert.NotEqual(Environment.CurrentManagedThreadId, resizeHandlerThread);
        host.AssertPaneMatchesMux(id, pane, "after resize");
    }

    [Fact]
    public async Task Output_with_a_seq_gap_disconnects_with_protocol_error()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        MuxClientSession session = client.OpenSession(id);
        var pane = new ClientPaneModel(session);

        Task<long> attach = session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, FakeMuxServerEnd.SnapshotJson()));
        await attach;
        fake.Raw.Send(MuxFrames.Output(id, 5, "gap"u8)); // expected 0

        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
        Assert.Equal(MuxErrorCodes.ProtocolError, client.DisconnectReason);
        Assert.DoesNotContain(pane.Events, e => e.StartsWith("out:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_malformed_snapshot_fails_the_attach_and_touches_no_buffer()
    {
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync();
        using MuxClient client = await connect;
        Guid id = Guid.NewGuid();
        var pane = new ClientPaneModel(client.OpenSession(id));

        Task<long> attach = pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();
        fake.Raw.Send(MuxFrames.Snapshot(request.Id, id, 0, "{not a snapshot"u8));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => attach);
        Assert.True(ex is MuxProtocolException { Code: MuxErrorCodes.ProtocolError } or IOException, ex.ToString());
        Assert.Empty(pane.Events);
        await TestWait.UntilAsync(() => !client.IsConnected, "the client drops the connection");
    }
}
```

`Client/MuxClientLimitsTests.cs` (closes #473):

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class MuxClientLimitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(MuxTestHost Host, MuxClient Client, Guid Id, ClientPaneModel Pane)> SetupAsync(
        MuxServerOptions? server = null, MuxAttachLimits? limits = null)
    {
        var host = new MuxTestHost(server);
        MuxClient client = await host.ConnectClientAsync(new MuxClientOptions { AttachLimits = limits ?? new MuxAttachLimits() });
        Guid id = await MuxTestHost.SpawnAsync(client);
        var pane = new ClientPaneModel(client.OpenSession(id, "scripted"));
        return (host, client, id, pane);
    }

    [Fact]
    public async Task The_server_refuses_a_snapshot_over_its_ceiling()
    {
        (MuxTestHost host, _, _, ClientPaneModel pane) = await SetupAsync(server: new MuxServerOptions { MaxSnapshotBytes = 1024, ForceConPtyFiltering = false });
        using (host)
        {
            var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct));
            Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
            Assert.Empty(pane.Events);
        }
    }

    [Fact]
    public async Task The_client_refuses_a_snapshot_over_its_byte_ceiling_and_detaches()
    {
        (MuxTestHost host, _, Guid id, ClientPaneModel pane) = await SetupAsync(limits: new MuxAttachLimits { MaxSnapshotBytes = 1024 });
        using (host)
        {
            var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct));
            Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
            Assert.Empty(pane.Events);
            await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the refusing client is detached server-side");
        }
    }

    [Fact]
    public async Task The_client_refuses_too_many_cells()
    {
        (MuxTestHost host, _, _, ClientPaneModel pane) = await SetupAsync(limits: new MuxAttachLimits { MaxCells = (80 * 24) - 1 });
        using (host)
        {
            var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct));
            Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
            Assert.Empty(pane.Events);
        }
    }

    [Fact]
    public async Task The_client_refuses_too_much_scrollback()
    {
        (MuxTestHost host, _, Guid id, ClientPaneModel pane) = await SetupAsync(limits: new MuxAttachLimits { MaxScrollbackRows = 10 });
        using (host)
        {
            host.Fake(id).Emit(string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i}\r\n")));
            await host.Mux(id).FlushAsync();

            var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => pane.Session.AttachAsync(1000, MuxTestHost.DefaultPresentation, Ct));
            Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
            Assert.Empty(pane.Events);
        }
    }

    [Fact]
    public async Task The_server_clamps_the_requested_scrollback()
    {
        (MuxTestHost host, _, Guid id, ClientPaneModel pane) = await SetupAsync(server: new MuxServerOptions { MaxAttachScrollbackRows = 5, ForceConPtyFiltering = false });
        using (host)
        {
            host.Fake(id).Emit(string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i}\r\n")));
            await host.Mux(id).FlushAsync();

            await pane.Session.AttachAsync(1000, MuxTestHost.DefaultPresentation, Ct);

            Assert.Equal(5, pane.LastSnapshot!.ScrollbackRowCount);
        }
    }
}
```

- [ ] **Step 3: Run to verify they fail** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~Client"`. Expected: build FAILS.

- [ ] **Step 4: Implement the options** — `MuxClientOptions.cs`:

```csharp
using Ntilde.Mux.Contracts;

namespace Ntilde.Mux;

public sealed class MuxClientOptions
{
    public int MinProtocolVersion { get; init; } = MuxProtocol.MinSupportedVersion;
    public int MaxProtocolVersion { get; init; } = MuxProtocol.MaxSupportedVersion;
    public string ClientKind { get; init; } = "ntilde";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public MuxAttachLimits AttachLimits { get; init; } = new();
    public Action<string>? Log { get; init; }
}

/// <summary>
/// What an attaching client is willing to adopt (closes #473). A rejection ceiling, never a clamp:
/// Cols is the base of every side-table key, so shrinking it would re-point entries, not refuse them.
/// Checked before <c>SnapshotReceived</c>, which is the only path to <c>TerminalStateTransfer.Restore</c>.
/// </summary>
public sealed class MuxAttachLimits
{
    /// <summary>Checked before deserialization.</summary>
    public int MaxSnapshotBytes { get; init; } = MuxProtocol.MaxFrameBytes;

    /// <summary>Visible grid, cols × rows.</summary>
    public long MaxCells { get; init; } = 1_000_000;

    public int MaxScrollbackRows { get; init; } = 50_000;
}
```

- [ ] **Step 5: Implement `MuxClient`** — `MuxClient.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Ntilde.Mux.Contracts;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>
/// One connection to a <see cref="MuxServer"/>. Its reader thread is the delivery thread for every
/// session opened on it: snapshots, output, resizes and exits are raised there, strictly in frame
/// order. RPC continuations are forced asynchronous so no awaiting caller ever runs on it.
/// </summary>
public sealed class MuxClient : IDisposable
{
    private readonly Stream _stream;
    private readonly MuxClientOptions _options;
    private readonly Thread _readerThread;
    private readonly Thread _senderThread;
    private readonly BlockingCollection<MuxOutboundFrame> _outbound = new(boundedCapacity: 1024);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<MuxResponse>> _pending = new();
    private readonly ConcurrentDictionary<long, MuxClientSession> _pendingAttaches = new();
    private readonly ConcurrentDictionary<Guid, MuxClientSession> _sessions = new();
    private long _nextId;
    private int _disconnected;
    private string? _disconnectReason;

    private MuxClient(Stream stream, MuxClientOptions options)
    {
        _stream = stream;
        _options = options;
        _readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "MuxClientRead" };
        _senderThread = new Thread(SendLoop) { IsBackground = true, Name = "MuxClientSend" };
        _senderThread.Start();
        _readerThread.Start();
    }

    public int ProtocolVersion { get; private set; }

    /// <summary>From Welcome. Construct the pane's parser with this so both halves of a snapshot parse identically.</summary>
    public bool ForceConPtyFiltering { get; private set; }

    public bool IsConnected => Volatile.Read(ref _disconnected) == 0;
    public string? DisconnectReason => Volatile.Read(ref _disconnectReason);
    public event Action<string?>? Disconnected;

    internal bool IsOnDeliveryThread => Thread.CurrentThread == _readerThread;

    public static async Task<MuxClient> ConnectAsync(Stream stream, MuxClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new MuxClientOptions();
        var client = new MuxClient(stream, options);
        try
        {
            WelcomeResult welcome = await client.RequestAsync(
                MuxMethods.Hello,
                new HelloParams { MinVersion = options.MinProtocolVersion, MaxVersion = options.MaxProtocolVersion, ClientKind = options.ClientKind },
                MuxJsonContext.Default.HelloParams,
                MuxJsonContext.Default.WelcomeResult,
                cancellationToken).ConfigureAwait(false);
            if (welcome.Version < options.MinProtocolVersion || welcome.Version > options.MaxProtocolVersion)
            {
                throw new MuxProtocolException(MuxErrorCodes.VersionMismatch,
                    $"Server chose protocol {welcome.Version}, outside this client's {options.MinProtocolVersion}..{options.MaxProtocolVersion}.");
            }

            client.ProtocolVersion = welcome.Version;
            client.ForceConPtyFiltering = welcome.ForceConPtyFiltering;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync(MuxMethods.ListSessions, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty,
            MuxJsonContext.Default.ListSessionsResult, cancellationToken).ConfigureAwait(false)).Sessions;

    public async Task<Guid> SpawnAsync(SpawnParams request, CancellationToken cancellationToken = default) =>
        (await RequestAsync(MuxMethods.Spawn, request, MuxJsonContext.Default.SpawnParams,
            MuxJsonContext.Default.SpawnResult, cancellationToken).ConfigureAwait(false)).SessionId;

    public Task KillAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        RequestAsync(MuxMethods.Kill, new SessionIdParams { SessionId = sessionId }, MuxJsonContext.Default.SessionIdParams,
            MuxJsonContext.Default.MuxEmpty, cancellationToken);

    public Task PingAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty, MuxJsonContext.Default.MuxEmpty, cancellationToken);

    /// <summary>
    /// An unattached session. Wire its events, then call <see cref="MuxClientSession.AttachAsync"/>:
    /// nothing is raised before a handler can exist, so nothing needs buffering (spec §9.6).
    /// </summary>
    public MuxClientSession OpenSession(Guid sessionId, string shellCommand = "", string? shellArguments = null)
    {
        var session = new MuxClientSession(this, sessionId, shellCommand, shellArguments);
        if (!_sessions.TryAdd(sessionId, session))
        {
            throw new InvalidOperationException($"Session {sessionId} is already open on this client; dispose it first.");
        }

        return session;
    }

    internal Task<TResult> RequestAsync<TParams, TResult>(
        string method, TParams parameters, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
        where TResult : class
    {
        long id = Interlocked.Increment(ref _nextId);
        return RequestCoreAsync(id, method, MuxFrames.ToElement(parameters, paramsInfo), resultInfo, cancellationToken);
    }

    internal async Task<long> AttachAsync(MuxClientSession session, int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken)
    {
        long id = Interlocked.Increment(ref _nextId);
        _pendingAttaches[id] = session;
        try
        {
            JsonElement p = MuxFrames.ToElement(
                new AttachParams { SessionId = session.Id, MaxScrollbackRows = maxScrollbackRows, Presentation = presentation },
                MuxJsonContext.Default.AttachParams);
            MuxResponse response = await SendAndAwaitAsync(id, MuxMethods.Attach, p, cancellationToken).ConfigureAwait(false);
            if (response.Error is { } error) throw new MuxProtocolException(error.Code, error.Message);
            return session.AttachedSeq;
        }
        finally
        {
            _pendingAttaches.TryRemove(id, out _);
        }
    }

    /// <summary>Fire-and-forget request (id 0: the server sends no response).</summary>
    internal void PostRequest<T>(string method, T parameters, JsonTypeInfo<T> typeInfo) =>
        Post(MuxFrames.Request(new MuxRequest { Id = 0, Method = method, Params = MuxFrames.ToElement(parameters, typeInfo) }));

    internal void SendInput(Guid sessionId, string text) => Post(MuxFrames.Input(sessionId, Encoding.UTF8.GetBytes(text)));

    internal void SendResize(Guid sessionId, int cols, int rows, MuxPresentation? presentation) =>
        PostRequest(MuxMethods.Resize, new ResizeParams { SessionId = sessionId, Cols = cols, Rows = rows, Presentation = presentation },
            MuxJsonContext.Default.ResizeParams);

    internal void Detach(MuxClientSession session)
    {
        _sessions.TryRemove(new KeyValuePair<Guid, MuxClientSession>(session.Id, session));
        PostRequest(MuxMethods.Detach, new SessionIdParams { SessionId = session.Id }, MuxJsonContext.Default.SessionIdParams);
    }

    public void Dispose()
    {
        OnDisconnected("client_disposed");
        if (Thread.CurrentThread != _readerThread) _readerThread.Join(TimeSpan.FromSeconds(5));
        if (Thread.CurrentThread != _senderThread) _senderThread.Join(TimeSpan.FromSeconds(5));
    }

    private async Task<TResult> RequestCoreAsync<TResult>(long id, string method, JsonElement parameters, JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken)
        where TResult : class
    {
        MuxResponse response = await SendAndAwaitAsync(id, method, parameters, cancellationToken).ConfigureAwait(false);
        if (response.Error is { } error) throw new MuxProtocolException(error.Code, error.Message);
        return MuxFrames.ParseParams(response.Result, resultInfo);
    }

    private async Task<MuxResponse> SendAndAwaitAsync(long id, string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<MuxResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            if (!IsConnected) throw Closed(); // after registering: OnDisconnected sets the flag before failing _pending
            Enqueue(MuxFrames.Request(new MuxRequest { Id = id, Method = method, Params = parameters }));
            return await tcs.Task.WaitAsync(_options.RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void Enqueue(MuxOutboundFrame frame)
    {
        try
        {
            _outbound.Add(frame);
        }
        catch (InvalidOperationException)
        {
            frame.Release();
            throw Closed();
        }
    }

    private void Post(MuxOutboundFrame frame)
    {
        try
        {
            _outbound.Add(frame);
        }
        catch (InvalidOperationException)
        {
            frame.Release(); // disconnected: fire-and-forget has nobody to tell
        }
    }

    private IOException Closed() => new($"The mux connection is closed ({DisconnectReason ?? "disconnected"}).");

    private void SendLoop()
    {
        try
        {
            foreach (MuxOutboundFrame frame in _outbound.GetConsumingEnumerable())
            {
                try { frame.WriteTo(_stream); }
                finally { frame.Release(); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            OnDisconnected("disconnected");
        }

        while (_outbound.TryTake(out MuxOutboundFrame? left)) left.Release();
    }

    private void ReadLoop()
    {
        string? reason = null;
        try
        {
            while (true)
            {
                MuxInboundFrame? frame = MuxFrameReader.Read(_stream);
                if (frame is null) break;
                using (frame)
                {
                    Dispatch(frame);
                }
            }
        }
        catch (MuxProtocolException ex)
        {
            reason = ex.Code;
            _options.Log?.Invoke($"[MuxClient] protocol error: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            reason = "disconnected";
        }
        catch (Exception ex)
        {
            reason = MuxErrorCodes.Internal;
            _options.Log?.Invoke($"[MuxClient] delivery failed: {ex}");
        }

        OnDisconnected(reason);
    }

    private void Dispatch(MuxInboundFrame frame)
    {
        switch (frame.Kind)
        {
            case MuxFrameKind.Response:
                OnResponse(MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxResponse));
                break;
            case MuxFrameKind.Notification:
                OnNotification(MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxNotification));
                break;
            case MuxFrameKind.Output:
            {
                if (!MuxFrames.TryParseOutput(frame.Payload, out Guid id, out long seq, out ReadOnlySpan<byte> data)) throw Malformed(frame.Kind);
                if (_sessions.TryGetValue(id, out MuxClientSession? session)) session.DeliverOutput(seq, data);
                break;
            }

            case MuxFrameKind.ResizeEvent:
            {
                if (!MuxFrames.TryParseResizeEvent(frame.Payload, out Guid id, out long seq, out int cols, out int rows)) throw Malformed(frame.Kind);
                if (_sessions.TryGetValue(id, out MuxClientSession? session)) session.DeliverResize(seq, cols, rows);
                break;
            }

            case MuxFrameKind.Snapshot:
                OnSnapshot(frame.Payload);
                break;
            default:
                throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"A server may not send {frame.Kind} frames.");
        }
    }

    private static MuxProtocolException Malformed(MuxFrameKind kind) =>
        new(MuxErrorCodes.ProtocolError, $"Malformed {kind} frame.");

    private void OnResponse(MuxResponse response)
    {
        if (response.Id == 0)
        {
            // Connection-level: the server is about to close. Keep the reason for DisconnectReason.
            if (response.Error is { } error) Interlocked.CompareExchange(ref _disconnectReason, error.Code, null);
            return;
        }

        if (_pending.TryGetValue(response.Id, out TaskCompletionSource<MuxResponse>? tcs)) tcs.TrySetResult(response);
    }

    private void OnNotification(MuxNotification notification)
    {
        if (notification.Method == MuxMethods.Exited)
        {
            ExitedNotification exited = MuxFrames.ParseParams(notification.Params, MuxJsonContext.Default.ExitedNotification);
            if (_sessions.TryGetValue(exited.SessionId, out MuxClientSession? session)) session.DeliverExited(exited.ExitCode);
        }

        // Unknown notifications are ignored: a newer server may send more than this client knows.
    }

    private void OnSnapshot(ReadOnlySpan<byte> payload)
    {
        if (!MuxFrames.TryParseSnapshot(payload, out long requestId, out Guid sessionId, out long seq, out ReadOnlySpan<byte> json))
        {
            throw Malformed(MuxFrameKind.Snapshot);
        }

        if (!_pendingAttaches.TryRemove(requestId, out MuxClientSession? session) || session.Id != sessionId)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Unsolicited snapshot for {sessionId} (request {requestId}).");
        }

        _pending.TryGetValue(requestId, out TaskCompletionSource<MuxResponse>? tcs);
        TerminalStateSnapshot snapshot;
        try
        {
            snapshot = DecodeSnapshot(json);
        }
        catch (MuxProtocolException ex)
        {
            tcs?.TrySetResult(new MuxResponse { Id = requestId, Error = new MuxError { Code = ex.Code, Message = ex.Message } });
            if (ex.Code != MuxErrorCodes.SnapshotTooLarge) throw; // malformed: this connection is no longer trustworthy

            // Too large is a policy refusal, not corruption: the server already subscribed us, so undo that.
            PostRequest(MuxMethods.Detach, new SessionIdParams { SessionId = sessionId }, MuxJsonContext.Default.SessionIdParams);
            return;
        }

        session.DeliverSnapshot(seq, snapshot, json.Length);
        tcs?.TrySetResult(new MuxResponse { Id = requestId });
    }

    private TerminalStateSnapshot DecodeSnapshot(ReadOnlySpan<byte> json)
    {
        MuxAttachLimits limits = _options.AttachLimits;
        if (json.Length > limits.MaxSnapshotBytes)
        {
            throw new MuxProtocolException(MuxErrorCodes.SnapshotTooLarge,
                $"Snapshot is {json.Length} bytes; this client accepts at most {limits.MaxSnapshotBytes}.");
        }

        TerminalStateSnapshot snapshot;
        try
        {
            snapshot = TerminalStateSerializer.FromBytes(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Malformed snapshot: {ex.Message}", ex);
        }

        if (snapshot.Cols <= 0 || snapshot.Rows <= 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Snapshot geometry {snapshot.Cols}x{snapshot.Rows} is invalid.");
        }

        if ((long)snapshot.Cols * snapshot.Rows > limits.MaxCells)
        {
            throw new MuxProtocolException(MuxErrorCodes.SnapshotTooLarge,
                $"Snapshot grid {snapshot.Cols}x{snapshot.Rows} exceeds this client's {limits.MaxCells}-cell ceiling.");
        }

        if (snapshot.ScrollbackRowCount > limits.MaxScrollbackRows)
        {
            throw new MuxProtocolException(MuxErrorCodes.SnapshotTooLarge,
                $"Snapshot carries {snapshot.ScrollbackRowCount} scrollback rows; this client accepts at most {limits.MaxScrollbackRows}.");
        }

        return snapshot;
    }

    private void OnDisconnected(string? reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        Interlocked.CompareExchange(ref _disconnectReason, reason ?? "disconnected", null);
        _outbound.CompleteAdding();
        try { _stream.Dispose(); }
        catch (IOException) { }

        IOException closed = Closed();
        foreach (TaskCompletionSource<MuxResponse> tcs in _pending.Values) tcs.TrySetException(closed);
        foreach (MuxClientSession session in _sessions.Values) session.DeliverDisconnected(DisconnectReason);
        Disconnected?.Invoke(DisconnectReason);
    }
}
```

- [ ] **Step 6: Implement `MuxClientSession`** — `MuxClientSession.cs`:

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>
/// A pane's view of a multiplexed session. The pane keeps its own parser and buffer: restore on
/// <see cref="SnapshotReceived"/>, parse <see cref="OnOutputReceived"/>, resize on
/// <see cref="StreamResize"/> - exactly where the event sits in the stream, never ahead of it
/// (<see cref="OrdersResizeInStream"/>). Device queries are answered by the mux
/// (<see cref="AnswersDeviceQueries"/>). All events are raised on the owning client's reader thread.
/// </summary>
public sealed class MuxClientSession : ITerminalSession, ITerminalSessionCapabilities
{
    private const int MaxIncompleteUtf8Bytes = 3;
    private static readonly long SessionInfoMaxAgeMs = 1000;

    private readonly MuxClient _client;
    private Utf8ChunkDecoder _decoder = new();
    private char[] _chars = new char[Utf8ChunkDecoder.GetMaxCharCount(4096)];
    private long _expectedOffset = -1;   // next raw offset this session accepts; -1 = not attached
    private long _deliveredOffset;       // advanced after handlers return (what tests wait on)
    private long _attachedSeq = -1;
    private MuxPresentation? _presentation;
    private int _exited;
    private int _exitCode;
    private int _exitNotified;
    private int _hasActiveChildren;
    private long _sessionInfoAtMs = long.MinValue / 2;
    private int _sessionInfoInFlight;
    private int _recording;
    private int _flightRecording;
    private int _disposed;

    internal MuxClientSession(MuxClient client, Guid sessionId, string shellCommand, string? shellArguments)
    {
        _client = client;
        Id = sessionId;
        ShellCommand = shellCommand;
        ShellArguments = shellArguments;
    }

    public Guid Id { get; }
    public string ShellCommand { get; }
    public string? ShellArguments { get; }
    public bool AnswersDeviceQueries => true;
    public bool OrdersResizeInStream => true;
    public bool ForceConPtyFiltering => _client.ForceConPtyFiltering;
    public bool IsAttached => Interlocked.Read(ref _expectedOffset) >= 0;
    public long AttachedSeq => Interlocked.Read(ref _attachedSeq);

    /// <summary>Raw stream offset whose effects have been delivered to every handler.</summary>
    public long StreamPosition => Interlocked.Read(ref _deliveredOffset);

    internal int LastSnapshotByteCount { get; private set; }

    public event Action<TerminalStateSnapshot>? SnapshotReceived;
    public event Action<string>? OnOutputReceived;
    public event Action<int, int>? StreamResize;
    public event Action<int>? OnExit;
    public event Action<string?>? Disconnected;

    /// <summary>Returns the snapshot's <c>StreamSeq</c>. <see cref="SnapshotReceived"/> has fired by then.</summary>
    public Task<long> AttachAsync(int maxScrollbackRows, MuxPresentation presentation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _presentation = presentation;
        return _client.AttachAsync(this, maxScrollbackRows, presentation, cancellationToken);
    }

    public void SendInput(string input)
    {
        if (string.IsNullOrEmpty(input) || Volatile.Read(ref _disposed) != 0) return;
        _client.SendInput(Id, input);
    }

    /// <summary>Requests a resize. Does not raise <see cref="StreamResize"/>: that comes back in-stream.</summary>
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || Volatile.Read(ref _disposed) != 0) return;
        MuxPresentation? presentation = _presentation is { } current ? current with { Cols = cols, Rows = rows } : null;
        if (presentation is not null) _presentation = presentation;
        _client.SendResize(Id, cols, rows, presentation);
    }

    /// <summary>Pushes new cell metrics / colours (theme or font change). Latest client wins.</summary>
    public void UpdatePresentation(MuxPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _presentation = presentation;
        _client.SendResize(Id, presentation.Cols, presentation.Rows, presentation);
    }

    public bool IsProcessRunning => Volatile.Read(ref _exited) == 0;
    public int? ExitCode => Volatile.Read(ref _exited) != 0 ? Volatile.Read(ref _exitCode) : null;

    /// <summary>The last probed value; a stale read kicks off a refresh and returns immediately.</summary>
    public bool HasActiveChildProcesses
    {
        get
        {
            RefreshSessionInfoIfStale();
            return Volatile.Read(ref _hasActiveChildren) != 0;
        }
    }

    public async Task<SessionInfoResult> RefreshSessionInfoAsync(CancellationToken cancellationToken = default)
    {
        SessionInfoResult info = await _client.RequestAsync(MuxMethods.SessionInfo, new SessionIdParams { SessionId = Id },
            MuxJsonContext.Default.SessionIdParams, MuxJsonContext.Default.SessionInfoResult, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _hasActiveChildren, info.HasActiveChildProcesses ? 1 : 0);
        Interlocked.Exchange(ref _sessionInfoAtMs, Environment.TickCount64);
        return info;
    }

    public bool IsRecording => Volatile.Read(ref _recording) != 0;

    /// <summary>The path is on the mux's machine (the same one, in Phase 1-3).</summary>
    public void StartRecording(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        Volatile.Write(ref _recording, 1);
        _client.PostRequest(MuxMethods.StartRecording, new StartRecordingParams { SessionId = Id, Path = filePath }, MuxJsonContext.Default.StartRecordingParams);
    }

    public void StopRecording()
    {
        Volatile.Write(ref _recording, 0);
        _client.PostRequest(MuxMethods.StopRecording, new SessionIdParams { SessionId = Id }, MuxJsonContext.Default.SessionIdParams);
    }

    public bool IsFlightRecording => Volatile.Read(ref _flightRecording) != 0;

    public void EnableFlightRecording(long maxTotalBytes)
    {
        Volatile.Write(ref _flightRecording, 1);
        _client.PostRequest(MuxMethods.EnableFlightRecording, new EnableFlightRecordingParams { SessionId = Id, MaxBytes = maxTotalBytes },
            MuxJsonContext.Default.EnableFlightRecordingParams);
    }

    public void DisableFlightRecording()
    {
        Volatile.Write(ref _flightRecording, 0);
        _client.PostRequest(MuxMethods.DisableFlightRecording, new SessionIdParams { SessionId = Id }, MuxJsonContext.Default.SessionIdParams);
    }

    public Task<ExportFlightResult> ExportFlightRecordingAsync(CancellationToken cancellationToken = default) =>
        _client.RequestAsync(MuxMethods.ExportFlight, new SessionIdParams { SessionId = Id },
            MuxJsonContext.Default.SessionIdParams, MuxJsonContext.Default.ExportFlightResult, cancellationToken);

    /// <summary>
    /// Synchronous by interface. Blocking is safe because responses complete on the reader thread
    /// with continuations forced asynchronous - which is also why calling it FROM a delivery handler
    /// (on that reader thread) would deadlock, and is refused.
    /// </summary>
    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
    {
        if (_client.IsOnDeliveryThread)
        {
            throw new InvalidOperationException("TryExportFlightRecording cannot run on the mux delivery thread.");
        }

        try
        {
            ExportFlightResult result = ExportFlightRecordingAsync().GetAwaiter().GetResult();
            if (!result.Exported || result.Bytes is null)
            {
                info = default;
                return false;
            }

            File.WriteAllBytes(filePath, result.Bytes);
            info = new FlightExportInfo(result.EventCount, result.FirstEventMs, result.LastEventMs, result.TruncatedAtStart);
            return true;
        }
        catch (Exception ex) when (ex is IOException or MuxProtocolException or TimeoutException)
        {
            info = default;
            return false;
        }
    }

    public Task KillAsync(CancellationToken cancellationToken = default) => _client.KillAsync(Id, cancellationToken);

    public void Kill() => _client.PostRequest(MuxMethods.Kill, new SessionIdParams { SessionId = Id }, MuxJsonContext.Default.SessionIdParams);

    /// <summary>Detaches. The session keeps running in the mux; use <see cref="Kill"/> to end it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _expectedOffset, -1);
        _client.Detach(this);
    }

    internal void DeliverSnapshot(long seq, TerminalStateSnapshot snapshot, int byteCount)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (seq != snapshot.StreamSeq)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Snapshot frame says seq {seq}; its payload says {snapshot.StreamSeq}.");
        }

        byte[] tail = snapshot.DecoderTail ?? [];
        if (tail.Length > MaxIncompleteUtf8Bytes)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, $"Decoder tail of {tail.Length} bytes cannot be an incomplete UTF-8 sequence.");
        }

        // Seed a fresh decoder with the pending bytes: they complete with the first Output's bytes.
        var decoder = new Utf8ChunkDecoder();
        if (tail.Length > 0 && decoder.Decode(tail, _chars) != 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError, "Decoder tail decoded to text; it must be an incomplete UTF-8 prefix.");
        }

        _decoder = decoder;
        long next = snapshot.StreamSeq + tail.Length;
        LastSnapshotByteCount = byteCount;
        Interlocked.Exchange(ref _attachedSeq, snapshot.StreamSeq);
        Interlocked.Exchange(ref _expectedOffset, next);
        SnapshotReceived?.Invoke(snapshot);
        Interlocked.Exchange(ref _deliveredOffset, next);
    }

    internal void DeliverOutput(long seq, ReadOnlySpan<byte> data)
    {
        long expected = Interlocked.Read(ref _expectedOffset);
        if (expected < 0) return; // not attached, or detached with frames still in flight
        if (seq != expected)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError,
                $"Session {Id}: output at stream offset {seq}, expected {expected}. Healing a gap here would silently desynchronise the pane.");
        }

        long next = expected + data.Length;
        if (Interlocked.CompareExchange(ref _expectedOffset, next, expected) != expected) return; // detached meanwhile

        int maxChars = Utf8ChunkDecoder.GetMaxCharCount(data.Length);
        if (_chars.Length < maxChars) _chars = new char[maxChars];
        int count = _decoder.Decode(data, _chars);
        if (count > 0) OnOutputReceived?.Invoke(new string(_chars, 0, count));
        Interlocked.Exchange(ref _deliveredOffset, next);
    }

    internal void DeliverResize(long seq, int cols, int rows)
    {
        long expected = Interlocked.Read(ref _expectedOffset);
        if (expected < 0) return;
        if (seq != expected || cols <= 0 || rows <= 0)
        {
            throw new MuxProtocolException(MuxErrorCodes.ProtocolError,
                $"Session {Id}: resize {cols}x{rows} at offset {seq}, expected offset {expected}.");
        }

        StreamResize?.Invoke(cols, rows);
    }

    internal void DeliverExited(int exitCode)
    {
        Volatile.Write(ref _exitCode, exitCode);
        Volatile.Write(ref _exited, 1);
        if (Interlocked.Exchange(ref _exitNotified, 1) == 0) OnExit?.Invoke(exitCode);
    }

    internal void DeliverDisconnected(string? reason)
    {
        Interlocked.Exchange(ref _expectedOffset, -1);
        Disconnected?.Invoke(reason);
    }

    private void RefreshSessionInfoIfStale()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _sessionInfoAtMs) < SessionInfoMaxAgeMs) return;
        if (!_client.IsConnected || Interlocked.Exchange(ref _sessionInfoInFlight, 1) != 0) return;
        _ = RefreshInBackgroundAsync();
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshSessionInfoAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or MuxProtocolException or TimeoutException)
        {
            // The cached value stands; the next read retries.
        }
        finally
        {
            Volatile.Write(ref _sessionInfoInFlight, 0);
        }
    }
}
```

- [ ] **Step 7: Run** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~Client"`, then all of `tests/Ntilde.Mux.Tests`. Expected: all PASS. `scripts/build.ps1 build src/Ntilde.Mux -c Release` → 0 warnings.

- [ ] **Step 8: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add src/Ntilde.Mux tests/Ntilde.Mux.Tests
git commit -m "feat(mux): client and client session with in-stream delivery and attach limits

Closes #473."
```

---

### Task 8: End-to-end scenario suite

Everything below goes through the real server, real client and the in-memory transport. These are the task's §4 coverage items that Task 7 did not already close.

**Files:**
- Create: `tests/Ntilde.Mux.Tests/Scenarios/{MidStreamAttachTests.cs, ResizeOrderingTests.cs, MultiClientTests.cs, DetachReattachTests.cs, SlowClientTests.cs, FlightExportTests.cs, AttachLatencyTests.cs}`

**Interfaces:**
- Consumes: everything from Tasks 5-7 (`MuxTestHost`, `ClientPaneModel`, `RawMuxConnection`, `StreamCuts`, `ParityCorpus`, `TerminalStateAssert`).
- Produces: nothing new in production code. If a scenario exposes a divergence, fix it in `src/Ntilde.Mux` **at the mechanism**, add the smallest regression test next to the component that owns it, and record it for the PR's "divergences found" section.

- [ ] **Step 1: Write the scenarios**

`Scenarios/MidStreamAttachTests.cs`:

```csharp
using Ntilde.Mux.Tests.Support;
using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.Mux.Tests.Scenarios;

/// <summary>
/// The Phase 0 corpus, attached mid-stream through the wire, at cuts inside escape sequences and
/// inside UTF-8 code points, under both ConPTY filtering modes.
/// </summary>
public sealed class MidStreamAttachTests
{
    public static TheoryData<string, bool> Streams()
    {
        var data = new TheoryData<string, bool>();
        foreach ((string name, _) in ParityCorpus.All())
        {
            data.Add(name, false);
            data.Add(name, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public async Task Attaching_mid_stream_converges_on_the_mux(string name, bool forceConPtyFiltering)
    {
        byte[] corpus = ParityCorpus.All().Single(c => c.Name == name).Bytes;
        foreach (int cut in StreamCuts.Interesting(corpus))
        {
            using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = forceConPtyFiltering });
            MuxClient client = await host.ConnectClientAsync();
            Guid id = await MuxTestHost.SpawnAsync(client);
            host.Fake(id).EmitInChunks(corpus.AsSpan(0, cut), chunkSize: 7);
            await host.Mux(id).FlushAsync();

            ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
            host.Fake(id).EmitInChunks(corpus.AsSpan(cut), chunkSize: 7);
            await host.SettleAsync(id, client);

            host.AssertPaneMatchesMux(id, pane, $"{name} @ {cut}, conpty={forceConPtyFiltering}");
        }
    }

    [Fact]
    public async Task An_attach_inside_a_code_point_carries_the_pending_bytes()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit([0xF0, 0x9F]); // first half of U+1F600
        await host.Mux(id).FlushAsync();

        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit([0x98, 0x80]);
        await host.SettleAsync(id, client);

        Assert.Equal(["snapshot@0+2", "out:\U0001F600"], pane.Events);
        host.AssertPaneMatchesMux(id, pane, "emoji split across the attach");
    }
}
```

`Scenarios/ResizeOrderingTests.cs`:

```csharp
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;
using Ntilde.VT.Tests.StateTransfer;
using Xunit.Sdk;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class ResizeOrderingTests
{
    [Fact]
    public async Task Resizes_interleaved_with_flowing_output_converge()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        (int Cols, int Rows)[] sizes = [(100, 30), (40, 10), (132, 43), (80, 24), (61, 17)];

        for (int i = 0; i < 200; i++)
        {
            host.Fake(id).Emit($"\x1b[{(i % 20) + 1};{(i % 70) + 1}Hline {i} \x1b[3{i % 8}mcolour\x1b[0m wrapping text that is long enough to wrap\r\n");
            if (i % 40 == 7) pane.Session.Resize(sizes[i / 40].Cols, sizes[i / 40].Rows);
        }

        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, pane, "resize storm");
    }

    /// <summary>
    /// The resize arrives exactly between the output it separates - and a client that applied it
    /// when it asked (eagerly) would end up somewhere else. Proven by replaying the recorded
    /// events both ways from the same snapshot.
    /// </summary>
    [Fact]
    public async Task StreamResize_sits_between_the_output_it_separates_and_eager_resizing_diverges()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        const string A = "\x1b[1;80HX"; // X in the last column of an 80-wide row
        const string B = "Y";

        host.Fake(id).Emit(A);
        await host.SettleAsync(id, client);
        pane.Session.Resize(40, 24);
        await host.SettleAsync(id, client);
        host.Fake(id).Emit(B);
        await host.SettleAsync(id, client);

        Assert.Equal(["snapshot@0+0", "out:" + A, "resize:40x24", "out:" + B], pane.Events);
        host.AssertPaneMatchesMux(id, pane, "in-stream");

        HeadlessTerminalSession mux = host.Mux(id);
        (TerminalBuffer inStream, AnsiParser inStreamParser) = Replay(pane.LastSnapshot!, eager: false, A, B);
        (TerminalBuffer eager, AnsiParser eagerParser) = Replay(pane.LastSnapshot!, eager: true, A, B);

        TerminalStateAssert.AssertEquivalent("[in-stream replay]", mux.Buffer, mux.Parser, inStream, inStreamParser);
        Assert.ThrowsAny<XunitException>(() =>
            TerminalStateAssert.AssertEquivalent("[eager replay]", mux.Buffer, mux.Parser, eager, eagerParser));
    }

    private static (TerminalBuffer, AnsiParser) Replay(TerminalStateSnapshot snapshot, bool eager, string a, string b)
    {
        TerminalStateSnapshot copy = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        TerminalStateTransfer.Restore(buffer, parser, copy);
        if (eager) buffer.Resize(40, 24);
        parser.Process(a);
        if (!eager) buffer.Resize(40, 24);
        parser.Process(b);
        return (buffer, parser);
    }
}
```

`Scenarios/MultiClientTests.cs`:

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class MultiClientTests
{
    [Fact]
    public async Task Latest_resize_wins_and_both_clients_equal_the_mux()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);
        host.Fake(id).Emit("some text\r\nmore text");

        p1.Session.Resize(100, 30);
        await host.SettleAsync(id, c1, c2);
        p2.Session.Resize(90, 20);
        await host.SettleAsync(id, c1, c2);

        Assert.Equal((90, 20), (host.Mux(id).Cols, host.Mux(id).Rows));
        Assert.Equal((90, 20), host.Fake(id).Resizes.Last());
        host.AssertPaneMatchesMux(id, p1, "client 1");
        host.AssertPaneMatchesMux(id, p2, "client 2");
    }

    [Fact]
    public async Task Attaching_resizes_to_the_attaching_client_and_the_other_client_follows_in_stream()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);

        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id, MuxTestHost.DefaultPresentation with { Cols = 100, Rows = 30 });
        await host.SettleAsync(id, c1, c2);

        Assert.Contains("resize:100x30", p1.Events);
        host.AssertPaneMatchesMux(id, p1, "client 1");
        host.AssertPaneMatchesMux(id, p2, "client 2");
    }

    [Fact]
    public async Task The_latest_clients_presentation_answers_CSI_14_t_once()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);

        p2.Session.UpdatePresentation(new MuxPresentation { Cols = 90, Rows = 20, CellWidthPx = 9, CellHeightPx = 18 });
        await host.SettleAsync(id, c1, c2);
        host.Fake(id).Emit("\x1b[14t");
        await host.SettleAsync(id, c1, c2);

        Assert.Single(host.Fake(id).SentInput, s => s == "\x1b[4;360;810t");
    }

    [Fact]
    public async Task Device_queries_are_answered_exactly_once_with_two_clients_attached()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);

        host.Fake(id).Emit("\x1b[c");
        await host.SettleAsync(id, c1, c2);

        Assert.Single(host.Fake(id).SentInput, s => s.StartsWith("\x1b[?", StringComparison.Ordinal));
        Assert.NotEmpty(p1.Responses); // each pane's parser produced a reply ...
        Assert.NotEmpty(p2.Responses); // ... and nothing sent it
    }
}
```

`Scenarios/DetachReattachTests.cs`:

```csharp
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class DetachReattachTests
{
    [Fact]
    public async Task A_detached_session_keeps_running_and_a_reattach_converges()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel first = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("before detach\r\n");
        await host.SettleAsync(id, client);

        first.Session.Dispose();
        await host.SettleAsync(id, client);
        Assert.Equal(0, host.Mux(id).AttachedClients);
        host.Fake(id).Emit("\x1b[1mwhile detached\x1b[0m\r\n");
        await host.SettleAsync(id, client);
        Assert.True(host.Mux(id).IsExited is false);

        ClientPaneModel second = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("after reattach");
        await host.SettleAsync(id, client);

        host.AssertPaneMatchesMux(id, second, "reattached");
        Assert.DoesNotContain(first.Events, e => e.Contains("after reattach", StringComparison.Ordinal));
    }
}
```

`Scenarios/SlowClientTests.cs`:

```csharp
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class SlowClientTests
{
    [Fact]
    public async Task A_client_that_stops_reading_is_disconnected_and_nobody_else_notices()
    {
        using var host = new MuxTestHost(new MuxServerOptions { ClientSendBudgetBytes = 256 * 1024, ForceConPtyFiltering = false }, pipeCapacityBytes: 16 * 1024);
        var closed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Server.ConnectionClosed += c => closed.TrySetResult(c.CloseReason);
        MuxClient healthy = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(healthy);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(healthy, id);

        RawMuxConnection slow = host.ConnectRaw();
        await slow.HelloAsync();
        slow.Request(MuxMethods.Attach, new AttachParams { SessionId = id, MaxScrollbackRows = 0, Presentation = MuxTestHost.DefaultPresentation },
            MuxJsonContext.Default.AttachParams);
        using (MuxInboundFrame? snapshot = await slow.ReadAsync()) Assert.Equal(MuxFrameKind.Snapshot, snapshot?.Kind);
        // ... and now it never reads again.

        byte[] chunk = new byte[4096];
        Array.Fill(chunk, (byte)'x');
        const int chunks = 512; // 2 MiB
        for (int i = 0; i < chunks; i++) host.Fake(id).Emit(chunk);

        Assert.Equal(MuxErrorCodes.ClientTooSlow, await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await host.SettleAsync(id, healthy);
        Assert.Equal(chunks * 4096L, host.Mux(id).StreamPosition);   // the parse thread never waited
        Assert.Equal(1, host.Mux(id).AttachedClients);
        host.AssertPaneMatchesMux(id, pane, "the healthy client");
    }
}
```

`Scenarios/FlightExportTests.cs`:

```csharp
using System.Text;
using Ntilde.Mux.Tests.Support;
using Ntilde.Replay;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class FlightExportTests
{
    [Fact]
    public async Task A_flight_export_crosses_the_wire_as_bytes_ReplayRunner_can_play()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted");

        session.EnableFlightRecording(1 << 20);
        await client.PingAsync(TestContext.Current.CancellationToken);
        host.Fake(id).Emit("hello\r\n");
        host.Fake(id).Emit("world");

        string path = Path.Combine(Path.GetTempPath(), $"mux-flight-{Guid.NewGuid():N}.rec");
        try
        {
            Assert.True(await Task.Run(() => session.TryExportFlightRecording(path, out _), TestContext.Current.CancellationToken));
            var replayed = new List<byte>();
            await new ReplayRunner(path).RunAsync(d => { replayed.AddRange(d); return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
            Assert.Equal("hello\r\nworld", Encoding.UTF8.GetString(replayed.ToArray()));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

`Scenarios/AttachLatencyTests.cs` (measurement for the report; asserts equality, never a time):

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class AttachLatencyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Measure_attach_latency_empty_and_with_10k_scrollback()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();

        var empty = Stopwatch.StartNew();
        Guid emptyId = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel emptyPane = await MuxTestHost.AttachPaneAsync(client, emptyId);
        await host.SettleAsync(emptyId, client);
        empty.Stop();
        host.AssertPaneMatchesMux(emptyId, emptyPane, "empty");

        Guid bigId = await MuxTestHost.SpawnAsync(client);
        var text = new StringBuilder();
        for (int i = 0; i < 10_100; i++) text.Append(CultureInfo.InvariantCulture, $"scrollback line {i:D5} with some padding text\r\n");
        host.Fake(bigId).EmitInChunks(Encoding.UTF8.GetBytes(text.ToString()), chunkSize: 64 * 1024);
        await host.Mux(bigId).FlushAsync();

        var big = Stopwatch.StartNew();
        ClientPaneModel bigPane = await MuxTestHost.AttachPaneAsync(client, bigId, maxScrollbackRows: 10_000);
        await host.SettleAsync(bigId, client);
        big.Stop();
        host.AssertPaneMatchesMux(bigId, bigPane, "10k scrollback");
        Assert.Equal(10_000, bigPane.LastSnapshot!.ScrollbackRowCount);

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[mux-phase1] attach latency: empty session {empty.Elapsed.TotalMilliseconds:F1} ms (spawn -> equal); " +
            $"80x24 + 10k scrollback {big.Elapsed.TotalMilliseconds:F1} ms (attach -> equal), snapshot {bigPane.Session.LastSnapshotByteCount} bytes"));
    }
}
```

- [ ] **Step 2: Run the scenarios** — `scripts/build.ps1 test tests/Ntilde.Mux.Tests --filter "FullyQualifiedName~Scenarios" --logger "console;verbosity=detailed"`. Expected: all PASS (mid-stream theory = 72 cases). Copy the `[mux-phase1]` line into the report notes. Any failure is a real divergence: debug with `superpowers:systematic-debugging`, fix at the mechanism (Step "Produces" above), re-run the **whole** Mux.Tests project. One exception: if the *eager* replay in `ResizeOrderingTests` unexpectedly matches the mux, `A` is not width-sensitive on this buffer — choose an `A` whose layout depends on the width (a glyph positioned beyond the new width); never weaken the `ThrowsAny` assertion.

- [ ] **Step 3: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add tests/Ntilde.Mux.Tests src/Ntilde.Mux
git commit -m "test(mux): end-to-end scenarios - mid-stream attach, resize ordering, multi-client, slow client"
```

---

### Task 9: The PtySmoke test with the real factory and a real shell

**Files:**
- Create: `tests/Ntilde.App.Tests/MuxRealShellSmokeTests.cs`
- Modify: `tests/Ntilde.App.Tests/Ntilde.App.Tests.csproj` (add `<ProjectReference Include="..\..\src\Ntilde.Mux\Ntilde.Mux.csproj" />`)

Lives in App.Tests because that is where `DefaultTerminalSessionFactory` and the `rusty_pty` native library are (spec §9.4).

**Interfaces:**
- Consumes: `Ntilde.Shell.DefaultTerminalSessionFactory.Instance`, `Ntilde.Pty.ShellHelper.GetDefaultShell()`, `MuxServer`, `MuxClient`, `InMemoryMuxListener`, `PtyRealShellCollection.Name`.

- [ ] **Step 1: Write the test**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;
using Ntilde.Pty;
using Ntilde.Replay;
using Ntilde.Shell;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests
{
    /// <summary>
    /// The one end-to-end check with a real PTY: the production session factory spawns a real
    /// shell under the mux, and an attached client sees what the shell prints.
    /// </summary>
    [Collection(PtyRealShellCollection.Name)]
    public class MuxRealShellSmokeTests
    {
        [Fact]
        [Trait("Category", "PtySmoke")]
        public async Task A_real_shell_spawned_through_the_mux_reaches_an_attached_client()
        {
            var ct = TestContext.Current.CancellationToken;
            string shell = ShellHelper.GetDefaultShell();
            var listener = new InMemoryMuxListener();
            using var server = new MuxServer(DefaultTerminalSessionFactory.Instance, new MuxServerOptions());
            server.Start(listener);
            using MuxClient client = await MuxClient.ConnectAsync(listener.Connect(), cancellationToken: ct);

            Guid id = await client.SpawnAsync(new SpawnParams
            {
                Command = shell, Cols = 80, Rows = 24, SkipPowerShellPostLaunchInit = true, Title = "smoke",
            }, ct);
            using MuxClientSession session = client.OpenSession(id, shell);
            var buffer = new TerminalBuffer(80, 24);
            var parser = new AnsiParser(buffer, session.ForceConPtyFiltering) { ImageDecoder = null };
            object gate = new();
            session.SnapshotReceived += s => { lock (gate) TerminalStateTransfer.Restore(buffer, parser, s); };
            session.OnOutputReceived += t => { lock (gate) parser.Process(t); };
            session.StreamResize += (c, r) => { lock (gate) buffer.Resize(c, r); };
            await session.AttachAsync(1000, new MuxPresentation { Cols = 80, Rows = 24, CellWidthPx = 9, CellHeightPx = 18 }, ct);

            string Screen() { lock (gate) return string.Join('\n', BufferSnapshot.Capture(buffer).Lines); }

            // Let the shell draw its prompt before typing (early input can be eaten by line editors).
            await WaitUntilAsync(() => Screen().Trim().Length > 0, TimeSpan.FromSeconds(30));
            await Task.Delay(500, ct);
            session.SendInput("echo ntilde-mux-smoke\r");

            // The typed command and its output: the marker appears twice.
            await WaitUntilAsync(() => CountOf(Screen(), "ntilde-mux-smoke") >= 2, TimeSpan.FromSeconds(30));
            Assert.True(Assert.Single(await client.ListSessionsAsync(ct)).Running);

            await session.KillAsync(ct);
            Assert.Empty(await client.ListSessionsAsync(ct));
        }

        private static int CountOf(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Timed out waiting for the shell through the mux.");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }
    }
}
```

- [ ] **Step 2: Run** — `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~MuxRealShellSmokeTests" --blame-hang-timeout 5m`. Expected: PASS.

- [ ] **Step 3: Commit**

```bash
scripts/build.ps1 format whitespace --no-restore
git add tests/Ntilde.App.Tests
git commit -m "test(mux): PtySmoke - a real shell through the mux reaches an attached client"
```

---

### Task 10: Docs and full verification

**Files:**
- Modify: `docs/ARCHITECTURE.md` (§2 graph + assembly table; §12 arch-test list; §14 known debt), `docs/MODULE_OWNERSHIP.md`

- [ ] **Step 1: `docs/ARCHITECTURE.md`**

§2 graph — add, following the file's existing arrow style:

```
Mux  ──► Pty, VT, Replay, Mux.Contracts
Mux.Contracts                           (leaf)
```

Assembly table — two rows after `Ntilde.AgentHost.Contracts`:

| `Ntilde.Mux.Contracts` | (leaf) | Multiplexer wire protocol: framing, frame kinds, binary payload codecs, source-generated JSON DTOs, error codes, version negotiation |
| `Ntilde.Mux` | Pty, VT, Replay, Mux.Contracts | Multiplexer core: headless authoritative sessions (one parse thread each), server, client + `MuxClientSession : ITerminalSession`, in-memory transport. **Must not reference Platform, App, Avalonia or SkiaSharp** |

Add `Mux.Contracts` to the prose list of zero-reference leaves beside `AgentHost.Contracts`. §12: add `MuxContracts_must_be_a_leaf_assembly`, `Mux_must_not_depend_on_ui_platform_or_app`, `Mux_references_only_approved_ntilde_assemblies`, `MuxContracts_csproj_must_have_no_project_references`, `Mux_only_references_Pty_Vt_Replay_and_MuxContracts`, `Mux_does_not_use_the_MuxContracts_namespace`, and the two new theory rows. §14: add "Mux snapshot capture holds the buffer read lock (~27 ms at 10k rows); the mux parser has no image decoder, so kitty images exist only in clients and are absent from a reattach snapshot (Phase 2)."

- [ ] **Step 2: `docs/MODULE_OWNERSHIP.md`** — two sections in the file's existing format, after `Ntilde.AgentHost.Contracts`:

`## Ntilde.Mux.Contracts (src/Ntilde.Mux.Contracts/)` — Namespace `Ntilde.Mux.Contracts`; depends on nothing. Invariants: frame = `u8 kind` + `u32` LE length + payload, payload ≤ `MaxFrameBytes`, header validated before any payload byte is read; every JSON type goes through `MuxJsonContext`; binary parsers length-check before slicing; version negotiation picks the highest common version or refuses.

`## Ntilde.Mux (src/Ntilde.Mux/)` — Namespace `Ntilde.Mux` (+ `.Transport`); depends on Pty, VT, Replay, Mux.Contracts. Invariants: **one parse thread per session** owns the headless parser and buffer, and control items run only between `Process()` calls; **seq semantics**: `Output.seq` = raw offset before the chunk, `ResizeEvent.seq` = offset it applies at, snapshot `StreamSeq + DecoderTail.Length` = offset at capture, and clients drop the connection on any gap; **the mux answers device queries** and clients never do; latest resize wins; no thread-pool work on the output path; a slow client is disconnected, never waited for; attach limits are rejection ceilings, never clamps (#473). Tests: `tests/Ntilde.Mux.Tests/` (+ `MuxRealShellSmokeTests` in App.Tests).

Also add `tests/Ntilde.Mux.Tests/` to the "Tests (First-Class Owners)" section: "Scripted-session suite for the mux: contracts, transport, headless session, server, client, and end-to-end scenarios. Shares `TerminalStateAssert` and `ParityCorpus` with VT.Tests by file link."

- [ ] **Step 3: Full verification** — run each separately and record the counts for the report:

```
scripts/build.ps1 test tests/Ntilde.Mux.Tests
scripts/build.ps1 test tests/Ntilde.VT.Tests                  # expect 728/0/0
scripts/build.ps1 test tests/Ntilde.Platform.Tests            # expect 212/0/30 skipped
scripts/build.ps1 test tests/Ntilde.Architecture.Tests        # expect 93/0
scripts/build.ps1 test tests/Ntilde.Rendering.Tests           # expect 100
scripts/build.ps1 test tests/Ntilde.McpServer.Tests           # expect 198
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot" --blame-hang-timeout 5m
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane=PlatformBoot" --blame-hang-timeout 5m
scripts/build.ps1 build src/Ntilde.Mux.Contracts -c Release   # 0 warnings (AOT analyzers on, warnings are errors)
scripts/build.ps1 build src/Ntilde.Mux -c Release             # 0 warnings
scripts/build.ps1 publish src/Ntilde.App -c Release -r win-x64   # still succeeds, no IL2026/IL3050
scripts/build.ps1 format whitespace --verify-no-changes --no-restore
```

App.Tests lanes: previous baseline + the new tests (2 tap tests + 1 smoke in `Lane!=PlatformBoot`), 0 failed. The App publish does not include `Ntilde.Mux` (nothing in App references it yet); the library builds are the AOT check for the new code — say so in the report.

- [ ] **Step 4: Commit and push**

```bash
scripts/build.ps1 format whitespace --no-restore
git add docs/ARCHITECTURE.md docs/MODULE_OWNERSHIP.md
git commit -m "docs: Ntilde.Mux and Ntilde.Mux.Contracts in the architecture and ownership maps"
git push -u origin feat/mux-phase1-core
```

Then open the PR against **`dev-mux`** (`gh pr create --base dev-mux`), body covering the task's seven report items: files by deliverable, test commands + counts, the shipped frame catalogue (spec §6), the threading model (spec §5), divergences found during Task 8 and their fixes, the `[mux-phase1]` latency line, and the Phase 2 open questions (spec §11). Include `Closes #473`.

---

## Self-review notes (for the executor)

- Spec §3 → Task 1; §6 → Task 3; §5 threading → Tasks 5-7; §7 limits → Tasks 5 (server snapshot ceiling), 6 (scrollback clamp, budget), 7 (client limits); §9 deviations are baked in; §10 coverage list → Tasks 5, 7, 8, 9.
- Names used across tasks: `HeadlessTerminalSession.PostAttach(sink, requestId, maxScrollbackRows, presentation, maxSnapshotBytes)`, `FlushAsync`, `InvokeAsync`, `StreamPosition`; `MuxClientSession.StreamPosition`, `AttachedSeq`, `LastSnapshotByteCount`; `MuxTestHost.SettleAsync(sessionId, params MuxClient[])`, `AssertPaneMatchesMux(sessionId, pane, because)`.
- Ordering rule every test relies on: **control items outrank data**. A test that emits and then posts a control item must `FlushAsync()` between them when the control item's effect depends on the data having been parsed.

