using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerOptionsValidationTests
{
    private static MuxServerOptions Invalid(string name) => name switch
    {
        "budget-zero" => new MuxServerOptions { ClientSendBudgetBytes = 0 },
        "budget-negative" => new MuxServerOptions { ClientSendBudgetBytes = -1 },
        "snapshot-bound-zero" => new MuxServerOptions { MaxQueuedSnapshotBytes = 0 },
        "snapshot-zero" => new MuxServerOptions { MaxSnapshotBytes = 0 },
        "snapshot-over-frame" => new MuxServerOptions { MaxSnapshotBytes = MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes + 1 },
        "inbound-zero" => new MuxServerOptions { MaxInboundFrameBytes = 0 },
        "inbound-over-frame" => new MuxServerOptions { MaxInboundFrameBytes = MuxProtocol.MaxFrameBytes + 1 },
        "scrollback-negative" => new MuxServerOptions { MaxAttachScrollbackRows = -1 },
        "cells-zero" => new MuxServerOptions { MaxCells = 0 },
        "dimension-zero" => new MuxServerOptions { MaxDimension = 0 },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("budget-zero")]
    [InlineData("budget-negative")]
    [InlineData("snapshot-bound-zero")]
    [InlineData("snapshot-zero")]
    [InlineData("snapshot-over-frame")] // MuxFrames.Snapshot would throw inside ExecuteAttach: no reply, a 30 s client hang
    [InlineData("inbound-zero")]
    [InlineData("inbound-over-frame")]
    [InlineData("scrollback-negative")]
    [InlineData("cells-zero")]
    [InlineData("dimension-zero")]
    public void Nonsensical_options_are_refused_at_construction(string name)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MuxServer(new ScriptedSessionFactory(), Invalid(name)).Dispose());
    }

    [Fact]
    public void The_defaults_and_the_boundaries_are_accepted()
    {
        new MuxServer(new ScriptedSessionFactory()).Dispose();
        new MuxServer(new ScriptedSessionFactory(), new MuxServerOptions
        {
            ClientSendBudgetBytes = 1,
            MaxQueuedSnapshotBytes = 1,
            MaxSnapshotBytes = MuxProtocol.MaxFrameBytes - MuxFrames.SnapshotHeaderBytes,
            MaxInboundFrameBytes = MuxProtocol.MaxFrameBytes,
            MaxAttachScrollbackRows = 0,
            MaxCells = 1,
            MaxDimension = 1,
        }).Dispose();
    }
}
