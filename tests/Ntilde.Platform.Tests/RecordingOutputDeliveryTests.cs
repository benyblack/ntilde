using Ntilde.Pty;

namespace Ntilde.Platform.Tests;

public sealed class RecordingOutputDeliveryTests
{
    [Fact]
    public void A_second_decoded_host_cannot_steal_delivery_from_the_installed_host()
    {
        var delivery = new RecordingOutputDelivery();
        byte[] bytes = [0xE2, 0x82];
        ReadOnlyMemory<byte> received = default;
        var host = delivery.CreateDecodedHost((_, data) => received = data);
        Assert.Throws<InvalidOperationException>(() => delivery.CreateDecodedHost((_, _) => { }));
        delivery.Bytes = bytes;
        host(string.Empty);
        Assert.True(received.Span.SequenceEqual(bytes));
        delivery.Bytes = default;
        host("retained text");
        Assert.True(received.IsEmpty);
    }
}
