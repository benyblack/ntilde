using System.Collections.Concurrent;
using System.Text;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Sessions;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class NativeSshSessionInteractionTests
{
    [Fact]
    public async Task PromptWaitsForInteractionResponseBeforePollingFurtherEvents()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(new NativeSshEvent(
            NativeSshEventKind.HostKeyPrompt,
            Encoding.UTF8.GetBytes("""{"host":"srv","port":22,"algorithm":"ssh-ed25519","fingerprint":"SHA256:test"}"""),
            flags: NativeSshEventFlags.Json));
        interop.Enqueue(NativeSshEvent.Data(Encoding.UTF8.GetBytes("ready\n")));
        interop.Enqueue(NativeSshEvent.ExitStatus(0));
        interop.Enqueue(NativeSshEvent.Closed());

        var handler = new PendingInteractionHandler();
        var outputs = new List<string>();
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var session = new NativeSshSession(CreateProfile(), interop: interop, interactionHandler: handler);
        session.OnOutputReceived += outputs.Add;
        session.OnExit += code => exit.TrySetResult(code);

        await handler.WaitForRequestAsync();
        await Task.Delay(100);

        Assert.Equal(1, interop.PollCallCount);
        Assert.Empty(outputs);
        Assert.Empty(interop.Submissions);

        handler.Complete(SshInteractionResponse.AcceptHostKey());

        Assert.Equal(0, await exit.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("""{"accept":true}""", interop.Submissions.Single().PayloadJson);
        Assert.Contains("ready", outputs.Single());
    }

    [Fact]
    public async Task PromptCancellationSubmitsDeterministicRejectPayload()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(new NativeSshEvent(
            NativeSshEventKind.HostKeyPrompt,
            Encoding.UTF8.GetBytes("""{"host":"srv","port":22,"algorithm":"ssh-ed25519","fingerprint":"SHA256:test"}"""),
            flags: NativeSshEventFlags.Json));

        var handler = new PendingInteractionHandler();
        using var session = new NativeSshSession(CreateProfile(), interop: interop, interactionHandler: handler);

        await handler.WaitForRequestAsync();
        handler.Complete(SshInteractionResponse.Cancel());
        await WaitUntilAsync(() => interop.Submissions.Count > 0);

        var submission = interop.Submissions.Single();
        Assert.Equal(NativeSshResponseKind.HostKeyDecision, submission.Kind);
        Assert.Equal("""{"accept":false}""", submission.PayloadJson);
    }

    [Fact]
    public async Task PasswordPromptCarriesNativeProfileContext()
    {
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(TargetPasswordPrompt());
        interop.Enqueue(TargetPasswordPrompt());

        var requests = new List<SshInteractionRequest>();
        var handler = new RecordingInteractionHandler(requests);
        var profile = CreateProfile();

        using var session = new NativeSshSession(profile, interop: interop, interactionHandler: handler);

        await WaitUntilAsync(() => requests.Count >= 2);

        Assert.Equal(profile.Id, requests[0].ProfileId);
        Assert.Equal(profile.Name, requests[0].ProfileName);
        Assert.Equal(profile.User, requests[0].ProfileUser);
        Assert.Equal(profile.Host, requests[0].ProfileHost);
        Assert.Equal("native.example", requests[0].Host);
        Assert.Equal(22, requests[0].Port);
        Assert.Equal("nova", requests[0].User);
        Assert.False(requests[0].IsJumpHop);
        Assert.True(requests[0].RememberPasswordInVault);
        Assert.True(requests[0].AllowVaultPasswordReuse);

        Assert.True(requests[1].RememberPasswordInVault);
        Assert.False(requests[1].AllowVaultPasswordReuse);
    }

    [Fact]
    public async Task JumpHopPasswordPrompt_DoesNotSpendOrTouchTheTargetsVaultEntry()
    {
        // In a chain the bastion prompts first. The one-shot vault reuse used to be spent on that
        // first prompt — handing the bastion the target's saved password. It must now survive the
        // bastion's prompt and apply to the target's.
        var interop = new FakeNativeSshInterop();
        interop.Enqueue(new NativeSshEvent(
            NativeSshEventKind.PasswordPrompt,
            Encoding.UTF8.GetBytes("""{"prompt":"Password:","host":"bastion.example","port":2200,"user":"jump","isJumpHop":true}"""),
            flags: NativeSshEventFlags.Json));
        interop.Enqueue(TargetPasswordPrompt());

        var requests = new List<SshInteractionRequest>();
        var handler = new RecordingInteractionHandler(requests);

        using var session = new NativeSshSession(CreateProfile(), interop: interop, interactionHandler: handler);

        await WaitUntilAsync(() => requests.Count >= 2);

        SshInteractionRequest bastion = requests[0];
        Assert.True(bastion.IsJumpHop);
        Assert.Equal("bastion.example", bastion.Host);
        Assert.Equal(2200, bastion.Port);
        Assert.Equal("jump", bastion.User);
        Assert.Equal(session.Id, bastion.SessionId);
        Assert.False(bastion.AllowVaultPasswordReuse);
        Assert.False(bastion.RememberPasswordInVault);

        SshInteractionRequest target = requests[1];
        Assert.False(target.IsJumpHop);
        Assert.True(target.AllowVaultPasswordReuse);
        Assert.True(target.RememberPasswordInVault);
    }

    private static NativeSshEvent TargetPasswordPrompt()
    {
        return new NativeSshEvent(
            NativeSshEventKind.PasswordPrompt,
            Encoding.UTF8.GetBytes("""{"prompt":"Password:","host":"native.example","port":22,"user":"nova","isJumpHop":false}"""),
            flags: NativeSshEventFlags.Json);
    }

    private static SshProfile CreateProfile()
    {
        return new SshProfile
        {
            Id = Guid.Parse("6892b728-776c-4cb0-8e31-5875f69e8086"),
            BackendKind = SshBackendKind.Native,
            Host = "native.example",
            User = "nova",
            Port = 22
        };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }

    private sealed class PendingInteractionHandler : ISshInteractionHandler
    {
        private readonly TaskCompletionSource<SshInteractionRequest> _request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SshInteractionResponse> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            _request.TrySetResult(request);
            return _response.Task.WaitAsync(cancellationToken);
        }

        public Task<SshInteractionRequest> WaitForRequestAsync() => _request.Task;

        public void Complete(SshInteractionResponse response)
        {
            _response.TrySetResult(response);
        }
    }

    private sealed class RecordingInteractionHandler : ISshInteractionHandler
    {
        private readonly List<SshInteractionRequest> _requests;

        public RecordingInteractionHandler(List<SshInteractionRequest> requests)
        {
            _requests = requests;
        }

        public Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return Task.FromResult(SshInteractionResponse.Cancel());
        }
    }

    private sealed class FakeNativeSshInterop : INativeSshInterop
    {
        private readonly ConcurrentQueue<NativeSshEvent> _events = new();

        public int PollCallCount { get; private set; }
        public List<(NativeSshResponseKind Kind, string PayloadJson)> Submissions { get; } = new();

        public NovaSshSafeHandle Connect(NativeSshConnectionOptions options) => new(new IntPtr(1), ownsHandle: false);

        public void RunSftpTransfer(NativeSshConnectionOptions connectionOptions, NativeSftpTransferOptions transferOptions, Action<NativeSftpTransferProgress>? progress, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(NativeSshConnectionOptions connectionOptions, string remotePath, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle)
        {
            PollCallCount++;
            return _events.TryDequeue(out NativeSshEvent? nextEvent)
                ? nextEvent
                : null;
        }

        public void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
        {
        }

        public void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows)
        {
        }

        public int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options) => 1;

        public void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
        {
        }

        public void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId)
        {
        }

        public void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data)
        {
            Submissions.Add((responseKind, Encoding.UTF8.GetString(data)));
        }

        public void Close(NovaSshSafeHandle sessionHandle)
        {
        }

        public void Enqueue(NativeSshEvent nextEvent)
        {
            _events.Enqueue(nextEvent);
        }
    }
}
