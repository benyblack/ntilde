using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Ntilde.Platform.Security;

/// <summary>
/// An owned, pinned UTF-8 secret. The owner disposes it after use; consumers that outlive the owner
/// must clone it. Disposal must not race a read. Pinning prevents GC relocation, not paging or dumps.
/// </summary>
[JsonConverter(typeof(SecretBufferJsonConverter))]
public sealed class SecretBuffer : IDisposable
{
    private byte[]? _bytes;
    private readonly int _length;

    private SecretBuffer(int length)
    {
        _length = length;
        _bytes = GC.AllocateArray<byte>(checked(length + 1), pinned: true);
    }

    public int Length => Span.Length;
    public ReadOnlySpan<byte> Span => (_bytes ?? throw new ObjectDisposedException(nameof(SecretBuffer))).AsSpan(0, _length);

    // For synchronous C-string APIs only; the trailing zero is part of the owned allocation.
    public ref readonly byte GetPinnableReference() => ref (_bytes ?? throw new ObjectDisposedException(nameof(SecretBuffer)))[0];

    public static SecretBuffer FromText(ReadOnlySpan<char> text)
    {
        var result = new SecretBuffer(Encoding.UTF8.GetByteCount(text));
        Encoding.UTF8.GetBytes(text, result._bytes);
        return result;
    }

    public static SecretBuffer FromUtf8(ReadOnlySpan<byte> bytes)
    {
        var result = new SecretBuffer(bytes.Length);
        bytes.CopyTo(result._bytes);
        return result;
    }

    /// <summary>Calls a synchronous native API with a NUL-terminated pointer. The pointer must not escape.</summary>
    public T UsePointer<T>(Func<IntPtr, T> action)
    {
        byte[] bytes = _bytes ?? throw new ObjectDisposedException(nameof(SecretBuffer));
        try { return action(Marshal.UnsafeAddrOfPinnedArrayElement(bytes, 0)); }
        finally { GC.KeepAlive(this); }
    }

    public static SecretBuffer FromPointer(IntPtr address, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var result = new SecretBuffer(length);
        try { Marshal.Copy(address, result._bytes!, 0, length); return result; }
        catch { result.Dispose(); throw; }
    }

    public static SecretBuffer FromNullTerminatedPointer(IntPtr address)
    {
        int length = 0;
        while (Marshal.ReadByte(address, length) != 0) length = checked(length + 1);
        return FromPointer(address, length);
    }

    public SecretBuffer Clone() => FromUtf8(Span);

    public bool IsWhiteSpace
    {
        get
        {
            ReadOnlySpan<byte> remaining = Span;
            while (!remaining.IsEmpty)
            {
                if (Rune.DecodeFromUtf8(remaining, out Rune rune, out int consumed) != OperationStatus.Done || !Rune.IsWhiteSpace(rune)) return false;
                remaining = remaining[consumed..];
            }
            return true;
        }
    }

    public bool ContentEquals(SecretBuffer other) => CryptographicOperations.FixedTimeEquals(Span, other.Span);

    /// <summary>Only for APIs that require a string (UI/OpenSSH) and tests. The returned copy cannot be wiped.</summary>
    public string DangerousGetString() => Encoding.UTF8.GetString(Span);

    public override string ToString() => "[secret]";

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    private void Clear()
    {
        byte[]? bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }

    ~SecretBuffer() => Clear();
}

public sealed class SecretBufferJsonConverter : JsonConverter<SecretBuffer>
{
    public override SecretBuffer Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Secrets must be read directly into an owned buffer.");

    public override void Write(Utf8JsonWriter writer, SecretBuffer value, JsonSerializerOptions options) => writer.WriteStringValue(value.Span);
}

/// <summary>Serializes credentials without an intermediate JSON string or an uncleared serializer output buffer.</summary>
public static class SecretJson
{
    public static SecretBuffer Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var output = new ClearingBufferWriter();
        using (var writer = new Utf8JsonWriter(output))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }
        return SecretBuffer.FromUtf8(output.WrittenSpan);
    }

    private sealed class ClearingBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _bytes = GC.AllocateArray<byte>(256, pinned: true);
        private int _written;
        public ReadOnlySpan<byte> WrittenSpan => _bytes.AsSpan(0, _written);
        public void Advance(int count) => _written += count;
        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _bytes.AsMemory(_written); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _bytes.AsSpan(_written); }
        private void Ensure(int sizeHint)
        {
            if (_bytes.Length - _written >= Math.Max(1, sizeHint)) return;
            byte[] next = GC.AllocateArray<byte>(checked(Math.Max(_bytes.Length * 2, _written + sizeHint)), pinned: true);
            WrittenSpan.CopyTo(next);
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = next;
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
    }
}
