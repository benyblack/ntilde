namespace Ntilde.Settings;

/// <summary>A page that can show a sub-page in place of its own content (Appearance's theme editor).</summary>
public interface IHasSubPage
{
    bool IsSubPageOpen { get; }

    /// <summary>
    /// Returns to the page's own content, so a deep link or search result into it can be shown.
    /// </summary>
    /// <remarks>
    /// A page may refuse - leaving would throw away unsaved work - and then stays on its sub-page
    /// and says why. Callers check <see cref="IsSubPageOpen"/> afterwards.
    /// </remarks>
    void CloseSubPage();
}
