using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Navigation;
using DotCharter.Presentation.Shared;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Configuration;
using DotCharter.Tone.Logging;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>
///     The settings page's chrome over the tabs the app registers: the rail,
///     the footer's verbs and its unsaved count, and the cross-tab search
///     (<c>SettingsShellViewModel.Search.cs</c>).
///     <para>
///         Opening and closing go through <see cref="SettingsSession" />: the app
///         shell sets <see cref="SettingsSession.IsOpen" /> as its Settings tab is
///         entered or left, and this loads or reverts every tab in answer; Save
///         and Cancel set it false, and the app shell leaves the tab. The work
///         is posted so the tab switch paints first.
///     </para>
///     <para>
///         Saving is explicit: Save is enabled only while something is unsaved, and
///         Reset section asks before it discards the selected tab's edits.
///     </para>
/// </summary>
public sealed partial class SettingsShellViewModel : ObservableObject, ISettingsShellScreen, IDisposable
{
    private readonly IReadOnlyList<ISettingsTab> _tabs;
    private readonly IReadOnlyList<ISettingsChangeSource> _changes;
    private readonly SettingsSession _session;
    private readonly ILocalizationService _localization;
    private readonly IUiDispatcher _ui;
    private readonly IDialogs _dialogs;
    private readonly IAppLogger? _logger;
    private readonly List<SettingsRailSectionViewModel> _sections = [];
    private readonly List<SettingsRailItemViewModel> _items = [];
    private readonly AsyncRelayCommand _resetSection;
    private readonly RelayCommand _save;
    private SettingsRailItemViewModel? _selected;
    private int _unsavedCount;
    private string _lastUnsavedSignature = "";
    private bool _loaded;

    public SettingsShellViewModel(
        IEnumerable<ISettingsTab> tabs,
        IEnumerable<ISettingsChangeSource> changes,
        SettingsSession session,
        ILocalizationService localization,
        IUiDispatcher ui,
        IDialogs dialogs,
        IAppLogger? logger = null)
    {
        _tabs = SettingsRail.Order(tabs);
        _changes = [.. changes];
        _session = session;
        _localization = localization;
        _ui = ui;
        _dialogs = dialogs;
        _logger = logger;

        BuildRail();
        TabScreens = [.. _tabs.Where(t => t.Screen is not null).Select(t => t.Screen!)];

        _resetSection = new AsyncRelayCommand(ResetSectionAsync, () => SelectedTabUnsavedCount > 0);
        _save = new RelayCommand(Save, () => HasUnsavedChanges);
        CancelCommand = new RelayCommand(Cancel);
        InitializeSearch();

        foreach (var source in _changes) source.PropertyChanged += OnSourceChanged;
        _session.PropertyChanged += OnSessionChanged;
        _localization.PropertyChanged += OnLanguageChanged;

        if (_items.Count > 0) Select(_items[0]);
        Recount();

        // Built while the page is already open: the shell builds this surface when
        // its tab is first shown, which is after it set IsOpen, so the
        // change this listens for has already happened.
        if (_session.IsOpen) _ui.Post(() => { if (_session.IsOpen && !_loaded) LoadAll(); });
    }

    public IReadOnlyList<ISettingsRailSection> Sections => _sections;

    public IReadOnlyList<Type> TabScreens { get; }

    public Type? CurrentScreen => _selected?.Tab.Screen;

    /// <summary>The selected tab, for the tests and the host.</summary>
    public ISettingsTab? SelectedTab => _selected?.Tab;

    public string UnsavedChangesText => _unsavedCount == 0
        ? _localization["Settings_NothingUnsaved"]
        : _localization.Plural("Settings_UnsavedOne", "Settings_Unsaved", _unsavedCount);

    public bool HasUnsavedChanges => _unsavedCount > 0;

    public ICommand ResetSectionCommand => _resetSection;

    public ICommand CancelCommand { get; }

    public ICommand SaveCommand => _save;

    private int SelectedTabUnsavedCount =>
        (_selected?.Tab as ISettingsChangeSource)?.UnsavedSettings.Count ?? 0;

    public void Select(string key)
    {
        if (_items.FirstOrDefault(i => i.Tab.Key == key) is { } item) Select(item);
    }

    /// <summary>Re-reads every tab and takes the unsaved baseline — the page and the disk agree.</summary>
    public void LoadAll()
    {
        // Timed per tab: this runs on the UI thread in the frame Settings opens, so a slow
        // tab is a visible hitch.
        var total = System.Diagnostics.Stopwatch.StartNew();
        var slowest = ("", 0.0);
        foreach (var tab in _tabs)
        {
            var one = System.Diagnostics.Stopwatch.StartNew();
            tab.Load();
            if (one.Elapsed.TotalMilliseconds > slowest.Item2) slowest = (tab.Key, one.Elapsed.TotalMilliseconds);
        }

        foreach (var source in _changes) source.Rebaseline();
        _loaded = true;
        Recount();
        _logger?.Info(AppLogCategory.Settings,
            $"Settings loaded in {total.Elapsed.TotalMilliseconds:0.0} ms (slowest tab: {slowest.Item1}, {slowest.Item2:0.0} ms).");
        if (_unsavedCount > 0)
        {
            _logger?.Warning(AppLogCategory.Application,
                "Settings opened with " + _unsavedCount + " unsaved change(s) before any edit: " +
                string.Join(", ", _changes.SelectMany(c => c.UnsavedSettings)));
        }
    }

    public void Dispose()
    {
        foreach (var source in _changes) source.PropertyChanged -= OnSourceChanged;
        _session.PropertyChanged -= OnSessionChanged;
        _localization.PropertyChanged -= OnLanguageChanged;
    }

    private void BuildRail()
    {
        var byKey = _tabs.ToDictionary(t => t.Key, StringComparer.Ordinal);
        foreach (var run in SettingsRail.Entries.Where(e => byKey.ContainsKey(e.Key)).GroupBy(e => e.Section))
        {
            var items = run.Select(e => new SettingsRailItemViewModel(byKey[e.Key], e, _localization, Select)).ToList();
            _items.AddRange(items);
            _sections.Add(new SettingsRailSectionViewModel(SettingsRail.SectionTitleKey(run.Key), items, _localization));
        }
    }

    private void Select(SettingsRailItemViewModel item)
    {
        if (_selected is { } previous && !ReferenceEquals(previous, item))
        {
            previous.IsSelected = false;
            (previous.Tab as IScreenLifecycle)?.OnHidden();
        }

        _selected = item;
        item.IsSelected = true;
        (item.Tab as IScreenLifecycle)?.OnShown();
        OnPropertyChanged(nameof(CurrentScreen));
        _resetSection.NotifyCanExecuteChanged();
    }

    private void Save()
    {
        if (!HasUnsavedChanges) return;
        foreach (var tab in _tabs) tab.Save();
        foreach (var source in _changes) source.Rebaseline();
        Recount();
        Close();
    }

    private void Cancel()
    {
        RevertAll();
        Close();
    }

    private async Task ResetSectionAsync()
    {
        var item = _selected;
        var count = SelectedTabUnsavedCount;
        if (item is null || count == 0) return;

        var confirmed = await _dialogs.MessageAsync(
            _localization["Settings_ResetSectionConfirmTitle"],
            _localization.Plural("Settings_ResetSectionConfirmOne", "Settings_ResetSectionConfirm", count, item.Title),
            isConfirmation: true);
        if (!confirmed || !ReferenceEquals(item, _selected)) return;

        item.Tab.Reset();
        Recount();
    }

    private void RevertAll()
    {
        _loaded = false;
        foreach (var tab in _tabs) tab.Revert();
    }

    private void Close()
    {
        _loaded = false;
        _session.IsOpen = false;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsSession.ReloadRequests))
        {
            if (_session.IsOpen) LoadAll();
            return;
        }

        if (e.PropertyName is not (null or "" or nameof(SettingsSession.IsOpen))) return;
        if (_session.IsOpen) _ui.Post(() => { if (_session.IsOpen) LoadAll(); });
        else if (_loaded) _ui.Post(() => { if (_loaded && !_session.IsOpen) RevertAll(); });
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or "" or nameof(ISettingsChangeSource.UnsavedSettings)) Recount();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var section in _sections) section.Refresh();
        OnPropertyChanged(nameof(UnsavedChangesText));
        OnPropertyChanged(nameof(SearchResultCountText));
    }

    /// <summary>Recounts, and NAMES what it counted: a bare number on the footer is unfalsifiable.</summary>
    private void Recount()
    {
        var differing = _changes.SelectMany(c => c.UnsavedSettings).ToList();
        var count = differing.Count;
        if (count != _unsavedCount)
        {
            _unsavedCount = count;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(UnsavedChangesText));
        }

        _save.NotifyCanExecuteChanged();
        _resetSection.NotifyCanExecuteChanged();

        var signature = string.Join(", ", differing);
        if (signature == _lastUnsavedSignature) return;
        _lastUnsavedSignature = signature;
        if (count > 0) _logger?.Info(AppLogCategory.Application, "Settings unsaved: " + signature);
    }
}
