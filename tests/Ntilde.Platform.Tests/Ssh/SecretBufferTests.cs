using System.Reflection;
using System.Runtime.InteropServices;
using Ntilde.Platform.Security;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Native;

namespace Ntilde.Platform.Tests.Ssh;

public sealed class SecretBufferTests
{
    internal static byte[] RetainedBytes(SecretBuffer value) =>
        (byte[])typeof(SecretBuffer).GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;

    [Theory]
    [InlineData("")]
    [InlineData("café-🔑-пароль\"\\\n\0")]
    public void Owned_copy_survives_source_disposal_and_is_wiped_on_dispose(string text)
    {
        var source = SecretBuffer.FromText(text);
        using var copy = source.Clone();
        byte[] retained = RetainedBytes(source);
        source.Dispose();
        source.Dispose();

        Assert.All(retained, value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => source.Clone());
        Assert.Equal(text, copy.DangerousGetString());
        Assert.Equal("[secret]", copy.ToString());
    }

    [Fact]
    public void Native_pointer_is_terminated_and_stable_across_compacting_GC()
    {
        using var secret = SecretBuffer.FromText("café-🔑");
        secret.UsePointer(address =>
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.Equal("café-🔑", Marshal.PtrToStringUTF8(address));
            Assert.Equal(0, Marshal.ReadByte(address, secret.Length));
            secret.UsePointer(after => { Assert.Equal(address, after); return 0; });
            return 0;
        });
    }

    [Theory]
    [InlineData("café-🔑-пароль\"\\\n\0")]
    [InlineData("   ")]
    public void Json_round_trip_preserves_secret_text(string text)
    {
        using var response = SshInteractionResponse.FromSecret(text);
        using var payload = NativeSshInteractionJson.BuildResponsePayload(NativeSshResponseKind.Password, response);
        using var parsed = System.Text.Json.JsonDocument.Parse(payload.Span.ToArray());
        Assert.Equal(text, parsed.RootElement.GetProperty("text").GetString());
        byte[] retained = RetainedBytes(payload);
        payload.Dispose();
        Assert.All(retained, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Json_growth_preserves_all_answers_and_response_disposal_wipes_each()
    {
        string text = new('x', 30_000);
        using var response = SshInteractionResponse.FromKeyboardResponses(text, "🔑", "");
        byte[][] retained = response.KeyboardResponses.Select(RetainedBytes).ToArray();
        using var payload = NativeSshInteractionJson.BuildResponsePayload(NativeSshResponseKind.KeyboardInteractive, response);
        using var parsed = System.Text.Json.JsonDocument.Parse(payload.Span.ToArray());
        Assert.Equal(new[] { text, "🔑", "" }, parsed.RootElement.GetProperty("responses").EnumerateArray().Select(value => value.GetString()));
        response.Dispose();
        foreach (byte[] buffer in retained) Assert.All(buffer, value => Assert.Equal(0, value));
    }
}
