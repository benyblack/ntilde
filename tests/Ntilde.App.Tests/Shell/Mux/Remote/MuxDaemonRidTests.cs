using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// <see cref="MuxDaemonRid"/>: which published platform an executable's header says it was built for,
/// from tiny synthetic headers, so a picked file for another platform is refused before it is uploaded.
/// </summary>
public sealed class MuxDaemonRidTests
{
    /// <summary>An ELF header up to <c>e_machine</c>: 64-bit, little-endian, ET_EXEC.</summary>
    internal static byte[] Elf(ushort machine, byte elfClass = 2, byte data = 1) =>
        [0x7F, (byte)'E', (byte)'L', (byte)'F', elfClass, data, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, (byte)machine, (byte)(machine >> 8)];

    /// <summary>A 64-bit little-endian Mach-O header up to <c>cputype</c>.</summary>
    internal static byte[] MachO(uint cpuType) =>
        [0xCF, 0xFA, 0xED, 0xFE, (byte)cpuType, (byte)(cpuType >> 8), (byte)(cpuType >> 16), (byte)(cpuType >> 24)];

    /// <summary>A header for <paramref name="rid"/>, then a body.</summary>
    internal static byte[] ExecutableFor(string rid, int bodySize = 4096)
    {
        byte[] header = rid switch
        {
            MuxDaemonRid.LinuxX64 => Elf(62),
            MuxDaemonRid.LinuxArm64 => Elf(183),
            MuxDaemonRid.OsxArm64 => MachO(0x0100000C),
            _ => throw new ArgumentOutOfRangeException(nameof(rid)),
        };
        byte[] bytes = new byte[header.Length + bodySize];
        header.CopyTo(bytes, 0);
        new Random(11).NextBytes(bytes.AsSpan(header.Length));
        return bytes;
    }

    public static TheoryData<byte[], string?, string> Headers() => new()
    {
        { Elf(62), "linux-x64", "a linux-x64 executable" },
        { Elf(183), "linux-arm64", "a linux-arm64 executable" },
        { MachO(0x0100000C), "osx-arm64", "an osx-arm64 executable" },
        { MachO(0x01000007), null, "an Intel macOS executable" },
        { MachO(0x00000012), null, "a macOS executable for another processor" },
        { Elf(40, elfClass: 1), null, "an ELF executable for another processor" },     // 32-bit ARM
        { Elf(62, data: 2), null, "an ELF executable for another processor" },         // big-endian
        { Elf(243), null, "an ELF executable for another processor" },                 // RISC-V
        { new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1 }, null, "an ELF executable for another processor" },
        { new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2 }, null, "a universal macOS executable (pick the osx-arm64 build)" },
        { "#!/bin/sh\necho hi\n"u8.ToArray(), null, "not a Linux or macOS executable" },
        { new byte[] { (byte)'M', (byte)'Z', 0x90, 0 }, null, "not a Linux or macOS executable" },
        { Array.Empty<byte>(), null, "not a Linux or macOS executable" },
    };

    [Theory]
    [MemberData(nameof(Headers))]
    public void The_header_names_the_platform(byte[] bytes, string? rid, string description)
    {
        Assert.Equal(rid, MuxDaemonRid.Of(bytes));
        Assert.Equal(description, MuxDaemonRid.Describe(bytes));
    }

    [Fact]
    public void The_published_rids_are_the_three_release_builds()
    {
        Assert.Equal(["linux-x64", "linux-arm64", "osx-arm64"], MuxDaemonRid.All);
        Assert.All(MuxDaemonRid.All, rid => Assert.True(MuxDaemonRid.IsKnown(rid)));
        Assert.False(MuxDaemonRid.IsKnown("osx-x64"));
        Assert.False(MuxDaemonRid.IsKnown(null));
    }
}
