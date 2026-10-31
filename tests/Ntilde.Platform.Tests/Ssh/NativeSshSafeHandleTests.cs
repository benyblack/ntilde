using System.Net;
using System.Net.Sockets;
using Ntilde.Platform.Ssh.Native;
using Xunit;

namespace Ntilde.Platform.Tests.Ssh;

public class NativeSshSafeHandleTests
{
    [Fact]
    public void Device_reply_rejects_invalid_closed_and_oversized_inputs_before_native_admission()
    {
        var interop = new NativeSshInterop();
        Assert.False(interop.TryWriteDeviceReply(null!, "reply"u8));
        using var invalid = new NovaSshSafeHandle();
        Assert.False(interop.TryWriteDeviceReply(invalid, "reply"u8));
        using var closed = new NovaSshSafeHandle(new IntPtr(1), ownsHandle: false);
        closed.Dispose();
        Assert.False(interop.TryWriteDeviceReply(closed, "reply"u8));
        using var nonInvalid = new NovaSshSafeHandle(new IntPtr(1), ownsHandle: false);
        Assert.False(interop.TryWriteDeviceReply(nonInvalid, new byte[64 * 1024 + 1]));
    }

    [Fact]
    public async Task Device_reply_calls_the_bounded_export_on_a_live_native_handle()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var interop = new NativeSshInterop();
        using var handle = interop.Connect(new NativeSshConnectionOptions
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            User = "reply-test"
        });
        using var peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The peer never sends an SSH banner, so establishment cannot consume worker commands.
        // One 32 KiB reply fits, but two (with their command surcharge) exceed the native budget.
        byte[] reply = new byte[32 * 1024];
        Assert.True(interop.TryWriteDeviceReply(handle, reply));
        Assert.False(interop.TryWriteDeviceReply(handle, reply));
    }

    [Fact]
    public void Dispose_IsIdempotent_AndDoesNotThrow()
    {
        var handle = new NovaSshSafeHandle();
        Assert.True(handle.IsInvalid);

        var ex = Record.Exception(() =>
        {
            handle.Dispose();
            handle.Dispose();
        });

        Assert.Null(ex);
        Assert.True(handle.IsClosed);
    }

    [Fact]
    public void Interop_SessionMethods_NoOp_OnDisposedHandle()
    {
        var interop = new NativeSshInterop();
        var handle = new NovaSshSafeHandle();
        handle.Dispose();

        var ex = Record.Exception(() =>
        {
            _ = interop.PollEvent(handle);
            interop.Write(handle, new byte[] { 1 });
            interop.Resize(handle, 80, 24);
            interop.Close(handle);
        });

        Assert.Null(ex);
    }
}
