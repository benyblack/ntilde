namespace Ntilde.Mux.Tests.Support;

internal static class ParserFaultInjection
{
    /// <summary>
    /// Makes the session's parser throw from inside <c>Process</c> on its next device reply (e.g.
    /// after a DA query <c>ESC [ c</c>): to the parse thread that is a parser failure, and the
    /// session faults. Installed on the parse thread, which is the only thread that reads it.
    /// </summary>
    /// <remarks>
    /// The fake's ThrowOnSendInput used to serve as this trigger, but device replies now go to the
    /// session's input writer thread, so a throwing child SendInput no longer reaches the parser.
    /// </remarks>
    public static Task MakeParserThrowOnReplyAsync(this HeadlessTerminalSession mux) =>
        mux.InvokeAsync(() =>
        {
            mux.Parser.OnResponse = _ => throw new InvalidOperationException("injected parser callback failure");
            return 0;
        });
}
