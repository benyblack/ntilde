using System.Text;
using Ntilde.Shell.Clipboard;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.Shell;

public class KittyClipboardSessionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FakeClipboard : IKittyClipboard
    {
        public bool Available { get; set; } = true;
        public bool AllowRead;
        public int Prompts, Reads, Lists, Writes;
        public Exception? Failure;
        public Func<CancellationToken, Task<bool>>? Confirmation;
        public Dictionary<string, byte[]> Values = new() { ["text/plain"] = Encoding.UTF8.GetBytes("private text"), ["image/png"] = new byte[8193] };
        public Task<string[]> GetTypesAsync() { Lists++; return Task.FromResult(Values.Keys.ToArray()); }
        public Task<byte[]?> ReadAsync(string mime) { Reads++; if (Failure != null) throw Failure; return Task.FromResult(Values.GetValueOrDefault(mime)); }
        public Task WriteAsync(IReadOnlyDictionary<string, byte[]> data) { if (Failure != null) throw Failure; Writes++; Values = data.ToDictionary(p => p.Key, p => p.Value); return Task.CompletedTask; }
        public Task<bool> ConfirmReadAsync(string name, CancellationToken cancellation) { Prompts++; return Confirmation?.Invoke(cancellation) ?? Task.FromResult(AllowRead); }
    }
    private sealed class Harness : IDisposable
    {
        public readonly FakeClipboard Clipboard = new();
        public readonly Clock Clock = new();
        public readonly List<string> Replies = [];
        public readonly Queue<Func<Task>> Work = new();
        public bool WriteAllowed = true, Alive = true;
        public readonly KittyClipboardSession Session;
        public Harness() => Session = new(Clipboard, Replies.Add, work => { Work.Enqueue(work); return Task.CompletedTask; }, () => WriteAllowed, () => Alive, Clock);
        public void Send(string packet) => Session.Accept(packet);
        public async Task Drain() { while (Work.TryDequeue(out var work)) await work(); }
        public void Dispose() => Session.Dispose();
    }
    private static string B64(string value) => KittyClipboardPacket.Encode(value);
    private static string Data(string mime, string value) => "type=wdata:mime=" + B64(mime) + ";" + value;
    private static string Read(string mime) => "type=read:id=request;" + B64(mime);

    [Fact]
    public async Task Writes_binary_and_text_atomically_with_split_base64_and_forward_aliases()
    {
        using var h = new Harness();
        h.Send("type=write:id=a");
        h.Send("type=walias:mime=" + B64("image/png") + ";" + B64("application/x-image"));
        h.Send(Data("text/plain", "SG"));
        h.Send(Data("text/plain", "VsbG8="));
        h.Send(Data("image/png", "AAEC/w=="));
        Assert.Equal(0, h.Clipboard.Writes);
        h.Send("type=wdata");
        Assert.Equal(0, h.Clipboard.Writes);
        await h.Drain();
        Assert.Equal("Hello", Encoding.UTF8.GetString(h.Clipboard.Values["text/plain"]));
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, h.Clipboard.Values["image/png"]);
        Assert.Same(h.Clipboard.Values["image/png"], h.Clipboard.Values["application/x-image"]);
        Assert.Equal(KittyClipboardPacket.Response("write", "DONE", "a"), Assert.Single(h.Replies));
    }

    [Theory]
    [InlineData("Zg=", "")]
    [InlineData("Zg==", "AAAA")]
    [InlineData("Zg==\n", "")]
    [InlineData("Zh==", "")]
    [InlineData("AA!A", "")]
    [InlineData("====", "")]
    public async Task Invalid_encoding_aborts_and_ignores_rest_until_a_new_write(string first, string second)
    {
        using var h = new Harness();
        h.Send("type=write:id=x");
        h.Send(Data("text/plain", first));
        h.Send(Data("text/plain", second));
        h.Send("type=wdata");
        h.Send(Data("text/plain", "SGk="));
        await h.Drain();
        Assert.Equal(0, h.Clipboard.Writes);
        Assert.Contains("status=EINVAL:id=x", Assert.Single(h.Replies));
        h.Send("type=write"); h.Send(Data("text/plain", "SGk=")); h.Send("type=wdata"); await h.Drain();
        Assert.Equal(1, h.Clipboard.Writes);
    }

    [Fact]
    public async Task Denied_reads_never_fetch_data_and_listing_does_not_prompt()
    {
        using var h = new Harness();
        h.Send(Read("text/plain")); await h.Drain();
        Assert.Contains("status=EPERM:id=request", Assert.Single(h.Replies));
        Assert.Equal(1, h.Clipboard.Prompts);
        Assert.Equal(0, h.Clipboard.Reads);
        Assert.Equal(0, h.Clipboard.Lists);
        h.Replies.Clear(); h.Send(Read(".")); await h.Drain();
        Assert.Equal(1, h.Clipboard.Prompts);
        Assert.Equal(0, h.Clipboard.Reads);
        Assert.Equal(3, h.Replies.Count);
        Assert.Contains("mime=Lg==;" + B64("text/plain image/png"), h.Replies[1]);
    }

    [Fact]
    public async Task Allowed_reads_skip_missing_types_and_chunk_each_type_at_4096_bytes()
    {
        using var h = new Harness();
        h.Clipboard.AllowRead = true;
        h.Send(Read("image/absent image/png text/plain")); await h.Drain();
        Assert.Equal(6, h.Replies.Count);
        Assert.Contains("status=OK:id=request", h.Replies[0]);
        Assert.Contains("status=DONE:id=request", h.Replies[^1]);
        byte[] output = h.Replies.Skip(1).Take(3).SelectMany(r => Convert.FromBase64String(r[(r.LastIndexOf(';') + 1)..^2])).ToArray();
        Assert.Equal(h.Clipboard.Values["image/png"], output);
        Assert.All(h.Replies.Skip(1).SkipLast(1), r => Assert.InRange(Convert.FromBase64String(r[(r.LastIndexOf(';') + 1)..^2]).Length, 0, 4096));
    }

    [Fact]
    public async Task Primary_is_explicitly_unavailable_and_never_falls_back_to_clipboard()
    {
        using var h = new Harness();
        h.Send("type=write:loc=primary:id=p");
        h.Send("type=read:loc=primary;" + B64(".")); await h.Drain();
        Assert.All(h.Replies, r => Assert.Contains("status=ENOSYS", r));
        Assert.Equal(0, h.Clipboard.Lists + h.Clipboard.Reads + h.Clipboard.Writes + h.Clipboard.Prompts);
    }

    [Fact]
    public async Task Write_setting_is_checked_at_start_and_again_at_commit()
    {
        using var h = new Harness { WriteAllowed = false };
        h.Send("type=write"); h.Send(Data("text/plain", "SGk=")); h.Send("type=wdata"); await h.Drain();
        Assert.Contains("status=EPERM", Assert.Single(h.Replies));
        h.WriteAllowed = true; h.Send("type=write"); h.Send(Data("text/plain", "SGk=")); h.Send("type=wdata"); h.WriteAllowed = false; await h.Drain();
        Assert.Equal(0, h.Clipboard.Writes);
        Assert.Contains("status=EPERM", h.Replies[^1]);
    }

    [Fact]
    public async Task Reset_or_closed_pane_prevents_queued_clipboard_access()
    {
        using var h = new Harness();
        h.Send("type=write"); h.Send(Data("text/plain", "SGk=")); h.Send("type=wdata");
        h.Session.Reset();
        h.Send("type=write"); // reset must not allow unbounded queued 64 MiB writes
        Assert.Contains("status=EBUSY", Assert.Single(h.Replies));
        await h.Drain();
        h.Send(Read("text/plain")); h.Alive = false; await h.Drain();
        Assert.Equal(0, h.Clipboard.Writes + h.Clipboard.Reads + h.Clipboard.Prompts);
    }

    [Fact]
    public async Task Paste_token_is_single_use_tty_scoped_named_and_expires()
    {
        using var h = new Harness();
        await h.Session.NotifyPasteAsync();
        string token = h.Replies[0].Split(":pw=")[1][..^2];
        Assert.Equal(0, h.Clipboard.Reads + h.Clipboard.Prompts);
        using var other = new Harness();
        other.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain")); await other.Drain();
        Assert.Equal(1, other.Clipboard.Prompts);
        h.Send("type=read:pw=" + token + ";" + B64("text/plain")); await h.Drain();
        Assert.Equal(1, h.Clipboard.Prompts);
        h.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain")); await h.Drain();
        Assert.Equal(1, h.Clipboard.Reads);
        Assert.Equal(1, h.Clipboard.Prompts);
        h.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain")); await h.Drain();
        Assert.Equal(2, h.Clipboard.Prompts);
        await h.Session.NotifyPasteAsync(); token = h.Replies[^3].Split(":pw=")[1][..^2];
        h.Clock.Now = h.Clock.Now.AddSeconds(16);
        h.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain")); await h.Drain();
        Assert.Equal(3, h.Clipboard.Prompts);
    }

    [Fact]
    public async Task Error_statuses_and_transaction_timeout_are_explicit()
    {
        using var h = new Harness();
        h.Clipboard.Failure = new IOException();
        h.Send("type=write"); h.Send(Data("text/plain", "AA==")); h.Send("type=wdata"); await h.Drain();
        Assert.Contains("status=EIO", Assert.Single(h.Replies));
        h.Send("type=write"); h.Clock.Now = h.Clock.Now.AddMinutes(2); h.Send("type=wdata");
        Assert.Contains("status=EBUSY", h.Replies[^1]);
    }

    [Theory]
    [InlineData("type=walias:mime=dGV4dC9wbGFpbg==;dGV4dC9wbGFpbg==")]
    [InlineData("type=walias:mime=aW1hZ2UvcG5n;dGV4dC9odG1s")]
    [InlineData("type=wdata:mime=YmFk;AA==")]
    [InlineData("type=wdata;AA==")]
    public async Task Invalid_alias_or_MIME_discards_the_whole_transaction(string packet)
    {
        using var h = new Harness();
        h.Send("type=write"); h.Send(Data("text/plain", "SGk=")); h.Send(packet); h.Send("type=wdata");
        await h.Drain();
        Assert.Equal(0, h.Clipboard.Writes);
        Assert.Contains("status=EINVAL", Assert.Single(h.Replies));
    }

    [Fact]
    public async Task A_MIME_type_cannot_resume_after_another_type_starts()
    {
        using var h = new Harness();
        h.Send("type=write"); h.Send(Data("text/plain", "SGk=")); h.Send(Data("text/html", "SGk="));
        h.Send(Data("text/plain", "SGk=")); h.Send("type=wdata"); await h.Drain();
        Assert.Equal(0, h.Clipboard.Writes);
        Assert.Contains("status=EINVAL", Assert.Single(h.Replies));
    }

    [Fact]
    public async Task Only_one_pane_accesses_the_clipboard_and_reset_cancels_its_prompt()
    {
        using var h = new Harness();
        using var other = new Harness();
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Clipboard.Confirmation = cancellation =>
        {
            cancellation.Register(() => consent.TrySetResult(false));
            return consent.Task;
        };
        h.Send(Read("text/plain"));
        var waiting = h.Drain();
        Assert.False(waiting.IsCompleted);
        other.Send(Read(".")); await other.Drain();
        Assert.Contains("status=EBUSY", Assert.Single(other.Replies));
        Assert.Equal(0, other.Clipboard.Lists);
        h.Session.Reset(); await waiting;
        Assert.Equal(0, h.Clipboard.Reads);
        other.Replies.Clear(); other.Send(Read(".")); await other.Drain();
        Assert.Equal(3, other.Replies.Count);
        h.Session.Dispose(); h.Session.Dispose(); h.Session.Reset();
    }

    [Fact]
    public async Task Exact_64_MiB_is_accepted_and_one_extra_byte_is_rejected()
    {
        using var h = new Harness();
        string chunk = Data("application/octet-stream", new string('A', 4096)); // 3072 decoded bytes, no padding
        void Fill()
        {
            h.Send("type=write");
            for (int count = 0; count < KittyClipboardPacket.MaxDataBytes / 3072; count++) h.Send(chunk);
            h.Send(Data("application/octet-stream", Convert.ToBase64String(new byte[KittyClipboardPacket.MaxDataBytes % 3072])));
        }
        Fill(); h.Send("type=walias:mime=" + B64("application/octet-stream") + ";" + B64("application/x-one")); h.Send("type=wdata"); await h.Drain();
        Assert.Equal(KittyClipboardPacket.MaxDataBytes, h.Clipboard.Values["application/octet-stream"].Length);
        h.Clipboard.Values.Clear();
        // A different type avoids a padding violation and isolates the aggregate decoded-byte cap.
        Fill(); h.Send(Data("text/plain", "AA==")); h.Send("type=wdata"); await h.Drain();
        Assert.Equal(1, h.Clipboard.Writes);
        Assert.Contains("status=EFBIG", h.Replies[^1]);
        Fill(); h.Send("type=walias:mime=" + B64("application/octet-stream") + ";" + B64("application/x-one application/x-two"));
        h.Send("type=wdata"); await h.Drain();
        Assert.Equal(1, h.Clipboard.Writes);
        Assert.Contains("status=EFBIG", h.Replies[^1]);
    }

    [Fact]
    public void Mac_MIME_conversion_round_trips_custom_and_standard_types_without_clipboard_access()
    {
        foreach (string mime in new[] { "image/png", "text/html", "text/plain", "application/x-ntilde-test" })
            Assert.Equal(mime, ClipboardMimeNames.ToMime(ClipboardMimeNames.ToPlatform(mime)));
    }
}
