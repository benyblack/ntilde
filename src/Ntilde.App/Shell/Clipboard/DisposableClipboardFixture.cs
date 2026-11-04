using Ntilde.AgentHost.Contracts;
using System.Text;

namespace Ntilde.Shell.Clipboard;

/// <summary>Private known-value storage. No native provider, consent callback, file or user text enters this object.</summary>
internal sealed class DisposableClipboardFixture
{
    private readonly object _gate = new();
    private readonly Func<bool> _isCurrent;
    private readonly string _seed;
    private readonly string _runId;
    private string _value;
    private string? _armed;
    private string _state = "active";
    private int _writes;
    private bool _restored;
    private bool _complete;
    internal Guid Id { get; } = Guid.NewGuid();

    internal DisposableClipboardFixture(string runId, Func<bool> isCurrent)
    {
        if (!ClipboardFixtureValidation.IsRunId(runId)) throw new ArgumentException("Invalid QA run identifier.", nameof(runId));
        _isCurrent = isCurrent;
        _runId = runId;
        _seed = "terminfo-private-seed-" + runId;
        _value = _seed;
    }

    internal ClipboardFixtureResult? Apply(ClipboardFixtureParams input)
    {
        lock (_gate)
        {
            if (input.FixtureId != Id || input.RunId != _runId || !ClipboardFixtureValidation.IsValid(input)) return null;
            if (_state != "active") return input.Operation == "close" ? Snapshot(false) : null;
            if (!IsCurrent()) { Abort(); return input.Operation == "close" ? Snapshot(false) : null; }
            return ApplyActive(input);
        }
    }

    private ClipboardFixtureResult? ApplyActive(ClipboardFixtureParams input)
    {
        switch (input.Operation)
        {
            case "armWrite":
            case "seed":
                if (_armed != null) { Abort(); return null; }
                if (input.Operation == "armWrite") _armed = input.Value;
                else _value = input.Value ?? throw new InvalidOperationException("A validated seed is required.");
                return Snapshot(false);
            case "read": return ReadCurrent();
            case "close": return CloseCurrent();
            default: return null;
        }
    }

    private ClipboardFixtureResult? ReadCurrent()
    {
        var observed = Snapshot(true);
        if (IsCurrent()) return observed;
        Abort();
        return null;
    }

    private ClipboardFixtureResult CloseCurrent()
    {
        if (_armed != null) Abort();
        else
        {
            _value = _seed;
            _restored = _value == _seed;
            _complete = _restored && IsCurrent();
            _state = _complete ? "closed" : "aborted";
        }
        return Snapshot(false);
    }

    internal bool AcceptOsc52(string targets, byte[] bytes)
    {
        lock (_gate)
        {
            if (_state != "active") return false;
            if (!IsCurrent() || targets != "c" || _armed == null || bytes.Length != 51
                || !bytes.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(_armed)))
            { Abort(); return false; }
            _value = _armed;
            _armed = null;
            _writes++;
            return true;
        }
    }

    internal void Interrupt()
    {
        lock (_gate) { if (_state == "active") Abort(); }
    }

    internal ClipboardFixtureResult Observe()
    {
        lock (_gate) return Snapshot(false);
    }

    private bool IsCurrent()
    {
        try { return _isCurrent(); }
        catch (Exception) { return false; }
    }

    private void Abort()
    {
        _value = _seed;
        _armed = null;
        _restored = true;
        _complete = false;
        _state = "aborted";
    }

    private ClipboardFixtureResult Snapshot(bool includeValue) => new()
    {
        FixtureId = Id,
        State = _state,
        Value = includeValue ? _value : null,
        AcceptedWrites = _writes,
        Restored = _restored,
        Complete = _complete,
    };
}
