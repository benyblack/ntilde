using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.CommandAssist.Domain;
using Ntilde.Controls;
using Ntilde.Services.Ssh;
using Ntilde.Settings;
using Ntilde.Settings.Pages;
using Ntilde.Shell;
using Ntilde.VT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ntilde
{
    /// <summary>
    /// The Settings frame. It holds a sidebar built from <see cref="SettingsPageRegistry"/>, every
    /// page for the window's lifetime, and the Save / Cancel footer. Pages own their controls behind
    /// <see cref="ISettingsPage"/> and share one <see cref="SettingsSession"/>.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly SettingsSession _session;
        private readonly IReadOnlyList<ISettingsPage> _pages;
        private readonly Dictionary<SettingsPage, ListBoxItem> _navItems = new();
        private readonly ListBox _sideNav;
        private readonly Button _btnSave;
        private SettingsPage _currentPage;
        private bool _syncingNav;
        private TextBox _searchBox = null!;
        private ListBox _searchResults = null!;
        private Control _searchView = null!;
        private Panel _pageHost = null!;
        private SettingsSearchIndex _searchIndex = new([]);

        public TerminalSettings Settings => _session.Settings; // Expose for main window to grab without reloading disk

        /// <summary>
        /// The session persistence setting as this window last loaded it - when it opened, or after an Import/Restore
        /// reloaded settings.json (spec R1): saving a different one forgets the first-close dialog's remembered answer.
        /// </summary>
        private string? _sessionPersistenceLoaded;

        /// <summary>
        /// R1: when the effective persistence mode changed from <paramref name="before"/> to <paramref name="after"/>,
        /// the first-close answer is forgotten, so turning persistence back on asks again. A spelling that does not
        /// change the mode (a typo read as Off, saved as "Off") is not a change.
        /// </summary>
        private static void ForgetCloseChoiceIfPersistenceChanged(string? before, string? after)
        {
            if (Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(before) != Ntilde.Shell.Mux.SessionPersistenceMode.IsKeepOnClose(after))
            {
                Ntilde.Shell.Mux.MuxCloseChoiceStore.Default.Forget();
            }
        }

        /// <summary>
        /// F1: set once a successful Import or Restore has replaced configuration on disk out from
        /// under this window (see <see cref="ReloadSettingsAfterExternalChangeAsync"/>). The owning
        /// <c>MainWindow.OpenSettings</c> must adopt the reloaded <see cref="Settings"/> regardless
        /// of how this dialog eventually closes - Save, Cancel, or the window's X - because closing
        /// any other way than Save previously left <c>MainWindow._settings</c> pointing at the
        /// stale PRE-import object. Any later ordinary <c>_settings.Save()</c> from MainWindow (e.g.
        /// the "Font: Increase" palette command) would then silently overwrite the just-imported
        /// configuration on disk.
        /// </summary>
        internal bool ConfigurationReplacedExternally { get; private set; }

        internal SettingsSession Session => _session;

        /// <summary>The page on screen. MainWindow remembers it so Ctrl+, reopens where the user left off.</summary>
        internal SettingsPage CurrentPage => _currentPage;

        internal SettingsSearchIndex SearchIndex => _searchIndex;

        public event Action<double>? OnOpacityChanged;
        public event Action<string>? OnBlurChanged;
        public event Action<string, double, string>? OnBgImageChanged;
        public event Action<string>? OnFontChanged;
        public event Action<double>? OnFontSizeChanged;
        /// <summary>Live preview for Appearance > Window > Interface scale; the value is already clamped.</summary>
        public event Action<double>? OnUiScaleChanged;
        public event Action<string>? OnThemeChanged;

        /// <summary>
        /// The live Command Assist history store, injected by <c>MainWindow.OpenSettings</c> so the
        /// "Clear history" button acts on the same instance the panes append to.
        /// </summary>
        /// <remarks>
        /// Null for a window opened outside that path (a test, the parameterless constructor XAML
        /// tooling uses), in which case the row says so rather than building a second store over the
        /// same file.
        /// </remarks>
        internal IHistoryStore? CommandAssistHistoryStore
        {
            get => _session.HistoryStore;
            set => _session.HistoryStore = value;
        }

        /// <summary>
        /// Raised after command history has actually been cleared, so the host can refresh anything that
        /// was showing the rows that just went away.
        /// </summary>
        /// <remarks>
        /// An event rather than a direct call because this window cannot see panes, and it fires only on
        /// the success path - a failed clear leaves the surfaces alone, since they are still accurate.
        /// </remarks>
        internal event Action? OnCommandAssistHistoryCleared;

        /// <summary>
        /// The live Command Assist snippet store, injected by <c>MainWindow.OpenSettings</c> for the
        /// same reason <see cref="CommandAssistHistoryStore"/> is (V2 Phase 4b).
        /// </summary>
        /// <remarks>
        /// Null for a window opened outside that path, in which case the snippet section says so
        /// rather than building a second store over the same file.
        /// </remarks>
        internal ISnippetStore? CommandAssistSnippetStore
        {
            get => _session.SnippetStore;
            set => _session.SnippetStore = value;
        }

        /// <summary>
        /// Raised after a snippet was added, edited or deleted, so the host can refresh any assist
        /// surface still showing the old rows.
        /// </summary>
        internal event Action? OnCommandAssistSnippetsChanged;

        /// <summary>Test seam for the restore confirmation; see <see cref="BackupPage.RestoreConfirmationOverride"/>.</summary>
        internal Func<BackupPage.SnapshotRow, Task<bool>>? RestoreConfirmationOverride
        {
            get => GetPage<BackupPage>().RestoreConfirmationOverride;
            set => GetPage<BackupPage>().RestoreConfirmationOverride = value;
        }

        /// <summary>
        /// Whether the owning window has session persistence on: the "Quit and close all shells…" link is shown only
        /// then. Set by <c>MainWindow.OpenSettings</c>; false for a window opened any other way.
        /// </summary>
        internal bool SessionPersistenceActive
        {
            get => GetPage<GeneralPage>().QuitLinkVisible;
            set => GetPage<GeneralPage>().QuitLinkVisible = value;
        }

        /// <summary>
        /// True once the "Quit and close all shells…" link was clicked; the window then closes without
        /// saving, and the owner runs the quit once the dialog is gone.
        /// </summary>
        internal bool QuitAndCloseAllRequested { get; private set; }

        public SettingsWindow() : this(new SettingsTarget(SettingsPageRegistry.DefaultPage)) { }

        public SettingsWindow(SettingsTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            InitializeComponent();
            // Hold this window at the interface scale it opened with, then size it for that scale
            // (its 880x620 layout keeps 880x620 of logical room - pinning alone shrank it to 440
            // DIPs at 200% and clipped the slider). A held window ignores later UiScale.Apply
            // calls: without that the Interface scale slider rescaled the very window it lives in
            // and the thumb ran away from the pointer. Every other window previews live; this one
            // adopts the new scale the next time it opens. The hold is the ceiling: a screen that
            // cannot fit the window still reduces below it, and FitWindow re-checks on Opened
            // against the screen the window actually landed on.
            UiScale.PinScale(this, UiScale.Current);
            UiScale.FitWindow(this);

            var settings = TerminalSettings.Load();
            if (new SshLegacyProfileMigrationService().MigrateLegacyProfiles(settings))
            {
                settings.Save();
            }

            _session = new SettingsSession(settings);
            _sessionPersistenceLoaded = _session.Settings.SessionPersistence;
            _session.SetExternalReloadHandler(ReloadSettingsAfterExternalChangeAsync);
            ApplyTheme();

            _sideNav = this.FindControl<ListBox>("SideNav")!;
            _btnSave = this.FindControl<Button>("BtnSave")!;

            // Every page joins the window now and stays: Save collects from all of them, and a page
            // that is not on screen still needs the window's resources for the rows it builds in code.
            var host = this.FindControl<Panel>("PageHost")!;
            _pages = SettingsPageRegistry.Pages.Select(descriptor => descriptor.Create()).ToList();
            foreach (ISettingsPage page in _pages)
            {
                page.View.IsVisible = false;
                host.Children.Add(page.View);
            }

            foreach (ISettingsPage page in _pages)
            {
                page.Load(_session);
            }

            // After the first Load on purpose: loading sets every control, and those changes are
            // not previews. The cost is that the first Load's repaint request is dropped (the sink
            // has no subscriber yet); the ApplyTheme() above already painted the active theme.
            // (MainWindow subscribes after construction, so its previews are unaffected.)
            WireSessionEvents();
            BuildSidebar();
            WireSearch();

            // Tunnel, so the chord works from any focused field; ShortcutsPage's editors are the
            // one place it must not fire (the user may be recording Ctrl+S as a binding).
            AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
            _sideNav.AddHandler(KeyDownEvent, OnSideNavKeyDown, RoutingStrategies.Tunnel);

            _btnSave.Click += (_, _) => SaveAndClose();
            this.FindControl<Button>("BtnCancel")!.Click += (_, _) => Close();
            GetPage<GeneralPage>().QuitAndCloseAllRequested += () =>
            {
                QuitAndCloseAllRequested = true;
                Close(false); // nothing is saved
            };

            // The snippet store arrives by property assignment after construction, so the list is
            // read once the window opens (the old WireCommandAssistSnippetsRow did the same).
            Opened += async (_, _) => await GetPage<CommandAssistPage>().ReloadSnippetsAsync();

            // A page that owns resources (timers, subscriptions) implements IDisposable and is released with the window.
            Closed += (_, _) =>
            {
                foreach (ISettingsPage page in _pages)
                {
                    (page as IDisposable)?.Dispose();
                }
            };

            NavigateTo(target);
        }

        internal T GetPage<T>() where T : class, ISettingsPage => _pages.OfType<T>().Single();

        internal ISettingsPage GetPage(SettingsPage page) => _pages.Single(candidate => candidate.Page == page);

        internal ListBoxItem NavItem(SettingsPage page) => _navItems[page];

        /// <summary>
        /// Shows <paramref name="target"/>'s page and, when it names a row, scrolls to the row and
        /// highlights it. Deep links, validation and search all come through here.
        /// </summary>
        internal void NavigateTo(SettingsTarget target) => NavigateTo(target, focusWith: null, expandDetails: false);

        /// <param name="target">The page, and the row to reveal on it.</param>
        /// <param name="focusWith">
        /// For a search jump, how the row's control takes focus once it is shown (Tab draws the focus
        /// ring). Null leaves focus where it is, as a deep link does.
        /// </param>
        /// <param name="expandDetails">Opens the row's "More" text, where a search matched.</param>
        private void NavigateTo(SettingsTarget target, NavigationMethod? focusWith, bool expandDetails)
        {
            ArgumentNullException.ThrowIfNull(target);
            ShowPage(target.Page);
            if (target.RowId is not { } rowId)
            {
                return;
            }

            // BringIntoView needs real layout. A window that is not open yet has none, so wait for
            // Opened; an open one only needs the pass this page switch just scheduled.
            if (IsVisible)
            {
                Dispatcher.UIThread.Post(() => RevealRow(target.Page, rowId, focusWith, expandDetails), DispatcherPriority.Loaded);
                return;
            }

            void RevealOnceOpened(object? sender, EventArgs e)
            {
                Opened -= RevealOnceOpened;
                Dispatcher.UIThread.Post(() => RevealRow(target.Page, rowId, focusWith, expandDetails), DispatcherPriority.Loaded);
            }

            Opened += RevealOnceOpened;
        }

        /// <summary>
        /// Scrolls the row carrying <paramref name="rowId"/> into view and highlights it; a search
        /// jump also opens its details and focuses its control (see NavigateTo's parameters). False
        /// when it is not on screen.
        /// </summary>
        internal bool RevealRow(SettingsPage page, string rowId, NavigationMethod? focusWith = null, bool expandDetails = false)
        {
            if (page != _currentPage)
            {
                return false; // the user moved on before layout ran
            }

            // A row on a page whose sub-page is showing has no layout yet. Close the sub-page and
            // reveal after the layout pass that brings the page's own content back.
            if (GetPage(page) is IHasSubPage { IsSubPageOpen: true } host)
            {
                host.CloseSubPage();
                if (host.IsSubPageOpen)
                {
                    return false; // the page refused (it says why); reposting would loop forever
                }

                Dispatcher.UIThread.Post(() => RevealRow(page, rowId, focusWith, expandDetails), DispatcherPriority.Loaded);
                return true;
            }

            Control? row = GetPage(page).View.GetLogicalDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => string.Equals(SettingsNavigation.GetRowId(control), rowId, StringComparison.Ordinal));
            if (row is null)
            {
                return false;
            }

            // The details make the row taller: reveal it after the layout pass that measures them.
            if (expandDetails && row is SettingRow { IsExpanded: false } collapsed)
            {
                collapsed.IsExpanded = true;
                Dispatcher.UIThread.Post(() => RevealRow(page, rowId, focusWith), DispatcherPriority.Loaded);
                return true;
            }

            row.BringIntoView();
            SettingsNavigation.Flash(row);
            if (focusWith is { } method)
            {
                FirstFocusableIn(row)?.Focus(method);
            }

            return true;
        }

        /// <summary>
        /// The first control in <paramref name="row"/>'s content that Tab could reach, or null. A
        /// SettingRow's own "More" button is not its content, so it is never the one.
        /// </summary>
        private static InputElement? FirstFocusableIn(Control row)
        {
            Control? content = row is ContentControl contentRow ? contentRow.Content as Control : row;
            return content?.GetSelfAndVisualDescendants()
                .OfType<InputElement>()
                .FirstOrDefault(element => element.Focusable
                    && element.IsEffectivelyEnabled
                    && element.IsEffectivelyVisible
                    && KeyboardNavigation.GetIsTabStop(element));
        }

        private void ShowPage(SettingsPage page)
        {
            // Every page change ends a search, or the page would stay hidden behind the results: a
            // sidebar click or arrow, a jump, a deep link, a Save stopped by a page's validation.
            ClearSearch();

            foreach (ISettingsPage candidate in _pages)
            {
                candidate.View.IsVisible = candidate.Page == page;
            }

            _currentPage = page;
            _syncingNav = true;
            try
            {
                _sideNav.SelectedItem = _navItems[page];
            }
            finally
            {
                _syncingNav = false;
            }
        }

        private void BuildSidebar()
        {
            string? group = null;
            foreach (SettingsPageDescriptor descriptor in SettingsPageRegistry.Pages)
            {
                if (!string.Equals(descriptor.Group, group, StringComparison.Ordinal))
                {
                    var header = new ListBoxItem
                    {
                        IsEnabled = false,
                        Content = new TextBlock { Text = descriptor.Group, Classes = { "SectionHeader" } },
                    };
                    header.Classes.Add("NavGroup");
                    if (group is null)
                    {
                        header.Classes.Add("First");
                    }

                    _sideNav.Items.Add(header);
                    group = descriptor.Group;
                }

                var glyph = new TextBlock { Text = descriptor.Glyph, Classes = { "NavGlyph" } };
                if (descriptor.MonoGlyph)
                {
                    glyph.Classes.Add("Mono");
                }

                var title = new TextBlock
                {
                    Text = descriptor.Title,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(title, 1);

                var item = new ListBoxItem
                {
                    Tag = descriptor.Page,
                    Content = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*"), Children = { glyph, title } },
                };
                AutomationProperties.SetName(item, descriptor.Title);
                _navItems[descriptor.Page] = item;
                _sideNav.Items.Add(item);
            }

            _sideNav.SelectionChanged += OnSideNavSelectionChanged;
        }

        private void OnSideNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_syncingNav)
            {
                return;
            }

            if (_sideNav.SelectedItem is ListBoxItem { Tag: SettingsPage page })
            {
                ShowPage(page);
                return;
            }

            // A group header got selected (from code, or a keyboard path that reached it). Headers
            // are never a destination: carry on in the direction of travel to the next page.
            int from = _sideNav.Items.IndexOf(_navItems[_currentPage]);
            int to = _sideNav.SelectedIndex;
            int step = to > from ? 1 : -1;
            for (int i = to; i >= 0 && i < _sideNav.ItemCount; i += step)
            {
                if (_sideNav.Items[i] is ListBoxItem { Tag: SettingsPage next })
                {
                    ShowPage(next);
                    return;
                }
            }

            ShowPage(_currentPage);
        }

        /// <summary>The platform's command modifier: Cmd on macOS, Ctrl elsewhere.</summary>
        internal static KeyModifiers PrimaryModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        private void OnWindowKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyModifiers != PrimaryModifier || IsShortcutCapture(e.Source))
            {
                return;
            }

            if (e.Key == Key.S)
            {
                e.Handled = true;

                // Moving focus first commits an edit still pending in a NumericUpDown or TextBox,
                // exactly as clicking the button would.
                _btnSave.Focus();
                SaveAndClose();
            }
            else if (e.Key == Key.F)
            {
                e.Handled = true;
                _searchBox.Focus();
                _searchBox.SelectAll();
            }
        }

        private static bool IsShortcutCapture(object? source)
            => source is TextBox box && box.Classes.Contains(ShortcutsPage.CaptureClass);

        /// <summary>Up / Down move between pages directly, so a group header is never a stop.</summary>
        private void OnSideNavKeyDown(object? sender, KeyEventArgs e)
        {
            int step = e.Key switch { Key.Down => 1, Key.Up => -1, _ => 0 };
            if (step == 0 || e.KeyModifiers != KeyModifiers.None)
            {
                return;
            }

            e.Handled = true;
            List<SettingsPage> order = SettingsPageRegistry.Pages.Select(descriptor => descriptor.Page).ToList();
            int next = order.IndexOf(_currentPage) + step;
            if (next < 0 || next >= order.Count)
            {
                return;
            }

            ShowPage(order[next]);
            _navItems[order[next]].Focus(NavigationMethod.Directional);
        }

        private void WireSearch()
        {
            _searchBox = this.FindControl<TextBox>("SettingsSearchBox")!;
            _searchResults = this.FindControl<ListBox>("SearchResultsList")!;
            _searchView = this.FindControl<Control>("SearchResultsView")!;
            _pageHost = this.FindControl<Panel>("PageHost")!;

            _searchResults.ItemTemplate = new FuncDataTemplate<SettingsSearchResult>(
                (result, _) => result is null ? new TextBlock() : CreateResultView(result));
            _searchBox.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty)
                {
                    UpdateSearch();
                }
            };
            _searchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
            _searchResults.Tapped += (_, e) =>
            {
                // Only a tap on a result jumps, and to that result: the list fills the content area
                // (a tap on its empty space or scroll bar is not a choice), and a Ctrl/Cmd+click
                // toggles the selection off the very result it lands on.
                if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { Content: SettingsSearchResult tapped })
                {
                    JumpTo(tapped, NavigationMethod.Pointer);
                }
            };
            _searchResults.AddHandler(KeyDownEvent, OnResultsKeyDown, RoutingStrategies.Tunnel);

            // The container's content is the record itself, so without a name a screen reader
            // reads the record's ToString().
            _searchResults.ContainerPrepared += (_, e) =>
            {
                if (_searchResults.Items[e.Index] is SettingsSearchResult result)
                {
                    AutomationProperties.SetName(e.Container, $"{result.Entry.Title} — {ResultLocation(result.Entry)}");
                    AutomationProperties.SetHelpText(e.Container, result.Entry.Description);
                }
            };

            // A click on the page already selected changes no selection, so ShowPage never runs;
            // the click itself ends the search.
            _sideNav.Tapped += (_, e) =>
            {
                if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { Tag: SettingsPage })
                {
                    ClearSearch();
                }
            };

            RebuildSearchIndex();

            // Command Assist rewrites its descriptions when a chord is rebound (its handler runs
            // first, having subscribed in Load), so the index re-reads the rows after it.
            _session.ShortcutsChanged += RebuildSearchIndex;
        }

        private void RebuildSearchIndex()
            => _searchIndex = SettingsSearchIndex.Build(_pages.Select(page => (SettingsPageRegistry.Get(page.Page), page.View)));

        private void UpdateSearch()
        {
            string query = (_searchBox.Text ?? string.Empty).Trim();
            bool searching = query.Length > 0;
            _pageHost.IsVisible = !searching;
            _searchView.IsVisible = searching;
            if (!searching)
            {
                _searchResults.ItemsSource = null;
                return;
            }

            IReadOnlyList<SettingsSearchResult> results = _searchIndex.Search(query);
            _searchResults.ItemsSource = results;
            _searchResults.SelectedIndex = results.Count > 0 ? 0 : -1;
            _searchResults.IsVisible = results.Count > 0;
            if (results.Count > 0)
            {
                // The list keeps its scroll offset across new items; the selected first result must show.
                _searchResults.ScrollIntoView(0);
            }

            var heading = this.FindControl<TextBlock>("SearchResultsHeading")!;
            heading.IsVisible = results.Count > 0;
            heading.Text = results.Count == 1 ? $"1 result for “{query}”" : $"{results.Count} results for “{query}”";

            var none = this.FindControl<TextBlock>("SearchNoResults")!;
            none.IsVisible = results.Count == 0;
            none.Text = $"No settings match “{query}”.";
        }

        private void OnSearchKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveResultSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveResultSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    JumpTo(_searchResults.SelectedItem as SettingsSearchResult, NavigationMethod.Tab);
                    e.Handled = true;
                    break;
                case Key.Escape when !string.IsNullOrEmpty(_searchBox.Text):
                    ClearSearch(); // the page that was showing comes back
                    e.Handled = true;
                    break;
            }
        }

        private void OnResultsKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    JumpTo(_searchResults.SelectedItem as SettingsSearchResult, NavigationMethod.Tab);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    // As in the box: the page comes back, and focus returns to the box (the focused
                    // result goes away with the results).
                    _searchBox.Focus();
                    ClearSearch();
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>Empties the search box, which puts the current page back in place of the results.</summary>
        private void ClearSearch()
        {
            // Null only while the constructor has not reached WireSearch yet.
            if (_searchBox is { Text.Length: > 0 })
            {
                _searchBox.Text = string.Empty;
            }
        }

        private void MoveResultSelection(int step)
        {
            if (!_searchResults.IsVisible || _searchResults.ItemCount == 0)
            {
                return;
            }

            _searchResults.SelectedIndex = Math.Clamp(_searchResults.SelectedIndex + step, 0, _searchResults.ItemCount - 1);
            _searchResults.ScrollIntoView(_searchResults.SelectedIndex);
        }

        /// <summary>
        /// Jumps to <paramref name="result"/>: its page, its row (details opened when only they
        /// matched), and focus on the row's control, drawn as <paramref name="focusWith"/> says.
        /// </summary>
        private void JumpTo(SettingsSearchResult? result, NavigationMethod focusWith)
        {
            if (result is null)
            {
                return;
            }

            // Focus parks in the box first: a clicked result's container goes away with the
            // results, and a page result has no row to take it.
            _searchBox.Focus();
            var target = new SettingsTarget(result.Entry.Page, result.Entry.RowId);
            NavigateTo(target, focusWith, result.MatchedOnlyInDetails); // ShowPage clears the search
        }

        /// <summary>The acronyms Settings copy uses: the words a group header keeps upper-case when a result writes it in sentence case.</summary>
        private static readonly HashSet<string> GroupHeaderAcronyms = new(StringComparer.Ordinal) { "SSH", "OSC" };

        /// <summary>
        /// A result's first line: "Page" for a page itself (it has no row), else the page, then the
        /// row's group in sentence case - "Appearance › Background image".
        /// </summary>
        internal static string ResultLocation(SettingsSearchEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.RowId is null)
            {
                return "Page";
            }

            return entry.Group is null ? entry.PageTitle : $"{entry.PageTitle} › {SentenceCase(entry.Group)}";
        }

        /// <summary>A group header ("BACKGROUND IMAGE") in sentence case, its acronyms kept upper-case.</summary>
        private static string SentenceCase(string header)
        {
            string[] words = header.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                string letters = new(words[i].Where(char.IsLetter).ToArray());
                if (!GroupHeaderAcronyms.Contains(letters))
                {
                    words[i] = words[i].ToLowerInvariant();
                }
            }

            string text = string.Join(' ', words);
            return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        }

        private Control CreateResultView(SettingsSearchResult result)
        {
            SettingsSearchEntry entry = result.Entry;
            var panel = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = ResultLocation(entry),
                        Classes = { "RowDesc" },
                    },
                    // The match is emphasised by weight in the title's own colour: accent-coloured
                    // text was hard to read on the selected result's accent background.
                    new MatchHighlightTextBlock
                    {
                        SourceText = entry.Title,
                        HighlightStart = result.TitleMatchStart,
                        HighlightLength = result.TitleMatchLength,
                        HighlightFontWeight = FontWeight.Bold,
                        Foreground = this.FindPaletteBrush("NtFg"),
                        FontSize = 13,
                    },
                },
            };

            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = entry.Description,
                    Classes = { "RowDesc" },
                    MaxLines = 2,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }

            return panel;
        }

        private void WireSessionEvents()
        {
            SettingsPreviewSink preview = _session.Preview;
            preview.OpacityChanged += value => OnOpacityChanged?.Invoke(value);
            preview.BlurChanged += value => OnBlurChanged?.Invoke(value);
            preview.BackgroundImageChanged += (path, opacity, stretch) => OnBgImageChanged?.Invoke(path, opacity, stretch);
            preview.FontChanged += family => OnFontChanged?.Invoke(family);
            preview.FontSizeChanged += size => OnFontSizeChanged?.Invoke(size);
            preview.UiScaleChanged += scale => OnUiScaleChanged?.Invoke(scale);
            preview.ThemeChanged += name => OnThemeChanged?.Invoke(name);
            preview.RepaintRequested += theme => ApplyTheme(theme);
            _session.HistoryCleared += () => OnCommandAssistHistoryCleared?.Invoke();
            _session.SnippetsChanged += () => OnCommandAssistSnippetsChanged?.Invoke();
        }

        /// <summary>
        /// Validates every page before anything is written, so a refused Save leaves the in-memory
        /// settings untouched (the old window wrote every field first and validated shortcuts last).
        /// </summary>
        private void SaveAndClose()
        {
            foreach (ISettingsPage page in _pages)
            {
                if (page.Validate() is { } error)
                {
                    NavigateTo(new SettingsTarget(error.Page, error.RowId));
                    return;
                }
            }

            TerminalSettings target = _session.Settings;
            foreach (ISettingsPage page in _pages)
            {
                page.Collect(target);
            }

            _session.CommitDrafts(target);
            target.Save();
            // R1: a changed persistence setting forgets the first-close answer; turned back on, the next close asks again.
            ForgetCloseChoiceIfPersistenceChanged(_sessionPersistenceLoaded, target.SessionPersistence);

            Close(true); // Return true to indicate saved
        }

        /// <summary>
        /// I1: a successful Import or Restore changes settings.json (and possibly
        /// profiles/keybindings/title-bar layout) on disk out from under this already-open
        /// window. The session's settings, and the state each page derived from them (the profile
        /// list, the shortcut and title bar drafts), are now a stale pre-change snapshot; if
        /// SaveAndClose ran against that snapshot afterward, it would silently revert the very
        /// change the user just made. So this reloads the session, then runs every page's Load in
        /// registry order, which re-derives each page's own state from the fresh settings.
        /// <para>
        /// Asynchronous solely because of the trailing snippet reload (see the comment at the end
        /// of the body); everything else here is synchronous and completes before the first
        /// suspension point. Callers must await it - both the Import and Restore click handlers
        /// already do - so the whole window, snippet rows included, is consistent with disk before
        /// the success status is shown.
        /// </para>
        /// </summary>
        internal async Task ReloadSettingsAfterExternalChangeAsync()
        {
            // F1: record that configuration was replaced externally so MainWindow.OpenSettings can
            // adopt the reload no matter how this dialog eventually closes.
            ConfigurationReplacedExternally = true;

            _session.Reload(TerminalSettings.Load());
            // R1: the main window adopts these settings whatever this dialog does next (F1), so a changed persistence
            // mode forgets the first-close answer here, as a Save would.
            ForgetCloseChoiceIfPersistenceChanged(_sessionPersistenceLoaded, _session.Settings.SessionPersistence);
            _sessionPersistenceLoaded = _session.Settings.SessionPersistence;

            foreach (ISettingsPage page in _pages)
            {
                page.Load(_session);
            }

            RebuildSearchIndex();

            // The one reload a page's Load does not cover, and the reason this method is async at
            // all. CommandAssistPage.ReloadSnippetsAsync is driven by the frame's one-shot `Opened`
            // handler rather than by Load, because the snippet store arrives by property assignment
            // after the constructor has run. This method runs on an ALREADY-OPEN window and never
            // reopens it, so `Opened` does not fire again: without this call, an import/restore
            // whose bundle carries a different command-assist/snippets.json (which BackupService
            // replaces wholesale in BOTH modes) leaves the visible rows describing the pre-import
            // file until the window is closed and reopened - and the user can edit or delete
            // against them.
            //
            // Awaited last, after every synchronous repopulation above, so the parts of this method
            // that cannot fail on I/O are already done before the first suspension point. Null-safe
            // for a window opened outside MainWindow.OpenSettings: the page says the snippets are
            // unavailable when no store is set.
            await GetPage<CommandAssistPage>().ReloadSnippetsAsync();

            // Codex review (PR #364, P2): refreshing this window's panel is only half of what a
            // snippet change owes the app. MainWindow.OpenSettings subscribes
            // DismissCommandAssistSurfaces to this event, and the add/edit/delete paths all raise it,
            // because the rows an open assist bubble or popup is showing are a snapshot of a ranking
            // pass that nothing else invalidates (see DismissCommandAssistSurfaces' own remarks,
            // written for the identical "Clear history" case). An import/restore is the most
            // destructive snippet change there is - snippets.json is replaced wholesale in BOTH modes
            // - so without this a popup left open behind the dialog goes on displaying, and
            // accepting, snippets the import just deleted.
            //
            // Raised unconditionally rather than only when the bundle actually carried Snippets, for
            // the same reason ConfigurationReplacedExternally is set unconditionally above: dismissing
            // a surface that did not need it costs the user nothing (the next keystroke rebuilds it
            // from the store), while missing one shows them rows that no longer exist.
            _session.RaiseSnippetsChanged();
        }

        private void ApplyTheme(TerminalTheme? theme = null)
        {
            theme ??= _session.Settings.ActiveTheme;

            var contrastColor = theme.GetContrastForeground();
            ThemePaletteResources.Apply(Resources, theme);

            Background = new SolidColorBrush(theme.Background.ToAvaloniaColor());
            Foreground = new SolidColorBrush(contrastColor.ToAvaloniaColor());

            // Set the window theme variant so standard controls (ComboBox, ScrollBar, etc.) adapt
            RequestedThemeVariant = contrastColor == TermColor.Black ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }
}
