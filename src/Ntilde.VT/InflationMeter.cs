namespace Ntilde.VT;

/// <summary>
/// Counts the bytes zlib inflation produces on the current thread: Kitty <c>o=z</c> payloads and
/// snapshot images. Diagnostics only, so tests can prove that a restore's inflation stays bounded.
/// </summary>
internal static class InflationMeter
{
    [ThreadStatic] internal static long Bytes;
}
