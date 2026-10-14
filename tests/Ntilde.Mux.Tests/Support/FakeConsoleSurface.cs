using System.Collections.Concurrent;
using System.Text;
using Ntilde.Mux.TextClient;
using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.Mux.Tests.Support;

/// <summary>A scripted terminal: records output, feeds typed input, and tracks raw mode for the restore assertions.</summary>
internal sealed class FakeConsoleSurface : IConsoleSurface
{
    private readonly object _gate = new();
    private readonly StringBuilder _output = new();
    private readonly BlockingCollection<string> _input = new();
    private string _pending = string.Empty;
    private int _writes;

    public FakeConsoleSurface(int cols = 80, int rows = 24) => Size = (cols, rows);

    public (int Cols, int Rows) Size { get; private set; }
    public bool IsRaw { get { lock (_gate) return _raw; } }
    public int EnterRawCount { get { lock (_gate) return _enterRaw; } }
    public int RestoreCount { get { lock (_gate) return _restore; } }

    /// <summary>1-based: that <see cref="Write"/> call throws <see cref="WriteFailure"/> (0 = never).</summary>
    public int ThrowOnWriteNumber { get; set; }

    /// <summary>What the scripted <see cref="Write"/> failure throws; an <see cref="IOException"/> by default.</summary>
    public Func<Exception> WriteFailure { get; set; } = () => new IOException("scripted console failure");

    /// <summary>When set, every <see cref="Read"/> throws it.</summary>
    public Func<Exception>? ReadFailure { get; set; }

    /// <summary>Runs on the writing thread before each <see cref="Write"/> is recorded, outside the lock (it may block).</summary>
    public Action<string>? BeforeWrite { get; set; }

    public string Output { get { lock (_gate) return _output.ToString(); } }

    public event Action? Resized;

    private bool _raw;
    private int _enterRaw;
    private int _restore;

    public void EnterRawMode()
    {
        lock (_gate)
        {
            _raw = true;
            _enterRaw++;
        }
    }

    public void RestoreMode()
    {
        lock (_gate)
        {
            if (_raw) _restore++;
            _raw = false;
        }
    }

    public void Write(string text)
    {
        BeforeWrite?.Invoke(text);
        lock (_gate)
        {
            _writes++;
            if (ThrowOnWriteNumber > 0 && _writes == ThrowOnWriteNumber) throw WriteFailure();
            _output.Append(text);
        }
    }

    public int Read(char[] buffer)
    {
        if (ReadFailure is { } failure) throw failure();
        if (_pending.Length == 0)
        {
            try
            {
                if (!_input.TryTake(out string? next, Timeout.Infinite)) return 0;
                _pending = next;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
        }

        int n = Math.Min(buffer.Length, _pending.Length);
        _pending.CopyTo(0, buffer, 0, n);
        _pending = _pending[n..];
        return n;
    }

    public void Type(string text) => _input.Add(text);

    public void Resize(int cols, int rows)
    {
        Size = (cols, rows);
        Resized?.Invoke();
    }

    /// <summary>The screen an outer terminal would show after everything written so far.</summary>
    public string ScreenText()
    {
        var outer = new TerminalBuffer(Size.Cols, Size.Rows);
        new AnsiParser(outer, forceConPtyFiltering: false) { ImageDecoder = null }.Process(Output);
        return string.Join('\n', TerminalExporter.GetVisibleRowTexts(outer));
    }

    public void Dispose() => _input.CompleteAdding();
}
