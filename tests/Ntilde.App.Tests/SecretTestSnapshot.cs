using Ntilde.Platform.Security;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Tests;

internal static class SecretTestSnapshot
{
    internal static byte[] Bytes(SecretBuffer value) =>
        (byte[])typeof(SecretBuffer).GetField("_bytes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(value)!;

    // Interop arguments belong to the operation and are wiped when it ends. A recording fake
    // takes its own copies while the call is running so assertions can inspect what was sent.
    internal static NativeSshConnectionOptions Copy(NativeSshConnectionOptions source)
    {
        var copy = new NativeSshConnectionOptions { Host = source.Host, User = source.User };
        foreach (var property in typeof(NativeSshConnectionOptions).GetProperties())
        {
            object? value = property.GetValue(source);
            if (value is SecretBuffer secret) value = secret.Clone();
            else if (property.Name == nameof(NativeSshConnectionOptions.JumpHopPasswords))
                value = source.JumpHopPasswords.Select(secret => secret?.Clone()).ToArray();
            property.SetValue(copy, value);
        }
        return copy;
    }
}
