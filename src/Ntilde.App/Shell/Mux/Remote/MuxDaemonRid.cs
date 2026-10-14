using System.Buffers.Binary;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The platforms <c>ntilde-mux</c> is published for (Phase 4 spec §2 decision 1, §10.3), and what an
/// executable's header says it was built for, so that a file picked for another platform is refused
/// before it is uploaded (spec §9 step 2(b)).
/// </summary>
internal static class MuxDaemonRid
{
    public const string LinuxX64 = "linux-x64";
    public const string LinuxArm64 = "linux-arm64";
    public const string OsxArm64 = "osx-arm64";

    /// <summary>Every RID a release publishes <c>ntilde-mux-&lt;rid&gt;</c> for.</summary>
    public static IReadOnlyList<string> All { get; } = [LinuxX64, LinuxArm64, OsxArm64];

    private const ushort ElfMachineX86_64 = 62;
    private const ushort ElfMachineAarch64 = 183;
    private const uint MachO64Magic = 0xFEEDFACF;
    private const uint MachOCpuX86_64 = 0x01000007;
    private const uint MachOCpuArm64 = 0x0100000C;
    private const uint FatMachOMagic = 0xCAFEBABE;

    public static bool IsKnown(string? rid) => rid is LinuxX64 or LinuxArm64 or OsxArm64;

    /// <summary>The published RID <paramref name="executable"/> was built for; null when it is none of them.</summary>
    public static string? Of(ReadOnlySpan<byte> executable) => Classify(executable).Rid;

    /// <summary>What <paramref name="executable"/> is, for a message: <c>a linux-x64 executable</c>, <c>not a Linux or macOS executable</c>.</summary>
    public static string Describe(ReadOnlySpan<byte> executable) => Classify(executable).Description;

    /// <remarks>
    /// ELF: <c>\x7fELF</c>, <c>EI_CLASS</c> 2 (64-bit), <c>EI_DATA</c> 1 (little-endian), <c>e_machine</c> at
    /// offset 18. Mach-O: the 64-bit magic, then <c>cputype</c>, both little-endian as Apple silicon writes them.
    /// </remarks>
    private static (string? Rid, string Description) Classify(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F')
        {
            if (bytes.Length >= 20 && bytes[4] == 2 && bytes[5] == 1)
            {
                switch (BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]))
                {
                    case ElfMachineX86_64: return (LinuxX64, "a linux-x64 executable");
                    case ElfMachineAarch64: return (LinuxArm64, "a linux-arm64 executable");
                }
            }

            return (null, "an ELF executable for another processor");
        }

        if (bytes.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == MachO64Magic)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) switch
            {
                MachOCpuArm64 => (OsxArm64, "an osx-arm64 executable"),
                MachOCpuX86_64 => (null, "an Intel macOS executable"),
                _ => (null, "a macOS executable for another processor"),
            };
        }

        if (bytes.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(bytes) == FatMachOMagic)
        {
            return (null, "a universal macOS executable (pick the osx-arm64 build)");
        }

        return (null, "not a Linux or macOS executable");
    }
}
