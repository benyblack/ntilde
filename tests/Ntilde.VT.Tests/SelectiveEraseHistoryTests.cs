namespace Ntilde.VT.Tests;

public sealed class SelectiveEraseHistoryTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(bool alternate)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        for (int line = 0; line < 12; line++) parser.Process($"line{line}\r\n");
        Assert.True(buffer.Scrollback.Count > 0);
        if (alternate) parser.Process("\u001b[?1049halt screen");
        parser.Process("\u001b[3;4H\u001b[31;1m");
        return (buffer, parser);
    }

    [Theory]
    [InlineData(false, "\u001b[?3J")]
    [InlineData(true, "\u001b[?3J")]
    [InlineData(false, "\u001b[?03J")]
    [InlineData(true, "\u001b[?03J")]
    public void Undefined_selective_display_mode_three_preserves_complete_state_and_image_anchors(bool alternate, string sequence)
    {
        var (buffer, parser) = Create(false);
        var image = new TerminalImage(new object(), 0, buffer.Scrollback.Count + 1, 2, 1);
        buffer.AddImage(image);
        if (alternate) parser.Process("\u001b[?1049halt screen\u001b[3;4H\u001b[31;1m");
        var before = TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue));
        int imageRow = image.CellY;

        parser.Process(sequence);

        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue)));
        Assert.Same(image, Assert.Single(buffer.Images));
        Assert.Equal(imageRow, image.CellY);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordinary_display_mode_three_still_clears_main_history(bool alternate)
    {
        var (buffer, parser) = Create(alternate);
        var before = buffer.ExportState(int.MaxValue);
        parser.Process("\u001b[3J");
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal(0, buffer.Scrollback.Count);
        Assert.Equal(before.Main.CellsBase64, after.Main.CellsBase64);
        Assert.Equal(before.Alt.CellsBase64, after.Alt.CellsBase64);
        Assert.Equal((before.CursorRow, before.CursorCol), (after.CursorRow, after.CursorCol));
    }

    [Theory]
    [InlineData(false, 0, 'J')]
    [InlineData(false, 1, 'J')]
    [InlineData(false, 2, 'J')]
    [InlineData(true, 0, 'J')]
    [InlineData(true, 1, 'J')]
    [InlineData(true, 2, 'J')]
    [InlineData(false, 0, 'K')]
    [InlineData(false, 1, 'K')]
    [InlineData(false, 2, 'K')]
    [InlineData(true, 0, 'K')]
    [InlineData(true, 1, 'K')]
    [InlineData(true, 2, 'K')]
    public void Defined_selective_modes_keep_the_current_unprotected_alias(bool alternate, int mode, char final)
    {
        var (ordinary, ordinaryParser) = Create(alternate);
        var (selective, selectiveParser) = Create(alternate);
        ordinaryParser.Process($"\u001b[{mode}{final}");
        selectiveParser.Process($"\u001b[?{mode}{final}");
        Assert.Equal(TerminalStateSerializer.ToBytes(ordinary.ExportState(int.MaxValue)),
            TerminalStateSerializer.ToBytes(selective.ExportState(int.MaxValue)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undefined_selective_line_mode_three_remains_ignored(bool alternate)
    {
        var (buffer, parser) = Create(alternate);
        var before = TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue));
        parser.Process("\u001b[?3K");
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue)));
    }
}
