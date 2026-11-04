using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class KittyImageLifecycleTests
{
    private const string Pixel = "/wAA/w==";
    private static string Command(string controls, string payload = "") => $"\x1b_G{controls};{payload}\x1b\\";
    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies) Create()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false)
        {
            ImageDecoder = new FixedSizeImageDecoder(1, 1),
            CellWidth = 8,
            CellHeight = 16
        };
        var replies = new List<string>(); parser.OnResponse = replies.Add;
        return (buffer, parser, replies);
    }

    [Fact]
    public void Transmit_only_allocates_a_numbered_image_without_placing_or_moving_the_cursor()
    {
        var (buffer, parser, replies) = Create();
        parser.Process("\x1b[3;5H" + Command("a=t,f=32,s=1,v=1,I=13", Pixel));
        Assert.Empty(buffer.Images); Assert.Equal(2, buffer.CursorRow); Assert.Equal(4, buffer.CursorCol);
        Assert.Equal("\x1b_Gi=1,I=13;OK\x1b\\", Assert.Single(replies));
        parser.Process(Command("a=p,I=13,C=1"));
        Assert.Equal(1u, Assert.Single(buffer.Images).KittyImageId);
        Assert.Equal("\x1b_Gi=1,I=13;OK\x1b\\", replies[1]);
    }

    [Fact]
    public void Reusing_a_number_allocates_distinct_ids_and_later_number_commands_select_the_newest()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", Pixel));
        object first = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", Pixel));
        Assert.Equal(new uint?[] { 1, 2 }, buffer.Images.Select(image => image.KittyImageId));
        Assert.Equal(new[] { "\x1b_Gi=1,I=13;OK\x1b\\", "\x1b_Gi=2,I=13;OK\x1b\\" }, replies);
        parser.Process(Command("a=d,d=N,I=13"));
        Assert.Same(first, Assert.Single(buffer.Images).ImageHandle);
        parser.Process(Command("a=p,I=13,C=1"));
        Assert.Same(first, buffer.Images[^1].ImageHandle);
        Assert.Equal("\x1b_Gi=1,I=13;OK\x1b\\", replies[^1]);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("4000000000")]
    [InlineData("4294967295")]
    public void Explicit_unsigned_ids_and_placement_correlation_are_exact(string id)
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command($"a=t,f=32,s=1,v=1,i={id}", Pixel));
        parser.Process(Command($"a=p,i={id},p=4294967295,C=1"));
        Assert.Equal(uint.Parse(id, CultureInfo.InvariantCulture), Assert.Single(buffer.Images).KittyImageId);
        Assert.Equal($"\x1b_Gi={id};OK\x1b\\", replies[0]);
        Assert.Equal($"\x1b_Gi={id},p=4294967295;OK\x1b\\", replies[1]);
    }

    [Fact]
    public void Retransmitting_an_id_retires_old_data_and_placements_before_transmit_only_ack()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=7,C=1", Pixel));
        object old = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=t,f=32,s=1,v=1,i=7", Pixel));
        Assert.Empty(buffer.Images);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(old, Assert.Single(retired));
        parser.Process(Command("a=p,i=7,C=1"));
        Assert.NotSame(old, Assert.Single(buffer.Images).ImageHandle);
        Assert.Equal(3, replies.Count);
    }

    [Fact]
    public void Soft_delete_keeps_data_live_and_hard_delete_retires_it_once_after_snapshot_users_exit()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=7,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=d,d=i,i=7")); Assert.Empty(buffer.Images);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        parser.Process(Command("a=p,i=7,C=1")); Assert.Same(handle, Assert.Single(buffer.Images).ImageHandle);
        int snapshot = buffer.BeginSnapshotSession();
        parser.Process(Command("a=d,d=I,i=7")); Assert.Empty(buffer.Images);
        buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        buffer.EndSnapshotSession(snapshot);
        buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Same(handle, Assert.Single(retired));
        parser.Process(Command("a=p,i=7")); Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("reset")]
    [InlineData("import")]
    public void Hidden_transmitted_assets_are_retired_once_by_full_cleanup(string operation)
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=7,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=d,d=i,i=7")); Assert.Empty(buffer.Images);
        if (operation == "clear") buffer.ClearImages();
        else if (operation == "reset") buffer.Reset();
        else buffer.ImportState(new TerminalBuffer(80, 24).ExportState(0));
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
        parser.Process(Command("a=p,i=7")); Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("i=7,I=13")]
    [InlineData("i=4294967296")]
    [InlineData("I=-1")]
    [InlineData("I=13\rINJECT")]
    [InlineData("i=+7")]
    public void Malformed_identifiers_never_allocate_or_reflect_control_bytes(string identity)
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command($"a=T,f=32,s=1,v=1,{identity}", Pixel));
        Assert.Empty(buffer.Images); string reply = Assert.Single(replies);
        Assert.Contains(";EINVAL:", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("INJECT", reply, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', reply);
    }

    [Theory]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]
    [InlineData(2, false, false)]
    public void Quiet_policy_distinguishes_success_and_error_responses(int quiet, bool successReply, bool errorReply)
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command($"a=T,f=32,s=1,v=1,I=13,q={quiet}", Pixel));
        Assert.Single(buffer.Images); Assert.Equal(successReply ? 1 : 0, replies.Count);
        replies.Clear(); parser.Process(Command($"a=p,i=99,q={quiet}"));
        Assert.Equal(errorReply ? 1 : 0, replies.Count);
        if (errorReply) Assert.Contains(";ENOENT:", Assert.Single(replies), StringComparison.Ordinal);
    }

    [Fact]
    public void Anonymous_images_have_no_correlated_reply_or_placement_identity_and_can_be_freed()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=0,p=7,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        Assert.Null(buffer.Images[0].KittyPlacementId); Assert.Empty(replies);
        parser.Process(Command("a=d,d=A")); Assert.Empty(buffer.Images); Assert.Empty(replies);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
    }

    [Fact]
    public void Placement_identity_replaces_only_that_placement_and_soft_erase_keeps_the_asset()
    {
        var (buffer, parser, _) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=7,p=1,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process("\x1b[3;5H" + Command("a=p,i=7,p=2,C=1"));
        parser.Process("\x1b[4;6H" + Command("a=p,i=7,p=1,C=1"));
        Assert.Equal(2, buffer.Images.Count);
        Assert.Equal(new uint?[] { 2, 1 }, buffer.Images.Select(image => image.KittyPlacementId));
        Assert.Equal(5, buffer.Images[1].CellX); Assert.Equal(3, buffer.Images[1].CellY);
        parser.Process(Command("a=d,d=i,i=7,p=1"));
        Assert.Equal(2u, Assert.Single(buffer.Images).KittyPlacementId);
        parser.Process("\x1b[2J"); Assert.Empty(buffer.Images);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        parser.Process(Command("a=p,i=7,C=1")); Assert.Same(handle, Assert.Single(buffer.Images).ImageHandle);
    }

    [Fact]
    public void Screen_namespaces_preserve_hidden_assets_while_reusing_the_same_id()
    {
        var (buffer, parser, _) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=7,C=1", Pixel));
        object main = Assert.Single(buffer.Images).ImageHandle;
        parser.Process("\x1b[?1049h" + Command("a=T,f=32,s=1,v=1,i=7,C=1", Pixel));
        Assert.Equal(2, buffer.Images.Count);
        parser.Process(Command("a=d,d=I,i=7"));
        Assert.Same(main, Assert.Single(buffer.Images).ImageHandle);
        parser.Process("\x1b[?1049l" + Command("a=p,i=7,C=1"));
        Assert.All(buffer.Images, image => Assert.Same(main, image.ImageHandle));
    }

    [Fact]
    public void Identity_quota_evicts_old_assets_and_never_reuses_a_live_allocated_id()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=t,f=32,s=1,v=1,i=1", Pixel));
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", Pixel));
        Assert.Equal(2u, Assert.Single(buffer.Images).KittyImageId);
        for (int id = 3; id <= 130; id++)
            parser.Process(Command($"a=T,f=32,s=1,v=1,i={id},C=1", Pixel));
        parser.Process(Command("a=p,i=1,C=1")); Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
        parser.Process(Command("a=p,I=13,C=1")); Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
        parser.Process(Command("a=p,i=130,C=1")); Assert.Equal("\x1b_Gi=130;OK\x1b\\", replies[^1]);
        Assert.Contains(buffer.Images, image => image.KittyImageId == 130u);
    }

    [Fact]
    public void Pixel_quota_evicts_before_retained_decoded_data_can_grow_without_bound()
    {
        var (buffer, parser, replies) = Create(); parser.ImageDecoder = new FixedSizeImageDecoder(2000, 2000);
        for (int id = 1; id <= 5; id++)
            parser.Process(Command($"a=T,f=100,i={id},C=1", "AQIDBA=="));
        Assert.Equal(4, buffer.Images.Count);
        parser.Process(Command("a=p,i=1,C=1")); Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Equal(5, retired.Count);
        Assert.Equal(5, retired.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public void Placement_quota_does_not_release_an_asset_still_owned_by_storage()
    {
        var (buffer, parser, _) = Create();
        parser.Process(Command("a=t,f=32,s=1,v=1,i=7", Pixel));
        for (int i = 0; i < 600; i++) parser.Process(Command("a=p,i=7,C=1"));
        Assert.Equal(512, buffer.Images.Count);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        parser.Process(Command("a=d,d=I,i=7")); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(buffer.Images); Assert.Single(retired);
    }

    [Fact]
    public void A_delete_aborts_partial_upload_and_the_next_complete_command_starts_clean()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,m=1", Pixel[..4]));
        parser.Process(Command("a=d,d=A")); Assert.Empty(replies); Assert.Empty(buffer.Images);
        parser.Process(Command("a=T,f=32,s=1,v=1,I=14,C=1", Pixel));
        Assert.Equal("\x1b_Gi=1,I=14;OK\x1b\\", Assert.Single(replies)); Assert.Single(buffer.Images);
    }

    [Theory]
    [InlineData("f")]
    [InlineData("a")]
    [InlineData("c")]
    public void Unsupported_animation_actions_have_negative_correlated_replies(string action)
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command($"a={action},i=7", Pixel));
        Assert.Empty(buffer.Images); Assert.StartsWith("\x1b_Gi=7;ENOTSUP:", Assert.Single(replies), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("f=8")]
    [InlineData("f=101")]
    [InlineData("f=not-a-format")]
    [InlineData("o=unknown")]
    public void Unsupported_format_or_compression_is_a_correlated_failure(string controls)
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command($"a=T,i=7,s=1,v=1,{controls}", Pixel));
        Assert.Empty(buffer.Images); Assert.StartsWith("\x1b_Gi=7;ENOTSUP:", Assert.Single(replies), StringComparison.Ordinal);
    }

    [Fact]
    public void Cross_chunk_control_metadata_is_bounded_and_the_command_cannot_decode_after_overflow()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,i=7,f=32,s=1,v=1,m=1", Pixel[..4]));
        parser.Process(Command("long-unbounded-key=" + new string('x', 4096) + ",m=1", Pixel[4..]));
        AnsiParserState pending = parser.ExportState();
        Assert.True(pending.KittyPayloadOverflow); Assert.Empty(pending.KittyPayload);
        Assert.All(pending.KittyPendingParams, pair => { Assert.Single(pair.Key); Assert.True(pair.Value.Length <= 64); });
        parser.Process(Command("m=0"));
        Assert.Empty(buffer.Images); Assert.Contains(";E2BIG:", Assert.Single(replies), StringComparison.Ordinal);
        parser.Process(Command("a=T,f=32,s=1,v=1,i=8,C=1", Pixel)); Assert.Single(buffer.Images);
    }

    [Fact]
    public void Shared_opaque_handles_are_owned_until_the_last_distinct_asset_is_freed()
    {
        var (buffer, parser, _) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", Pixel));
        parser.Process(Command("a=d,d=I,i=1"));
        Assert.Same(handle, Assert.Single(buffer.Images).ImageHandle);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        parser.Process(Command("a=d,d=I,i=2")); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
    }

    [Fact]
    public void Shared_anonymous_handles_do_not_merge_distinct_storage_ownership()
    {
        var (buffer, parser, _) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process(Command("a=T,f=32,s=1,v=1,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=T,f=32,s=1,v=1,C=1", Pixel)); Assert.Equal(2, buffer.Images.Count);
        parser.Process(Command("a=d,d=A")); Assert.Empty(buffer.Images);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deleting_a_nonexistent_scoped_placement_preserves_retained_data(bool useNumber)
    {
        var (buffer, parser, replies) = Create();
        string identity = useNumber ? "I=13" : "i=7";
        string delete = useNumber ? "d=N,I=13" : "d=I,i=7";
        parser.Process(Command($"a=t,f=32,s=1,v=1,{identity}", Pixel)); Assert.Empty(buffer.Images);
        parser.Process(Command($"a=d,{delete},p=99")); Assert.Empty(buffer.Images);
        parser.Process(Command($"a=p,{identity},p=1,C=1"));
        Assert.Single(buffer.Images); Assert.EndsWith(";OK\x1b\\", replies[^1], StringComparison.Ordinal);
        parser.Process(Command($"a=d,{delete},p=1")); Assert.Empty(buffer.Images);
        parser.Process(Command($"a=p,{identity},C=1")); Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Transmit_only_replacement_notifies_the_live_view_that_old_placements_disappeared()
    {
        var (buffer, parser, _) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=7,C=1", Pixel));
        int notifications = 0; buffer.OnInvalidate += () => notifications++;
        parser.Process(Command("a=t,f=32,s=1,v=1,i=7", Pixel));
        Assert.Empty(buffer.Images); Assert.Equal(1, notifications);
    }

    [Fact]
    public void Transmit_only_quota_eviction_notifies_the_live_view_that_old_placements_disappeared()
    {
        var (buffer, parser, _) = Create(); parser.ImageDecoder = new FixedSizeImageDecoder(2000, 2000);
        for (int i = 1; i <= 4; i++) parser.Process(Command($"a=T,f=100,i={i},C=1", "AQIDBA=="));
        int notifications = 0; buffer.OnInvalidate += () => notifications++;
        parser.Process(Command("a=t,f=100,i=5", "AQIDBA=="));
        Assert.Equal(3, buffer.Images.Count); Assert.Equal(1, notifications);
    }

    [Fact]
    public void Readmission_cancels_pending_retirement_until_the_shared_handle_is_finally_deleted()
    {
        var (buffer, parser, _) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=1,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=d,d=I,i=1"));
        parser.Process(Command("a=T,f=32,s=1,v=1,i=2,C=1", Pixel));
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired); Assert.Same(handle, Assert.Single(buffer.Images).ImageHandle);
        parser.Process(Command("a=d,d=I,i=2")); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
    }

    [Fact]
    public void An_obsolete_retirement_cannot_bypass_a_new_snapshot_after_readmission()
    {
        var (buffer, parser, _) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=1,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=d,d=I,i=1"));
        // Force the first retirement tick strictly before the later snapshot. Wait for
        // that clock condition rather than relying on a fixed delay or test speed.
        long firstRetirementObservedAt = Environment.TickCount64;
        Assert.True(System.Threading.SpinWait.SpinUntil(() => Environment.TickCount64 > firstRetirementObservedAt,
            TimeSpan.FromSeconds(30)));
        parser.Process(Command("a=T,f=32,s=1,v=1,i=2,C=1", Pixel));
        int snapshot = buffer.BeginSnapshotSession();
        var retired = new List<object>();
        try
        {
            parser.Process(Command("a=d,d=I,i=2"));
            buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        }
        finally { buffer.EndSnapshotSession(snapshot); }
        buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Same(handle, Assert.Single(retired));
    }

    [Fact]
    public void A_handle_handed_to_the_disposer_cannot_be_readmitted_or_retired_twice()
    {
        var (buffer, parser, replies) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process(Command("a=T,f=32,s=1,v=1,i=1,C=1", Pixel));
        parser.Process(Command("a=d,d=I,i=1"));
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
        parser.Process(Command("a=T,f=32,s=1,v=1,i=2,C=1", Pixel));
        Assert.Empty(buffer.Images); Assert.Contains(";EINVAL:", replies[^1], StringComparison.Ordinal);
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
    }

    [Fact]
    public async System.Threading.Tasks.Task Disposal_handoff_does_not_wait_for_a_blocked_parser_and_late_readmission_is_refused()
    {
        var (buffer, parser, replies) = Create(); var decoder = new SharedHandleDecoder(); parser.ImageDecoder = decoder;
        parser.Process(Command("a=T,f=32,s=1,v=1,i=1,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=d,d=I,i=1"));
        using var entered = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        decoder.BeforeDecode = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        };
        System.Threading.Tasks.Task writing = System.Threading.Tasks.Task.Run(
            () => parser.Process(Command("a=T,f=32,s=1,v=1,i=2,C=1", Pixel)), TestContext.Current.CancellationToken);
        var retired = new List<object>();
        System.Threading.Tasks.Task<bool>? draining = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            draining = System.Threading.Tasks.Task.Run(() => buffer.DrainRetiredImageHandles(retired, long.MaxValue), TestContext.Current.CancellationToken);
            Assert.True(await draining.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Same(handle, Assert.Single(retired));
        }
        finally
        {
            release.Set();
            await writing.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (draining is not null) await draining.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        Assert.Empty(buffer.Images); Assert.Contains(";EINVAL:", replies[^1], StringComparison.Ordinal);
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
    }

    [Theory]
    [InlineData("iterm")]
    [InlineData("sixel")]
    public void Prior_non_Kitty_retirement_is_cancelled_when_its_handle_is_admitted_to_Kitty(string protocol)
    {
        var (buffer, parser, _) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process(protocol == "iterm" ? "\x1b]1337;File=inline=1:/wAA/w==\x07" : "\x1bPq~\x1b\\");
        object handle = Assert.Single(buffer.Images).ImageHandle;
        buffer.ClearImages(); Assert.Empty(buffer.Images);
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", Pixel));
        Assert.Same(handle, Assert.Single(buffer.Images).ImageHandle);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired);
        parser.Process(Command("a=d,d=N,I=13")); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
    }

    [Fact]
    public void A_handle_handed_off_by_non_Kitty_retirement_cannot_be_admitted_to_Kitty()
    {
        var (buffer, parser, replies) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process("\x1b]1337;File=inline=1:/wAA/w==\x07");
        buffer.ClearImages(); var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", Pixel));
        Assert.Empty(buffer.Images); Assert.Contains(";EINVAL:", replies[^1], StringComparison.Ordinal);
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
    }

    [Fact]
    public void Releasing_Kitty_storage_does_not_dispose_a_handle_still_used_by_a_non_Kitty_placement()
    {
        var (buffer, parser, _) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process("\x1b]1337;File=inline=1:/wAA/w==\x07");
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1", Pixel));
        parser.Process(Command("a=d,d=N,I=13")); Assert.Same(handle, Assert.Single(buffer.Images).ImageHandle);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Same(handle, Assert.Single(retired));
    }

    [Fact]
    public void Non_Kitty_admission_cancels_pending_retirement_without_changing_its_normal_grace()
    {
        var buffer = new TerminalBuffer(80, 24); object handle = new();
        buffer.AddImage(new TerminalImage(handle, 0, 0, 1, 1)); buffer.ClearImages();
        buffer.AddImage(new TerminalImage(handle, 1, 1, 1, 1));
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        buffer.ClearImages();
        buffer.DrainRetiredImageHandles(retired, Environment.TickCount64 - 60_000); Assert.Empty(retired);
        buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Same(handle, Assert.Single(retired));
    }

    [Fact]
    public void Legacy_frame_replacement_with_the_same_handle_does_not_retire_its_live_replacement()
    {
        var buffer = new TerminalBuffer(80, 24); object handle = new();
        buffer.AddKittyFrame(new TerminalImage(handle, 0, 0, 1, 1), 1);
        buffer.AddKittyFrame(new TerminalImage(handle, 1, 1, 1, 1), 1);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Empty(retired);
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Same(handle, Assert.Single(retired));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removed_Sixel_source_retires_even_when_its_decoded_handle_remains_shared(bool retainedByKitty)
    {
        var buffer = new TerminalBuffer(80, 24) { SixelSources = new SixelSourceBudget(100, 2) };
        var liveSource = buffer.SixelSources.Retain("q~");
        var removedSource = buffer.SixelSources.Retain("q~~");
        object shared = new();
        if (retainedByKitty) buffer.StoreKittyImage(shared, 1, 1, 7, 0);
        else buffer.AddImage(new TerminalImage(shared, 0, 0, 1, 1));
        buffer.AddKittyFrame(new TerminalImage(shared, 1, 1, 1, 1) { SixelSource = removedSource }, 1);
        buffer.AddKittyFrame(new TerminalImage(new object(), 1, 1, 1, 1), 1);
        // Retired source text must be evicted before an older live source, even
        // though another placement or retained asset still owns the decoded handle.
        buffer.SixelSources.Retain("q~~~");
        Assert.Equal("q~", liveSource.Data);
        Assert.Null(removedSource.Data);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.DoesNotContain(shared, retired);
    }

    [Fact]
    public async System.Threading.Tasks.Task Unstored_handle_retirement_checks_ownership_under_the_buffer_write_lock()
    {
        var buffer = new TerminalBuffer(80, 24); object handle = new();
        System.Threading.Tasks.Task? retiring = null;
        bool waitedForOwner;
        buffer.Lock.EnterWriteLock();
        try
        {
            retiring = System.Threading.Tasks.Task.Run(() => buffer.RetireUnstoredKittyHandle(handle), TestContext.Current.CancellationToken);
            Assert.True(System.Threading.SpinWait.SpinUntil(() => retiring.IsCompleted || buffer.Lock.WaitingWriteCount > 0,
                TimeSpan.FromSeconds(30)));
            waitedForOwner = buffer.Lock.WaitingWriteCount > 0;
            buffer.AddImage(new TerminalImage(handle, 0, 0, 1, 1));
        }
        finally { buffer.Lock.ExitWriteLock(); }
        await retiring!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(waitedForOwner);
        var retired = new List<object>(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Empty(retired); Assert.Same(handle, Assert.Single(buffer.Images).ImageHandle);
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public void Rejected_Sixel_admission_releases_source_without_cursor_or_newline_side_effects(bool retainSources, bool fullSourceCount, bool fullCharBudget)
    {
        var buffer = new TerminalBuffer(80, 24) { SixelSources = new SixelSourceBudget(fullCharBudget ? 4 : 100, fullSourceCount ? 1 : 2) };
        var decoder = new SharedHandleDecoder();
        var parser = new AnsiParser(buffer) { ImageDecoder = decoder, CellWidth = 8, CellHeight = 16 };
        parser.Process("\x1b]1337;File=inline=1:/wAA/w==\x07");
        object handedOff = Assert.Single(buffer.Images).ImageHandle;
        buffer.ClearImages(); var retired = new List<object>();
        buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Same(handedOff, Assert.Single(retired));
        var liveSource = buffer.SixelSources.Retain("q!2~");
        buffer.PlaceSixelImage(new object(), 1, 1, liveSource);
        TerminalImage liveImage = Assert.Single(buffer.Images);
        // A new parser has no pending post-image newline policy from the earlier
        // valid iTerm2 frame; rejected frames must not arm one themselves.
        parser = new AnsiParser(buffer) { ImageDecoder = decoder, RetainSixelSources = retainSources };
        parser.Process("\x1b[3;5H");
        for (int i = 0; i < 8; i++)
        {
            parser.Process("\x1bPq~\x1b\\");
            Assert.Equal((2, 4), (buffer.CursorRow, buffer.CursorCol));
            Assert.Equal(1, buffer.SixelSources.Count);
            Assert.Equal("q!2~", liveSource.Data);
            Assert.Same(liveImage, Assert.Single(buffer.Images));
        }
        parser.Process("\nX");
        Assert.Equal(3, buffer.CursorRow);
        buffer.DrainRetiredImageHandles(retired, long.MaxValue); Assert.Single(retired);
    }

    [Fact]
    public void RIS_clears_both_banks_and_shared_Kitty_generic_handles_only_after_snapshot_exit()
    {
        var (buffer, parser, replies) = Create(); parser.ImageDecoder = new SharedHandleDecoder();
        parser.Process("main" + Command("a=T,f=32,s=1,v=1,i=7,C=1", Pixel));
        object handle = Assert.Single(buffer.Images).ImageHandle;
        parser.Process("\x1b[?47halt");
        buffer.AddImage(new TerminalImage(handle, 0, 0, 1, 1));
        Assert.Equal(2, buffer.Images.Count);
        Assert.All(buffer.Images, image => Assert.Same(handle, image.ImageHandle));
        Assert.Single(buffer.Images, image => image.IsAltScreenImage);
        Assert.Single(buffer.Images, image => !image.IsAltScreenImage);
        int snapshot = buffer.BeginSnapshotSession();
        var retired = new List<object>();
        try
        {
            parser.Process("\x1b" + "c");
            Assert.False(buffer.IsAltScreenActive);
            Assert.Empty(buffer.Images);
            Assert.All(BufferSnapshot.Capture(buffer).Lines, line => Assert.All(line, value => Assert.Equal(' ', value)));
            parser.Process(Command("a=p,i=7"));
            Assert.Contains(";ENOENT:", replies[^1], StringComparison.Ordinal);
            parser.Process("\x1b[?47h"); Assert.Empty(buffer.Images);
            Assert.All(BufferSnapshot.Capture(buffer).Lines, line => Assert.All(line, value => Assert.Equal(' ', value)));
            parser.Process("\x1b[?47l");
            buffer.DrainRetiredImageHandles(retired, long.MaxValue);
            Assert.Empty(retired);
        }
        finally { buffer.EndSnapshotSession(snapshot); }
        buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Same(handle, Assert.Single(retired));
        buffer.ClearImages(); buffer.DrainRetiredImageHandles(retired, long.MaxValue);
        Assert.Single(retired);
    }

    private sealed class SharedHandleDecoder : IImageDecoder
    {
        private readonly object _handle = new();
        public Action? BeforeDecode { get; set; }
        private object Decode(out int width, out int height) { BeforeDecode?.Invoke(); width = height = 1; return _handle; }
        public object? DecodeImageBytes(byte[] data, out int width, out int height) => Decode(out width, out height);
        public object? DecodeSixel(string data, out int width, out int height) => Decode(out width, out height);
        public object? DecodeRawImage(byte[] data, int bpp, int w, int h, out int width, out int height) => Decode(out width, out height);
    }

    [Fact]
    public void A_numbered_chunked_transmission_acknowledges_only_the_complete_payload()
    {
        var (buffer, parser, replies) = Create();
        parser.Process(Command("a=T,f=32,s=1,v=1,I=13,C=1,m=1", Pixel[..4]));
        Assert.Empty(replies); Assert.Empty(buffer.Images);
        parser.Process(Command("m=0", Pixel[4..]));
        Assert.Equal("\x1b_Gi=1,I=13;OK\x1b\\", Assert.Single(replies)); Assert.Single(buffer.Images);
    }
}
