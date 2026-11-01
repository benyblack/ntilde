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
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now.ToUnixTimeMilliseconds();
    }
    private sealed class FakeClipboard : IKittyClipboard
    {
        public bool Available { get; set; } = true;
        public bool AllowRead;
        public int Prompts, Reads, Lists, Writes, ReadThread;
        public Exception? Failure;
        public Action? OnRead;
        public bool IgnoreReadBudget;
        public int SnapshotsDisposed;
        public Func<CancellationToken, Task<bool>>? Confirmation;
        public Dictionary<string, byte[]> Values = new() { ["text/plain"] = Encoding.UTF8.GetBytes("private text"), ["image/png"] = new byte[8193] };
        public Task<IKittyClipboardSnapshot> OpenReadAsync()
        {
            Lists++;
            return Task.FromResult<IKittyClipboardSnapshot>(new Snapshot(this, Values.ToDictionary(p => p.Key, p => p.Value)));
        }
        private sealed class Snapshot(FakeClipboard owner, Dictionary<string, byte[]> values) : IKittyClipboardSnapshot
        {
            public IReadOnlyList<string> Types => values.Keys.ToArray();
            public Task<byte[]?> ReadAsync(string mime, int maxBytes = KittyClipboardPacket.MaxDataBytes)
            {
                owner.ReadThread = Environment.CurrentManagedThreadId;
                owner.Reads++;
                owner.OnRead?.Invoke();
                if (owner.Failure != null) throw owner.Failure;
                byte[]? bytes = values.GetValueOrDefault(mime);
                if (!owner.IgnoreReadBudget && bytes?.Length > maxBytes) throw new IOException("Read budget exceeded.");
                return Task.FromResult(bytes);
            }
            public void Dispose() => owner.SnapshotsDisposed++;
        }
        public Task WriteAsync(IReadOnlyDictionary<string, byte[]> data) { if (Failure != null) throw Failure; Writes++; Values = data.ToDictionary(p => p.Key, p => p.Value); return Task.CompletedTask; }
        public Task<bool> ConfirmReadAsync(string name, CancellationToken cancellation) { Prompts++; return Confirmation?.Invoke(cancellation) ?? Task.FromResult(AllowRead); }
    }
    private sealed class Harness : IDisposable
    {
        public readonly FakeClipboard Clipboard = new();
        public readonly Clock Clock = new();
        public readonly List<string> Replies = [];
        public readonly List<int> ReplyThreads = [];
        public Action<string>? OnReply;
        public Func<string, bool>? TrySendOverride;
        public Func<TimeSpan, CancellationToken, Task>? DelayOverride;
        public readonly Queue<Func<Task>> Work = new();
        public bool WriteAllowed = true, Alive = true;
        public readonly KittyClipboardSession Session;
        public Harness(TimeSpan? replyTimeout = null) => Session = new(Clipboard, reply => TrySendOverride?.Invoke(reply) ?? Record(reply),
            work => { Work.Enqueue(work); return Task.CompletedTask; }, () => WriteAllowed, () => Alive, Clock,
            replyPolicy: new ClipboardReplyPolicy((delay, cancellation) => DelayOverride?.Invoke(delay, cancellation) ?? Task.Delay(delay, cancellation), replyTimeout, work => { Work.Enqueue(work); return Task.CompletedTask; }));
        public bool Record(string reply)
        {
            Replies.Add(reply);
            ReplyThreads.Add(Environment.CurrentManagedThreadId);
            OnReply?.Invoke(reply);
            return true;
        }
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
    public async Task Cancellation_is_rechecked_after_waiting_for_the_admission_lock()
    {
        object gate = new();
        using var reached = new ManualResetEventSlim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int admitted = 0;
        var writer = new ClipboardReplyWriter(_ => { admitted++; return true; }, () => true,
            admissionGate: gate, beforeAdmissionForTest: reached.Set);
        Task<bool>? result = null;
        var sender = new Thread(() => result = writer.SendAsync("packet", cancellation.Token));
        lock (gate)
        {
            sender.Start();
            Assert.True(reached.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            cancellation.Cancel();
        }
        Assert.True(sender.Join(TimeSpan.FromSeconds(5)));
        Assert.NotNull(result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await result);
        Assert.Equal(0, admitted);
    }

    [Theory]
    [InlineData("type=wdat")]
    [InlineData("type=unknown;AAAA")]
    [InlineData("type=;AA==")]
    public async Task Unknown_packets_abort_the_active_write_instead_of_committing_truncated_data(string packet)
    {
        using var h = new Harness();
        h.Send("type=write:id=owner"); h.Send(Data("text/plain", "SGk=")); h.Send(packet); h.Send("type=wdata");
        await h.Drain();
        Assert.Contains("status=EINVAL:id=owner", Assert.Single(h.Replies));
        Assert.Equal(0, h.Clipboard.Writes);
    }

    [Theory]
    [InlineData("EPERM")]
    [InlineData("ENOSYS")]
    [InlineData("EINVAL")]
    public async Task Immediate_write_errors_retry_when_the_device_queue_is_temporarily_full(string status)
    {
        using var h = new Harness();
        int attempts = 0;
        h.TrySendOverride = reply => ++attempts < 3 ? false : h.Record(reply);
        h.DelayOverride = (delay, _) => { h.Clock.Now += delay; return Task.CompletedTask; };
        if (status == "EPERM") h.WriteAllowed = false;
        h.Send(status == "ENOSYS" ? "type=write:loc=primary" : "type=write");
        if (status == "EINVAL") h.Send("type=wdat");
        Assert.Empty(h.Replies);
        Assert.Single(h.Work);
        await h.Drain();
        Assert.Contains("status=" + status, Assert.Single(h.Replies));
        Assert.Equal(3, attempts);
        Assert.Equal(0, h.Clipboard.Writes);
    }

    [Fact]
    public async Task Busy_errors_retry_without_changing_the_already_pending_write()
    {
        using var h = new Harness();
        int busyAttempts = 0;
        h.TrySendOverride = reply => reply.Contains("status=EBUSY", StringComparison.Ordinal) && ++busyAttempts < 3 ? false : h.Record(reply);
        h.DelayOverride = (delay, _) => { h.Clock.Now += delay; return Task.CompletedTask; };
        h.Send("type=write:id=first"); h.Send(Data("text/plain", "SGk=")); h.Send("type=wdata");
        h.Send("type=write:id=second"); await h.Drain();
        Assert.Equal(1, h.Clipboard.Writes);
        Assert.Contains(h.Replies, reply => reply.Contains("status=DONE:id=first", StringComparison.Ordinal));
        Assert.Contains(h.Replies, reply => reply.Contains("status=EBUSY:id=second", StringComparison.Ordinal));
        Assert.Equal(3, busyAttempts);
    }

    [Fact]
    public async Task Error_reply_floods_keep_one_pump_and_a_bounded_queue()
    {
        using var h = new Harness { WriteAllowed = false };
        h.TrySendOverride = _ => false;
        h.DelayOverride = (_, _) => { h.Alive = false; return Task.CompletedTask; };
        for (int i = 0; i < 1000; i++) h.Send("type=write:id=" + i);
        Assert.Single(h.Work);
        Assert.InRange(h.Session.QueuedErrorRepliesForTest, 1, 64);
        await h.Drain();
        Assert.Equal(0, h.Session.QueuedErrorRepliesForTest);
        Assert.Equal(0, h.Clipboard.Writes);
    }

    [Fact]
    public async Task Reset_invalidates_queued_error_replies()
    {
        using var h = new Harness { WriteAllowed = false };
        h.TrySendOverride = _ => false;
        h.Send("type=write");
        Assert.Single(h.Work);
        h.Session.Reset();
        h.TrySendOverride = h.Record;
        await h.Drain();
        Assert.Empty(h.Replies);
    }

    [Fact]
    public async Task An_empty_chunk_does_not_reopen_a_padded_base64_stream()
    {
        using var h = new Harness();
        h.Send("type=write"); h.Send(Data("text/plain", "Zg=="));
        h.Send(Data("text/plain", "")); h.Send(Data("text/plain", "AA==")); h.Send("type=wdata");
        await h.Drain();
        Assert.Equal(0, h.Clipboard.Writes);
        Assert.Contains("status=EINVAL", Assert.Single(h.Replies));
    }

    [Theory]
    [InlineData("type=wdata:loc=primary:mime=dGV4dC9wbGFpbg==;AA==")]
    [InlineData("type=walias:loc=primary:mime=dGV4dC9wbGFpbg==;dGV4dC9odG1s")]
    [InlineData("type=wdata:loc=primary")]
    public async Task Primary_write_fragments_never_reach_the_system_clipboard(string fragment)
    {
        using var h = new Harness();
        h.Send("type=write:id=owner"); h.Send(Data("text/plain", "SGk=")); h.Send(fragment); h.Send("type=wdata");
        await h.Drain();
        Assert.Equal(0, h.Clipboard.Writes);
        Assert.Contains("status=ENOSYS:id=owner", Assert.Single(h.Replies));
    }

    [Fact]
    public async Task Aggregate_read_over_64_MiB_fails_before_sending_any_DATA()
    {
        using var h = new Harness();
        h.Clipboard.AllowRead = true;
        h.Clipboard.IgnoreReadBudget = true; // exercise the session's backstop against a misbehaving provider
        h.Clipboard.Values = new() { ["image/png"] = new byte[KittyClipboardPacket.MaxDataBytes], ["text/plain"] = [0] };
        h.Send(Read("image/png text/plain")); await h.Drain();
        Assert.Contains("status=EBUSY", Assert.Single(h.Replies));
        Assert.Equal(1, h.Clipboard.SnapshotsDisposed);
    }

    [Fact]
    public async Task A_slow_bounded_reply_queue_preserves_every_chunk_and_DONE()
    {
        using var h = new Harness();
        h.Clipboard.AllowRead = true;
        byte[] input = new byte[800 * 4096 + 1]; // exceeds the real PTY's 768-reply watermark
        for (int i = 0; i < input.Length; i++) input[i] = (byte)(i % 251);
        h.Clipboard.Values = new() { ["application/octet-stream"] = input };
        var pending = new Queue<string>();
        int waits = 0;
        h.TrySendOverride = reply =>
        {
            if (pending.Count == 2) return false;
            pending.Enqueue(reply);
            return true;
        };
        h.DelayOverride = (delay, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            waits++;
            h.Clock.Now += delay;
            h.Record(pending.Dequeue());
            return Task.CompletedTask;
        };
        h.Send(Read("application/octet-stream")); await h.Drain();
        while (pending.TryDequeue(out string? reply)) h.Record(reply);
        Assert.True(waits > 0);
        Assert.Contains("status=DONE", h.Replies[^1]);
        byte[] output = h.Replies.Where(reply => reply.Contains("status=DATA", StringComparison.Ordinal))
            .SelectMany(reply => Convert.FromBase64String(reply[(reply.LastIndexOf(';') + 1)..^2])).ToArray();
        Assert.Equal(input, output);
    }

    [Fact]
    public async Task Reset_during_reply_backpressure_cancels_the_read_and_releases_the_gate()
    {
        using var h = new Harness();
        h.Clipboard.AllowRead = true;
        h.TrySendOverride = reply =>
        {
            if (reply.Contains("status=DATA", StringComparison.Ordinal)) return false;
            return h.Record(reply);
        };
        h.DelayOverride = (_, cancellation) =>
        {
            h.Session.Reset();
            return Task.FromCanceled(cancellation);
        };
        h.Send(Read("text/plain")); await h.Drain();
        Assert.Contains("status=OK", Assert.Single(h.Replies));
        using var other = new Harness();
        other.Send(Read(".")); await other.Drain();
        Assert.Equal(3, other.Replies.Count);
    }

    [Fact]
    public async Task An_expired_write_does_not_block_a_later_read_forever()
    {
        using var h = new Harness();
        h.Send("type=write:id=old");
        h.Clock.Now = h.Clock.Now.AddMinutes(2);
        h.Send(Read(".")); await h.Drain();
        Assert.Contains("type=write:status=EBUSY:id=old", h.Replies[0]);
        Assert.Contains("type=read:status=DONE", h.Replies[^1]);
        Assert.Equal(1, h.Clipboard.Lists);
    }

    [Fact]
    public async Task Reply_capacity_timeout_fails_the_read_instead_of_claiming_DONE()
    {
        using var h = new Harness(TimeSpan.FromMilliseconds(10));
        h.Clipboard.AllowRead = true;
        h.TrySendOverride = reply =>
        {
            if (reply.Contains("status=DATA", StringComparison.Ordinal)) return false;
            return h.Record(reply);
        };
        h.DelayOverride = (delay, _) => { h.Clock.Now += delay; return Task.CompletedTask; };
        h.Send(Read("text/plain")); await h.Drain();
        Assert.Contains("status=EBUSY", h.Replies[^1]);
        Assert.DoesNotContain(h.Replies, reply => reply.Contains("status=DONE", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Replies, reply => reply.Contains("status=DATA", StringComparison.Ordinal));
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
    public async Task Multiple_MIME_reads_use_one_snapshot_even_if_the_clipboard_changes()
    {
        using var h = new Harness();
        h.Clipboard.AllowRead = true;
        byte[] originalPng = [1, 2, 3];
        h.Clipboard.Values["image/png"] = originalPng;
        h.Clipboard.OnRead = () => h.Clipboard.Values = new() { ["text/plain"] = Encoding.UTF8.GetBytes("changed"), ["image/png"] = [9] };
        h.Send(Read("text/plain image/png")); await h.Drain();
        Assert.Equal(1, h.Clipboard.Lists);
        Assert.Equal(1, h.Clipboard.SnapshotsDisposed);
        Assert.Contains(";" + B64("private text"), h.Replies[1]);
        Assert.Contains(";" + Convert.ToBase64String(originalPng), h.Replies[2]);
    }

    [Fact]
    public async Task A_request_keeps_its_slot_until_the_terminal_packet_is_admitted()
    {
        using var h = new Harness();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool allowDone = false;
        h.TrySendOverride = reply =>
        {
            if (!allowDone && reply.Contains("status=DONE", StringComparison.Ordinal)) return false;
            return h.Record(reply);
        };
        h.DelayOverride = (_, cancellation) => { waiting.TrySetResult(); return resume.Task.WaitAsync(cancellation); };
        h.Send(Read(".")); var first = h.Drain();
        await waiting.Task;
        h.Send(Read("."));
        Assert.Empty(h.Work);
        Assert.Contains("status=EBUSY", h.Replies[^1]);
        allowDone = true;
        resume.SetResult(); await first;
        h.Send(Read(".")); await h.Drain();
        Assert.Equal(2, h.Clipboard.Lists);
    }

    [Fact]
    public async Task A_client_can_start_the_next_read_immediately_from_DONE()
    {
        using var h = new Harness();
        bool followup = false;
        Task? next = null;
        h.OnReply = reply =>
        {
            if (!followup && reply.Contains("status=DONE", StringComparison.Ordinal))
            {
                followup = true;
                next = Task.Run(() => h.Send(Read(".")));
            }
        };
        h.Send(Read(".")); await h.Drain();
        Assert.True(followup);
        Assert.NotNull(next);
        await next;
        await h.Drain();
        Assert.Equal(2, h.Clipboard.Lists);
        Assert.Equal(6, h.Replies.Count);
        Assert.DoesNotContain(h.Replies, reply => reply.Contains("status=EBUSY", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_response_encoding_and_transmission_leave_the_clipboard_thread()
    {
        using var h = new Harness();
        h.Clipboard.AllowRead = true;
        h.Send(Read("image/png"));
        // Model a UI dispatcher with a dedicated thread; synchronous fake APIs
        // make their thread identity deterministic without touching the system clipboard.
        var clipboardThread = new Thread(() => h.Drain().GetAwaiter().GetResult());
        clipboardThread.Start();
        clipboardThread.Join();
        Assert.Equal(5, h.Replies.Count);
        Assert.All(h.ReplyThreads, thread => Assert.NotEqual(h.Clipboard.ReadThread, thread));
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
        Assert.Equal(2, h.Clipboard.Reads);
        Assert.Equal(0, h.Clipboard.Prompts);
        using var other = new Harness();
        other.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain")); await other.Drain();
        Assert.Equal(1, other.Clipboard.Prompts);
        h.Send("type=read:pw=" + token + ";" + B64("text/plain")); await h.Drain();
        Assert.Equal(1, h.Clipboard.Prompts);
        h.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain")); await h.Drain();
        Assert.Equal(2, h.Clipboard.Reads);
        Assert.Equal(1, h.Clipboard.Prompts);
        h.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain")); await h.Drain();
        Assert.Equal(2, h.Clipboard.Prompts);
        await h.Session.NotifyPasteAsync(); token = h.Replies[^3].Split(":pw=")[1][..^2];
        h.Clock.Now = h.Clock.Now.AddSeconds(16);
        h.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain")); await h.Drain();
        Assert.Equal(3, h.Clipboard.Prompts);
    }

    [Fact]
    public async Task Paste_token_reads_only_the_content_captured_at_the_paste_gesture()
    {
        using var h = new Harness();
        await h.Session.NotifyPasteAsync();
        string token = h.Replies[0].Split(":pw=")[1][..^2];
        Assert.Equal(1, h.Clipboard.Lists);
        Assert.Equal(1, h.Clipboard.SnapshotsDisposed);
        Array.Fill(h.Clipboard.Values["text/plain"], (byte)'X'); // a provider may reuse its buffers
        h.Clipboard.Values = new() { ["text/plain"] = Encoding.UTF8.GetBytes("new sensitive content"), ["text/html"] = [1, 2, 3] };
        h.Replies.Clear();
        h.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain text/html image/png"));
        await h.Drain();
        Assert.Equal(1, h.Clipboard.Lists); // redemption never reacquires the native clipboard
        Assert.Equal(0, h.Clipboard.Prompts);
        Assert.Contains(h.Replies, reply => reply.Contains(B64("private text"), StringComparison.Ordinal));
        Assert.DoesNotContain(h.Replies, reply => reply.Contains(B64("new sensitive content"), StringComparison.Ordinal) || reply.Contains(":mime=" + B64("text/html"), StringComparison.Ordinal));
        Assert.Contains("status=DONE", h.Replies[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reset_or_replacement_paste_invalidates_the_old_content_grant(bool reset)
    {
        using var h = new Harness();
        await h.Session.NotifyPasteAsync();
        string token = h.Replies[0].Split(":pw=")[1][..^2];
        if (reset) h.Session.Reset();
        else await h.Session.NotifyPasteAsync();
        h.Send("type=read:pw=" + token + ":name=" + B64("Paste event") + ";" + B64("text/plain"));
        await h.Drain();
        Assert.Equal(1, h.Clipboard.Prompts);
        Assert.Contains("status=EPERM", h.Replies[^1]);
    }

    [Fact]
    public async Task Paste_capture_over_the_aggregate_limit_issues_no_password_or_data()
    {
        using var h = new Harness();
        h.Clipboard.Values = new() { ["application/octet-stream"] = new byte[KittyClipboardPacket.MaxDataBytes], ["text/plain"] = [1] };
        await h.Session.NotifyPasteAsync();
        Assert.Contains("status=EBUSY", Assert.Single(h.Replies));
        Assert.DoesNotContain(":pw=", h.Replies[0]);
        Assert.Equal(1, h.Clipboard.SnapshotsDisposed);
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
