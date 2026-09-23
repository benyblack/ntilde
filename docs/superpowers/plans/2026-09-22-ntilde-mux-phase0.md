# ntilde Multiplexer Phase 0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the seams (session factory, session capability flags, shared UTF-8 chunk decoder, persisted mux fields) and the correctness proof (full terminal-state snapshot + parser-state export/import, and a parity test showing "snapshot + tail" ≡ "continuous") that a later `ntilde mux serve` daemon will stand on — with zero user-visible behaviour change.

**Architecture:** Everything is additive. `Ntilde.Pty` gains three contract types (`ITerminalSessionFactory` + request records, `ITerminalSessionCapabilities`, `Utf8ChunkDecoder`) and two nullable `PaneNode` fields. `Ntilde.VT` gains `AnsiParserState` / `TerminalStateSnapshot` plus `ExportState`/`ImportState` on `AnsiParser` and `TerminalBuffer`, serialized through a new source-generated JSON context. `Ntilde.App` gains a `DefaultTerminalSessionFactory` that reproduces today's two spawn branches exactly and is injected into `TerminalPane` the way `CommandAssistServices` already is. The existing `ReplaySnapshot` / `ApplySnapshot` path is not touched.

**Tech Stack:** .NET 10, C# (nullable enabled), xUnit v3, Avalonia headless (`[AvaloniaFact]`), `System.Text.Json` source generation (Native AOT — `IL2026;IL3050` are errors), `System.Text.Unicode.Utf8`.

**Spec:** `docs/superpowers/specs/2026-09-22-ntilde-mux-phase0.md`

---

## Global Constraints

Every task's requirements implicitly include this section.

- **Never call raw `dotnet build` / `dotnet test`.** Use `scripts/build.ps1` (Windows/PowerShell) or `scripts/build.sh` (Git Bash / Linux / macOS). The wrappers pass `-nodeReuse:false` and set `DOTNET_CLI_USE_MSBUILD_SERVER=0`; without them the build hangs on captured stdout.
- **Run test projects individually.** Never a whole-solution `test`.
- **`tests/Ntilde.App.Tests` always gets `--blame-hang-timeout 5m`**, and the two lanes run *separately, never concurrently*:
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot" --blame-hang-timeout 5m`
  - `scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane=PlatformBoot" --blame-hang-timeout 5m`
- **`Ntilde.VT` is a leaf** — no project references at all (arch test `Vt_csproj_must_have_no_project_references`, `Vt_must_be_a_leaf_assembly`). No Avalonia, Skia, native interop or I/O in VT.
- **`Ntilde.Pty` must not reference `Ntilde.VT`** (arch tests `Pty_must_not_depend_on_Vt`, `Pty_csproj_must_not_reference_Vt`). Pty may reference `Ntilde.Replay` only — it already does.
- **Native AOT:** `Ntilde.App` fails the publish on `IL2026`/`IL3050`. All JSON must go through a `JsonSerializerContext` source-generated context. No `JsonSerializer.Serialize(object)` reflection overloads.
- **No new `TerminalSettings` fields in this phase.** (A new field needs the `TerminalPane.ApplySettings` whitelist plus the `McpServer` `SettingsTools` schema and validators, or two gating drift-guard tests fail.)
- **Additive only.** Do not refactor `MainWindow` / `TerminalPane` beyond the seams named here.
- **Hot path:** `RustPtySession.ReadLoop` must not gain per-chunk heap allocations beyond what exists today (today: one `char[]` allocated once outside the loop, one `string` per chunk).
- **Branch:** all work on `feat/mux-phase0-seams`, cut from `main`. Do **not** touch `claude/ntilde-multiplexer-mbxx9b`.
- **Concurrency warning:** other sessions commit into this top-level checkout and switch its branch mid-task. Verify `git rev-parse --abbrev-ref HEAD` before every commit, or work in a `git worktree`.
- Commit messages end with:
  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  ```

---

## File Structure

### Created

| File | Responsibility |
|---|---|
| `src/Ntilde.Pty/Utf8ChunkDecoder.cs` | Stateful byte→UTF-16 chunk decoder with an inspectable pending tail and a consumed-byte counter. |
| `src/Ntilde.Pty/ITerminalSessionCapabilities.cs` | Optional capability interface a session may implement. |
| `src/Ntilde.Pty/ITerminalSessionFactory.cs` | Factory interface + `TerminalSessionRequest` + `SshSessionDescriptor`. |
| `src/Ntilde.App/Shell/DefaultTerminalSessionFactory.cs` | The production factory: today's two spawn branches, moved verbatim. |
| `src/Ntilde.VT/AnsiParserState.cs` | DTO for every `AnsiParser` field that persists across `Process()` calls. |
| `src/Ntilde.VT/AnsiParser.StateTransfer.cs` | `AnsiParser.ExportState()` / `ImportState()` (new partial file). |
| `src/Ntilde.VT/TerminalStateSnapshot.cs` | Full-state snapshot DTO tree + `TerminalStateJsonContext` + `TerminalStateSerializer`. |
| `src/Ntilde.VT/TerminalBuffer.StateTransfer.cs` | `TerminalBuffer.ExportState(int)` / `ImportState(TerminalStateSnapshot)` (new partial file). |
| `tests/Ntilde.Platform.Tests/Pty/Utf8ChunkDecoderTests.cs` | Differential + explicit-case decoder tests. |
| `tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs` | Factory seam + capability-gating pane tests. |
| `tests/Ntilde.VT.Tests/StateTransfer/AnsiParserStateTests.cs` | Parser export/import unit tests. |
| `tests/Ntilde.VT.Tests/StateTransfer/TerminalStateSnapshotTests.cs` | Buffer export/import + round-trip + size/timing tests. |
| `tests/Ntilde.VT.Tests/StateTransfer/ParityCorpus.cs` | The corpus: synthetic generators + `.rec` fixture loader. |
| `tests/Ntilde.VT.Tests/StateTransfer/ParityHarness.cs` | Cut-point driver + the equality comparison. |
| `tests/Ntilde.VT.Tests/StateTransfer/SnapshotTailParityTests.cs` | The parity tests themselves. |

### Modified

| File | Change |
|---|---|
| `src/Ntilde.Pty/RustPtySession.cs` | `Encoding.UTF8.GetDecoder()` → `Utf8ChunkDecoder`. |
| `src/Ntilde.Pty/SessionModels.cs` | `PaneNode.MuxSessionId`, `PaneNode.MuxEndpoint`. |
| `src/Ntilde.Platform/Ssh/Sessions/NativeSshSession.cs` | `Encoding.UTF8.GetDecoder()` → `Utf8ChunkDecoder`. |
| `src/Ntilde.App/Controls/TerminalPane.axaml.cs` | Factory property + `InitializeSessionCore` calls it; capability gating on `OnResponse` and `SendInBandResize`; `InitializeSessionCore` becomes `internal`. |
| `src/Ntilde.App/Shell/AppServiceBundle.cs` | Optional `SessionFactory` member. |
| `src/Ntilde.App/MainWindow.axaml.cs` | `_sessionFactory` field + `WirePane` injection. |
| `src/Ntilde.VT/AnsiParser.cs` | `class AnsiParser` → `partial class AnsiParser`. |
| `tests/Ntilde.VT.Tests/Ntilde.VT.Tests.csproj` | `ProjectReference` to `Ntilde.Replay`; linked `.rec` fixtures. |
| `tests/Ntilde.App.Tests/Core/SessionManagerTests.cs` | `PaneNode` mux-field round trip + legacy-JSON load. |

---

## Task 1: `Utf8ChunkDecoder`

**Files:**
- Create: `src/Ntilde.Pty/Utf8ChunkDecoder.cs`
- Create: `tests/Ntilde.Platform.Tests/Pty/Utf8ChunkDecoderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Ntilde.Pty.Utf8ChunkDecoder` with
  `int Decode(ReadOnlySpan<byte> input, Span<char> destination)`,
  `static int GetMaxCharCount(int byteCount)`,
  `long ConsumedBytes { get; }`,
  `ReadOnlySpan<byte> PendingTail { get; }`,
  `void Reset()`.

**Why a Pty test project is not created:** there is none, and `tests/Ntilde.Platform.Tests` already references `Ntilde.Platform` → `Ntilde.Pty`, so the type is reachable there. (Checked: `src/Ntilde.Pty` has no sibling test project in the solution.)

- [ ] **Step 1: Write the failing tests**

Create `tests/Ntilde.Platform.Tests/Pty/Utf8ChunkDecoderTests.cs`:

```csharp
using System;
using System.Text;
using Ntilde.Pty;
using Xunit;

namespace Ntilde.Platform.Tests.Pty;

/// <summary>
/// <see cref="Utf8ChunkDecoder"/> replaces <c>Encoding.UTF8.GetDecoder()</c> on both live
/// output paths (RustPtySession.ReadLoop, NativeSshSession.EmitOutput). "Replaces" has to mean
/// byte-for-byte identical output for every split of every input, including malformed ones —
/// a decoder that differs by one U+FFFD changes what the parser sees, which changes the screen.
/// So the headline test is differential against the type it replaces, not against hand-written
/// expectations.
/// </summary>
public class Utf8ChunkDecoderTests
{
    /// <summary>Decodes <paramref name="chunks"/> through the new decoder, concatenating output.</summary>
    private static string DecodeAll(byte[][] chunks)
    {
        var decoder = new Utf8ChunkDecoder();
        var sb = new StringBuilder();
        foreach (byte[] chunk in chunks)
        {
            char[] dest = new char[Utf8ChunkDecoder.GetMaxCharCount(chunk.Length)];
            int n = decoder.Decode(chunk, dest);
            sb.Append(dest, 0, n);
        }

        return sb.ToString();
    }

    /// <summary>Decodes the same chunks through the stateful decoder this type replaces.</summary>
    private static string DecodeAllReference(byte[][] chunks)
    {
        Decoder reference = Encoding.UTF8.GetDecoder();
        var sb = new StringBuilder();
        foreach (byte[] chunk in chunks)
        {
            char[] dest = new char[Encoding.UTF8.GetMaxCharCount(chunk.Length)];
            int n = reference.GetChars(chunk, 0, chunk.Length, dest, 0, flush: false);
            sb.Append(dest, 0, n);
        }

        return sb.ToString();
    }

    private static byte[][] SplitAt(byte[] input, int offset) =>
        [input[..offset], input[offset..]];

    public static TheoryData<string> Corpus() => new()
    {
        "hello world",
        "café naïve",          // 2-byte sequences
        "你好世界",       // 3-byte sequences
        "\U0001F600\U0001F1EC\U0001F1E7",  // 4-byte sequences (emoji, regional indicators)
        "áé́",            // combining marks
        "\U0001F468‍\U0001F469‍\U0001F467", // ZWJ family cluster
        "\u001b[31mred\u001b[0m",          // escape sequences stay intact
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void MatchesEncodingUtf8Decoder_AtEverySplitOffset(string text)
    {
        byte[] input = Encoding.UTF8.GetBytes(text);

        for (int offset = 0; offset <= input.Length; offset++)
        {
            byte[][] chunks = SplitAt(input, offset);
            Assert.Equal(DecodeAllReference(chunks), DecodeAll(chunks));
        }
    }

    [Fact]
    public void MatchesEncodingUtf8Decoder_ForRandomByteStreams_AtEverySplitOffset()
    {
        // Fixed seed: a differential failure must be reproducible from the test name alone.
        var rng = new Random(0x5EED);

        for (int iteration = 0; iteration < 200; iteration++)
        {
            byte[] input = new byte[rng.Next(1, 48)];
            rng.NextBytes(input);

            for (int offset = 0; offset <= input.Length; offset++)
            {
                byte[][] chunks = SplitAt(input, offset);
                Assert.Equal(DecodeAllReference(chunks), DecodeAll(chunks));
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FourByteCodePoint_SplitAnywhere_DecodesAsOneCodePoint(int firstChunkLength)
    {
        byte[] input = Encoding.UTF8.GetBytes("\U0001F600");
        Assert.Equal(4, input.Length);

        byte[][] chunks = SplitAt(input, firstChunkLength);
        Assert.Equal("\U0001F600", DecodeAll(chunks));
    }

    [Fact]
    public void InvalidBytes_ProduceTheSameReplacementRunAsEncodingUtf8()
    {
        byte[] input = [0xC3, 0x28, 0xA0, 0xA1, 0xE2, 0x28, 0xA1, 0xF0, 0x28, 0x8C, 0x28];

        Assert.Equal(DecodeAllReference([input]), DecodeAll([input]));
    }

    [Fact]
    public void LoneContinuationBytes_ProduceTheSameReplacementRunAsEncodingUtf8()
    {
        byte[] input = [0x80, 0x80, 0xBF, (byte)'a', 0x80];

        Assert.Equal(DecodeAllReference([input]), DecodeAll([input]));
    }

    [Fact]
    public void PendingTail_HoldsTheIncompletePrefixAndClearsWhenCompleted()
    {
        byte[] input = Encoding.UTF8.GetBytes("\U0001F600");
        var decoder = new Utf8ChunkDecoder();
        char[] dest = new char[Utf8ChunkDecoder.GetMaxCharCount(4)];

        Assert.Equal(0, decoder.Decode(input.AsSpan(0, 3), dest));
        Assert.Equal(new byte[] { input[0], input[1], input[2] }, decoder.PendingTail.ToArray());
        Assert.Equal(0, decoder.ConsumedBytes);

        Assert.Equal(2, decoder.Decode(input.AsSpan(3), dest));
        Assert.Empty(decoder.PendingTail.ToArray());
        Assert.Equal(4, decoder.ConsumedBytes);
    }

    [Fact]
    public void Reset_DropsThePendingTailAndCountsItAsConsumed()
    {
        byte[] input = Encoding.UTF8.GetBytes("\U0001F600");
        var decoder = new Utf8ChunkDecoder();
        char[] dest = new char[Utf8ChunkDecoder.GetMaxCharCount(4)];

        decoder.Decode(input.AsSpan(0, 3), dest);
        decoder.Reset();

        Assert.Empty(decoder.PendingTail.ToArray());
        Assert.Equal(3, decoder.ConsumedBytes);

        // The stream resumes cleanly: the orphaned continuation byte is replaced, not joined.
        int n = decoder.Decode(input.AsSpan(3), dest);
        Assert.Equal("�", new string(dest, 0, n));
    }

    [Fact]
    public void ConsumedBytes_ExcludesThePendingTail()
    {
        byte[] input = Encoding.UTF8.GetBytes("ab你");  // 'a','b' then a 3-byte sequence
        var decoder = new Utf8ChunkDecoder();
        char[] dest = new char[Utf8ChunkDecoder.GetMaxCharCount(input.Length)];

        decoder.Decode(input.AsSpan(0, 3), dest); // 'a', 'b', first byte of the 3-byte sequence
        Assert.Equal(2, decoder.ConsumedBytes);
        Assert.Single(decoder.PendingTail.ToArray());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```
scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~Utf8ChunkDecoderTests"
```

Expected: FAIL — `error CS0246: The type or namespace name 'Utf8ChunkDecoder' could not be found`.

- [ ] **Step 3: Implement `Utf8ChunkDecoder`**

Create `src/Ntilde.Pty/Utf8ChunkDecoder.cs`:

```csharp
using System;
using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace Ntilde.Pty
{
    /// <summary>
    /// Stateful UTF-8 → UTF-16 chunk decoder with an <em>inspectable</em> pending tail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Behaviourally identical to <c>Encoding.UTF8.GetDecoder()</c> used with
    /// <c>flush: false</c>, which is what it replaces on both live output paths
    /// (<c>RustPtySession.ReadLoop</c>, <c>NativeSshSession.EmitOutput</c>). Identical is the
    /// requirement, not an aspiration: a difference of one U+FFFD changes what
    /// <c>AnsiParser</c> sees, which changes the screen. <c>Utf8ChunkDecoderTests</c> pins that
    /// differentially, across every split offset of every corpus input including malformed ones.
    /// </para>
    /// <para>
    /// The reason for a bespoke type is the one thing <see cref="Decoder"/> will not tell you:
    /// how many bytes it is holding back. The multiplexer snapshot has to record a byte offset
    /// into the session's output stream plus the partial code point straddling it, so an
    /// attaching pane can resume decoding at exactly the right place. <see cref="ConsumedBytes"/>
    /// and <see cref="PendingTail"/> are that pair.
    /// </para>
    /// <para>
    /// Allocation-free per chunk: the carry lives in a fixed 3-byte field and the splice buffer
    /// is stack-allocated, so the read loop's per-chunk cost is unchanged.
    /// </para>
    /// </remarks>
    public sealed class Utf8ChunkDecoder
    {
        /// <summary>
        /// Longest incomplete-but-valid UTF-8 prefix: a 4-byte sequence missing its last byte.
        /// </summary>
        private const int MaxTailLength = 3;

        /// <summary>
        /// Carry plus enough of the next chunk to finish any sequence the carry could start
        /// (3 + 4). Sized so one splice always decides the carry rather than looping.
        /// </summary>
        private const int SpliceLength = MaxTailLength + 4;

        private readonly byte[] _tail = new byte[MaxTailLength];
        private int _tailLength;
        private long _consumedBytes;

        /// <summary>
        /// Input bytes this decoder has finished with — either decoded into output, or discarded
        /// by <see cref="Reset"/>. Bytes still held in <see cref="PendingTail"/> are NOT counted,
        /// so the offset of the next byte the caller should feed is
        /// <c>ConsumedBytes + PendingTail.Length</c>. Monotonic for the life of the instance.
        /// </summary>
        public long ConsumedBytes => _consumedBytes;

        /// <summary>
        /// The incomplete-but-valid UTF-8 prefix held back from the last <see cref="Decode"/>,
        /// at most <see cref="MaxTailLength"/> bytes. Empty when nothing is pending.
        /// </summary>
        public ReadOnlySpan<byte> PendingTail => _tail.AsSpan(0, _tailLength);

        /// <summary>
        /// Upper bound on the chars a <see cref="Decode"/> of <paramref name="byteCount"/> bytes
        /// can emit. Matches <c>Encoding.UTF8.GetMaxCharCount</c>: the carry contributes at most
        /// one char, because a held-back prefix is by construction a single maximal subpart and
        /// therefore at most one U+FFFD.
        /// </summary>
        public static int GetMaxCharCount(int byteCount) => Encoding.UTF8.GetMaxCharCount(byteCount);

        /// <summary>
        /// Drops the pending tail. Use when the byte stream itself broke (a failed read), where
        /// the held-back prefix will never be completed. The discarded bytes are added to
        /// <see cref="ConsumedBytes"/>: they are gone from the stream, and the counter's contract
        /// is "bytes this decoder will never look at again".
        /// </summary>
        public void Reset()
        {
            _consumedBytes += _tailLength;
            _tailLength = 0;
        }

        /// <summary>
        /// Decodes <paramref name="input"/>, appending to <paramref name="destination"/> and
        /// returning the number of chars written. <paramref name="destination"/> must be at
        /// least <see cref="GetMaxCharCount"/> of <paramref name="input"/>'s length.
        /// </summary>
        public int Decode(ReadOnlySpan<byte> input, Span<char> destination)
        {
            if (destination.Length < GetMaxCharCount(input.Length))
            {
                throw new ArgumentException(
                    $"Destination of {destination.Length} chars is too small for {input.Length} bytes; " +
                    $"needs {GetMaxCharCount(input.Length)}. Size it with Utf8ChunkDecoder.GetMaxCharCount.",
                    nameof(destination));
            }

            int written = 0;

            if (_tailLength > 0)
            {
                written += DrainCarry(ref input, destination);
            }

            if (!input.IsEmpty)
            {
                OperationStatus status = Utf8.ToUtf16(
                    input,
                    destination[written..],
                    out int bytesRead,
                    out int charsWritten,
                    replaceInvalidSequences: true,
                    isFinalBlock: false);

                // replaceInvalidSequences means InvalidData is impossible, and the destination is
                // pre-sized, so DestinationTooSmall is a caller bug we already rejected above.
                // Done or NeedMoreData are the only outcomes; both are handled identically.
                _ = status;

                written += charsWritten;
                _consumedBytes += bytesRead;

                int leftover = input.Length - bytesRead;
                if (leftover > 0)
                {
                    input[bytesRead..].CopyTo(_tail);
                    _tailLength = leftover;
                }
            }

            return written;
        }

        /// <summary>
        /// Splices the carried prefix with the head of <paramref name="input"/> and decodes what
        /// that completes, advancing <paramref name="input"/> past the bytes it took.
        /// </summary>
        private int DrainCarry(ref ReadOnlySpan<byte> input, Span<char> destination)
        {
            Span<byte> splice = stackalloc byte[SpliceLength];
            int carryLength = _tailLength;

            _tail.AsSpan(0, carryLength).CopyTo(splice);
            int taken = Math.Min(input.Length, SpliceLength - carryLength);
            input[..taken].CopyTo(splice[carryLength..]);
            int spliceLength = carryLength + taken;

            Utf8.ToUtf16(
                splice[..spliceLength],
                destination,
                out int bytesRead,
                out int charsWritten,
                replaceInvalidSequences: true,
                isFinalBlock: false);

            if (bytesRead < carryLength)
            {
                // Nothing completed: the whole splice is still one incomplete prefix, which can
                // only happen when `input` ran out. A prefix is at most 4 bytes, so the leftover
                // fits the carry; if it ever does not, the invariant this type rests on is wrong
                // and silence would corrupt the stream offset.
                int stillPending = spliceLength - bytesRead;
                if (stillPending > MaxTailLength)
                {
                    throw new InvalidOperationException(
                        $"UTF-8 carry of {stillPending} bytes exceeds the {MaxTailLength}-byte maximum " +
                        "for an incomplete sequence; the decoder's splice invariant is broken.");
                }

                splice[bytesRead..spliceLength].CopyTo(_tail);
                _tailLength = stillPending;
                _consumedBytes += bytesRead;
                input = input[taken..];
                return charsWritten;
            }

            _tailLength = 0;
            _consumedBytes += bytesRead;
            input = input[(bytesRead - carryLength)..];
            return charsWritten;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```
scripts/build.ps1 test tests/Ntilde.Platform.Tests --filter "FullyQualifiedName~Utf8ChunkDecoderTests"
```

Expected: PASS, 0 failed.

> If `MatchesEncodingUtf8Decoder_ForRandomByteStreams_AtEverySplitOffset` fails, the mismatch is
> real — `Utf8.ToUtf16` and `UTF8Encoding`'s decoder both use Unicode maximal-subpart
> substitution, so any divergence is a bug in the splice logic above, not an acceptable
> difference. Print both strings' code points before changing anything.

- [ ] **Step 5: Commit**

```bash
git add src/Ntilde.Pty/Utf8ChunkDecoder.cs tests/Ntilde.Platform.Tests/Pty/Utf8ChunkDecoderTests.cs
git commit -m "feat(pty): add Utf8ChunkDecoder with an inspectable pending tail

Byte-for-byte equivalent to Encoding.UTF8.GetDecoder(flush: false), plus the two
things the multiplexer snapshot needs and Decoder will not expose: how many stream
bytes are settled, and which partial code point is being held back.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Adopt `Utf8ChunkDecoder` on both live output paths

**Files:**
- Modify: `src/Ntilde.Pty/RustPtySession.cs:252`, `:1015`, `:1045`
- Modify: `src/Ntilde.Platform/Ssh/Sessions/NativeSshSession.cs:21`, `:509-520`

**Interfaces:**
- Consumes: `Ntilde.Pty.Utf8ChunkDecoder` (Task 1).
- Produces: nothing new — behaviour is unchanged by construction.

- [ ] **Step 1: Swap the field and call site in `RustPtySession`**

At `src/Ntilde.Pty/RustPtySession.cs:251-252`, replace:

```csharp
        // UTF-8 decoder with state - handles partial multi-byte sequences across reads
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
```

with:

```csharp
        // UTF-8 decoder with state - handles partial multi-byte sequences across reads.
        // Utf8ChunkDecoder rather than Encoding.UTF8.GetDecoder(): byte-identical output
        // (Utf8ChunkDecoderTests pins that differentially), and it can report how many stream
        // bytes are settled and what partial code point is pending - the pair a multiplexer
        // snapshot needs to let an attaching pane resume decoding mid-sequence.
        private readonly Utf8ChunkDecoder _utf8Decoder = new();
```

In `ReadLoop`, at `:990`, replace the char-buffer sizing:

```csharp
            char[] charBuffer = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
```

with:

```csharp
            char[] charBuffer = new char[Utf8ChunkDecoder.GetMaxCharCount(buffer.Length)];
```

At `:1015`, replace:

```csharp
                        int charCount = _utf8Decoder.GetChars(buffer, 0, read, charBuffer, 0);
```

with:

```csharp
                        int charCount = _utf8Decoder.Decode(buffer.AsSpan(0, read), charBuffer);
```

`_utf8Decoder.Reset()` at `:1045` needs no change — the method name and meaning are the same.

- [ ] **Step 2: Swap the field and call site in `NativeSshSession`**

At `src/Ntilde.Platform/Ssh/Sessions/NativeSshSession.cs:21`, replace:

```csharp
    private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();
```

with:

```csharp
    // See RustPtySession's field of the same name for why this is Utf8ChunkDecoder rather than
    // Encoding.UTF8.GetDecoder(): byte-identical output, plus a reportable stream offset and
    // pending tail for the multiplexer snapshot.
    private readonly Ntilde.Pty.Utf8ChunkDecoder _utf8Decoder = new();
```

Replace the body of `EmitOutput` at `:509-520`:

```csharp
    private void EmitOutput(byte[] payload)
    {
        _metrics.MarkFirstOutput();
        _recorder?.RecordChunk(payload, payload.Length);
        _flightRecorder?.RecordChunk(payload, payload.Length);

        char[] chars = new char[Ntilde.Pty.Utf8ChunkDecoder.GetMaxCharCount(payload.Length)];
        int charCount = _utf8Decoder.Decode(payload, chars);
        if (charCount > 0)
        {
            EmitText(new string(chars, 0, charCount));
        }
    }
```

- [ ] **Step 3: Remove any `using System.Text;` that is now unused**

Both files set `TreatWarningsAsErrors` via `Directory.Build.props` only where opted in, but an
unused `using` is not a warning by default — leave `using System.Text;` alone if anything else in
the file still needs it (`RustPtySession` uses `Encoding.UTF8.GetBytes` elsewhere; check
`NativeSshSession` for `Encoding.UTF8.GetString` in `EmitErrorAndExit`, which keeps it needed).

- [ ] **Step 4: Build both projects and run the affected suites**

```
scripts/build.ps1 build src/Ntilde.Platform
scripts/build.ps1 test tests/Ntilde.Platform.Tests
```

Expected: build succeeds; Platform.Tests PASS with 0 failed.

- [ ] **Step 5: Run the App.Tests lanes (these exercise the real PTY read loop)**

```
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot" --blame-hang-timeout 5m
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane=PlatformBoot" --blame-hang-timeout 5m
```

Expected: PASS, 0 failed, in both lanes. Record the executed counts — a run that reports a
summary but also `Test Run Aborted.` is not a result (the wrapper fails it for you).

> Known-flaky and not caused by this change: `PtyBackspaceAtLineStart…InWindowsPowerShell`
> (~33% on windows-latest). Re-run rather than debug.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.Pty/RustPtySession.cs src/Ntilde.Platform/Ssh/Sessions/NativeSshSession.cs
git commit -m "refactor(pty,ssh): decode output through Utf8ChunkDecoder

Same bytes in, same string out - the differential test pins that. What changes is
that both paths can now say where they are in the stream, which is what an
attaching multiplexer client needs.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: `PaneNode` mux fields

**Files:**
- Modify: `src/Ntilde.Pty/SessionModels.cs:36-49`
- Modify: `tests/Ntilde.App.Tests/Core/SessionManagerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `PaneNode.MuxSessionId` (`string?`), `PaneNode.MuxEndpoint` (`string?`). Nothing reads
  them in this phase.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Ntilde.App.Tests/Core/SessionManagerTests.cs` (inside the existing
`SessionManagerTests` class; the file already has `using Ntilde.Pty;`):

```csharp
    /// <summary>
    /// The multiplexer's per-pane attach coordinates ride in the session file. They are written
    /// and read back here well before anything consumes them, because the shape of a persisted
    /// field is the expensive thing to change later - a session file written by one build has to
    /// load in the next one.
    /// </summary>
    [Fact]
    public void PaneNode_MuxFields_SurviveAJsonRoundTrip()
    {
        var session = new NtildeSession
        {
            Tabs =
            [
                new TabSession
                {
                    Root = new PaneNode
                    {
                        Type = NodeType.Leaf,
                        PaneId = "pane-1",
                        MuxSessionId = "s-42",
                        MuxEndpoint = "npipe://ntilde-mux/default"
                    }
                }
            ]
        };

        string json = System.Text.Json.JsonSerializer.Serialize(
            session, SessionSerializationContext.Default.NtildeSession);
        NtildeSession? loaded = System.Text.Json.JsonSerializer.Deserialize(
            json, SessionSerializationContext.Default.NtildeSession);

        PaneNode? root = loaded?.Tabs[0].Root;
        Assert.NotNull(root);
        Assert.Equal("s-42", root!.MuxSessionId);
        Assert.Equal("npipe://ntilde-mux/default", root.MuxEndpoint);
    }

    /// <summary>
    /// Every session.json on every user's disk predates these fields. Loading one must produce
    /// nulls, not a throw and not a default that later reads as "attached to a mux".
    /// </summary>
    [Fact]
    public void PaneNode_LegacyJsonWithoutMuxFields_LoadsWithNulls()
    {
        const string legacyJson = """
        {
          "ActiveTabIndex": 0,
          "Tabs": [
            {
              "Title": "Terminal",
              "Root": { "Type": 0, "PaneId": "pane-1", "Command": "pwsh.exe" }
            }
          ]
        }
        """;

        NtildeSession? loaded = System.Text.Json.JsonSerializer.Deserialize(
            legacyJson, SessionSerializationContext.Default.NtildeSession);

        PaneNode? root = loaded?.Tabs[0].Root;
        Assert.NotNull(root);
        Assert.Null(root!.MuxSessionId);
        Assert.Null(root.MuxEndpoint);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~PaneNode_Mux OR FullyQualifiedName~PaneNode_Legacy" --blame-hang-timeout 5m
```

Expected: FAIL — `error CS0117: 'PaneNode' does not contain a definition for 'MuxSessionId'`.

- [ ] **Step 3: Add the fields**

In `src/Ntilde.Pty/SessionModels.cs`, inside `class PaneNode`, after the `Arguments` property:

```csharp
        // ── Multiplexer attach coordinates (Phase 0: persisted, not yet read) ─────
        //
        // Nullable and omitted when null, because every session.json written before the
        // multiplexer exists has to keep loading. Nothing consumes these yet; they land now
        // so the persisted shape is settled before the daemon needs it.

        /// <summary>
        /// Id of the multiplexer session this pane attaches to, or <c>null</c> for a pane that
        /// owns its PTY directly (every pane today).
        /// </summary>
        public string? MuxSessionId { get; set; }

        /// <summary>
        /// Transport address of the multiplexer that owns <see cref="MuxSessionId"/>, or
        /// <c>null</c>. Stored per pane rather than per session file so a window can hold panes
        /// from more than one daemon.
        /// </summary>
        public string? MuxEndpoint { get; set; }
```

`SessionSerializationContext` needs no change: it already serializes `NtildeSession`, and source
generation walks the whole graph.

- [ ] **Step 4: Run the tests to verify they pass**

```
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~PaneNode_Mux OR FullyQualifiedName~PaneNode_Legacy" --blame-hang-timeout 5m
```

Expected: PASS, 2 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Ntilde.Pty/SessionModels.cs tests/Ntilde.App.Tests/Core/SessionManagerTests.cs
git commit -m "feat(pty): persist per-pane multiplexer attach coordinates

Nullable and unread for now. The persisted shape is the expensive thing to change
once session files exist in the wild, so it lands ahead of the daemon.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: `ITerminalSessionCapabilities` and the pane's gating

**Files:**
- Create: `src/Ntilde.Pty/ITerminalSessionCapabilities.cs`
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs:3324` (`InitializeSessionCore` → `internal`), `:3475-3484`, `:3585-3593`, `:3612-3619`
- Create: `tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs` (capability tests; Task 5 adds the factory tests to the same file)

**Interfaces:**
- Consumes: `Ntilde.Pty.ITerminalSession`.
- Produces: `Ntilde.Pty.ITerminalSessionCapabilities` with `bool AnswersDeviceQueries { get; }` and
  `bool OrdersResizeInStream { get; }`; `TerminalPane.InitializeSessionCore` becomes `internal`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Controls;
using Ntilde.Pty;
using Ntilde.Replay;
using Xunit;

namespace Ntilde.Tests.Controls;

/// <summary>
/// A session that records what it was told, and optionally claims to answer device queries
/// itself. Nothing here spawns a process, so these tests cost nothing and leave nothing behind.
/// </summary>
internal sealed class FakeTerminalSession : ITerminalSession, ITerminalSessionCapabilities
{
    private readonly bool _answersDeviceQueries;

    public FakeTerminalSession(bool answersDeviceQueries = false)
    {
        _answersDeviceQueries = answersDeviceQueries;
    }

    public List<string> SentInput { get; } = new();

    public bool AnswersDeviceQueries => _answersDeviceQueries;
    public bool OrdersResizeInStream => false;

    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand { get; set; } = "fake-shell";
    public string? ShellArguments { get; set; }
    public bool IsProcessRunning => true;
    public bool HasActiveChildProcesses => false;
    public int? ExitCode { get; private set; }
    public bool IsRecording => false;
    public bool IsFlightRecording => false;

    public event Action<string>? OnOutputReceived;
    public event Action<int>? OnExit;

    public void SendInput(string input) => SentInput.Add(input);
    public void Resize(int cols, int rows) { }
    public void StartRecording(string filePath) { }
    public void StopRecording() { }
    public void EnableFlightRecording(long maxTotalBytes) { }
    public void DisableFlightRecording() { }
    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
    {
        info = default;
        return false;
    }

    public void Dispose() { }

    public void EmitOutput(string text) => OnOutputReceived?.Invoke(text);

    public void EmitExit(int code)
    {
        ExitCode = code;
        OnExit?.Invoke(code);
    }
}

/// <summary>
/// A session that does NOT implement <see cref="ITerminalSessionCapabilities"/> at all, which is
/// what every session in the app is today. Both capability flags must read as false for it.
/// </summary>
internal sealed class PlainFakeTerminalSession : ITerminalSession
{
    public List<string> SentInput { get; } = new();

    public Guid Id { get; } = Guid.NewGuid();
    public string ShellCommand { get; set; } = "plain-fake-shell";
    public string? ShellArguments { get; set; }
    public bool IsProcessRunning => true;
    public bool HasActiveChildProcesses => false;
    public int? ExitCode => null;
    public bool IsRecording => false;
    public bool IsFlightRecording => false;

    public event Action<string>? OnOutputReceived;
    public event Action<int>? OnExit;

    public void SendInput(string input) => SentInput.Add(input);
    public void Resize(int cols, int rows) { }
    public void StartRecording(string filePath) { }
    public void StopRecording() { }
    public void EnableFlightRecording(long maxTotalBytes) { }
    public void DisableFlightRecording() { }
    public bool TryExportFlightRecording(string filePath, out FlightExportInfo info)
    {
        info = default;
        return false;
    }

    public void Dispose() { }

    public void EmitOutput(string text) => OnOutputReceived?.Invoke(text);
    public void EmitExit(int code) => OnExit?.Invoke(code);
}

/// <summary>
/// A factory that hands back a session the test controls, and records the request it was given.
/// </summary>
internal sealed class RecordingSessionFactory : ITerminalSessionFactory
{
    private readonly ITerminalSession _session;

    public RecordingSessionFactory(ITerminalSession session) => _session = session;

    public TerminalSessionRequest? LastRequest { get; private set; }

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        LastRequest = request;
        return _session;
    }
}

public class PaneCapabilityGatingTests
{
    /// <summary>
    /// A session that answers device queries itself (the multiplexer daemon owns the
    /// authoritative parser, and it is the one that must reply) must not also receive the local
    /// parser's reply - the child would get two DA1 answers for one query.
    /// </summary>
    [AvaloniaFact]
    public void SessionThatAnswersDeviceQueries_DoesNotReceiveTheLocalParsersReply()
    {
        var session = new FakeTerminalSession(answersDeviceQueries: true);
        using var pane = new TerminalPane();
        pane.SessionFactory = new RecordingSessionFactory(session);

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("fake-shell", string.Empty, profile: null, cols: 80, rows: 24);

        pane.Parser!.OnResponse?.Invoke("\u001b[?62;c");

        Assert.Empty(session.SentInput);
    }

    /// <summary>
    /// The default - every session in the app today - still gets the reply, or DA1 goes
    /// unanswered and TUIs stall on their capability probe.
    /// </summary>
    [AvaloniaFact]
    public void SessionWithoutCapabilities_StillReceivesTheLocalParsersReply()
    {
        var session = new PlainFakeTerminalSession();
        using var pane = new TerminalPane();
        pane.SessionFactory = new RecordingSessionFactory(session);

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("fake-shell", string.Empty, profile: null, cols: 80, rows: 24);

        pane.Parser!.OnResponse?.Invoke("\u001b[?62;c");

        Assert.Equal(["\u001b[?62;c"], session.SentInput);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~PaneCapabilityGatingTests" --blame-hang-timeout 5m
```

Expected: FAIL — `ITerminalSessionCapabilities`, `ITerminalSessionFactory`,
`TerminalSessionRequest`, and `TerminalPane.SessionFactory` do not exist, and
`InitializeSessionCore` is not accessible.

> These tests need Task 5's `ITerminalSessionFactory` / `TerminalSessionRequest` /
> `TerminalPane.SessionFactory` to compile. That is deliberate: they share one pane fixture, and
> splitting the fixture across two files to buy a smaller red step is not worth it. Add the
> factory types from Task 5 Step 3 first if you want a compiling red; otherwise treat "does not
> compile" as this task's red and Task 5's as well.

- [ ] **Step 3: Add the capability interface**

Create `src/Ntilde.Pty/ITerminalSessionCapabilities.cs`:

```csharp
namespace Ntilde.Pty
{
    /// <summary>
    /// Optional companion to <see cref="ITerminalSession"/>: what a session does for itself that
    /// the host would otherwise do on its behalf.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="ITerminalSession"/> and deliberately optional. Every
    /// session that exists today - <c>RustPtySession</c>, <c>OpenSshSession</c>,
    /// <c>NativeSshSession</c> - does none of this, and a host tests with
    /// <c>session is ITerminalSessionCapabilities { … : true }</c>, so "not implemented" reads as
    /// all-false with no edit to any existing type.
    ///
    /// The sessions that will implement it are the multiplexer's: a pane attached to
    /// <c>ntilde mux serve</c> is a second, downstream parser over a stream whose authoritative
    /// parser lives in the daemon. Anything the local parser would write back to the child has to
    /// be suppressed, or the child receives it twice.
    /// </remarks>
    public interface ITerminalSessionCapabilities
    {
        /// <summary>
        /// The session answers terminal device queries (DA1/DA2, DSR cursor reports, DECRPM,
        /// OSC colour queries) itself, so the host must not forward its own parser's
        /// <c>OnResponse</c> output into <see cref="ITerminalIO.SendInput"/>, and must not send
        /// unsolicited in-band resize reports (kitty mode 2048).
        /// </summary>
        bool AnswersDeviceQueries { get; }

        /// <summary>
        /// The session sequences window-size changes inside the output stream rather than as an
        /// out-of-band <see cref="ITerminalLifecycle.Resize"/> that races it.
        /// </summary>
        /// <remarks>
        /// Declared only in Phase 0 and read by nobody. Phase 1 consumes it: when a multiplexer
        /// session orders resizes in-stream, the pane stops resizing its local buffer directly
        /// and lets the resize arrive at its stream position, which is what keeps an attached
        /// pane's reflow identical to the daemon's.
        /// </remarks>
        bool OrdersResizeInStream { get; }
    }
}
```

- [ ] **Step 4: Gate the pane's two write-back paths**

In `src/Ntilde.App/Controls/TerminalPane.axaml.cs`:

(a) Change the signature at `:3324` from `private void InitializeSessionCore(` to
`internal void InitializeSessionCore(` — matching `internal void CreateAndWireParser()` above it,
so the seam can be driven from a test without spinning up a shell.

(b) Add this helper next to `InitializeSessionCore`:

```csharp
        /// <summary>
        /// True when <paramref name="session"/> answers device queries itself, so this pane's
        /// parser must not write its replies back to the child.
        /// </summary>
        /// <remarks>
        /// Evaluated at call time rather than cached, because the cached TermView handlers in
        /// <see cref="WireReusedTermViewHandlers"/> outlive any individual session: a pane can
        /// reconnect from a multiplexer-backed session to a local one and back.
        /// </remarks>
        private static bool SessionAnswersDeviceQueries(ITerminalSession? session)
            => session is ITerminalSessionCapabilities { AnswersDeviceQueries: true };
```

(c) Replace the `Parser.OnResponse` wiring at `:3477-3484`:

```csharp
            // Wire up Parser responses (e.g. DA1). The accumulator invalidation for these lives in
            // CreateAndWireParser, next to the parser's other observers.
            //
            // Skipped for a session that answers device queries itself: a multiplexer-attached
            // pane runs a second, downstream parser over a stream whose authoritative parser is
            // in the daemon, and both replying means the child gets two answers to one query.
            // The ObserveDeviceReply observer in CreateAndWireParser deliberately stays wired
            // either way - it only invalidates a local heuristic and writes nothing to the child.
            if (!SessionAnswersDeviceQueries(Session))
            {
                Parser.OnResponse += response =>
                {
                    Session.SendInput(response);
                };
            }
```

(d) In `WireReusedTermViewHandlers`, widen both mode-2048 guards. At `:3586`:

```csharp
                if (Parser is { InBandResizeReportsEnabled: true } && !SessionAnswersDeviceQueries(Session))
```

and at `:3613`:

```csharp
                if (Parser is { InBandResizeReportsEnabled: true } && Buffer != null
                    && !SessionAnswersDeviceQueries(Session) && cwMetric > 0 && chMetric > 0)
```

Leave the resize flow itself (`Session?.Resize(c, r)`) exactly as it is — `OrdersResizeInStream`
is declared only in this phase.

- [ ] **Step 5: Run the tests to verify they pass**

Run after Task 5 Step 3 has added the factory types (see the note in Step 2):

```
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~PaneCapabilityGatingTests" --blame-hang-timeout 5m
```

Expected: PASS, 2 passed.

- [ ] **Step 6: Commit** (together with Task 5, since they share the test file — see Task 5 Step 7)

---

## Task 5: `ITerminalSessionFactory` seam

**Files:**
- Create: `src/Ntilde.Pty/ITerminalSessionFactory.cs`
- Create: `src/Ntilde.App/Shell/DefaultTerminalSessionFactory.cs`
- Modify: `src/Ntilde.App/Controls/TerminalPane.axaml.cs:3390-3425` (the two spawn branches)
- Modify: `src/Ntilde.App/Shell/AppServiceBundle.cs`
- Modify: `src/Ntilde.App/MainWindow.axaml.cs:3682`, `:4499`
- Modify: `tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs`

**Interfaces:**
- Consumes: `Ntilde.Pty.ITerminalSession`.
- Produces:
  - `Ntilde.Pty.TerminalSessionRequest(string Command, string Arguments, string StartingDirectory, int Cols, int Rows, IReadOnlyDictionary<string,string>? EnvironmentOverrides, bool SkipPowerShellPostLaunchInit, SshSessionDescriptor? Ssh)`
  - `Ntilde.Pty.SshSessionDescriptor(Guid ProfileId, int DiagnosticsLevel, object? InteractionHandler, bool NativeSshEnabled)`
  - `Ntilde.Pty.ITerminalSessionFactory.Create(TerminalSessionRequest request) → ITerminalSession`
  - `Ntilde.Shell.DefaultTerminalSessionFactory.Instance`
  - `TerminalPane.SessionFactory` (`internal ITerminalSessionFactory?`, defaults to the above)

- [ ] **Step 1: Add the factory tests to the existing test file**

Append to `tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs`:

```csharp
public class PaneSessionFactoryTests
{
    /// <summary>
    /// The local branch's request must carry everything the inline <c>new RustPtySession(...)</c>
    /// used to pass positionally. Asserted field by field, because a request that silently drops
    /// the working directory or the shell-integration env overrides produces a pane that starts
    /// in the wrong place with integration off, and nothing throws.
    /// </summary>
    [AvaloniaFact]
    public void LocalSpawn_HandsTheFactoryTheShellCommandCwdGeometryAndEnv()
    {
        var session = new PlainFakeTerminalSession();
        var factory = new RecordingSessionFactory(session);
        using var pane = new TerminalPane();
        pane.SessionFactory = factory;

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("bash", "-l", profile: null, cols: 100, rows: 40);

        TerminalSessionRequest? request = factory.LastRequest;
        Assert.NotNull(request);
        Assert.Equal("bash", request!.Command);
        Assert.Equal("-l", request.Arguments);
        Assert.Equal(100, request.Cols);
        Assert.Equal(40, request.Rows);
        Assert.Null(request.Ssh);
    }

    /// <summary>
    /// The factory's session has to end up wired, not merely returned: output reaches the parser
    /// and therefore the buffer, and exit reaches HandleSessionExit. Both are asserted through
    /// observable effects rather than by reading private handler lists.
    /// </summary>
    [AvaloniaFact]
    public void FactorySession_IsWiredForOutputAndExit()
    {
        var session = new PlainFakeTerminalSession();
        using var pane = new TerminalPane();
        pane.SessionFactory = new RecordingSessionFactory(session);

        pane.CreateAndWireParser();
        pane.InitializeSessionCore("bash", string.Empty, profile: null, cols: 80, rows: 24);

        Assert.Same(session, pane.Session);

        session.EmitOutput("hello");
        Dispatcher.UIThread.RunJobs();

        pane.Buffer!.Lock.EnterReadLock();
        try
        {
            string firstRow = new string(
                Array.ConvertAll(pane.Buffer.ViewportRows[0].Cells, c => c.Character == '\0' ? ' ' : c.Character));
            Assert.StartsWith("hello", firstRow);
        }
        finally
        {
            pane.Buffer.Lock.ExitReadLock();
        }

        session.EmitExit(3);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, pane.LastExitCode);
    }

    /// <summary>
    /// A pane created outside the window's wiring funnel still spawns. Unlike
    /// CommandAssistServices - which throws when unset, because a second graph would be silently
    /// wrong - there is exactly one right default here, so defaulting beats throwing.
    /// </summary>
    [AvaloniaFact]
    public void SessionFactory_DefaultsToTheProductionFactory()
    {
        using var pane = new TerminalPane();

        Assert.Same(Ntilde.Shell.DefaultTerminalSessionFactory.Instance, pane.SessionFactory);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~PaneSessionFactoryTests" --blame-hang-timeout 5m
```

Expected: FAIL to compile — `ITerminalSessionFactory`, `TerminalSessionRequest`,
`TerminalPane.SessionFactory`, `DefaultTerminalSessionFactory` do not exist.

- [ ] **Step 3: Add the contract types to `Ntilde.Pty`**

Create `src/Ntilde.Pty/ITerminalSessionFactory.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Ntilde.Pty
{
    /// <summary>
    /// Everything a host knows about a session it wants opened, in one value.
    /// </summary>
    /// <remarks>
    /// A record rather than a parameter list because the point of the seam is that the host stops
    /// choosing an implementation: today's caller picks between <c>RustPtySession</c> and the SSH
    /// factory inline, and a multiplexer-backed pane must be able to route the same request to a
    /// daemon instead. A single value also means adding a field later does not re-break every
    /// implementation's signature.
    /// </remarks>
    /// <param name="Command">
    /// The executable to run, already split from any inline arguments and already merged with the
    /// shell-integration launch plan. Ignored when <paramref name="Ssh"/> is set.
    /// </param>
    /// <param name="Arguments">Arguments as a single command-line string; may be empty.</param>
    /// <param name="StartingDirectory">Working directory, or empty for the host's default.</param>
    /// <param name="Cols">Initial column count.</param>
    /// <param name="Rows">Initial row count.</param>
    /// <param name="EnvironmentOverrides">
    /// Extra environment for the child (the shell-integration bootstrap's variables), or null.
    /// </param>
    /// <param name="SkipPowerShellPostLaunchInit">
    /// Suppresses the PowerShell post-launch init injection, because the shell-integration launch
    /// plan already did that work.
    /// </param>
    /// <param name="Ssh">
    /// Set for an SSH pane, null for a local one. When set, every local field above is ignored.
    /// </param>
    public sealed record TerminalSessionRequest(
        string Command,
        string Arguments,
        string StartingDirectory,
        int Cols,
        int Rows,
        IReadOnlyDictionary<string, string>? EnvironmentOverrides,
        bool SkipPowerShellPostLaunchInit,
        SshSessionDescriptor? Ssh);

    /// <summary>
    /// The SSH half of a <see cref="TerminalSessionRequest"/>, kept deliberately opaque.
    /// </summary>
    /// <remarks>
    /// SSH profiles, diagnostics levels and interaction handlers all live in
    /// <c>Ntilde.Platform</c>, which references this assembly - so naming those types here would
    /// invert the dependency. The descriptor therefore carries a profile id, an integral
    /// diagnostics level and an untyped handler, and the implementation in the layer that owns
    /// those types does the resolution. Phase 0 keeps that resolution exactly where it is today
    /// (in App); only the call site moves.
    /// </remarks>
    /// <param name="ProfileId">Identifies the stored SSH profile to connect with.</param>
    /// <param name="DiagnosticsLevel">
    /// Integral value of <c>Ntilde.Platform.Ssh.Launch.SshDiagnosticsLevel</c>
    /// (0 = None, 1 = Verbose, 2 = VeryVerbose).
    /// </param>
    /// <param name="InteractionHandler">
    /// The host's <c>Ntilde.Platform.Ssh.Interactions.ISshInteractionHandler</c>, or null. Untyped
    /// for the layering reason above; implementations cast.
    /// </param>
    /// <param name="NativeSshEnabled">Whether the native SSH backend is permitted.</param>
    public sealed record SshSessionDescriptor(
        Guid ProfileId,
        int DiagnosticsLevel,
        object? InteractionHandler,
        bool NativeSshEnabled);

    /// <summary>
    /// Opens terminal sessions. One method, because the whole point is that the caller expresses
    /// what it wants and the implementation decides what backs it.
    /// </summary>
    public interface ITerminalSessionFactory
    {
        /// <summary>
        /// Opens a session for <paramref name="request"/>, or throws describing why it could not.
        /// Never returns a fallback session of a different kind than the one asked for - a
        /// request that named an SSH profile and quietly produced a local shell is worse than a
        /// visible failure.
        /// </summary>
        ITerminalSession Create(TerminalSessionRequest request);
    }
}
```

- [ ] **Step 4: Add the production factory to `Ntilde.App`**

Create `src/Ntilde.App/Shell/DefaultTerminalSessionFactory.cs`:

```csharp
using System;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Launch;
using Ntilde.Platform.Ssh.Sessions;
using Ntilde.Pty;

namespace Ntilde.Shell;

/// <summary>
/// The factory the app runs on: the two spawn branches that used to sit inline in
/// <c>TerminalPane.InitializeSessionCore</c>, moved without behaviour change.
/// </summary>
/// <remarks>
/// Stateless and shared. Everything that used to be read off the pane at spawn time - the
/// interaction handler, the native-SSH toggle, the diagnostics level - arrives in the request,
/// which is what lets a multiplexer factory be substituted later without the pane knowing.
/// </remarks>
public sealed class DefaultTerminalSessionFactory : ITerminalSessionFactory
{
    public static DefaultTerminalSessionFactory Instance { get; } = new();

    public ITerminalSession Create(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Ssh is { } ssh)
        {
            var sessionFactory = new SshSessionFactory(
                nativeInteractionHandler: ssh.InteractionHandler as ISshInteractionHandler,
                nativeSshEnabled: ssh.NativeSshEnabled);

            return sessionFactory.Create(
                ssh.ProfileId,
                request.Cols,
                request.Rows,
                (SshDiagnosticsLevel)ssh.DiagnosticsLevel,
                null,
                log: TerminalLogger.Log);
        }

        return new RustPtySession(
            request.Command,
            request.Cols,
            request.Rows,
            request.Arguments,
            request.StartingDirectory,
            skipPowerShellPostLaunchInit: request.SkipPowerShellPostLaunchInit,
            environmentOverrides: request.EnvironmentOverrides);
    }
}
```

> `TerminalLogger` lives in `Ntilde.VT`; `Ntilde.App` references it, so this compiles. Check the
> `using` the existing call site uses at `TerminalPane.axaml.cs:3403` and match it.

- [ ] **Step 5: Route `InitializeSessionCore` through the factory**

In `src/Ntilde.App/Controls/TerminalPane.axaml.cs`, add the injectable property next to
`CommandAssistServices` (around `:319`):

```csharp
        private ITerminalSessionFactory? _sessionFactory;

        /// <summary>
        /// Opens this pane's terminal session. Assigned by <c>MainWindow.WirePane</c> from the
        /// instance in <c>AppServiceBundle</c>; defaults to
        /// <see cref="Ntilde.Shell.DefaultTerminalSessionFactory.Instance"/>.
        /// </summary>
        /// <remarks>
        /// Defaulted rather than throwing-when-unset, which is where this differs from
        /// <see cref="CommandAssistServices"/>. That one throws because building a second graph
        /// is silently wrong; here there is exactly one right production implementation, and the
        /// pane has four public constructors reached from three creation sites plus session
        /// restore, so a default keeps every one of them spawning without a wiring edit.
        /// </remarks>
        internal ITerminalSessionFactory SessionFactory
        {
            get => _sessionFactory ??= Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
            set => _sessionFactory = value;
        }
```

Replace the two spawn branches at `:3389-3425`. The block that currently reads from
`if (profile != null && profile.Type == ConnectionType.SSH)` through the `Session ??= new
RustPtySession(...)` call becomes:

```csharp
                bool isSsh = profile != null && profile.Type == ConnectionType.SSH;
                var request = new TerminalSessionRequest(
                    Command: effectiveShell,
                    Arguments: args,
                    StartingDirectory: startingDir,
                    Cols: cols,
                    Rows: rows,
                    EnvironmentOverrides: _shellIntegrationEnvOverrides,
                    SkipPowerShellPostLaunchInit: _isShellIntegrationActive,
                    Ssh: isSsh
                        ? new SshSessionDescriptor(
                            ProfileId: profile!.Id,
                            DiagnosticsLevel: (int)_sshDiagnosticsLevel,
                            InteractionHandler: SshInteractionHandler,
                            NativeSshEnabled: _settings?.ExperimentalNativeSshEnabled ?? false)
                        : null);

                if (isSsh)
                {
                    try
                    {
                        Session = SessionFactory.Create(request);
                        ShellCommand = Session.ShellCommand;
                        ShellArgs = string.Empty;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TerminalPane] SSH connection failed for '{profile!.Name}': {ex.Message}");
                        WriteBanner($"\r\n[ERROR] SSH Connection Failed: {SanitizeBannerValue(ex.Message)}\r\n");

                        // Fail loudly: Do not fall back to RustPtySession with missing arguments.
                        return;
                    }
                }
                else
                {
                    Session = SessionFactory.Create(request);
                }
```

Everything after it — `TermView.SetSession(Session)`, the `OnExit` wiring,
`RegisterActiveSshSession`, the agent registration block, the outer `catch`, the output wiring and
`WireReusedTermViewHandlers()` — is unchanged.

> Two behaviours that must survive verbatim, and are easy to lose while moving this:
> the SSH branch's `ShellCommand = Session.ShellCommand` fix-up, and its `return` (never fall
> through to a local shell). The local branch keeps the outer `try`'s catch, which turns a spawn
> failure into the `[ERROR] Failed to spawn process` banner.
>
> The `Session ??=` of the original is not needed: the SSH branch either assigned `Session` or
> returned, so the local branch only ever ran with `Session` null.

- [ ] **Step 6: Wire it at the composition root**

In `src/Ntilde.App/Shell/AppServiceBundle.cs`:

```csharp
using Ntilde.Pty;

namespace Ntilde.Shell;

/// <summary>
/// <paramref name="Settings"/> is null in production (MainWindow loads settings.json from disk).
/// A non-null instance bypasses that load entirely: BuildForDesigner supplies fresh defaults so
/// designer previews and test-created windows never read the developer's live settings file,
/// whose contents (e.g. TabStripOrientation) would otherwise leak into layout assertions.
///
/// <paramref name="SessionFactory"/> is null everywhere today, which means
/// <see cref="DefaultTerminalSessionFactory"/>. It exists so a future multiplexer client can be
/// substituted at the composition root rather than inside the pane.
/// </summary>
public sealed record AppServiceBundle(
    StartupOrchestrator Startup,
    CommandAssistServices CommandAssist,
    TerminalSettings? Settings = null,
    ITerminalSessionFactory? SessionFactory = null);
```

In `src/Ntilde.App/MainWindow.axaml.cs`, next to `private readonly CommandAssistServices
_commandAssistServices;` at `:261`:

```csharp
        private readonly Ntilde.Pty.ITerminalSessionFactory _sessionFactory;
```

next to `:3682`:

```csharp
            _sessionFactory = services.SessionFactory ?? Ntilde.Shell.DefaultTerminalSessionFactory.Instance;
```

and in `WirePane`, next to `:4499`:

```csharp
            pane.SessionFactory = _sessionFactory;
```

- [ ] **Step 7: Run the tests to verify they pass**

```
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "FullyQualifiedName~PaneSessionFactoryTests OR FullyQualifiedName~PaneCapabilityGatingTests" --blame-hang-timeout 5m
```

Expected: PASS, 5 passed (3 factory + 2 capability).

- [ ] **Step 8: Run both App.Tests lanes in full**

```
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot" --blame-hang-timeout 5m
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane=PlatformBoot" --blame-hang-timeout 5m
```

Expected: PASS, 0 failed, both lanes. This is the gate for the spawn-path change — a pane that
stopped spawning shows up here and nowhere else.

- [ ] **Step 9: Commit**

```bash
git add src/Ntilde.Pty/ITerminalSessionCapabilities.cs src/Ntilde.Pty/ITerminalSessionFactory.cs \
        src/Ntilde.App/Shell/DefaultTerminalSessionFactory.cs src/Ntilde.App/Shell/AppServiceBundle.cs \
        src/Ntilde.App/MainWindow.axaml.cs src/Ntilde.App/Controls/TerminalPane.axaml.cs \
        tests/Ntilde.App.Tests/Controls/PaneSessionFactoryTests.cs
git commit -m "feat(app,pty): route pane session spawn through ITerminalSessionFactory

The two inline branches move into DefaultTerminalSessionFactory unchanged, including
the SSH ShellCommand fix-up and the refusal to fall back to a local shell. Sessions
can now also declare that they answer device queries themselves, which suppresses the
pane's OnResponse write-back and its unsolicited mode-2048 reports.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: `AnsiParser` state export/import

**Files:**
- Create: `src/Ntilde.VT/AnsiParserState.cs`
- Create: `src/Ntilde.VT/AnsiParser.StateTransfer.cs`
- Modify: `src/Ntilde.VT/AnsiParser.cs:9` (`public class` → `public partial class`)
- Create: `tests/Ntilde.VT.Tests/StateTransfer/AnsiParserStateTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Ntilde.VT.AnsiParserState` (a JSON-serializable class, fields listed below);
  `AnsiParser.ExportState() → AnsiParserState`; `AnsiParser.ImportState(AnsiParserState)`.

**The audit.** Every `AnsiParser` instance field that survives a `Process()` call, from
`src/Ntilde.VT/AnsiParser.cs` lines 12-295:

| Field | In state? | Why |
|---|---|---|
| `_state` | yes | the escape state machine's position |
| `_paramBuffer[0.._paramLen]` | yes | a CSI split across chunks |
| `_paramLen` | yes | implied by the exported slice's length |
| `_csiTruncated` | yes | decides whether the surviving prefix can be classified |
| `_oscStringBuffer` | yes | an OSC split across chunks |
| `_apcStringBuffer` | yes | an APC split across chunks |
| `_dcsStringBuffer` | yes | a DCS split across chunks |
| `_kittyPayloadBuffer` | yes | a chunked (`m=1`) kitty payload |
| `_kittyPayloadOverflow` | yes | latched once the chunked payload passed the cap |
| `_kittyPendingParams` | yes | the control params of a chunked kitty transmission |
| `_charsets[0..3]` | yes | DEC G0-G3 designations |
| `_gl` | yes | which slot is shifted to GL |
| `_pendingCharsetSlot` | yes | a designation split across chunks |
| `_swallowNextNewline` | yes | eats the newline after an inline image |
| `_verticalOffset` | yes | ConPTY inline-image cursor sync |
| `_lastGraphicChar` | yes | what `CSI Ps b` (REP) repeats |
| `_inBandResizeReportsEnabled` | yes | DECSET 2048 |
| `_textBuffer` | no | flushed before every state-affecting branch; always empty between `Process()` calls. Asserted, not assumed — see the test below. |
| `_sawCursorHideInBatch`, `_sawCursorShowAfterHideInBatch` | no | per-batch, reset inside `Process()` |
| `_isConPtyFilteringLikely` | no | construction-time configuration, not state; both sides must be constructed with the same `forceConPtyFiltering` |
| `_hyperlinks` (registry) | no | an interning cache. Identity travels through the buffer's hyperlink table (Task 7); re-interning on the far side would mint fresh instances for ids the table already resolved. |
| `_buffer` | no | the parser's target, supplied at construction |
| `ImageDecoder`, `ReadFileBytes`, `CellWidth`, `CellHeight`, `DefaultForeground`, `DefaultBackground`, `KittyKeyboardEnabled`, `AllowNativeKittyGraphics`, `MaxStringSequenceChars`, the `On*` delegates | no | host-supplied configuration and callbacks |

- [ ] **Step 1: Write the failing tests**

Create `tests/Ntilde.VT.Tests/StateTransfer/AnsiParserStateTests.cs`:

```csharp
using System;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// Pins that a parser's cross-call state is fully transferable. The parity test in
/// <c>SnapshotTailParityTests</c> is the real proof; these are the targeted cases that say
/// <em>which</em> field broke when it fails.
/// </summary>
public class AnsiParserStateTests
{
    private static (AnsiParser Parser, TerminalBuffer Buffer) NewPair(int cols = 80, int rows = 24)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        return (parser, buffer);
    }

    /// <summary>
    /// Transfers <paramref name="source"/>'s state onto a fresh parser over a fresh buffer of the
    /// same size, then returns the pair.
    /// </summary>
    private static (AnsiParser Parser, TerminalBuffer Buffer) Transfer(AnsiParser source, TerminalBuffer sourceBuffer)
    {
        (AnsiParser parser, TerminalBuffer buffer) = NewPair(sourceBuffer.Cols, sourceBuffer.Rows);
        parser.ImportState(source.ExportState());
        return (parser, buffer);
    }

    [Fact]
    public void CsiSplitAcrossChunks_CompletesAfterTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b[1;3");

        (AnsiParser c, TerminalBuffer bufC) = Transfer(a, bufA);
        c.Process("1H");
        a.Process("1H");

        Assert.Equal(a.ExportState().ToDebugString(), c.ExportState().ToDebugString());
        Assert.Equal(bufA.CursorRow, bufC.CursorRow);
        Assert.Equal(bufA.CursorCol, bufC.CursorCol);
    }

    [Fact]
    public void OscSplitAcrossChunks_CompletesAfterTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        string? titleA = null;
        a.OnTitleChanged = t => titleA = t;
        a.Process("\u001b]0;my ti");

        (AnsiParser c, _) = Transfer(a, bufA);
        string? titleC = null;
        c.OnTitleChanged = t => titleC = t;

        a.Process("tle\u0007");
        c.Process("tle\u0007");

        Assert.Equal("my title", titleA);
        Assert.Equal(titleA, titleC);
    }

    [Fact]
    public void ChunkedKittyPayload_ResumesAfterTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b_Ga=T,f=24,s=1,v=1,m=1;AAAA\u001b\\");

        AnsiParserState state = a.ExportState();

        Assert.NotEmpty(state.KittyPayload);
        Assert.NotEmpty(state.KittyPendingParams);

        (AnsiParser c, _) = Transfer(a, bufA);
        Assert.Equal(state.ToDebugString(), c.ExportState().ToDebugString());
    }

    [Fact]
    public void CharsetDesignationAndShift_SurviveTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b(0\u000e");  // designate G0 as DEC special graphics, then SO

        (AnsiParser c, TerminalBuffer bufC) = Transfer(a, bufA);
        a.Process("q");
        c.Process("q");

        bufA.Lock.EnterReadLock();
        bufC.Lock.EnterReadLock();
        try
        {
            Assert.Equal(bufA.ViewportRows[0].Cells[0].Character, bufC.ViewportRows[0].Cells[0].Character);
            Assert.NotEqual('q', bufC.ViewportRows[0].Cells[0].Character);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
            bufA.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void InBandResizeMode2048_SurvivesTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b[?2048h");
        Assert.True(a.InBandResizeReportsEnabled);

        (AnsiParser c, _) = Transfer(a, bufA);
        Assert.True(c.InBandResizeReportsEnabled);
    }

    [Fact]
    public void RepeatCharacter_RepeatsTheSameCharacterAfterTransfer()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("x");

        (AnsiParser c, TerminalBuffer bufC) = Transfer(a, bufA);
        a.Process("\u001b[3b");
        c.Process("\u001b[3b");

        bufA.Lock.EnterReadLock();
        bufC.Lock.EnterReadLock();
        try
        {
            for (int col = 0; col < 4; col++)
            {
                Assert.Equal(bufA.ViewportRows[0].Cells[col].Character, bufC.ViewportRows[0].Cells[col].Character);
            }

            Assert.Equal('x', bufC.ViewportRows[0].Cells[3].Character);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
            bufA.Lock.ExitReadLock();
        }
    }

    /// <summary>
    /// The text batching buffer is deliberately not exported, on the claim that it is always
    /// empty when <c>Process()</c> returns. That claim is load-bearing - if it were ever false,
    /// a snapshot would silently drop pending printable text - so it is asserted, not assumed.
    /// </summary>
    [Theory]
    [InlineData("plain text")]
    [InlineData("text then \u001b[31mSGR")]
    [InlineData("wide 你好 and emoji \U0001F600")]
    [InlineData("incomplete escape \u001b[1;")]
    [InlineData("\u001b]8;;https://example.com\u0007linked")]
    public void TextBatchingBuffer_IsEmptyWhenProcessReturns(string input)
    {
        (AnsiParser a, _) = NewPair();
        a.Process(input);

        Assert.Equal(0, a.ExportState().PendingTextLength);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```
scripts/build.ps1 test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~AnsiParserStateTests"
```

Expected: FAIL — `AnsiParserState`, `ExportState`, `ImportState` do not exist.

- [ ] **Step 3: Add `AnsiParserState`**

Create `src/Ntilde.VT/AnsiParserState.cs`:

```csharp
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace Ntilde.VT
{
    /// <summary>
    /// Every <see cref="AnsiParser"/> field that survives a <c>Process()</c> call, so a second
    /// parser can be dropped into the first one's position mid-escape-sequence and see the same
    /// stream the same way.
    /// </summary>
    /// <remarks>
    /// Read-only configuration is deliberately absent: the image decoder, cell metrics, default
    /// colours, the kitty kill switch, the string-sequence cap, and the ConPTY-filtering flag are
    /// host settings, not stream position. Two parsers exchanging state must be constructed with
    /// the same <c>forceConPtyFiltering</c>, because that one changes how a sequence is
    /// interpreted rather than what has been seen.
    ///
    /// The hyperlink registry is also absent by design: link identity is reference equality, so
    /// it travels with the buffer's hyperlink table (<see cref="TerminalStateSnapshot"/>) where
    /// cells can point at the same instances. Re-interning here would mint fresh instances for
    /// ids the table had already resolved, and adjacent cells would stop grouping.
    /// </remarks>
    public sealed class AnsiParserState
    {
        /// <summary>Escape state-machine position, as the ordinal of <c>AnsiParser.State</c>.</summary>
        [JsonPropertyName("state")] public int State { get; set; }

        /// <summary>The live CSI parameter/intermediate run, empty when not inside a CSI.</summary>
        [JsonPropertyName("csi")] public string CsiParams { get; set; } = string.Empty;

        /// <summary>The CSI run overflowed its cap, so the surviving prefix cannot be classified.</summary>
        [JsonPropertyName("csi_trunc")] public bool CsiTruncated { get; set; }

        [JsonPropertyName("osc")] public string OscBuffer { get; set; } = string.Empty;
        [JsonPropertyName("apc")] public string ApcBuffer { get; set; } = string.Empty;
        [JsonPropertyName("dcs")] public string DcsBuffer { get; set; } = string.Empty;

        /// <summary>Accumulated payload of a chunked (<c>m=1</c>) kitty graphics transmission.</summary>
        [JsonPropertyName("kitty_payload")] public string KittyPayload { get; set; } = string.Empty;

        /// <summary>Latched once a chunked kitty payload exceeded its cap.</summary>
        [JsonPropertyName("kitty_overflow")] public bool KittyPayloadOverflow { get; set; }

        /// <summary>Control parameters of the in-flight chunked kitty transmission.</summary>
        [JsonPropertyName("kitty_params")]
        public Dictionary<string, string> KittyPendingParams { get; set; } = new();

        /// <summary>G0..G3 designations, as ordinals of <c>AnsiParser.Charset</c>. Length 4.</summary>
        [JsonPropertyName("charsets")] public int[] Charsets { get; set; } = new int[4];

        /// <summary>Which of <see cref="Charsets"/> is shifted to GL (0..3).</summary>
        [JsonPropertyName("gl")] public int Gl { get; set; }

        /// <summary>Slot awaiting its designation byte, or -1.</summary>
        [JsonPropertyName("pending_charset")] public int PendingCharsetSlot { get; set; } = -1;

        [JsonPropertyName("swallow_nl")] public bool SwallowNextNewline { get; set; }
        [JsonPropertyName("voffset")] public int VerticalOffset { get; set; }

        /// <summary>What <c>CSI Ps b</c> would repeat, or null.</summary>
        [JsonPropertyName("last_graphic")] public string? LastGraphicChar { get; set; }

        /// <summary>DECSET 2048 - kitty in-band resize reports.</summary>
        [JsonPropertyName("inband_resize")] public bool InBandResizeReportsEnabled { get; set; }

        /// <summary>
        /// Length of the parser's text-batching buffer at export. Always 0 in practice - the
        /// buffer is flushed before every state-affecting branch - and exported purely so that
        /// claim can be asserted rather than assumed. Not re-imported.
        /// </summary>
        [JsonPropertyName("pending_text_len")] public int PendingTextLength { get; set; }

        /// <summary>
        /// A stable, human-readable rendering for assertions and failure messages. Not a
        /// serialization format - use <c>TerminalStateSerializer</c> for that.
        /// </summary>
        public string ToDebugString()
        {
            var sb = new StringBuilder();
            sb.Append("state=").Append(State);
            sb.Append(" csi=").Append(CsiParams).Append(" trunc=").Append(CsiTruncated);
            sb.Append(" osc=").Append(OscBuffer);
            sb.Append(" apc=").Append(ApcBuffer);
            sb.Append(" dcs=").Append(DcsBuffer);
            sb.Append(" kitty=").Append(KittyPayload).Append(" ovf=").Append(KittyPayloadOverflow);
            sb.Append(" kparams=");
            foreach (KeyValuePair<string, string> kv in KittyPendingParams)
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(',');
            }

            sb.Append(" charsets=");
            foreach (int c in Charsets)
            {
                sb.Append(c).Append(',');
            }

            sb.Append(" gl=").Append(Gl).Append(" pend=").Append(PendingCharsetSlot);
            sb.Append(" swallow=").Append(SwallowNextNewline);
            sb.Append(" voff=").Append(VerticalOffset);
            sb.Append(" lastgfx=").Append(LastGraphicChar ?? "<null>");
            sb.Append(" inband=").Append(InBandResizeReportsEnabled);
            return sb.ToString();
        }
    }
}
```

> `KittyPendingParams` must be serialized in a stable order for `ToDebugString` to be a fair
> comparison. `Dictionary<string,string>` preserves insertion order in practice but does not
> promise it; if `ChunkedKittyPayload_ResumesAfterTransfer` ever flakes on ordering, sort the
> keys in `ToDebugString` rather than changing the dictionary type (the parser's own field is a
> `Dictionary` and copying it verbatim is the point).

- [ ] **Step 4: Make `AnsiParser` partial and add the transfer methods**

In `src/Ntilde.VT/AnsiParser.cs:9`, change:

```csharp
    public class AnsiParser
```

to:

```csharp
    public partial class AnsiParser
```

Create `src/Ntilde.VT/AnsiParser.StateTransfer.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace Ntilde.VT
{
    public partial class AnsiParser
    {
        /// <summary>
        /// Captures every field that survives a <see cref="Process"/> call, so another parser can
        /// resume this one's position in the stream - mid-CSI, mid-OSC, mid-kitty-payload, mid
        /// charset designation. See <see cref="AnsiParserState"/> for what is deliberately left out.
        /// </summary>
        public AnsiParserState ExportState()
        {
            var state = new AnsiParserState
            {
                State = (int)_state,
                CsiParams = new string(_paramBuffer, 0, _paramLen),
                CsiTruncated = _csiTruncated,
                OscBuffer = new string(_oscStringBuffer.ToArray()),
                ApcBuffer = new string(_apcStringBuffer.ToArray()),
                DcsBuffer = new string(_dcsStringBuffer.ToArray()),
                KittyPayload = _kittyPayloadBuffer.ToString(),
                KittyPayloadOverflow = _kittyPayloadOverflow,
                KittyPendingParams = new Dictionary<string, string>(_kittyPendingParams),
                Charsets = new int[4],
                Gl = _gl,
                PendingCharsetSlot = _pendingCharsetSlot,
                SwallowNextNewline = _swallowNextNewline,
                VerticalOffset = _verticalOffset,
                LastGraphicChar = _lastGraphicChar?.ToString(),
                InBandResizeReportsEnabled = _inBandResizeReportsEnabled,
                PendingTextLength = _textBuffer.Length,
            };

            for (int i = 0; i < 4; i++)
            {
                state.Charsets[i] = (int)_charsets[i];
            }

            return state;
        }

        /// <summary>
        /// Adopts <paramref name="state"/> wholesale. The caller is responsible for having
        /// constructed this parser with the same <c>forceConPtyFiltering</c> as the exporter and
        /// for importing the matching <see cref="TerminalStateSnapshot"/> into this parser's
        /// buffer - the two halves are only meaningful together.
        /// </summary>
        public void ImportState(AnsiParserState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            _state = (State)state.State;

            EnsureParamCapacity(state.CsiParams.Length);
            state.CsiParams.AsSpan().CopyTo(_paramBuffer);
            _paramLen = state.CsiParams.Length;
            _csiTruncated = state.CsiTruncated;

            _oscStringBuffer.Clear();
            _oscStringBuffer.AddRange(state.OscBuffer);
            _apcStringBuffer.Clear();
            _apcStringBuffer.AddRange(state.ApcBuffer);
            _dcsStringBuffer.Clear();
            _dcsStringBuffer.AddRange(state.DcsBuffer);

            _kittyPayloadBuffer.Clear();
            _kittyPayloadBuffer.Append(state.KittyPayload);
            _kittyPayloadOverflow = state.KittyPayloadOverflow;
            _kittyPendingParams = new Dictionary<string, string>(state.KittyPendingParams);

            for (int i = 0; i < 4 && i < state.Charsets.Length; i++)
            {
                _charsets[i] = (Charset)state.Charsets[i];
            }

            _gl = state.Gl;
            _pendingCharsetSlot = state.PendingCharsetSlot;
            _swallowNextNewline = state.SwallowNextNewline;
            _verticalOffset = state.VerticalOffset;
            _lastGraphicChar = string.IsNullOrEmpty(state.LastGraphicChar) ? null : state.LastGraphicChar![0];
            _inBandResizeReportsEnabled = state.InBandResizeReportsEnabled;

            // Deliberately not restored: the text-batching buffer (always empty between Process()
            // calls - AnsiParserStateTests asserts it), the per-batch cursor-visibility flags
            // (reset inside Process()), and the hyperlink interning registry (identity travels
            // with the buffer's link table instead).
            _textBuffer.Clear();
            _sawCursorHideInBatch = false;
            _sawCursorShowAfterHideInBatch = false;
        }

        /// <summary>Grows <see cref="_paramBuffer"/> to hold at least <paramref name="length"/> chars.</summary>
        private void EnsureParamCapacity(int length)
        {
            if (_paramBuffer.Length < length)
            {
                _paramBuffer = new char[Math.Max(length, _paramBuffer.Length * 2)];
            }
        }
    }
}
```

> `_charsets` is declared `readonly`, so `ImportState` writes its elements rather than replacing
> the array — which is what the code above does. If the compiler complains about `_kittyPendingParams`
> being reassigned, check whether the field is `readonly` in `AnsiParser.cs:65` (it is not today);
> if it becomes readonly, clear-and-copy instead.

- [ ] **Step 5: Run the tests to verify they pass**

```
scripts/build.ps1 test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~AnsiParserStateTests"
```

Expected: PASS, 11 passed (6 facts + 5 theory cases).

> If `TextBatchingBuffer_IsEmptyWhenProcessReturns` fails for any input, do not export
> `_textBuffer` to make it pass — find the branch that returns without flushing and flush it, or
> if the flush is genuinely deferred by design, add `_textBuffer`'s contents to `AnsiParserState`
> and restore them in `ImportState`. Either way the answer goes in the report.

- [ ] **Step 6: Commit**

```bash
git add src/Ntilde.VT/AnsiParserState.cs src/Ntilde.VT/AnsiParser.StateTransfer.cs \
        src/Ntilde.VT/AnsiParser.cs tests/Ntilde.VT.Tests/StateTransfer/AnsiParserStateTests.cs
git commit -m "feat(vt): export and import AnsiParser cross-call state

Everything that survives a Process() call, so a second parser can be dropped into the
first one's stream position mid-sequence. Configuration is deliberately excluded, and
the claim that the text-batching buffer is always empty at the boundary is asserted
rather than assumed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: `TerminalStateSnapshot` and buffer export/import

**Files:**
- Create: `src/Ntilde.VT/TerminalStateSnapshot.cs`
- Create: `src/Ntilde.VT/TerminalBuffer.StateTransfer.cs`
- Create: `tests/Ntilde.VT.Tests/StateTransfer/TerminalStateSnapshotTests.cs`

**Interfaces:**
- Consumes: `Ntilde.VT.AnsiParserState` (Task 6).
- Produces:
  - `Ntilde.VT.TerminalStateSnapshot` + nested `TerminalScreenState`, `TerminalCursorState`, `TerminalSgrState`, `TerminalModeState`, `TerminalKittyKeyboardState`, `TerminalHyperlinkEntry`, `TerminalGraphemeState`
  - `Ntilde.VT.TerminalStateJsonContext` (source-generated)
  - `Ntilde.VT.TerminalStateSerializer.ToBytes(TerminalStateSnapshot) → byte[]` and `FromBytes(ReadOnlySpan<byte>) → TerminalStateSnapshot`
  - `TerminalBuffer.ExportState(int maxScrollbackRows) → TerminalStateSnapshot`
  - `TerminalBuffer.ImportState(TerminalStateSnapshot)`

**What goes in, and what is left out.** The rule from the spec: a field earns its place if the
parser or buffer consults it when applying future bytes, or the renderer shows it.

*Included:* both screens' cells (base64 blittable `TerminalCell[]`, `CellsSizeOf` +
`CellsLayoutId` validated as `ReplaySnapshot` does), per-screen row wraps, extended text, per-cell
hyperlink ids; scrollback rows (capped, with `ScrollbackTruncated` / `ScrollbackRowsDropped`) with
the same three side tables; `_isAltScreen`; cursor row/col and `_isPendingWrap`; `ScrollTop` /
`ScrollBottom`; `_tabStops`; `_savedCursors.Main` / `.Alt` (DECSC); `_screenCursorStates.Main` /
`.Alt`; `_restoreMainCursorOnAltExit`; the full current-SGR set including `_currentHyperlink`'s
id; `ModeState` (all 15 properties); `KittyKeyboardState` (both stacks, both counts, which is
active); `_isSynchronizedOutput` + `_lastSyncStart`; the grapheme-continuation state
(`_highSurrogateBuffer`, `_lastCharCol`, `_lastCharRow`, `_isAfterZwj`); the hyperlink table;
`AnsiParserState`; `DecoderTail`; `StreamSeq`; `Version`; `Cols`/`Rows`.

*Excluded, with reasons:*

| State | Why not |
|---|---|
| inline images (`_images`) | spec says so explicitly; `SKBitmap` handles are not transferable and the snapshot would carry megabytes |
| `_packedFg`/`_packedBg`/`_packedFlags`/`_isStyleDirty` | derived cache over the SGR fields. `ImportState` leaves `_isStyleDirty` true so the first write recomputes it. |
| `_prevCursorCol`/`_prevCursorRow`/`_maxColThisRow` | written by the write path and never read anywhere in `Ntilde.VT` (grep-verified); carrying dead state would invite someone to trust it |
| `_hasSnapshotState`, `_lastSnapshotThemeEpoch` | render-diff cache. Left false on import, so the first `CaptureRenderSnapshot` is a full repaint — correct, just not incremental. |
| `_commandStartMark`, `_commandOutputStartMark`, `_isAcceptingCommandInput` | Command Assist's marks carry a `ScrollbackPages.Generation` epoch that is meaningless in another process's coordinate space, and the next `OSC 133;B` re-arms them. Transplanting them would produce marks that pass the epoch check and point at the wrong rows. |
| `Theme`, `MaxHistory`, `MaxScrollbackBytes` | host configuration |
| selection | not owned by `TerminalBuffer` — it is passed into `CaptureRenderSnapshot` by the App |
| `Lock`, `OnInvalidate`, `OnScreenSwitched` | plumbing |

- [ ] **Step 1: Write the failing tests**

Create `tests/Ntilde.VT.Tests/StateTransfer/TerminalStateSnapshotTests.cs`:

```csharp
using System;
using System.Diagnostics;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

public class TerminalStateSnapshotTests
{
    private const int UnlimitedScrollback = int.MaxValue;

    private static (AnsiParser Parser, TerminalBuffer Buffer) NewPair(int cols = 80, int rows = 24)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        return (parser, buffer);
    }

    [Fact]
    public void ExportImport_RestoresTheInactiveScreen()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("main screen text");
        a.Process("\u001b[?1049h");   // into the alt screen
        a.Process("alt screen text");

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);

        (AnsiParser c, TerminalBuffer bufC) = NewPair();
        bufC.ImportState(snapshot);

        // Both are on the alt screen; leaving it must reveal the main screen's text.
        a.Process("\u001b[?1049l");
        c.Process("\u001b[?1049l");

        Assert.Equal(RowText(bufA, 0), RowText(bufC, 0));
        Assert.StartsWith("main screen text", RowText(bufC, 0));
    }

    [Fact]
    public void ExportImport_RestoresScrollback()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 20, rows: 4);
        for (int i = 0; i < 30; i++)
        {
            a.Process($"line{i}\r\n");
        }

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);
        Assert.False(snapshot.ScrollbackTruncated);

        (_, TerminalBuffer bufC) = NewPair(cols: 20, rows: 4);
        bufC.ImportState(snapshot);

        bufA.Lock.EnterReadLock();
        bufC.Lock.EnterReadLock();
        try
        {
            Assert.Equal(bufA.Scrollback.Count, bufC.Scrollback.Count);
            Assert.Equal(bufA.TotalLines, bufC.TotalLines);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
            bufA.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void ExportState_CapsScrollbackAndSaysSo()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 20, rows: 4);
        for (int i = 0; i < 100; i++)
        {
            a.Process($"line{i}\r\n");
        }

        TerminalStateSnapshot snapshot = bufA.ExportState(maxScrollbackRows: 10);

        Assert.True(snapshot.ScrollbackTruncated);
        Assert.Equal(10, snapshot.ScrollbackRowCount);
        Assert.True(snapshot.ScrollbackRowsDropped > 0);

        // The rows kept are the NEWEST ones - the ones a user scrolling up sees first.
        (_, TerminalBuffer bufC) = NewPair(cols: 20, rows: 4);
        bufC.ImportState(snapshot);
        bufC.Lock.EnterReadLock();
        try
        {
            Assert.Equal(10, bufC.Scrollback.Count);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void ExportImport_RestoresTabStopsSavedCursorSyncAndKittyStack()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b[3g");            // clear all tab stops
        a.Process("\u001b[10G\u001bH");    // set one at column 10
        a.Process("\u001b[5;7H\u001b7");   // move, then DECSC
        a.Process("\u001b[?2026h");        // begin synchronized output
        a.Process("\u001b[>1u");           // kitty keyboard push

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);

        (_, TerminalBuffer bufC) = NewPair();
        bufC.ImportState(snapshot);

        Assert.Equal(snapshot.TabStops, bufC.ExportState(UnlimitedScrollback).TabStops);
        Assert.Equal(snapshot.SavedCursorMain.Row, bufC.ExportState(UnlimitedScrollback).SavedCursorMain.Row);
        Assert.Equal(snapshot.SavedCursorMain.Col, bufC.ExportState(UnlimitedScrollback).SavedCursorMain.Col);
        Assert.True(bufC.ExportState(UnlimitedScrollback).IsSynchronizedOutput);
        Assert.Equal(1, bufC.Modes.KittyKeyboard.Flags);
        Assert.Equal(1, bufC.Modes.KittyKeyboard.StackDepth);
    }

    [Fact]
    public void ExportImport_RestoresHyperlinkIdentityIncludingGrouping()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b]8;id=x;https://example.com\u0007AB\u001b]8;;\u0007 ");
        a.Process("\u001b]8;id=x;https://example.com\u0007CD\u001b]8;;\u0007");

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);

        (_, TerminalBuffer bufC) = NewPair();
        bufC.ImportState(snapshot);

        bufC.Lock.EnterReadLock();
        try
        {
            Ntilde.VT.Links.Hyperlink? first = bufC.ViewportRows[0].GetHyperlink(0);
            Ntilde.VT.Links.Hyperlink? later = bufC.ViewportRows[0].GetHyperlink(3);

            Assert.NotNull(first);
            Assert.Equal("https://example.com", first!.Uri);
            // Same explicit id and URI: one anchor, so one instance - reference equality is the
            // identity test OSC 8 defines, and an import that minted two instances would silently
            // stop the two runs underlining together.
            Assert.Same(first, later);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Export → bytes → import into a fresh buffer → export again must produce the same bytes.
    /// The import in the middle is the point: a serializer round trip alone only proves the JSON
    /// survives, while this proves the buffer reconstructed from it is the same buffer. Anything
    /// the import drops, reorders or recomputes differently shows up as a byte difference.
    /// </summary>
    [Fact]
    public void ExportImportExport_IsByteIdentical()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 40, rows: 8);
        a.Process("hello \u001b[31mworld\u001b[0m 你好 \U0001F600\r\n");
        a.Process("\u001b]8;id=k;https://example.com\u0007link\u001b]8;;\u0007\r\n");
        a.Process("\u001b[3g\u001b[1;9H\u001bH\u001b[4;4H\u001b7");
        a.Process("\u001b[?1049hinside alt\u001b[>1u");
        for (int i = 0; i < 20; i++)
        {
            a.Process($"scroll {i}\r\n");
        }

        TerminalStateSnapshot first = bufA.ExportState(UnlimitedScrollback);
        first.Parser = a.ExportState();
        first.DecoderTail = [0xF0, 0x9F];
        first.StreamSeq = 12345;

        byte[] bytes = TerminalStateSerializer.ToBytes(first);

        TerminalStateSnapshot restored = TerminalStateSerializer.FromBytes(bytes);
        (AnsiParser c, TerminalBuffer bufC) = NewPair(cols: 40, rows: 8);
        bufC.ImportState(restored);
        c.ImportState(restored.Parser);

        TerminalStateSnapshot second = bufC.ExportState(UnlimitedScrollback);
        second.Parser = c.ExportState();
        second.DecoderTail = restored.DecoderTail;
        second.StreamSeq = restored.StreamSeq;

        Assert.Equal(bytes, TerminalStateSerializer.ToBytes(second));
    }

    /// <summary>
    /// Reports the size and cost of the shape the multiplexer will actually send on attach.
    /// Not a threshold assertion - the numbers go in the phase report - but it does fail if the
    /// snapshot grows past a bound no plausible implementation should reach, which is how a
    /// regression that starts embedding images or scrollback twice would surface.
    /// </summary>
    [Fact]
    public void Measure_SerializedSizeAndTiming_For80x24With10kScrollback()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 80, rows: 24);
        bufA.MaxHistory = 20000;
        for (int i = 0; i < 10_000; i++)
        {
            a.Process($"scrollback line {i} with some filler text to make it realistic\r\n");
        }

        var exportWatch = Stopwatch.StartNew();
        TerminalStateSnapshot snapshot = bufA.ExportState(maxScrollbackRows: 10_000);
        exportWatch.Stop();

        byte[] bytes = TerminalStateSerializer.ToBytes(snapshot);

        var importWatch = Stopwatch.StartNew();
        TerminalStateSnapshot restored = TerminalStateSerializer.FromBytes(bytes);
        (_, TerminalBuffer bufC) = NewPair(cols: 80, rows: 24);
        bufC.ImportState(restored);
        importWatch.Stop();

        // Printed so the phase report can quote real numbers rather than estimates.
        Console.WriteLine(
            $"[mux-phase0] 80x24 + 10k scrollback: {bytes.Length} bytes serialized, " +
            $"export {exportWatch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"deserialize+import {importWatch.Elapsed.TotalMilliseconds:F1} ms");

        Assert.Equal(10_000, snapshot.ScrollbackRowCount);
        Assert.True(
            bytes.Length < 64 * 1024 * 1024,
            $"Snapshot ballooned to {bytes.Length} bytes for 80x24 + 10k scrollback; something is " +
            "being embedded that should not be (images? scrollback twice?).");
    }

    private static string RowText(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            TerminalCell[] cells = buffer.ViewportRows[row].Cells;
            var chars = new char[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                chars[i] = cells[i].Character == '\0' ? ' ' : cells[i].Character;
            }

            return new string(chars).TrimEnd();
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```
scripts/build.ps1 test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~TerminalStateSnapshotTests"
```

Expected: FAIL — `TerminalStateSnapshot`, `TerminalStateSerializer`, `ExportState`, `ImportState`
do not exist.

- [ ] **Step 3: Add `TerminalStateSnapshot` and the serializer**

Create `src/Ntilde.VT/TerminalStateSnapshot.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ntilde.VT
{
    /// <summary>
    /// Complete, transferable terminal state: enough for a second
    /// <see cref="TerminalBuffer"/> + <see cref="AnsiParser"/> to be dropped into a running
    /// session's position and see every subsequent byte the same way the original does.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ReplaySnapshot"/>, which stays exactly as it is so recorded
    /// replay files keep working. That one covers the active viewport and a handful of modes,
    /// which is right for "redraw what the screen looked like"; this one covers the inactive
    /// screen, scrollback, tab stops, saved cursors, synchronized output, the kitty keyboard
    /// stacks, hyperlink identity and the parser's own position, which is what "resume applying
    /// bytes" needs.
    ///
    /// Inline images are deliberately excluded: the handles are decoder-owned and not
    /// transferable, and carrying the pixels would dominate the payload.
    /// </remarks>
    public sealed class TerminalStateSnapshot
    {
        /// <summary>Bump when a field's meaning changes, not when one is added.</summary>
        public const int CurrentVersion = 1;

        [JsonPropertyName("v")] public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("cols")] public int Cols { get; set; }
        [JsonPropertyName("rows")] public int Rows { get; set; }

        /// <summary><c>Unsafe.SizeOf&lt;TerminalCell&gt;()</c> at export, validated on import.</summary>
        [JsonPropertyName("cells_sizeof")] public int CellsSizeOf { get; set; }

        /// <summary><c>TerminalCell.TerminalCellLayoutId</c> at export, validated on import.</summary>
        [JsonPropertyName("cells_layout_id")] public string? CellsLayoutId { get; set; }

        [JsonPropertyName("main")] public TerminalScreenState Main { get; set; } = new();
        [JsonPropertyName("alt")] public TerminalScreenState Alt { get; set; } = new();
        [JsonPropertyName("alt_active")] public bool IsAltScreenActive { get; set; }

        [JsonPropertyName("sb")] public TerminalScreenState Scrollback { get; set; } = new();
        [JsonPropertyName("sb_rows")] public int ScrollbackRowCount { get; set; }

        /// <summary>The export hit its row cap, so older rows are absent.</summary>
        [JsonPropertyName("sb_trunc")] public bool ScrollbackTruncated { get; set; }

        /// <summary>How many of the oldest scrollback rows the cap dropped.</summary>
        [JsonPropertyName("sb_dropped")] public int ScrollbackRowsDropped { get; set; }

        [JsonPropertyName("cx")] public int CursorCol { get; set; }
        [JsonPropertyName("cy")] public int CursorRow { get; set; }
        [JsonPropertyName("pw")] public bool IsPendingWrap { get; set; }
        [JsonPropertyName("sr_top")] public int ScrollTop { get; set; }
        [JsonPropertyName("sr_bottom")] public int ScrollBottom { get; set; }
        [JsonPropertyName("tabs")] public bool[] TabStops { get; set; } = [];

        [JsonPropertyName("saved_main")] public TerminalCursorState SavedCursorMain { get; set; } = new();
        [JsonPropertyName("saved_alt")] public TerminalCursorState SavedCursorAlt { get; set; } = new();
        [JsonPropertyName("screen_main")] public TerminalCursorState ScreenCursorMain { get; set; } = new();
        [JsonPropertyName("screen_alt")] public TerminalCursorState ScreenCursorAlt { get; set; } = new();
        [JsonPropertyName("restore_main_on_alt_exit")] public bool RestoreMainCursorOnAltExit { get; set; }

        [JsonPropertyName("sgr")] public TerminalSgrState Sgr { get; set; } = new();
        [JsonPropertyName("modes")] public TerminalModeState Modes { get; set; } = new();
        [JsonPropertyName("kitty_kbd")] public TerminalKittyKeyboardState KittyKeyboard { get; set; } = new();

        [JsonPropertyName("sync")] public bool IsSynchronizedOutput { get; set; }

        /// <summary><c>DateTime.UtcNow.Ticks</c> at the last BSU; drives the sync-output timeout.</summary>
        [JsonPropertyName("sync_start_ticks")] public long LastSyncStartUtcTicks { get; set; }

        [JsonPropertyName("grapheme")] public TerminalGraphemeState Grapheme { get; set; } = new();

        /// <summary>
        /// Every hyperlink any exported cell points at. Cells carry an index into this list, which
        /// is how OSC 8's reference-equality identity survives the wire: two cells with the same
        /// index are re-attached to the same instance on import.
        /// </summary>
        [JsonPropertyName("links")] public List<TerminalHyperlinkEntry> Hyperlinks { get; set; } = [];

        /// <summary>Index into <see cref="Hyperlinks"/> of the link the cursor is writing under, or -1.</summary>
        [JsonPropertyName("cur_link")] public int CurrentHyperlinkId { get; set; } = -1;

        /// <summary>The parser's position in the stream. Applied with <c>AnsiParser.ImportState</c>.</summary>
        [JsonPropertyName("parser")] public AnsiParserState Parser { get; set; } = new();

        /// <summary>
        /// The partial UTF-8 code point the source's decoder was holding when this was taken;
        /// feed it to the new decoder before the tail bytes. See <c>Utf8ChunkDecoder.PendingTail</c>.
        /// </summary>
        [JsonPropertyName("tail")] public byte[]? DecoderTail { get; set; }

        /// <summary>
        /// Byte offset into the session's output stream that this snapshot corresponds to:
        /// <c>Utf8ChunkDecoder.ConsumedBytes</c>. The next unseen byte is at
        /// <c>StreamSeq + (DecoderTail?.Length ?? 0)</c>.
        /// </summary>
        [JsonPropertyName("seq")] public long StreamSeq { get; set; }
    }

    /// <summary>One screen's (or the scrollback's) cells and per-row side tables.</summary>
    public sealed class TerminalScreenState
    {
        /// <summary>Row-major <c>TerminalCell[]</c> blob, base64 of the blittable bytes.</summary>
        [JsonPropertyName("cells")] public string? CellsBase64 { get; set; }

        [JsonPropertyName("wrap")] public bool[]? RowWraps { get; set; }

        /// <summary>Extended grapheme clusters keyed by <c>row * cols + col</c>.</summary>
        [JsonPropertyName("ext")] public Dictionary<int, string>? ExtendedText { get; set; }

        /// <summary>
        /// Index into <see cref="TerminalStateSnapshot.Hyperlinks"/> keyed by
        /// <c>row * cols + col</c>.
        /// </summary>
        [JsonPropertyName("links")] public Dictionary<int, int>? LinkIds { get; set; }
    }

    /// <summary>A DECSC-style saved cursor, mirroring <see cref="CursorState"/>.</summary>
    public sealed class TerminalCursorState
    {
        [JsonPropertyName("row")] public int Row { get; set; }
        [JsonPropertyName("col")] public int Col { get; set; }
        [JsonPropertyName("sgr")] public TerminalSgrState Sgr { get; set; } = new();
        [JsonPropertyName("pw")] public bool IsPendingWrap { get; set; }
    }

    /// <summary>The SGR attribute set, shared by the live style and the saved cursors.</summary>
    public sealed class TerminalSgrState
    {
        [JsonPropertyName("fg")] public uint Foreground { get; set; }
        [JsonPropertyName("bg")] public uint Background { get; set; }
        [JsonPropertyName("fgi")] public short FgIndex { get; set; } = -1;
        [JsonPropertyName("bgi")] public short BgIndex { get; set; } = -1;
        [JsonPropertyName("dfg")] public bool IsDefaultForeground { get; set; } = true;
        [JsonPropertyName("dbg")] public bool IsDefaultBackground { get; set; } = true;
        [JsonPropertyName("inv")] public bool IsInverse { get; set; }
        [JsonPropertyName("bold")] public bool IsBold { get; set; }
        [JsonPropertyName("faint")] public bool IsFaint { get; set; }
        [JsonPropertyName("italic")] public bool IsItalic { get; set; }
        [JsonPropertyName("ul")] public bool IsUnderline { get; set; }
        [JsonPropertyName("blink")] public bool IsBlink { get; set; }
        [JsonPropertyName("strike")] public bool IsStrikethrough { get; set; }
        [JsonPropertyName("hidden")] public bool IsHidden { get; set; }
    }

    /// <summary>Every property of <see cref="ModeState"/> except the kitty stacks.</summary>
    public sealed class TerminalModeState
    {
        [JsonPropertyName("m_x10")] public bool MouseModeX10 { get; set; }
        [JsonPropertyName("m_btn")] public bool MouseModeButtonEvent { get; set; }
        [JsonPropertyName("m_any")] public bool MouseModeAnyEvent { get; set; }
        [JsonPropertyName("m_sgr")] public bool MouseModeSGR { get; set; }
        [JsonPropertyName("ckm")] public bool IsApplicationCursorKeys { get; set; }
        [JsonPropertyName("awm")] public bool IsAutoWrapMode { get; set; } = true;
        [JsonPropertyName("decom")] public bool IsOriginMode { get; set; }
        [JsonPropertyName("focus")] public bool IsFocusEventReporting { get; set; }
        [JsonPropertyName("bp")] public bool IsBracketedPasteMode { get; set; }
        [JsonPropertyName("cv")] public bool IsCursorVisible { get; set; } = true;
        [JsonPropertyName("cblink")] public bool IsCursorBlinkEnabled { get; set; } = true;
        [JsonPropertyName("cstyle")] public int CursorStyle { get; set; }
        [JsonPropertyName("irm")] public bool IsInsertMode { get; set; }
        [JsonPropertyName("lnm")] public bool IsLineFeedNewLineMode { get; set; }
        [JsonPropertyName("srm")] public bool IsEchoEnabled { get; set; } = true;
    }

    /// <summary>Both kitty keyboard flag stacks and which one is live.</summary>
    public sealed class TerminalKittyKeyboardState
    {
        [JsonPropertyName("main")] public int[] MainStack { get; set; } = [];
        [JsonPropertyName("alt")] public int[] AltStack { get; set; } = [];
        [JsonPropertyName("alt_active")] public bool IsAltScreenActive { get; set; }
    }

    /// <summary>The grapheme-continuation state the write path consults for the next char.</summary>
    public sealed class TerminalGraphemeState
    {
        /// <summary>A lone high surrogate awaiting its pair, or null.</summary>
        [JsonPropertyName("hi")] public string? HighSurrogate { get; set; }

        [JsonPropertyName("col")] public int LastCharCol { get; set; } = -1;
        [JsonPropertyName("row")] public int LastCharRow { get; set; } = -1;
        [JsonPropertyName("zwj")] public bool IsAfterZwj { get; set; }
    }

    /// <summary>One OSC 8 link identity: the (URI, id) pair that defines it.</summary>
    public sealed class TerminalHyperlinkEntry
    {
        [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = false)]
    [JsonSerializable(typeof(TerminalStateSnapshot))]
    public partial class TerminalStateJsonContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// JSON envelope for <see cref="TerminalStateSnapshot"/>, with the cell blobs base64 inside -
    /// the same shape <see cref="ReplaySnapshot"/> uses, so one reader can handle both.
    /// </summary>
    /// <remarks>
    /// Source-generated rather than reflection-based because <c>Ntilde.App</c> publishes Native
    /// AOT with <c>IL2026</c> and <c>IL3050</c> as errors, and this type is reachable from there.
    /// </remarks>
    public static class TerminalStateSerializer
    {
        public static byte[] ToBytes(TerminalStateSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            return JsonSerializer.SerializeToUtf8Bytes(
                snapshot, TerminalStateJsonContext.Default.TerminalStateSnapshot);
        }

        public static TerminalStateSnapshot FromBytes(ReadOnlySpan<byte> bytes)
        {
            TerminalStateSnapshot? snapshot = JsonSerializer.Deserialize(
                bytes, TerminalStateJsonContext.Default.TerminalStateSnapshot);

            return snapshot ?? throw new InvalidOperationException(
                "Terminal state payload deserialized to null; the envelope is not a TerminalStateSnapshot.");
        }
    }
}
```

- [ ] **Step 4a: Add the `KittyKeyboardState` transfer accessors**

`KittyKeyboardState`'s stacks are private with no accessor beyond `Clone()`. Add to
`src/Ntilde.VT/KittyKeyboardState.cs`, below `Clone()`:

```csharp
        /// <summary>
        /// The main-screen flag stack, bottom first. State-transfer plumbing for
        /// <see cref="TerminalStateSnapshot"/> - deliberately internal, because the protocol
        /// surface is push/pop/set and nothing outside Ntilde.VT should reach past it.
        /// </summary>
        internal int[] ExportMainStackForState()
        {
            lock (_gate) { return _mainStack.AsSpan(0, _mainCount).ToArray(); }
        }

        /// <summary>The alternate-screen flag stack, bottom first. See <see cref="ExportMainStackForState"/>.</summary>
        internal int[] ExportAltStackForState()
        {
            lock (_gate) { return _altStack.AsSpan(0, _altCount).ToArray(); }
        }

        /// <summary>
        /// Replaces both stacks and the active-screen selection. Incoming lengths are clamped to
        /// <see cref="MaxStackDepth"/> - the payload arrives from another process, and a longer
        /// array is a bug or an attack, never something to honour.
        /// </summary>
        internal void ImportStacksForState(int[] main, int[] alt, bool altActive)
        {
            lock (_gate)
            {
                _mainCount = Math.Min(main.Length, MaxStackDepth);
                Array.Copy(main, _mainStack, _mainCount);
                _altCount = Math.Min(alt.Length, MaxStackDepth);
                Array.Copy(alt, _altStack, _altCount);
                _altActive = altActive;
                RefreshCurrentFlagsNoLock();
            }
        }
```

- [ ] **Step 4b: Add `TerminalBuffer.ExportState` / `ImportState`**

Create `src/Ntilde.VT/TerminalBuffer.StateTransfer.cs` with this shape:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ntilde.VT.Links;
using Ntilde.VT.Storage;

namespace Ntilde.VT
{
    public partial class TerminalBuffer
    {
        /// <summary>
        /// Captures everything a second buffer needs to apply the same future bytes the same way:
        /// both screens, scrollback (newest <paramref name="maxScrollbackRows"/> rows), tab stops,
        /// saved cursors, modes, kitty keyboard stacks, hyperlink identity and grapheme
        /// continuation. <see cref="TerminalStateSnapshot.Parser"/>,
        /// <see cref="TerminalStateSnapshot.DecoderTail"/> and
        /// <see cref="TerminalStateSnapshot.StreamSeq"/> are left for the caller to fill - the
        /// buffer does not own a parser or a decoder.
        /// </summary>
        public TerminalStateSnapshot ExportState(int maxScrollbackRows)
        {
            Lock.EnterReadLock();
            try
            {
                var links = new HyperlinkTableBuilder();
                var snapshot = new TerminalStateSnapshot
                {
                    Version = TerminalStateSnapshot.CurrentVersion,
                    Cols = Cols,
                    Rows = Rows,
                    CellsSizeOf = Unsafe.SizeOf<TerminalCell>(),
                    CellsLayoutId = TerminalCell.TerminalCellLayoutId,
                    Main = ExportScreenNoLock(_mainScreen, links),
                    Alt = ExportScreenNoLock(_altScreen, links),
                    IsAltScreenActive = _isAltScreen,
                    CursorCol = _cursorCol,
                    CursorRow = _cursorRow,
                    IsPendingWrap = _isPendingWrap,
                    ScrollTop = ScrollTop,
                    ScrollBottom = ScrollBottom,
                    TabStops = (bool[])_tabStops.Clone(),
                    SavedCursorMain = ExportCursor(_savedCursors.Main),
                    SavedCursorAlt = ExportCursor(_savedCursors.Alt),
                    ScreenCursorMain = ExportCursor(_screenCursorStates.Main),
                    ScreenCursorAlt = ExportCursor(_screenCursorStates.Alt),
                    RestoreMainCursorOnAltExit = _restoreMainCursorOnAltExit,
                    Sgr = ExportLiveSgrNoLock(),
                    Modes = ExportModes(Modes),
                    KittyKeyboard = new TerminalKittyKeyboardState
                    {
                        MainStack = Modes.KittyKeyboard.ExportMainStackForState(),
                        AltStack = Modes.KittyKeyboard.ExportAltStackForState(),
                        IsAltScreenActive = Modes.KittyKeyboard.IsAltScreenActive,
                    },
                    IsSynchronizedOutput = _isSynchronizedOutput,
                    LastSyncStartUtcTicks = _lastSyncStart.Ticks,
                    Grapheme = new TerminalGraphemeState
                    {
                        HighSurrogate = _highSurrogateBuffer?.ToString(),
                        LastCharCol = _lastCharCol,
                        LastCharRow = _lastCharRow,
                        IsAfterZwj = _isAfterZwj,
                    },
                    CurrentHyperlinkId = links.IndexOf(_currentHyperlink),
                };

                snapshot.Scrollback = ExportScrollbackNoLock(maxScrollbackRows, links, snapshot);
                snapshot.Hyperlinks = links.Entries;
                return snapshot;
            }
            finally
            {
                Lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Adopts <paramref name="snapshot"/> wholesale. The buffer must already be the
        /// snapshot's size; a caller that cannot guarantee that resizes first. Derived caches
        /// (the packed style, the render diff) are invalidated rather than carried, so the first
        /// write and the first render recompute them.
        /// </summary>
        public void ImportState(TerminalStateSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ValidateVersionAndCellLayout(snapshot);

            Lock.EnterWriteLock();
            try
            {
                Hyperlink?[] links = RebuildHyperlinks(snapshot.Hyperlinks);

                ImportScreenNoLock(_mainScreen, snapshot.Main, snapshot.Cols, links);
                ImportScreenNoLock(_altScreen, snapshot.Alt, snapshot.Cols, links);
                _isAltScreen = snapshot.IsAltScreenActive;
                _viewport = _isAltScreen ? _altScreen : _mainScreen;

                ImportScrollbackNoLock(snapshot, links);
                ImportTabStopsNoLock(snapshot.TabStops);

                _cursorCol = Math.Clamp(snapshot.CursorCol, 0, Math.Max(0, Cols - 1));
                _cursorRow = Math.Clamp(snapshot.CursorRow, 0, Math.Max(0, Rows - 1));
                _isPendingWrap = snapshot.IsPendingWrap;
                ScrollTop = Math.Clamp(snapshot.ScrollTop, 0, Math.Max(0, Rows - 1));
                ScrollBottom = Math.Clamp(snapshot.ScrollBottom, 0, Math.Max(0, Rows - 1));
                if (ScrollTop > ScrollBottom)
                {
                    ScrollTop = 0;
                    ScrollBottom = Math.Max(0, Rows - 1);
                }

                ImportCursor(_savedCursors.Main, snapshot.SavedCursorMain);
                ImportCursor(_savedCursors.Alt, snapshot.SavedCursorAlt);
                ImportCursor(_screenCursorStates.Main, snapshot.ScreenCursorMain);
                ImportCursor(_screenCursorStates.Alt, snapshot.ScreenCursorAlt);
                _restoreMainCursorOnAltExit = snapshot.RestoreMainCursorOnAltExit;

                ImportLiveSgrNoLock(snapshot.Sgr);
                _currentHyperlink = snapshot.CurrentHyperlinkId >= 0
                    && snapshot.CurrentHyperlinkId < links.Length
                        ? links[snapshot.CurrentHyperlinkId]
                        : null;

                ImportModes(Modes, snapshot.Modes);
                Modes.KittyKeyboard.ImportStacksForState(
                    snapshot.KittyKeyboard.MainStack,
                    snapshot.KittyKeyboard.AltStack,
                    snapshot.KittyKeyboard.IsAltScreenActive);

                _isSynchronizedOutput = snapshot.IsSynchronizedOutput;
                _lastSyncStart = new DateTime(snapshot.LastSyncStartUtcTicks, DateTimeKind.Utc);

                _highSurrogateBuffer = string.IsNullOrEmpty(snapshot.Grapheme.HighSurrogate)
                    ? null
                    : snapshot.Grapheme.HighSurrogate![0];
                _lastCharCol = snapshot.Grapheme.LastCharCol;
                _lastCharRow = snapshot.Grapheme.LastCharRow;
                _isAfterZwj = snapshot.Grapheme.IsAfterZwj;

                // Derived, never carried: the packed style cache is rebuilt from the SGR fields
                // above on the next write, and the render-diff cache stays cold so the first
                // CaptureRenderSnapshot is a full repaint rather than a diff against rows that
                // were never on this screen.
                _isStyleDirty = true;
                _hasSnapshotState = false;
            }
            finally
            {
                Lock.ExitWriteLock();
            }

            Invalidate();
        }

        // ── helpers: ExportScreenNoLock, ImportScreenNoLock, ExportScrollbackNoLock,
        //    ImportScrollbackNoLock, ImportTabStopsNoLock, ExportCursor, ImportCursor,
        //    ExportLiveSgrNoLock, ImportLiveSgrNoLock, ExportModes, ImportModes,
        //    ValidateVersionAndCellLayout, RebuildHyperlinks, HyperlinkTableBuilder
    }
}
```

Rules the helpers must follow: 

- **`ExportScreenNoLock` / `ImportScreenNoLock` (cells).** Flatten `_mainScreen` and `_altScreen` separately into `TerminalCell[Rows * Cols]`,
  `MemoryMarshal.AsBytes(...)` → `Convert.ToBase64String`. Note that when `_isAltScreen` is true,
  `_viewport` *is* `_altScreen` and `_mainScreen` holds the stashed main screen (see
  `EnterAltScreen` at `:664`, `SwitchToMainScreen` at `:723`) — so exporting the two fields
  directly is already both screens, no toggling required. Set `CellsSizeOf` to
  `Unsafe.SizeOf<TerminalCell>()` and `CellsLayoutId` to `TerminalCell.TerminalCellLayoutId`
  (`internal`, visible inside `Ntilde.VT`).
- **`ValidateVersionAndCellLayout`.** Throw an `InvalidOperationException` when
  `snapshot.Version != TerminalStateSnapshot.CurrentVersion`, naming both. Then mirror
  `ReplayRunner.cs:272-294`: if `CellsSizeOf` or `CellsLayoutId` disagrees with this build, throw
  naming both the expected and actual values. A mismatched blob reinterpreted is silent
  corruption, and `MemoryMarshal.Cast` will not notice.
- **`HyperlinkTableBuilder` / `RebuildHyperlinks`.** The builder holds a
  `Dictionary<Hyperlink, int>` with `ReferenceEqualityComparer.Instance` plus a
  `List<TerminalHyperlinkEntry>`; `IndexOf(link)` returns -1 for null, the existing index for a
  known instance, or appends and returns the new one. `RebuildHyperlinks` constructs exactly one
  `Hyperlink` per entry and every cell naming that index gets that instance — which is what
  preserves OSC 8 grouping, since identity there is reference equality. `Hyperlink`'s constructor
  is `internal`, so this compiles inside `Ntilde.VT`.
- **`ExportScrollbackNoLock` / `ImportScrollbackNoLock`.** Walk `_scrollback` from `Count - 1` down to `Math.Max(0, Count - maxScrollbackRows)`,
  then emit oldest-first. Set `ScrollbackRowCount`, `ScrollbackTruncated = Count > maxScrollbackRows`,
  `ScrollbackRowsDropped = Math.Max(0, Count - maxScrollbackRows)`. On import, `Clear()` the
  scrollback and `AppendRow(cells, isWrapped, extendedText, hyperlinks)` in order, rebuilding the
  `SmallMap<T>` side tables from the flat dictionaries.
- **`ImportTabStopsNoLock`.** `_tabStops` is `bool[Cols]`. If the incoming length differs from
  `Cols`, copy the overlap and fill the remainder with `IsDefaultTabStopColumn(col)` — the same
  rule `ResizeTabStopsNoLock` uses, so a snapshot taken at a different width degrades the way a
  resize does rather than losing every stop.
- **`ExportCursor` / `ImportCursor`.** `CursorState` maps field-for-field onto
  `TerminalCursorState` (see `CursorState.Clone()` at `src/Ntilde.VT/CursorState.cs` for the full
  list — `Row`, `Col`, the 14 SGR fields, `IsPendingWrap`). `TermColor.ToUint()` /
  `TermColor.FromUint()` for the two colours, as `ReplaySnapshot` does.
- **`ExportLiveSgrNoLock` / `ImportLiveSgrNoLock`.** The same 14 fields, read from and written to
  the buffer's own `_currentForeground` … `_isHidden`.
- **`ExportModes` / `ImportModes`.** All 15 `ModeState` properties except `KittyKeyboard` (handled
  separately above). `CursorStyle` travels as `(int)`.
- **Mode 2048 is deliberately not here:** it is `AnsiParser._inBandResizeReportsEnabled`, carried
  by `AnsiParserState`. `TerminalStateSnapshot.Parser` is where it lives.
- **Nothing touches** `_images`, `_commandStartMark`, `_commandOutputStartMark`,
  `_isAcceptingCommandInput`, `Theme`, `MaxHistory` or `MaxScrollbackBytes` — see the
  excluded-state table above for why each one stays out.

- [ ] **Step 5: Run the tests to verify they pass**

```
scripts/build.ps1 test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~TerminalStateSnapshotTests"
```

Expected: PASS, 7 passed. Copy the `[mux-phase0]` line from the output — it is item 5 of the
phase report.

- [ ] **Step 6: Run the whole VT suite**

```
scripts/build.ps1 test tests/Ntilde.VT.Tests
```

Expected: PASS, 0 failed. (`AnsiParser` becoming `partial` and `KittyKeyboardState` gaining
internal methods must not disturb anything.)

- [ ] **Step 7: Commit**

```bash
git add src/Ntilde.VT/TerminalStateSnapshot.cs src/Ntilde.VT/TerminalBuffer.StateTransfer.cs \
        src/Ntilde.VT/KittyKeyboardState.cs tests/Ntilde.VT.Tests/StateTransfer/TerminalStateSnapshotTests.cs
git commit -m "feat(vt): full-state terminal snapshot with export/import

Both screens, scrollback, tab stops, saved cursors, synchronized output, the kitty
keyboard stacks, hyperlink identity, grapheme continuation, and the parser's own
position. ReplaySnapshot and ApplySnapshot are untouched so recorded replays keep
working. Inline images are deliberately out.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: The parity harness and corpus

**Files:**
- Modify: `tests/Ntilde.VT.Tests/Ntilde.VT.Tests.csproj`
- Create: `tests/Ntilde.VT.Tests/StateTransfer/ParityCorpus.cs`
- Create: `tests/Ntilde.VT.Tests/StateTransfer/ParityHarness.cs`

**Interfaces:**
- Consumes: `TerminalBuffer.ExportState`/`ImportState`, `AnsiParser.ExportState`/`ImportState`,
  `Ntilde.Pty`-free (`Utf8ChunkDecoder` is in Pty, which VT.Tests must not reference — the
  harness uses `Encoding.UTF8.GetDecoder()`, and the decoder's own equivalence is Task 1's job).
- Produces:
  - `ParityCorpus.All() → IEnumerable<(string Name, byte[] Bytes)>`
  - `ParityHarness.AssertSnapshotTailParity(string name, byte[] corpus, int[] cutPoints, (int Offset, int Cols, int Rows)[] resizes, bool forceConPtyFiltering)`

- [ ] **Step 1: Give VT.Tests the fixtures and the Replay reference**

In `tests/Ntilde.VT.Tests/Ntilde.VT.Tests.csproj`, add to the `ProjectReference` group:

```xml
    <!-- BufferSnapshot.Capture is the comparison the parity test is specified in terms of, and
         ReplayReader parses the .rec corpus. Replay depends only on VT, so this adds no layering
         risk; the arch tests constrain production assemblies, not test ones. -->
    <ProjectReference Include="..\..\src\Ntilde.Replay\Ntilde.Replay.csproj" />
```

and a new group linking the existing fixtures rather than copying them:

```xml
  <ItemGroup>
    <!-- The same replay v2 fixtures the App.Tests ReplayRunner suites use. Linked, not copied:
         one corpus, so a fixture fixed in one place is fixed for both. -->
    <Content Include="..\Ntilde.App.Tests\Fixtures\Replay\*.rec">
      <Link>Fixtures/Replay/%(Filename)%(Extension)</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
```

- [ ] **Step 2: Write the corpus**

Create `tests/Ntilde.VT.Tests/StateTransfer/ParityCorpus.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// The byte streams the parity test replays. Two sources, for two different kinds of confidence:
/// recorded fixtures are what real programs actually emit, and synthetic generators reach the
/// corners no recording happens to contain (a CSI long enough to truncate, a kitty payload
/// chunked with m=1, a mode nothing in the fixtures enables).
/// </summary>
internal static class ParityCorpus
{
    public static IEnumerable<(string Name, byte[] Bytes)> All()
    {
        foreach ((string name, byte[] bytes) in Synthetic())
        {
            yield return (name, bytes);
        }

        foreach ((string name, byte[] bytes) in Recorded())
        {
            yield return (name, bytes);
        }
    }

    /// <summary>
    /// Data payloads of the linked replay fixtures, concatenated per file.
    /// </summary>
    /// <remarks>
    /// Through <c>ReplayReader.RunAsync</c> rather than by parsing the NDJSON here, because that
    /// is the code that knows a v2 file leads with a header line and a v1 file does not - and the
    /// fixture set contains both (<c>hello_world.rec</c> has a header; <c>vttest_cursor.rec</c>
    /// starts straight in on events). Resize, marker, input and snapshot events are ignored: the
    /// corpus is a byte stream, and the harness drives resizes itself at offsets it chooses.
    /// </remarks>
    public static IEnumerable<(string Name, byte[] Bytes)> Recorded()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Replay");
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (string path in Directory.GetFiles(dir, "*.rec").OrderBy(p => p, StringComparer.Ordinal))
        {
            var bytes = new List<byte>();
            var reader = new Ntilde.Replay.ReplayReader(path);

            reader.RunAsync(
                onDataCallback: data =>
                {
                    bytes.AddRange(data);
                    return Task.CompletedTask;
                },
                realtime: false).GetAwaiter().GetResult();

            if (bytes.Count > 0)
            {
                yield return (Path.GetFileNameWithoutExtension(path), bytes.ToArray());
            }
        }
    }

    public static IEnumerable<(string Name, byte[] Bytes)> Synthetic()
    {
        yield return ("csi-basic", Utf8(
            "\u001b[2J\u001b[H" +
            "\u001b[1;1Hrow one\u001b[2;1Hrow two\u001b[3;5Hindented\r\n" +
            "\u001b[10G\u001b[K\u001b[4A\u001b[2B\u001b[3C\u001b[1D" +
            "\u001b[2L\u001b[1M\u001b[3@\u001b[2P\u001b[5X"));

        yield return ("csi-long-params", Utf8(
            "before" + "\u001b[" + new string('1', 200) + ";2;3;4;5;6;7;8;9;10m" + "after"));

        yield return ("csi-truncating", Utf8(
            // Past MaxCsiParamChars (65536), so _csiTruncated latches - the surviving prefix can
            // no longer be classified, and both sides must agree about that.
            "start\u001b[" + new string('9', 70_000) + "m" + "end"));

        yield return ("osc-titles-cwd", Utf8(
            "\u001b]0;window and icon\u0007" +
            "\u001b]2;window only\u001b\\" +
            "\u001b]7;file:///tmp/somewhere\u0007" +
            "text"));

        yield return ("osc8-hyperlinks", Utf8(
            "\u001b]8;id=alpha;https://example.com/a\u0007first\u001b]8;;\u0007 plain " +
            "\u001b]8;;https://example.com/b\u0007anonymous\u001b]8;;\u0007 " +
            "\u001b]8;id=alpha;https://example.com/a\u0007rejoins\u001b]8;;\u0007"));

        yield return ("osc52-clipboard", Utf8(
            "\u001b]52;c;aGVsbG8gY2xpcGJvYXJk\u0007after" +
            "\u001b]52;c;?\u0007"));

        yield return ("dcs", Utf8(
            "\u001bP0;1|somedcsdata\u001b\\visible" +
            "\u001bPtmux;\u001b\u001b[31m\u001b\\more"));

        yield return ("apc-kitty-chunked", Utf8(
            "\u001b_Ga=T,f=24,s=2,v=2,m=1;AAAAAAAA\u001b\\" +
            "\u001b_Gm=1;BBBBBBBB\u001b\\" +
            "\u001b_Gm=0;CCCCCCCC\u001b\\" +
            "after image"));

        yield return ("alt-screen", Utf8(
            "main content here\r\n" +
            "\u001b[?1049h" + "\u001b[2J\u001b[Halt content\r\nsecond alt row" +
            "\u001b[?1049l" + "back on main\r\n" +
            "\u001b[?1049h" + "and in again"));

        yield return ("scroll-regions", Utf8(
            "\u001b[2J\u001b[H" +
            "\u001b[5;15r" + "\u001b[6;1Hinside region\r\n" +
            string.Concat(new string[12].Select_Filler()) +
            "\u001b[r" + "\u001b[1;1Hfull screen again"));

        yield return ("tab-stops", Utf8(
            "\u001b[3g" +
            "\u001b[1;5H\u001bH" +
            "\u001b[1;13H\u001bH" +
            "\u001b[1;1Ha\tb\tc\r\n" +
            "\u001b[1;13H\u001b[g" +
            "\u001b[2;1Hx\ty\tz"));

        yield return ("decsc-decrc", Utf8(
            "\u001b[8;20H\u001b[31;1m\u001b7" +
            "\u001b[1;1H\u001b[0mreset here" +
            "\u001b8restored"));

        yield return ("charsets", Utf8(
            "\u001b(0" + "qwertyuiop" + "\u001b(B" + "qwertyuiop" +
            "\u001b)0" + "\u000e" + "asdfgh" + "\u000f" + "asdfgh"));

        yield return ("sgr-256-and-rgb", Utf8(
            "\u001b[38;5;196mindexed fg\u001b[48;5;21m indexed bg" +
            "\u001b[38;2;12;34;56mrgb fg\u001b[48;2;200;100;50m rgb bg" +
            "\u001b[1;3;4;5;7;9mall attrs\u001b[0m plain"));

        yield return ("wide-and-graphemes", Utf8(
            "你好世界 ascii テスト\r\n" +
            "\U0001F600\U0001F1EC\U0001F1E7\U0001F468‍\U0001F469‍\U0001F467\r\n" +
            "éà combining\r\n" +
            // A wide char landing exactly on the last column, which is the wrap corner.
            "\u001b[1;79H你好"));

        yield return ("kitty-keyboard", Utf8(
            "\u001b[>1u" + "\u001b[>1u" + "\u001b[=1;2u" + "\u001b[<1u" + "\u001b[?u" +
            "\u001b[?1049h" + "\u001b[>1u" + "\u001b[?1049l"));

        yield return ("synchronized-output", Utf8(
            "\u001b[?2026h" + "batched text one\r\nbatched text two" + "\u001b[?2026l" +
            "\u001b[?2026h" + "left open"));

        yield return ("mode-2048-inband-resize", Utf8(
            "\u001b[?2048h" + "client wants in-band reports" + "\u001b[?2048l" +
            "\u001b[?2048h"));

        yield return ("mixed-modes", Utf8(
            "\u001b[?1h\u001b[?7l\u001b[?6h\u001b[?25l\u001b[?2004h\u001b[?1000h\u001b[?1006h" +
            "\u001b[4h\u001b[20h" +
            "text under all of those"));
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static IEnumerable<string> Select_Filler(this string[] rows)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            yield return $"filler row {i}\r\n";
        }
    }
}
```

> `Select_Filler` is a local convenience so the scroll-region case has enough output to force
> scrolling; if it reads oddly, replace it with a plain `for` loop building the string. The only
> requirement is that the region scrolls at least twice.

- [ ] **Step 3: Write the harness**

Create `tests/Ntilde.VT.Tests/StateTransfer/ParityHarness.cs` with this shape:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ntilde.Replay;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// One parser+buffer pair driven a byte at a time, tracking what a decoder would be holding back.
/// </summary>
/// <remarks>
/// Byte-at-a-time is the point, not a simplification: it makes every byte offset a legal cut
/// point and exercises mid-sequence chunking on every corpus for free. The pending-tail tracking
/// is here rather than in <c>Utf8ChunkDecoder</c> because Ntilde.VT.Tests must not reference
/// Ntilde.Pty - the decoder's own equivalence is pinned separately by
/// <c>Utf8ChunkDecoderTests</c>.
/// </remarks>
internal sealed class ParityRun
{
    private readonly System.Text.Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly char[] _chars = new char[8];
    private readonly List<byte> _pendingTail = new();

    public ParityRun(int cols, int rows, bool forceConPtyFiltering)
    {
        Buffer = new TerminalBuffer(cols, rows);
        Parser = new AnsiParser(Buffer, forceConPtyFiltering) { ImageDecoder = null };
        Parser.OnResponse = r => Responses.Add(r);
    }

    public TerminalBuffer Buffer { get; }
    public AnsiParser Parser { get; }
    public List<string> Responses { get; } = new();

    /// <summary>Bytes turned into chars so far; excludes <see cref="PendingTail"/>.</summary>
    public long ConsumedBytes { get; private set; }

    /// <summary>The incomplete UTF-8 prefix the decoder is holding.</summary>
    public byte[] PendingTail => _pendingTail.ToArray();

    /// <summary>
    /// Feeds <paramref name="corpus"/> from <paramref name="from"/> (inclusive) to
    /// <paramref name="to"/> (exclusive), applying every resize in <paramref name="resizes"/>
    /// whose offset falls in that range at the moment the feed reaches it.
    /// </summary>
    public void Feed(byte[] corpus, int from, int to, (int Offset, int Cols, int Rows)[] resizes)
    {
        for (int i = from; i < to; i++)
        {
            foreach ((int offset, int cols, int rows) in resizes.Where(r => r.Offset == i))
            {
                Buffer.Resize(cols, rows);
            }

            _pendingTail.Add(corpus[i]);
            int n = _decoder.GetChars(corpus, i, 1, _chars, 0, flush: false);
            if (n > 0)
            {
                ConsumedBytes += _pendingTail.Count;
                _pendingTail.Clear();
                Parser.Process(new string(_chars, 0, n));
            }
        }

        foreach ((int offset, int cols, int rows) in resizes.Where(r => r.Offset == to))
        {
            Buffer.Resize(cols, rows);
        }
    }
}

internal static class ParityHarness
{
    /// <summary>
    /// Proves "snapshot + tail" equals "continuous" for one corpus stream.
    /// </summary>
    /// <param name="name">Corpus name, quoted in every failure message.</param>
    /// <param name="corpus">The byte stream.</param>
    /// <param name="cutPoints">Byte offsets at which to snapshot and resume.</param>
    /// <param name="resizes">(byte offset, cols, rows) applied to every run at that offset.</param>
    /// <param name="forceConPtyFiltering">
    /// Passed to every <see cref="AnsiParser"/> so the result does not depend on the host OS.
    /// </param>
    public static void AssertSnapshotTailParity(
        string name,
        byte[] corpus,
        int[] cutPoints,
        (int Offset, int Cols, int Rows)[] resizes,
        bool forceConPtyFiltering)
    {
        foreach (int cut in cutPoints)
        {
            // A: the whole stream, continuously. Rebuilt per cut point so "responses after the
            // cut" can be isolated without replaying bookkeeping.
            var a = new ParityRun(80, 24, forceConPtyFiltering);
            a.Feed(corpus, 0, cut, resizes);
            int responsesBeforeCut = a.Responses.Count;
            a.Feed(corpus, cut, corpus.Length, resizes);
            List<string> responsesAfterCut = a.Responses.Skip(responsesBeforeCut).ToList();

            // B: the prefix only, then snapshot.
            var b = new ParityRun(80, 24, forceConPtyFiltering);
            b.Feed(corpus, 0, cut, resizes);

            TerminalStateSnapshot snapshot = b.Buffer.ExportState(int.MaxValue);
            snapshot.Parser = b.Parser.ExportState();
            snapshot.DecoderTail = b.PendingTail;
            snapshot.StreamSeq = b.ConsumedBytes;

            Assert.True(
                snapshot.StreamSeq + snapshot.DecoderTail!.Length == cut,
                $"[{name} @ {cut}] snapshot claims stream position " +
                $"{snapshot.StreamSeq}+{snapshot.DecoderTail.Length} but the cut was at {cut}.");

            // C: restored from the snapshot, then fed the tail INCLUDING the pending bytes.
            var c = new ParityRun(b.Buffer.Cols, b.Buffer.Rows, forceConPtyFiltering);
            c.Buffer.ImportState(snapshot);
            c.Parser.ImportState(snapshot.Parser);
            c.Feed(corpus, (int)snapshot.StreamSeq, corpus.Length, resizes);

            AssertEquivalent(name, cut, a, responsesAfterCut, c);
        }
    }

    private static void AssertEquivalent(
        string name, int cut, ParityRun a, List<string> responsesAfterCut, ParityRun c)
    {
        string where = $"[{name} @ cut {cut}]";

        // The rendered active screen, which is what a divergence actually looks like to a user.
        BufferSnapshot sa = BufferSnapshot.Capture(a.Buffer, includeAttributes: true);
        BufferSnapshot sc = BufferSnapshot.Capture(c.Buffer, includeAttributes: true);
        AssertLinesEqual($"{where} screen text", sa.Lines, sc.Lines);
        AssertLinesEqual($"{where} cell attributes", sa.AttributeLines!, sc.AttributeLines!);
        Assert.True(sa.CursorCol == sc.CursorCol && sa.CursorRow == sc.CursorRow
            && sa.IsAltScreen == sc.IsAltScreen,
            $"{where} cursor/screen: A=({sa.CursorCol},{sa.CursorRow},alt={sa.IsAltScreen}) " +
            $"C=({sc.CursorCol},{sc.CursorRow},alt={sc.IsAltScreen})");

        // Everything BufferSnapshot cannot see: the inactive screen, scrollback, and every
        // non-cell field. Compared through ExportState because it is the one view that covers
        // both screens without switching screens - switching would mutate what is under test.
        TerminalStateSnapshot ea = a.Buffer.ExportState(int.MaxValue);
        TerminalStateSnapshot ec = c.Buffer.ExportState(int.MaxValue);

        AssertEqual($"{where} main cells", ea.Main.CellsBase64, ec.Main.CellsBase64);
        // …and Alt.CellsBase64, Main/Alt RowWraps, ExtendedText, LinkIds,
        //    Scrollback.CellsBase64, ScrollbackRowCount, Scrollback.RowWraps,
        //    CursorCol, CursorRow, IsPendingWrap, ScrollTop, ScrollBottom, TabStops,
        //    SavedCursorMain/Alt, ScreenCursorMain/Alt, RestoreMainCursorOnAltExit,
        //    Sgr, Modes, KittyKeyboard, IsSynchronizedOutput, Grapheme,
        //    Hyperlinks, CurrentHyperlinkId.
        //
        // LastSyncStartUtcTicks is compared for PRESENCE only (both zero or both non-zero):
        // it is a wall-clock reading and the two runs take it at different instants.

        AssertEqual($"{where} parser state", a.Parser.ExportState().ToDebugString(),
            c.Parser.ExportState().ToDebugString());

        AssertLinesEqual($"{where} device replies", responsesAfterCut.ToArray(), c.Responses.ToArray());
    }

    /// <summary>
    /// Equality with a message that says what diverged and what both sides held. xUnit's own
    /// Assert.Equal has no user-message overload, and a parity failure reading only
    /// "Assert.Equal() Failure" costs an hour of bisecting cut points by hand.
    /// </summary>
    private static void AssertEqual<T>(string what, T expected, T actual)
    {
        Assert.True(
            EqualityComparer<T>.Default.Equals(expected, actual),
            $"{what} diverged.
  continuous: {expected}
  restored:   {actual}");
    }

    /// <summary>Compares row arrays and names the first differing row, with both sides.</summary>
    private static void AssertLinesEqual(string what, string[] expected, string[] actual)
    {
        Assert.True(expected.Length == actual.Length,
            $"{what}: row count {expected.Length} vs {actual.Length}");

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i] == actual[i],
                $"{what}: row {i} diverged.\n  continuous: {expected[i]}\n  restored:   {actual[i]}");
        }
    }
}
```

> Use the local `AssertEqual` / `AssertLinesEqual` helpers throughout rather than bare
> `Assert.Equal` — xUnit has no user-message overload, and the whole value of this test is that a
> failure names the corpus, the cut point, and the field. For collection fields
> (`RowWraps`, `TabStops`, the `LinkIds` maps) compare a stable rendering, not the collection
> object, for the same reason.

Notes on the parts the skeleton leaves implicit:

- **The comparison IS the specification.** Fill in every commented-out field in
  `AssertEquivalent`; a field left uncompared is a field the snapshot is free to get wrong.
- **`Modes`, `Sgr`, `Grapheme`, `KittyKeyboard`, `SavedCursor*`, `Hyperlinks`** are DTOs without
  value equality, so compare them property by property (or give each a `ToDebugString()` the way
  `AnsiParserState` has one, and compare that — the messages are better and it is less code).
- **`Hyperlinks`** compares as an ordered list of `(Uri, Id)` pairs, and the `LinkIds` dictionaries
  compare as `(key → index)` maps. Together those two say "the same cells point at the same link
  identities", which is what OSC 8 grouping means; comparing `Hyperlink` instances directly would
  always fail, because A's and C's are necessarily different objects.
- **Run A is rebuilt per cut point** rather than once per corpus. That costs a full replay per cut
  and buys the thing that matters: `responsesAfterCut` is exactly the replies the continuous run
  emitted from the cut onward, with no bookkeeping to get wrong.

- [ ] **Step 4: Build to verify the harness compiles**

```
scripts/build.ps1 build tests/Ntilde.VT.Tests
```

Expected: build succeeds. (Nothing calls the harness yet — Task 9 does.)

- [ ] **Step 5: Commit**

```bash
git add tests/Ntilde.VT.Tests/Ntilde.VT.Tests.csproj \
        tests/Ntilde.VT.Tests/StateTransfer/ParityCorpus.cs \
        tests/Ntilde.VT.Tests/StateTransfer/ParityHarness.cs
git commit -m "test(vt): parity corpus and snapshot/tail harness

The corpus pairs recorded replay fixtures with synthetic generators for the corners no
recording contains. The harness feeds byte-at-a-time so every cut point is a legal
boundary and mid-sequence chunking comes for free.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: The parity tests

**Files:**
- Create: `tests/Ntilde.VT.Tests/StateTransfer/SnapshotTailParityTests.cs`

**Interfaces:**
- Consumes: `ParityCorpus.All()`, `ParityHarness.AssertSnapshotTailParity(...)` (Task 8).
- Produces: the phase's proof.

- [ ] **Step 1: Write the tests**

Create `tests/Ntilde.VT.Tests/StateTransfer/SnapshotTailParityTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// The point of Phase 0: a pane that attaches to a running session mid-stream - takes a
/// snapshot, then parses the rest of the bytes itself - must end up in exactly the state it
/// would have reached by parsing the whole stream from the start. Everything else in this phase
/// exists so this can be asserted.
/// </summary>
/// <remarks>
/// A failure here is a bug in export/import, never a reason to relax the comparison. The
/// comparison IS the specification.
/// </remarks>
public class SnapshotTailParityTests
{
    /// <summary>
    /// Cut points per corpus. Deterministic (fixed seed), and deliberately not uniform: the
    /// interesting cuts land inside an escape sequence or inside a multi-byte code point, which
    /// uniform spacing across a stream of mostly-ASCII would mostly miss.
    /// </summary>
    private static int[] CutPointsFor(byte[] corpus)
    {
        var rng = new Random(0xC0FFEE);
        var cuts = new SortedSet<int>();

        // Anchors: the ends, and a spread across the middle.
        cuts.Add(0);
        cuts.Add(corpus.Length);
        for (int i = 1; i < 16; i++)
        {
            cuts.Add(corpus.Length * i / 16);
        }

        // Every offset that sits one byte after an ESC, so the cut lands inside a sequence.
        for (int i = 0; i < corpus.Length - 1; i++)
        {
            if (corpus[i] == 0x1B)
            {
                cuts.Add(i + 1);
            }
        }

        // Every offset that sits between the bytes of a multi-byte code point.
        for (int i = 1; i < corpus.Length; i++)
        {
            if ((corpus[i] & 0xC0) == 0x80)
            {
                cuts.Add(i);
            }
        }

        // Plus a random sample, so a corpus with no escapes still gets coverage in depth.
        for (int i = 0; i < 24 && corpus.Length > 0; i++)
        {
            cuts.Add(rng.Next(corpus.Length + 1));
        }

        // Bounded: a 200 KB vttest recording has thousands of ESCs, and the harness is O(n) per
        // cut. Take a deterministic, evenly-spread subset when there are too many.
        int[] ordered = cuts.ToArray();
        const int MaxCuts = 120;
        if (ordered.Length <= MaxCuts)
        {
            return ordered;
        }

        return Enumerable.Range(0, MaxCuts)
            .Select(i => ordered[(int)((long)i * ordered.Length / MaxCuts)])
            .Distinct()
            .ToArray();
    }

    public static TheoryData<string> CorpusNames()
    {
        var data = new TheoryData<string>();
        foreach ((string name, _) in ParityCorpus.All())
        {
            data.Add(name);
        }

        return data;
    }

    private static byte[] CorpusBytes(string name) =>
        ParityCorpus.All().First(c => c.Name == name).Bytes;

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void SnapshotPlusTail_EqualsContinuous(string name)
    {
        byte[] corpus = CorpusBytes(name);

        ParityHarness.AssertSnapshotTailParity(
            name,
            corpus,
            CutPointsFor(corpus),
            resizes: [],
            forceConPtyFiltering: false);
    }

    /// <summary>
    /// Same proof with resizes interleaved. A resize reflows, which rebuilds the absolute-row
    /// coordinate space - so this is the case most likely to expose a snapshot field that is
    /// nearly right.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void SnapshotPlusTail_EqualsContinuous_WithInterleavedResizes(string name)
    {
        byte[] corpus = CorpusBytes(name);
        if (corpus.Length < 8)
        {
            return;
        }

        (int Offset, int Cols, int Rows)[] resizes =
        [
            (corpus.Length / 4, 60, 20),
            (corpus.Length / 2, 100, 30),
            (corpus.Length * 3 / 4, 80, 24),
        ];

        ParityHarness.AssertSnapshotTailParity(
            name,
            corpus,
            CutPointsFor(corpus),
            resizes,
            forceConPtyFiltering: false);
    }

    /// <summary>
    /// ConPTY filtering changes how kitty graphics APC and capability probes are handled, and it
    /// is what every Windows session runs with. Both sides are constructed with the same value -
    /// this variant checks that the state transfer is correct under that value too, not that the
    /// two values agree with each other (they legitimately do not).
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void SnapshotPlusTail_EqualsContinuous_UnderConPtyFiltering(string name)
    {
        byte[] corpus = CorpusBytes(name);

        ParityHarness.AssertSnapshotTailParity(
            name,
            corpus,
            CutPointsFor(corpus),
            resizes: [],
            forceConPtyFiltering: true);
    }
}
```

- [ ] **Step 2: Run the parity tests**

```
scripts/build.ps1 test tests/Ntilde.VT.Tests --filter "FullyQualifiedName~SnapshotTailParityTests"
```

Expected on the first run: **failures.** That is the test doing its job. Work each one as follows:

1. Read which field the failure message names.
2. Find the `TerminalBuffer` / `AnsiParser` field behind it.
3. Decide: is it missing from the snapshot, restored wrongly, or derived state that `ImportState`
   should recompute rather than carry?
4. Fix `ExportState` / `ImportState`. **Never** relax the comparison or drop a corpus entry.
5. Re-run.

Record each divergence and its fix — item 4 of the phase report.

> Divergences worth expecting, so they are recognised rather than re-derived:
> - Anything reached through `_scrollback.Generation` (the epoch changes when a fresh
>   `ScrollbackPages` is built on import; nothing in the snapshot should depend on it).
> - `_isStyleDirty` — if the packed style cache is carried instead of recomputed, the first cell
>   written after import gets the exporter's style.
> - Row `Revision` / `Id` — these feed the render diff, not semantics; `BufferSnapshot.Capture`
>   already masks the `Dirty` flag, and nothing else should compare them.
> - A wide character straddling a cut, where the low surrogate arrives after the snapshot:
>   `TerminalGraphemeState.HighSurrogate` is what covers it.

- [ ] **Step 3: Run the full VT suite**

```
scripts/build.ps1 test tests/Ntilde.VT.Tests
```

Expected: PASS, 0 failed.

- [ ] **Step 4: Commit**

```bash
git add tests/Ntilde.VT.Tests/StateTransfer/SnapshotTailParityTests.cs \
        src/Ntilde.VT/TerminalBuffer.StateTransfer.cs src/Ntilde.VT/AnsiParser.StateTransfer.cs \
        src/Ntilde.VT/TerminalStateSnapshot.cs src/Ntilde.VT/AnsiParserState.cs
git commit -m "test(vt): prove snapshot+tail equals continuous parsing

Across recorded and synthetic corpora, at cut points chosen to land inside escape
sequences and inside multi-byte code points, with and without interleaved resizes,
and under ConPTY filtering. Fixes to export/import found by these are folded in.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Full verification sweep and the phase report

**Files:**
- Modify: `docs/ARCHITECTURE.md` (assembly table rows for VT and Pty)
- Modify: `docs/MODULE_OWNERSHIP.md` (public-surface lines for VT and Pty)

- [ ] **Step 1: Run the architecture tests**

```
scripts/build.ps1 test tests/Ntilde.Architecture.Tests
```

Expected: PASS, 0 failed. Specifically `Pty_must_not_depend_on_Vt`,
`Pty_csproj_must_not_reference_Vt`, `Vt_must_be_a_leaf_assembly`,
`Vt_csproj_must_have_no_project_references`, `Replay_only_depends_on_Vt`,
`No_production_assembly_references_test_assemblies`.

- [ ] **Step 2: Run every affected suite**

```
scripts/build.ps1 test tests/Ntilde.VT.Tests
scripts/build.ps1 test tests/Ntilde.Platform.Tests
scripts/build.ps1 test tests/Ntilde.McpServer.Tests
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane!=PlatformBoot" --blame-hang-timeout 5m
scripts/build.ps1 test tests/Ntilde.App.Tests --filter "Lane=PlatformBoot" --blame-hang-timeout 5m
```

Record pass/fail counts for each. The App.Tests lanes run **sequentially, never concurrently**,
and a summary followed by `Test Run Aborted.` is not a result (the wrapper fails it for you).

- [ ] **Step 3: Verify the Native AOT publish still succeeds**

The new JSON must be source-generated; a reflection path shows up as `IL2026`/`IL3050`, which the
App treats as errors only on publish.

```
scripts/build.ps1 publish src/Ntilde.App -c Release -r win-x64
```

Expected: publish succeeds with no `IL2026` / `IL3050`. (On Linux/macOS use the matching RID.)

- [ ] **Step 4: Update the two architecture docs**

In `docs/ARCHITECTURE.md`, §2's table, extend the `Ntilde.VT` "Owns" cell with
`, transferable terminal/parser state (TerminalStateSnapshot)` and the `Ntilde.Pty` cell with
`, session factory contract, UTF-8 chunk decoding`.

In `docs/MODULE_OWNERSHIP.md`, add to `Ntilde.VT`'s **Public surface** line:
`TerminalStateSnapshot`, `AnsiParserState`, `TerminalStateSerializer`; and to `Ntilde.Pty`'s:
`ITerminalSessionFactory`, `TerminalSessionRequest`, `SshSessionDescriptor`,
`ITerminalSessionCapabilities`, `Utf8ChunkDecoder`.

- [ ] **Step 5: Commit the docs**

```bash
git add docs/ARCHITECTURE.md docs/MODULE_OWNERSHIP.md
git commit -m "docs: record the Phase 0 multiplexer seams in the assembly tables

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 6: Write the phase report**

Answer all six items the spec asks for, in the PR description:

1. Branch name (`feat/mux-phase0-seams`) and files added/changed, grouped by deliverable.
2. Exact wrapper invocations run and their pass/fail counts, including both App.Tests lanes and
   the architecture tests.
3. Every field added to `TerminalStateSnapshot` and `AnsiParserState`, plus the excluded-state
   table from Task 6 and Task 7 with its reasons.
4. Every divergence the parity test exposed and how it was fixed (Task 9 Step 2).
5. The `[mux-phase0]` measurement line from Task 7 Step 5: serialized size for 80x24 + 10k
   scrollback, and export / import timings.
6. Open questions and the seams that were harder than described — in particular anything about
   `InitializeSessionCore`'s ordering (the shell-integration launch plan mutates `effectiveShell`
   and `args` *before* the request is built) and `NativeSshSession`'s per-payload `char[]`
   allocation, which this phase leaves as it found it.

- [ ] **Step 7: Open the PR**

```bash
git push -u origin feat/mux-phase0-seams
gh pr create --base main --title "Multiplexer Phase 0: seams and snapshot/tail parity" --body "<the report from Step 6>

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
```

---

## Notes for whoever executes this

- **Verify the branch before every commit.** Other sessions commit into this checkout and switch
  its branch mid-task: `git rev-parse --abbrev-ref HEAD` should say `feat/mux-phase0-seams`. A
  `git worktree` is the safer option for a multi-commit task like this one.
- **`tests/Ntilde.Platform.Tests` builds `rusty_ssh` with cargo** before it runs. Set
  `SKIP_RUST_NATIVE_BUILD=1` when the native library is already current and you only want the
  managed tests.
- **Known flakes — re-run, do not debug:** `PtyBackspaceAtLineStart…InWindowsPowerShell`
  (~33% on windows-latest), the CI "Unit Tests" 8-minute host hang after all tests pass, and the
  CI russh/elliptic-curve crate fetch failure.
- **A flaky new test is a real bug.** If a parity case fails intermittently, the cause is
  non-determinism in the snapshot (a wall-clock field, a hash ordering, a generation counter) —
  find it. Do not add a retry or raise a timeout.
