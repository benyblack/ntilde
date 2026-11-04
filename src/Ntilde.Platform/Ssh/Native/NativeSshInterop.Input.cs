namespace Ntilde.Platform.Ssh.Native;

public sealed partial class NativeSshInterop
{
    private readonly Func<NovaSshSafeHandle, byte[], nuint, int> _inputWriter;

    public NativeSshInterop() : this(NativeMethods.nova_ssh_write) { }

    // The same native admission boundary, injectable for deterministic result/disposal tests.
    internal NativeSshInterop(Func<NovaSshSafeHandle, byte[], nuint, int> inputWriter)
    {
        ArgumentNullException.ThrowIfNull(inputWriter);
        _inputWriter = inputWriter;
    }

    /// <summary>Uses the ordinary input command queue; true only for native acceptance.</summary>
    public bool TryWriteInput(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data)
    {
        if (sessionHandle is null || sessionHandle.IsInvalid || sessionHandle.IsClosed || data.IsEmpty) return false;
        byte[] payload = data.ToArray();
        try
        {
            int result = _inputWriter(sessionHandle, payload, (nuint)payload.Length);
            if (result == ResultClosed) return false;
            if (result != ResultOk) throw new InvalidOperationException($"Native SSH write failed with result {result}.");
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }
}
