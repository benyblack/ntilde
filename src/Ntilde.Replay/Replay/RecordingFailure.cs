using System;
using System.IO;

namespace Ntilde.Replay;

public enum RecordingFailureKind { Open, Write, Overflow, FlushTimeout }

/// <summary>A terminal recording failure; the partial file must not be reported as saved.</summary>
public sealed record RecordingFailure(RecordingFailureKind Kind, string Message, Guid RecordingId = default);

public sealed class RecordingException(RecordingFailure failure) : IOException(failure.Message)
{
    public RecordingFailure Failure { get; } = failure;
}
