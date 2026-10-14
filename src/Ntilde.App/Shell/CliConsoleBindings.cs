using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Ntilde;

internal static partial class CliConsoleBindings
{
    public static void Prepare()
    {
        if (OperatingSystem.IsWindows())
        {
            TryAttachParentConsole();
        }

        RebindOutputStream(Console.OpenStandardOutput, Console.SetOut);
        RebindOutputStream(Console.OpenStandardError, Console.SetError);
    }

    /// <summary>
    /// `mux attach` (spec §6.7): a console to draw on - the parent's when it has one, otherwise (a
    /// launch from Explorer) a new one. The text client opens CONIN$/CONOUT$ itself.
    /// </summary>
    /// <returns>True when this process attached to its parent's console (Windows): the keyboard may be shared.</returns>
    public static bool PrepareInteractive()
    {
        bool attachedToParent = false;
        if (OperatingSystem.IsWindows())
        {
            const int AttachParentProcess = -1;
            attachedToParent = AttachConsole(AttachParentProcess);
            if (!attachedToParent) _ = AllocConsole();
        }

        RebindOutputStream(Console.OpenStandardOutput, Console.SetOut);
        RebindOutputStream(Console.OpenStandardError, Console.SetError);
        return attachedToParent;
    }

    private static void RebindOutputStream(Func<Stream> streamFactory, Action<TextWriter> setter)
    {
        try
        {
            Stream stream = streamFactory();
            if (ReferenceEquals(stream, Stream.Null))
            {
                return;
            }

            setter(new StreamWriter(stream) { AutoFlush = true });
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    private static void TryAttachParentConsole()
    {
        const int AttachParentProcess = -1;
        _ = AttachConsole(AttachParentProcess);
    }
}
