using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Ntilde.VT.Tests;

/// <summary>
/// Kitty Unicode placeholders where images are retained for snapshots (persistent image policy 5, #989): the headless daemon and its clients must
/// accept, refuse and tile the same placeholders from the same bytes, and a snapshot must carry the virtual placements so a restore draws the same image.
/// </summary>
public sealed class PersistentKittyPlaceholderTests
{
    private const string Placeholder = "\U0010EEEE";
    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static string Mark(int number) => char.ConvertFromUtf32(KittyPlaceholders.DiacriticFor(number));
    private static string Cell(int row, int column) => Placeholder + Mark(row) + Mark(column);
    private static readonly string Pixel = Convert.ToBase64String([1, 2, 3, 255]);

    private sealed class Side
    {
        internal readonly TerminalBuffer Buffer = new(40, 20) { ImageSources = new ImageSourceBudget() };
        internal readonly AnsiParser Parser;
        internal readonly List<string> Replies = [];

        internal Side(int policy = PersistentImagePolicy.PlaceholderImagesVersion, IImageDecoder? decoder = null)
        {
            Parser = new AnsiParser(Buffer, forceConPtyFiltering: false) { ImageDecoder = decoder, CellWidth = 8, CellHeight = 16 };
            PersistentImagePolicy.Apply(Parser, policy);
            Parser.OnResponse = Replies.Add;
        }

        internal void Send(string text) => Parser.Process(text);

        /// <summary>What the next frame tiles: one entry per run of placeholder cells.</summary>
        internal List<RenderImageSnapshot> Runs()
        {
            Buffer.Lock.EnterReadLock();
            try { return Buffer.GetVisibleImagesSnapshot(Buffer.TotalLines - Buffer.Rows, Buffer.Rows).Where(image => image.GridColumns > 0).ToList(); }
            finally { Buffer.Lock.ExitReadLock(); }
        }
    }

    /// <summary>An image, a 2 x 2 virtual placement named 7, and the four cells that show it.</summary>
    private static string Traffic() =>
        Command("a=t,f=32,s=1,v=1,i=42", Pixel)
        + Command("a=p,U=1,i=42,p=7,c=2,r=2")
        + $"\x1b[3;4H\x1b[38;5;42m\x1b[58;5;7m{Cell(0, 0)}{Cell(0, 1)}\x1b[2;4H{Cell(1, 0)}{Cell(1, 1)}\x1b[0m";

    private static TerminalStateSnapshot Export(Side side)
    {
        TerminalStateSnapshot snapshot = side.Buffer.ExportState(int.MaxValue);
        snapshot.Parser = side.Parser.ExportState();
        return TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));
    }

    private static Side Restore(TerminalStateSnapshot snapshot, int policy = PersistentImagePolicy.PlaceholderImagesVersion)
    {
        var restored = new Side(policy);
        TerminalStateTransfer.Restore(restored.Buffer, restored.Parser, TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
        return restored;
    }

    private static string Json(TerminalBuffer buffer) => JsonSerializer.Serialize(buffer.ExportState(int.MaxValue).KittyImages);

    private static string Describe(IEnumerable<RenderImageSnapshot> runs) =>
        string.Join("|", runs.Select(run => $"{run.CellX},{run.CellY}+{run.CellWidth} g{run.GridColumn},{run.GridRow}/{run.GridColumns}x{run.GridRows}"));

    [Fact]
    public void A_policy_four_session_still_refuses_placeholders_and_a_policy_five_one_accepts_and_advertises_them()
    {
        var four = new Side(PersistentImagePolicy.CompositionImagesVersion);
        four.Send(Command("a=t,f=32,s=1,v=1,i=42", Pixel));
        four.Replies.Clear();
        four.Send(Command("a=p,U=1,i=42,c=2,r=2"));
        Assert.Contains(";ENOTSUP:", Assert.Single(four.Replies));
        four.Send(Command("a=q,i=31,e=T,U=1,s=1,v=1,t=d,f=24", "AAAA"));
        Assert.EndsWith(";ERR\x1b\\", four.Replies[^1], StringComparison.Ordinal);                                 // composition is advertised, placeholders are not
        Assert.Empty(four.Buffer.FindKittyImage(42, 0)!.VirtualPlacements);
        Assert.False(PersistentImagePolicy.AllowsPlaceholders(PersistentImagePolicy.CompositionImagesVersion));
        Assert.True(PersistentImagePolicy.AllowsPlaceholders(PersistentImagePolicy.PlaceholderImagesVersion));
        Assert.Equal(PersistentImagePolicy.PlaceholderImagesVersion, PersistentImagePolicy.EnabledVersion);

        var five = new Side();
        five.Send(Command("a=q,i=31,e=T,U=1,s=1,v=1,t=d,f=24", "AAAA"));
        Assert.Contains("U=1", five.Replies[0], StringComparison.Ordinal);
        Assert.EndsWith(";OK\x1b\\", five.Replies[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_daemon_without_a_decoder_and_a_client_with_one_hold_the_same_virtual_placements_and_tile_alike()
    {
        var daemon = new Side();
        var client = new Side(decoder: new FixedSizeImageDecoder(1, 1));
        daemon.Send(Traffic());
        client.Send(Traffic());

        Assert.All(daemon.Replies, reply => Assert.EndsWith(";OK\x1b\\", reply, StringComparison.Ordinal));
        Assert.Equal(daemon.Replies, client.Replies);
        KittyVirtualPlacement placement = Assert.Single(daemon.Buffer.FindKittyImage(42, 0)!.VirtualPlacements);
        Assert.Equal((7u, 2, 2), (placement.PlacementId, placement.Columns, placement.Rows));
        Assert.Equal(Json(daemon.Buffer), Json(client.Buffer));
        Assert.Contains("\"virtual\":[{\"placement\":7,\"cols\":2,\"rows\":2}]", Json(daemon.Buffer), StringComparison.Ordinal);
        Assert.Equal(2, daemon.Runs().Count);
        Assert.Equal(Describe(daemon.Runs()), Describe(client.Runs()));
    }

    [Fact]
    public void A_snapshot_carries_the_virtual_placements_and_a_restore_tiles_the_same_cells()
    {
        var source = new Side();
        source.Send(Traffic());
        TerminalStateSnapshot snapshot = Export(source);
        TerminalKittyImageState image = Assert.Single(snapshot.KittyImages!);
        Assert.Equal(7u, Assert.Single(image.VirtualPlacements!).Placement);

        Side restored = Restore(snapshot);
        Assert.Equal(Json(source.Buffer), Json(restored.Buffer));
        Assert.Equal(Describe(source.Runs()), Describe(restored.Runs()));
        Assert.NotEmpty(restored.Runs());

        // A restored session decides like the one it came from: the same new placements, the same refusals.
        source.Replies.Clear();
        string more = Command("a=p,U=1,i=42,p=8,c=3,r=1") + Command("a=p,U=1,i=42,p=7,c=1,r=1") + Command("a=d,d=I,i=42,p=8");
        source.Send(more);
        restored.Send(more);
        Assert.Equal(source.Replies, restored.Replies);
        Assert.Equal(Json(source.Buffer), Json(restored.Buffer));
        Assert.Equal(Describe(source.Runs()), Describe(restored.Runs()));
        Assert.All(restored.Replies, reply => Assert.EndsWith(";OK\x1b\\", reply, StringComparison.Ordinal));
    }

    [Fact]
    public void An_image_without_virtual_placements_exports_exactly_what_it_did_before_they_were_carried()
    {
        var side = new Side();
        side.Send(Command("a=T,f=32,s=1,v=1,i=1,p=1", Pixel));
        Assert.DoesNotContain("virtual", Json(side.Buffer), StringComparison.Ordinal);
        Assert.Null(Assert.Single(side.Buffer.ExportState(int.MaxValue).KittyImages!).VirtualPlacements);
    }

    [Fact]
    public void Deleting_every_virtual_placement_leaves_a_snapshot_with_none()
    {
        var side = new Side();
        side.Send(Traffic() + Command("a=d,d=i,i=42"));
        Assert.Null(Assert.Single(side.Buffer.ExportState(int.MaxValue).KittyImages!).VirtualPlacements);
        Assert.Empty(side.Runs());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("zero columns")]
    [InlineData("zero rows")]
    [InlineData("too many columns")]
    [InlineData("too many rows")]
    [InlineData("duplicate id")]
    [InlineData("too many placements")]
    [InlineData("a missing entry")]
    public void A_snapshot_no_exporter_could_have_made_is_refused_before_anything_is_restored(string fault)
    {
        var source = new Side();
        source.Send(Traffic());
        TerminalStateSnapshot snapshot = Export(source);
        List<TerminalKittyVirtualPlacementState> placements = snapshot.KittyImages![0].VirtualPlacements!;
        switch (fault)
        {
            case "none": placements.Clear(); break;
            case "zero columns": placements[0].Columns = 0; break;
            case "zero rows": placements[0].Rows = 0; break;
            case "too many columns": placements[0].Columns = 201; break;
            case "too many rows": placements[0].Rows = 201; break;
            case "duplicate id": placements.Add(new TerminalKittyVirtualPlacementState { Placement = 7, Columns = 1, Rows = 1 }); break;
            case "too many placements":
                placements.Clear();
                placements.AddRange(Enumerable.Range(1, TerminalBuffer.MaxKittyVirtualPlacements + 1).Select(id => new TerminalKittyVirtualPlacementState { Placement = (uint)id, Columns = 1, Rows = 1 }));
                break;
            default: placements.Add(null!); break;
        }
        var target = new Side();
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(target.Buffer, target.Parser, snapshot));
        Assert.Empty(target.Buffer.FindKittyImage(42, 0)?.VirtualPlacements ?? []);
    }

    [Fact]
    public void The_largest_grid_and_the_most_placements_an_image_may_hold_restore()
    {
        var source = new Side();
        source.Send(Command("a=t,f=32,s=1,v=1,i=42", Pixel));
        for (int id = 1; id <= TerminalBuffer.MaxKittyVirtualPlacements; id++) source.Send(Command($"a=p,U=1,i=42,p={id},c=200,r=200"));
        Assert.Equal(TerminalBuffer.MaxKittyVirtualPlacements, source.Buffer.FindKittyImage(42, 0)!.VirtualPlacements.Count);
        Side restored = Restore(Export(source));
        Assert.Equal(Json(source.Buffer), Json(restored.Buffer));
    }
}
