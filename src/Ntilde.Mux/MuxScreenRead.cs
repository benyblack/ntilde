using Ntilde.Mux.Contracts;
using Ntilde.VT;

namespace Ntilde.Mux;

/// <summary>
/// What <see cref="MuxClient.ReadScreenAsync"/> returns: the decoded screen, and the status the daemon took with it.
/// <see cref="Status"/> no longer carries the raw snapshot bytes (its <see cref="ReadScreenResult.Snapshot"/> is
/// empty): <see cref="Snapshot"/> is their decoded form.
/// </summary>
public sealed record MuxScreenRead(TerminalStateSnapshot Snapshot, ReadScreenResult Status);
