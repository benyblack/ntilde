using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class NativeSshInputAdmissionTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(-3, false)]
    public void Only_native_OK_is_acceptance_and_a_closed_worker_is_refusal(int result, bool accepted)
    {
        using var handle = new NovaSshSafeHandle(new IntPtr(1), ownsHandle: false);
        byte[] input = [27, 91, 77, 32, 128, 255]; byte[]? retained = null;
        var interop = new NativeSshInterop((actualHandle, payload, length) =>
        {
            Assert.Same(handle, actualHandle); Assert.Equal((nuint)input.Length, length);
            Assert.Equal(input, payload); retained = payload; return result;
        });
        Assert.Equal(accepted, interop.TryWriteInput(handle, input));
        Array.Fill(input, (byte)0);
        Assert.Equal(new byte[] { 27, 91, 77, 32, 128, 255 }, retained);
    }

    [Fact]
    public void Disposal_during_native_admission_is_refusal()
    {
        using var handle = new NovaSshSafeHandle(new IntPtr(1), ownsHandle: false);
        var interop = new NativeSshInterop((actualHandle, _, _) =>
        {
            actualHandle.Dispose();
            throw new ObjectDisposedException(nameof(NovaSshSafeHandle));
        });
        Assert.False(interop.TryWriteInput(handle, new byte[] { 128, 255 }));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-7)]
    public void Unexpected_native_errors_remain_distinguishable_from_refusal(int result)
    {
        using var handle = new NovaSshSafeHandle(new IntPtr(1), ownsHandle: false);
        var interop = new NativeSshInterop((_, _, _) => result);
        Assert.Contains($"result {result}", Assert.Throws<InvalidOperationException>(() =>
            interop.TryWriteInput(handle, new byte[] { 128, 255 })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Closed_invalid_and_empty_inputs_never_call_native_admission()
    {
        bool called = false;
        var interop = new NativeSshInterop((_, _, _) => { called = true; return 0; });
        using var invalid = new NovaSshSafeHandle(IntPtr.Zero, ownsHandle: false);
        using var closed = new NovaSshSafeHandle(new IntPtr(1), ownsHandle: false); closed.Dispose();
        using var live = new NovaSshSafeHandle(new IntPtr(2), ownsHandle: false);
        Assert.False(interop.TryWriteInput(invalid, new byte[] { 128 }));
        Assert.False(interop.TryWriteInput(closed, new byte[] { 128 }));
        Assert.False(interop.TryWriteInput(live, ReadOnlySpan<byte>.Empty));
        Assert.False(called);
    }
}
