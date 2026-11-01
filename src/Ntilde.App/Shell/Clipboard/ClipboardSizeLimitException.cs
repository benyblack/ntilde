namespace Ntilde.Shell.Clipboard;

internal sealed class ClipboardSizeLimitException(string message) : IOException(message);
