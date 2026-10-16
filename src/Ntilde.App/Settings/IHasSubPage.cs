using Avalonia.Input;

namespace Ntilde.Settings;

/// <summary>A page that can show a sub-page in place of its own content (Appearance's theme editor).</summary>
public interface IHasSubPage
{
    bool IsSubPageOpen { get; }

    /// <summary>
    /// Returns to the page's own content, so a deep link or search result into it can be shown.
    /// </summary>
    /// <param name="focusWith">
    /// How a search jump moves focus, or null for a deep link, which leaves focus where it is. A page that
    /// refuses moves focus into its sub-page this way, to where the user can act on the reason.
    /// </param>
    /// <remarks>
    /// A page may refuse - leaving would throw away unsaved work - and then stays on its sub-page
    /// and says why. Callers check <see cref="IsSubPageOpen"/> afterwards.
    /// </remarks>
    void CloseSubPage(NavigationMethod? focusWith);
}
