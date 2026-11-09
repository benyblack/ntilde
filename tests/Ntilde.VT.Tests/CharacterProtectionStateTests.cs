using System.Text;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>
/// DECSCA state: the pen mode, the per-cell protection it writes, and how both survive cell movement, resizing,
/// saved cursors, resets, full-state export/import and recording checkpoints. The erase behaviour that consumes
/// this state is pinned separately by <see cref="SelectiveErasureTests"/>.
/// </summary>
public sealed class CharacterProtectionStateTests
{
    private const string Esc = "\u001b";

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(int cols = 10, int rows = 4)
    {
        var buffer = new TerminalBuffer(cols, rows);
        return (buffer, new AnsiParser(buffer, forceConPtyFiltering: false));
    }

    private static bool[] Protection(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            var line = buffer.GetRowAbsolute((buffer.IsAltScreenActive ? 0 : buffer.Scrollback.Count) + row)!;
            return Enumerable.Range(0, buffer.Cols).Select(line.IsProtected).ToArray();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string Mask(TerminalBuffer buffer, int row) => new(Protection(buffer, row).Select(p => p ? 'P' : '.').ToArray());

    /// <summary>DECSCA is a pen mode, separate from the SGR set: SGR 0 and the other resets leave it alone.</summary>
    [Fact]
    public void The_pen_mode_is_independent_of_sgr()
    {
        var (buffer, parser) = Create();
        Assert.False(buffer.IsCharacterProtected);
        parser.Process(Esc + "[1\"q");
        Assert.True(buffer.IsCharacterProtected);
        parser.Process(Esc + "[0m" + Esc + "[m" + Esc + "[1;4;38;5;9m" + Esc + "[0m");
        Assert.True(buffer.IsCharacterProtected);
        parser.Process(Esc + "[2\"q");
        Assert.False(buffer.IsCharacterProtected);
    }

    /// <summary>Protected characters carry no visual attribute: the cell is identical to an unprotected one.</summary>
    [Fact]
    public void Protection_does_not_change_the_stored_cell()
    {
        var (protectedBuffer, protectedParser) = Create();
        var (plainBuffer, plainParser) = Create();
        protectedParser.Process(Esc + "[1;1H" + Esc + "[1\"qP");
        plainParser.Process(Esc + "[1;1HP");
        protectedBuffer.Lock.EnterReadLock();
        plainBuffer.Lock.EnterReadLock();
        try { Assert.Equal(plainBuffer.GetCell(0, 0), protectedBuffer.GetCell(0, 0)); }
        finally { plainBuffer.Lock.ExitReadLock(); protectedBuffer.Lock.ExitReadLock(); }
        Assert.Equal("P.........", Mask(protectedBuffer, 0));
        Assert.Equal("..........", Mask(plainBuffer, 0));
    }

    /// <summary>Wide glyphs protect both cells; erasing, overwriting and ECH clear protection.</summary>
    [Fact]
    public void Protection_follows_writes_and_ordinary_erasure()
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1;1H" + Esc + "[1\"q界ab" + Esc + "[0\"qcd");
        Assert.Equal("PPPP......", Mask(buffer, 0));
        parser.Process(Esc + "[1;4H" + Esc + "[X"); // ECH over the protected b
        Assert.Equal("PPP.......", Mask(buffer, 0));
        parser.Process(Esc + "[1;3HZ");              // unprotected overwrite of the protected a
        Assert.Equal("PP........", Mask(buffer, 0));
        parser.Process(Esc + "[1\"q" + Esc + "[1;6HQ"); // protected overwrite of the unprotected d
        Assert.Equal("PP...P....", Mask(buffer, 0));
        parser.Process(Esc + "[1;5H" + Esc + "[0K");  // EL from c to the end
        Assert.Equal("PP........", Mask(buffer, 0));
    }

    /// <summary>ED 0/1/2, DECERA, EL and ECH all remove protection together with the characters.</summary>
    [Theory]
    [InlineData("[1;1H[0J")]
    [InlineData("[2J")]
    [InlineData("[1;1H[2K")]
    [InlineData("[1;1;1;5$z")]
    public void Ordinary_erases_drop_protection(string erase)
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1;1H" + Esc + "[1\"qPQRST");
        parser.Process(erase.Replace("[", Esc + "["));
        Assert.Equal("..........", Mask(buffer, 0));
    }

    /// <summary>DECSC/DECRC carry the pen mode, per screen, and DECSTR and RIS reset it.</summary>
    [Fact]
    public void Saved_cursors_and_resets_cover_the_pen_mode()
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1\"q" + Esc + "7" + Esc + "[0\"q");
        Assert.False(buffer.IsCharacterProtected);
        parser.Process(Esc + "8");
        Assert.True(buffer.IsCharacterProtected);

        parser.Process(Esc + "[!p"); // DECSTR
        Assert.False(buffer.IsCharacterProtected);

        parser.Process(Esc + "[1\"q" + Esc + "7" + Esc + "c"); // RIS
        Assert.False(buffer.IsCharacterProtected);
        parser.Process(Esc + "8");
        Assert.False(buffer.IsCharacterProtected);
    }

    /// <summary>Entering the alternate screen gives it its own pen; leaving restores the main screen's.</summary>
    [Fact]
    public void The_pen_mode_is_per_screen()
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1\"q" + Esc + "[?1049h");
        Assert.False(buffer.IsCharacterProtected);
        parser.Process(Esc + "[?1049l");
        Assert.True(buffer.IsCharacterProtected);
    }

    /// <summary>Cells move with their protection: ICH, DCH, insert mode and scrolling.</summary>
    [Fact]
    public void Cell_movement_carries_protection()
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1;1H" + Esc + "[1\"qPQ" + Esc + "[0\"qRS" + Esc + "[1;1H" + Esc + "[3@");
        Assert.Equal("...PP.....", Mask(buffer, 0));
        parser.Process(Esc + "[1;1H" + Esc + "[3P");
        Assert.Equal("PP........", Mask(buffer, 0));

        parser.Process(Esc + "[2J" + Esc + "[2;1H" + Esc + "[1\"qrow2" + Esc + "[0\"q" + Esc + "[4;1H\n\n");
        Assert.Equal(".....", Mask(buffer, 0)[..5]);   // row 2 scrolled to row 0
        Assert.Equal(".....", Mask(buffer, 3)[..5]);   // the line that appeared is unprotected
    }

    /// <summary>A protected row scrolled off the top is history, which has no protection; the screen row it leaves behind is clean.</summary>
    [Fact]
    public void Scrolling_into_history_leaves_a_clean_screen_row()
    {
        var (buffer, parser) = Create(6, 3);
        parser.Process(Esc + "[1;1H" + Esc + "[1\"qkeep" + Esc + "[0\"q" + Esc + "[3;1H\n");
        Assert.Equal(1, buffer.Scrollback.Count);
        Assert.Equal("......", Mask(buffer, 2));
        Assert.False(buffer.HasProtectedCharacters());
    }

    /// <summary>Resizing keeps protection with its characters, through reflow and in the alternate bank.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resize_keeps_protection_with_the_characters(bool alternate)
    {
        var (buffer, parser) = Create(10, 4);
        if (alternate) parser.Process(Esc + "[?1049h");
        parser.Process(Esc + "[1;1HAB" + Esc + "[1\"qCD" + Esc + "[0\"qEF");
        buffer.Resize(14, 4);
        Assert.Equal("..PP", Mask(buffer, 0)[..4]);
        buffer.Resize(5, 4);
        Assert.Equal("..PP", Mask(buffer, 0)[..4]);
        parser.Process(Esc + "[1;1H" + Esc + "[?2J");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal('C', buffer.GetCell(2, 0).Character);
            Assert.Equal('D', buffer.GetCell(3, 0).Character);
            Assert.Equal(' ', buffer.GetCell(0, 0).Character);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>Protected blanks are authored content: a sparse-gap reflow must not replace them with unprotected padding, and a protected blank row below the cursor survives the trim.</summary>
    [Fact]
    public void Reflow_keeps_protected_blanks_in_gaps_and_below_the_cursor()
    {
        var (buffer, parser) = Create(20, 6);
        parser.Process(Esc + "[1;1HA" + Esc + "[1\"q" + new string(' ', 18) + Esc + "[0\"qZ");
        parser.Process(Esc + "[5;1H" + Esc + "[1\"q   " + Esc + "[0\"q" + Esc + "[1;1H");
        buffer.Resize(24, 6);
        Assert.Equal(".111111111111111111", Mask(buffer, 0).Replace('P', '1')[..19]);
        Assert.Equal(18, Mask(buffer, 0).Count(c => c == 'P'));
        Assert.Contains('P', Mask(buffer, 4));
    }

    /// <summary>A protected ordinary-wide glyph that a shrinking resize clips must not leave its protection on the blank that replaces it.</summary>
    [Fact]
    public void Resize_clipping_a_protected_wide_glyph_clears_its_protection()
    {
        var (buffer, parser) = Create(10, 4);
        parser.Process(Esc + "]66;s=2;A\a" + Esc + "[1;7H" + Esc + "[1\"q界" + Esc + "[0\"q");
        buffer.Resize(7, 4);
        Assert.False(Protection(buffer, 0)[6]);
    }

    /// <summary>A selective display erase over default blanks does not extend the authored extent, so a later wide glyph still reflows without a phantom space.</summary>
    [Fact]
    public void Selective_display_erase_does_not_extend_the_authored_extent()
    {
        var (buffer, parser) = Create(5, 3);
        parser.Process(Esc + "[1;1H" + Esc + "[1\"qP" + Esc + "[0\"q" + Esc + "[?2J" + Esc + "[1;2Habc界");
        buffer.Resize(8, 3);
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(buffer.Scrollback.Count)!;
            Assert.Equal("Pabc界", new string(row.Cells.Where(cell => !cell.IsWideContinuation).Select(cell => cell.Character == '\0' ? ' ' : cell.Character).ToArray()).TrimEnd());
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>The protection queries follow the cell accessors' lock pattern: callable with the read lock already held.</summary>
    [Fact]
    public void Protection_queries_work_under_a_caller_held_read_lock()
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1;1H" + Esc + "[1\"qP");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.True(buffer.HasProtectedCharacters());
            Assert.True(buffer.IsCellProtected(0, 0));
            Assert.False(buffer.IsCellProtected(1, 0));
            Assert.False(buffer.IsCellProtected(99, 99));
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.True(buffer.HasProtectedCharacters());
    }

    /// <summary>The full-state export (mux attach, session transfer) carries the pen mode and protected cells in both banks.</summary>
    [Fact]
    public void Full_state_round_trip_preserves_protection_in_both_banks_and_the_pens()
    {
        var (live, parser) = Create();
        parser.Process(Esc + "[1;1HMAIN" + Esc + "[1;3H" + Esc + "[1\"qpp" + Esc + "[0\"qq" + Esc + "[1\"q" + Esc + "7");
        parser.Process(Esc + "[?1049h" + Esc + "[2;2H" + Esc + "[1\"qAL" + Esc + "[0\"qT" + Esc + "[1\"q");
        var state = live.ExportState(int.MaxValue);
        Assert.NotNull(state.Alt.ProtectedCells);
        Assert.NotNull(state.Main.ProtectedCells);
        var decoded = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(state));
        var restored = new TerminalBuffer(10, 4);
        restored.ImportState(decoded);

        Assert.True(restored.IsAltScreenActive);
        Assert.True(restored.IsCharacterProtected);
        Assert.Equal(Mask(live, 1), Mask(restored, 1));
        parser.Process(Esc + "[?1049l");
        var restoredParser = new AnsiParser(restored, forceConPtyFiltering: false);
        restoredParser.Process(Esc + "[?1049l");
        Assert.Equal(Mask(live, 0), Mask(restored, 0));
        Assert.Equal("..PP......", Mask(restored, 0));
        // DECRC restores the saved pen on the restored terminal exactly as on the live one.
        parser.Process(Esc + "[0\"q" + Esc + "8");
        restoredParser.Process(Esc + "[0\"q" + Esc + "8");
        Assert.True(live.IsCharacterProtected);
        Assert.Equal(live.IsCharacterProtected, restored.IsCharacterProtected);
        Assert.Equal(TerminalStateSerializer.ToBytes(live.ExportState(int.MaxValue)),
            TerminalStateSerializer.ToBytes(restored.ExportState(int.MaxValue)));
    }

    /// <summary>A state exported before protection existed (no <c>prot</c> or <c>dsca</c> member) imports as unprotected.</summary>
    [Fact]
    public void State_without_protection_members_imports_unprotected()
    {
        var (live, parser) = Create();
        parser.Process(Esc + "[1;1HABC");
        var state = live.ExportState(int.MaxValue);
        string json = Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(state));
        Assert.DoesNotContain("\"prot\"", json);
        Assert.DoesNotContain("\"dsca\"", json);
        var restored = new TerminalBuffer(10, 4);
        restored.ImportState(TerminalStateSerializer.FromBytes(Encoding.UTF8.GetBytes(json)));
        Assert.False(restored.IsCharacterProtected);
        Assert.False(restored.HasProtectedCharacters());
    }

    /// <summary>An actual recording checkpoint carries the pen mode and protected cells; replaying the tail agrees with the live run.</summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Recorded_checkpoint_and_selective_tail_replay_identically()
    {
        string path = Path.GetTempFileName();
        try
        {
            var (live, parser) = Create();
            parser.Process(Esc + "[1;1H" + Esc + "[1\"qP" + Esc + "[0\"qABCDZ" + Esc + "[1\"q");
            const string tail = Esc + "[1;1;1;5${" + Esc + "[3;1H" + Esc + "[?2J";
            using (var recorder = new PtyRecorder(path, 10, 4))
            {
                recorder.RecordSnapshot(live, parser);
                byte[] data = Encoding.UTF8.GetBytes(tail);
                recorder.RecordChunkAt(100, data, data.Length);
            }
            parser.Process(tail);
            var replay = new TerminalBuffer(10, 4);
            var replayParser = new AnsiParser(replay, forceConPtyFiltering: false);
            bool applied = false;
            await new ReplayRunner(path).RunAsync(
                data => { replayParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    Assert.NotNull(snapshot.State);
                    ReplayRunner.ApplySnapshot(replay, replayParser, snapshot);
                    Assert.True(replay.IsCharacterProtected);
                    Assert.Equal("P.........", Mask(replay, 0));
                    applied = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            Assert.Equal(Mask(live, 0), Mask(replay, 0));
            Assert.Equal(BufferSnapshot.Capture(live).ToFormattedString(), BufferSnapshot.Capture(replay).ToFormattedString());
            replay.Lock.EnterReadLock();
            try
            {
                Assert.Equal('P', replay.GetCell(0, 0).Character);
                Assert.Equal(' ', replay.GetCell(5, 0).Character); // the tail's DECSED erased the unprotected Z outside the rectangle
                Assert.Equal(' ', replay.GetCell(1, 0).Character);
            }
            finally { replay.Lock.ExitReadLock(); }
        }
        finally { File.Delete(path); }
    }

    /// <summary>A checkpoint without a full-state block (cells plus side tables only) carries protection too.</summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Buffer_only_checkpoint_round_trips_protection()
    {
        var (live, parser) = Create();
        parser.Process(Esc + "[1;1H" + Esc + "[1\"qPQ" + Esc + "[0\"qR" + Esc + "[1\"q");
        string path = Path.GetTempFileName();
        try
        {
            using (var recorder = new PtyRecorder(path, 10, 4)) recorder.RecordSnapshot(live);
            ReplaySnapshot? captured = null;
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask,
                onSnapshotCallback: snapshot => { captured = snapshot; return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.NotNull(captured);
            var restored = new TerminalBuffer(10, 4);
            ReplayRunner.ApplySnapshot(restored, new AnsiParser(restored, forceConPtyFiltering: false), captured!);
            Assert.Equal("PP........", Mask(restored, 0));
            Assert.True(restored.IsCharacterProtected);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A checkpoint's protection keys only reach cells the checkpoint restored: a taller destination keeps its other rows as they were.</summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Buffer_only_checkpoint_protection_never_marks_cells_it_did_not_restore()
    {
        var (source, sourceParser) = Create(10, 2);
        sourceParser.Process(Esc + "[1;1H" + Esc + "[1\"qPQ");
        string path = Path.GetTempFileName();
        try
        {
            using (var recorder = new PtyRecorder(path, 10, 2)) recorder.RecordSnapshot(source);
            ReplaySnapshot? captured = null;
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask,
                onSnapshotCallback: snapshot => { captured = snapshot; return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.NotNull(captured);
            // a corrupt or foreign key beyond the two restored rows and one beyond the restored columns
            captured!.ProtectedCells = [.. captured.ProtectedCells ?? [], 3 * 10 + 0, 2 * 10 + 5];
            var (taller, tallerParser) = Create(10, 4);
            tallerParser.Process(Esc + "[4;1Hkeep" + Esc + "[3;6Hkeep");
            taller.ApplySnapshot(captured);   // directly, so the 10x2 checkpoint lands on a 10x4 buffer without any resize
            Assert.Equal("PP........", Mask(taller, 0));
            Assert.Equal("..........", Mask(taller, 2));
            Assert.Equal("..........", Mask(taller, 3));
        }
        finally { File.Delete(path); }
    }

    /// <summary>A narrower checkpoint restored directly into a wider buffer leaves the cells it did not replace, protection included.</summary>
    [Fact]
    [Trait("Category", "Replay")]
    public async Task Narrow_checkpoint_restore_keeps_protection_of_untouched_columns()
    {
        var (source, sourceParser) = Create(5, 2);
        sourceParser.Process(Esc + "[1;1HAB");
        string path = Path.GetTempFileName();
        try
        {
            using (var recorder = new PtyRecorder(path, 5, 2)) recorder.RecordSnapshot(source);
            ReplaySnapshot? captured = null;
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask,
                onSnapshotCallback: snapshot => { captured = snapshot; return Task.CompletedTask; }, ct: TestContext.Current.CancellationToken);
            var (wide, wideParser) = Create(10, 2);
            wideParser.Process(Esc + "[1;10H" + Esc + "[1\"qZ" + Esc + "[0\"q" + Esc + "[1;3H" + Esc + "[1\"qP" + Esc + "[0\"q");
            wide.ApplySnapshot(captured!);
            Assert.Equal("........." + "P", Mask(wide, 0));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Appending to a scaled block rebuilds it with the protection of the whole block, not of the one cell the cursor reads.</summary>
    [Fact]
    public void Resizing_an_attached_scaled_block_keeps_whole_block_protection()
    {
        var (buffer, parser) = Create(8, 4);
        parser.Process(Esc + "[1;1H" + Esc + "[1\"q" + Esc + "]66;s=2;\u2600\a" + Esc + "[0\"q");
        parser.Process(Esc + "[?47h");
        buffer.Resize(8, 3);
        buffer.Resize(8, 4);
        parser.Process(Esc + "[?47l");
        Assert.NotEqual(Protection(buffer, 0)[0], Protection(buffer, 1)[0]); // mixed protection after the round trip
        parser.Process("\uFE0F");
        parser.Process(Esc + "[?2J");
        buffer.Lock.EnterReadLock();
        try { Assert.Equal('\u2600', buffer.GetCell(0, 0).Character); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>A narrow character over a protected wide glyph's head leaves no protected orphan behind.</summary>
    [Fact]
    public void Overwriting_a_protected_wide_head_does_not_leave_a_protected_orphan()
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1;1H" + Esc + "[1\"q界" + Esc + "[0\"q");
        Assert.Equal("PP........", Mask(buffer, 0));
        parser.Process(Esc + "[1;1HX");
        Assert.Equal("..........", Mask(buffer, 0));
    }

    /// <summary>An unprotected write over the tail of a protected wide glyph, or a wide write straddling its head, leaves nothing protected behind.</summary>
    [Theory]
    [InlineData("[1;2HX")]    // over the continuation: the anchor loses protection too
    [InlineData("[1;1H界")]   // an identical wide write replaces the unit; the pen decides (unprotected here)
    public void Overwriting_any_half_of_a_protected_wide_glyph_unprotects_the_other_half(string write)
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1;1H" + Esc + "[1\"q界" + Esc + "[0\"q");
        parser.Process(Esc + write);
        Assert.DoesNotContain('P', Mask(buffer, 0));
    }

    /// <summary>A wide write that straddles the head of a protected wide glyph orphans its continuation, which must not stay protected.</summary>
    [Fact]
    public void A_straddling_wide_write_does_not_leave_a_protected_orphan()
    {
        var (buffer, parser) = Create();
        parser.Process(Esc + "[1;2H" + Esc + "[1\"q界" + Esc + "[0\"q");   // protected glyph on columns 2-3
        Assert.Equal(".PP.......", Mask(buffer, 0));
        parser.Process(Esc + "[1;1H界");                                    // unprotected glyph on columns 1-2
        Assert.Equal("..........", Mask(buffer, 0));
        Assert.False(buffer.HasProtectedCharacters());
    }

    /// <summary>The split-input guarantee holds for the pen sequence itself.</summary>
    [Fact]
    public void Decsca_is_equivalent_across_every_input_split()
    {
        const string input = Esc + "[1\"qP" + Esc + "[0\"qQ";
        for (int split = 0; split <= input.Length; split++)
        {
            var (buffer, parser) = Create();
            parser.Process(input[..split]);
            parser.Process(input[split..]);
            Assert.Equal("P.", Mask(buffer, 0)[..2]);
            Assert.False(buffer.IsCharacterProtected);
        }
    }

    /// <summary>OSC 66 sized text written under protection is protected as a unit and survives a selective erase whole; unprotected sized text does not.</summary>
    [Fact]
    public void Scaled_text_is_protected_and_erased_as_a_unit()
    {
        var (buffer, parser) = Create(8, 4);
        parser.Process(Esc + "[1;1H" + Esc + "[1\"q" + Esc + "]66;s=2;A\a" + Esc + "[0\"q" + Esc + "[1;5H" + Esc + "]66;s=2;B\a");
        Assert.Equal("PP......", Mask(buffer, 0));
        Assert.Equal("PP......", Mask(buffer, 1));
        parser.Process(Esc + "[?2J");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal('A', buffer.GetCell(0, 0).Character);
            Assert.Equal(' ', buffer.GetCell(4, 0).Character);
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal("PP......", Mask(buffer, 0));
        Assert.Equal("PP......", Mask(buffer, 1));
    }
}
