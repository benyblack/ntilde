namespace Ntilde.Shell
{
    /// <summary>
    /// How a titled OSC 777 / OSC 99 notification becomes the in-app toast. Pure so the mapping is
    /// unit-tested; OSC 9 (text only) keeps the pane's base tab title as the toast title.
    /// </summary>
    public static class OscNotificationToast
    {
        /// <summary>
        /// A title and a body show as-is. A body alone, or a title alone, is the message under the pane's
        /// own title, exactly how a plain OSC 9 text is shown.
        /// </summary>
        public static (string Title, string Message) Resolve(string paneTitle, string title, string body)
        {
            if (title.Length > 0 && body.Length > 0) return (title, body);
            return (paneTitle, title.Length > 0 ? title : body);
        }
    }
}
