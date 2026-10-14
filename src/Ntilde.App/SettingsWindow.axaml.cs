using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Ntilde.CommandAssist.Domain;
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

        public TerminalSettings Settings => _session.Settings; // Expose for main window to grab without reloading disk

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
            // not previews. (MainWindow subscribes after construction, so only the window's own
            // repaint is affected; it runs once, below, as ApplyTheme already did above.)
            WireSessionEvents();
            BuildSidebar();

            _btnSave.Click += (_, _) => SaveAndClose();
            this.FindControl<Button>("BtnCancel")!.Click += (_, _) => Close();

            // The snippet store arrives by property assignment after construction, so the list is
            // read once the window opens (the old WireCommandAssistSnippetsRow did the same).
            Opened += async (_, _) => await GetPage<CommandAssistPage>().ReloadSnippetsAsync();
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
        internal void NavigateTo(SettingsTarget target)
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
                Dispatcher.UIThread.Post(() => RevealRow(target.Page, rowId), DispatcherPriority.Loaded);
                return;
            }

            void RevealOnceOpened(object? sender, EventArgs e)
            {
                Opened -= RevealOnceOpened;
                Dispatcher.UIThread.Post(() => RevealRow(target.Page, rowId), DispatcherPriority.Loaded);
            }

            Opened += RevealOnceOpened;
        }

        /// <summary>Scrolls the row carrying <paramref name="rowId"/> into view and highlights it. False when it is not on screen.</summary>
        internal bool RevealRow(SettingsPage page, string rowId)
        {
            if (page != _currentPage)
            {
                return false; // the user moved on before layout ran
            }

            Control? row = GetPage(page).View.GetLogicalDescendants()
                .OfType<Control>()
                .FirstOrDefault(control => string.Equals(SettingsNavigation.GetRowId(control), rowId, StringComparison.Ordinal));
            if (row is null)
            {
                return false;
            }

            row.BringIntoView();
            SettingsNavigation.Flash(row);
            return true;
        }

        private void ShowPage(SettingsPage page)
        {
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
            Close(true); // Return true to indicate saved
        }

        /// <summary>
        /// I1: a successful Import or Restore changes settings.json (and possibly
        /// profiles/keybindings/title-bar layout) on disk out from under this already-open
        /// window. <c>_settings</c> and everything the constructor derived from it once, up
        /// front, are now a stale pre-change snapshot; if SaveAndClose ran against
        /// that snapshot afterward, it would silently revert the very change the user just made
        /// (the bug this fix closes). Reloading <c>_settings</c> alone is not enough -
        /// SaveAndClose also rebuilds <c>_settings.Profiles</c> from
        /// <c>_profilesList</c>, <c>_settings.Keybindings</c> from <c>_shortcutDraftBindings</c>,
        /// and <c>_settings.TitleBarItems</c> / <c>_settings.TitleBarOrder</c> from
        /// <c>_titleBarDraft</c> — each of those must be re-derived from the freshly reloaded
        /// settings too, or Save would still overwrite the imported/restored values for exactly
        /// those three areas even though every OTHER field would now be correctly preserved.
        /// Reloads the session, then runs every page's Load in registry order.
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
            foreach (ISettingsPage page in _pages)
            {
                page.Load(_session);
            }

            // The one populate path that is NOT reachable by repeating the constructor's sequence,
            // and the reason this method is async at all. PopulateCommandAssistSnippetsPanel is
            // driven by WireCommandAssistSnippetsRow's one-shot `Opened` handler rather than by the
            // constructor, because the snippet store arrives by property assignment after the
            // constructor has run. This method runs on an ALREADY-OPEN window and never reopens it,
            // so `Opened` does not fire again: without this call, an import/restore whose bundle
            // carries a different command-assist/snippets.json (which BackupService replaces
            // wholesale in BOTH modes) leaves the visible rows describing the pre-import file until
            // the window is closed and reopened - and the user can edit or delete against them.
            //
            // Awaited last, after every synchronous repopulation above, so the parts of this method
            // that cannot fail on I/O are already done before the first suspension point. Null-safe
            // for a window opened outside MainWindow.OpenSettings: TryGetSnippetEditor returns null
            // when CommandAssistSnippetStore is unset and the panel simply says so.
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
