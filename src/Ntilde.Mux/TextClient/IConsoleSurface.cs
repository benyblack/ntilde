namespace Ntilde.Mux.TextClient;

/// <summary>The terminal the text client draws on (spec §6.1). Real ones: Windows and Unix surfaces; tests: a fake.</summary>
public interface IConsoleSurface : IDisposable
{
    /// <summary>The visible grid, cols x rows.</summary>
    (int Cols, int Rows) Size { get; }

    /// <summary>Raw input (no line editing, echo or signal keys) and VT output.</summary>
    void EnterRawMode();

    /// <summary>Back to the modes found before <see cref="EnterRawMode"/>. Idempotent; safe from any thread.</summary>
    void RestoreMode();

    /// <summary>Writes the text as UTF-8, whatever the console's code page: the status line is not ASCII.</summary>
    void Write(string text);

    /// <summary>Blocks for input; returns the chars read, 0 when input is closed.</summary>
    int Read(char[] buffer);

    /// <summary>The grid changed (SIGWINCH, or a poll on Windows). Any thread.</summary>
    event Action? Resized;
}

/// <summary>Stdin/stdout is not an interactive terminal (a pipe, a redirect, no console).</summary>
public sealed class ConsoleUnavailableException : Exception
{
    public ConsoleUnavailableException() : this("An interactive terminal is required.") { }
    public ConsoleUnavailableException(string message) : base(message) { }
    public ConsoleUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
