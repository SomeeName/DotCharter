using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>One template in the Library page's list: its frets ("x0755x") AND its name.</summary>
public sealed class ChordLibraryEntry(ChordTemplate template, string frets, string name, bool isUnnamed)
{
    public ChordTemplate Template { get; } = template;

    public string Frets { get; } = frets;

    public string Name { get; } = name;

    public bool IsUnnamed { get; } = isUnnamed;

    /// <summary>What a screen reader says for the row: the frets, then the name.</summary>
    public override string ToString() => Frets + " " + Name;
}

/// <summary>Which templates a Library search keeps, pure: a NAME query matches any part of the name; a FRETS query
/// (digits, x and spaces only, e.g. <c>x07</c>) matches any run of the frets as the list shows them.</summary>
public static class TemplateSearch
{
    public static bool Matches(string name, string frets, string query)
    {
        var q = query.Trim();
        if (q.Length == 0 || name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) return true;
        var compact = q.Replace(" ", "", StringComparison.Ordinal);
        return compact.Length > 0 && compact.All(c => char.IsAsciiDigit(c) || c is 'x' or 'X') &&
               frets.Replace(" ", "", StringComparison.Ordinal).Contains(compact, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     The Note tab's Library page with nothing selected (design D): the arrangement's templates searched by
///     name AND frets through a <see cref="CoalescedFilterPass" /> (the list virtualizes), each entry showing both; the
///     picked one's grip and uses ("Select uses", "Next use"). Picking an entry BROWSES the chord panel to it
///     (<see cref="ChordTemplateEditorViewModel.Browse" />, never re-templating a chord), whose template card (name, flags, pin,
///     fingers, Save) edits it on the "Apply to" rung, as the old browse mode did. A plain collaborator, not DI.
/// </summary>
public sealed partial class ChordLibraryViewModel : ObservableObject, IDisposable
{
    private readonly ChartEditorViewModel _editor;
    private readonly ChordTemplateEditorViewModel _panel;
    private readonly SoundSectionsViewModel _sections;
    private readonly SoundQueueViewModel _queue;
    private readonly IHistoryService _history;
    private readonly ILocalizationService _loc;
    private readonly Func<double> _playheadMs;
    private CoalescedFilterPass? _pass;
    private bool _following;

    public ChordLibraryViewModel(ChartEditorViewModel editor, ChordTemplateEditorViewModel panel, SoundSectionsViewModel sections,
        SoundQueueViewModel queue, IHistoryService history, ILocalizationService localization, Func<double> playheadMs)
    {
        _editor = editor;
        _panel = panel;
        _sections = sections;
        _queue = queue;
        _history = history;
        _loc = localization;
        _playheadMs = playheadMs;
        _sections.PropertyChanged += OnSectionsChanged;
        _history.HistoryChanged += OnHistoryChanged;
        _panel.PropertyChanged += OnPanelChanged;
        _editor.PropertyChanged += OnEditorChanged;
    }

    /// <summary>The search, by name or frets.</summary>
    [ObservableProperty] private string _query = string.Empty;

    /// <summary>The templates the search keeps, in the arrangement's order.</summary>
    [ObservableProperty] private IReadOnlyList<ChordLibraryEntry> _entries = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected), nameof(SelectedFrets), nameof(UseCount), nameof(TotalUseCount), nameof(UsesText),
        nameof(HasUses))]
    private ChordLibraryEntry? _selected;

    /// <summary>How the search pass is scheduled; null is the UI dispatcher at Background priority (tests run it inline).</summary>
    internal Action<Action>? Post { get; set; }

    /// <summary>How many times the list was rebuilt (a structural probe for tests).</summary>
    internal int Passes { get; private set; }

    public bool HasSelected => Selected is not null;

    public bool NoEntries => Entries.Count == 0;

    public string SelectedFrets => Selected?.Frets ?? string.Empty;

    /// <summary>How many chords on the ACTIVE level use the picked template: the set "Select uses" and "Next use" walk.</summary>
    public int UseCount => Selected is { } entry && _editor.ActiveLevel is { } level ? UsesOn(level, entry).Count() : 0;

    /// <summary>How many chords use the picked template on every level of the arrangement.</summary>
    public int TotalUseCount => Selected is { } entry
        ? _editor.ActiveArrangement?.Levels.Sum(level => UsesOn(level, entry).Count()) ?? 0
        : 0;

    public bool HasUses => UseCount > 0;

    /// <summary>"3 uses", plus "· 12 in all" when other levels use it too, so the count matches what the verbs reach.</summary>
    public string UsesText
    {
        get
        {
            var here = _loc.Plural("Inspector_Library_UsesOne", "Inspector_Library_Uses", UseCount);
            var total = TotalUseCount;
            return total == UseCount ? here : here + " · " + _loc.Format("Inspector_Library_UsesInAll", total);
        }
    }

    private CoalescedFilterPass Pass => _pass ??= new CoalescedFilterPass(Rebuild, Post);

    partial void OnQueryChanged(string value) => Pass.Request();

    partial void OnEntriesChanged(IReadOnlyList<ChordLibraryEntry> value) => OnPropertyChanged(nameof(NoEntries));

    partial void OnSelectedChanged(ChordLibraryEntry? value)
    {
        if (_following || value is null) return;
        _panel.Browse(value.Template);
    }

    /// <summary>Rebuilds the list now (the page came on screen, the chart changed).</summary>
    public void Refresh() => Pass.RunImmediately();

    private void Rebuild()
    {
        Passes++;
        var arrangement = _editor.ActiveArrangement;
        var strings = arrangement?.Tuning.Length ?? 6;
        var entries = arrangement?.ChordTemplates
            .Select(t => new ChordLibraryEntry(t, GripEdit.FretText(t, strings), ChordShapeNames.DisplayName(t, strings),
                ChordShapeNames.IsUnnamed(t)))
            .Where(e => TemplateSearch.Matches(e.Name, e.Frets, Query))
            .ToArray() ?? [];
        Entries = entries;
        Follow();
    }

    /// <summary>Keeps the list's pick on the template the chord panel shows.</summary>
    private void Follow()
    {
        _following = true;
        try
        {
            Selected = _panel.Template is { } shown ? Entries.FirstOrDefault(e => e.Template.Id == shown.Id) : null;
        }
        finally
        {
            _following = false;
        }
    }

    /// <summary>A new template, copied from the picked one (the chord panel's own verb).</summary>
    [RelayCommand]
    private void NewTemplate()
    {
        _panel.CreateNewTemplateCommand.Execute(null);
        Refresh();
    }

    /// <summary>Selects every chord on this level that uses the picked template and seeks to the first.</summary>
    [RelayCommand]
    private void SelectUses()
    {
        if (Selected is not { } entry || _editor.ActiveLevel is not { } level) return;
        var uses = UsesOn(level, entry).OrderBy(c => c.Position).ToArray();
        if (uses.Length == 0) return;
        _queue.SelectAndSeek(uses[0]);
        foreach (var chord in uses.Skip(1)) _editor.Selection.SelectedChords.Add(chord);
    }

    /// <summary>Seeks the playhead to the next chord after it that uses the picked template (wrapping). It leaves the
    /// selection alone: the Library page is offered only with nothing selected, so selecting would leave the page and a
    /// second press could not step on.</summary>
    [RelayCommand]
    private void NextUse()
    {
        if (Selected is not { } entry || _editor.ActiveLevel is not { } level || _editor.Beats.Count == 0) return;
        var uses = UsesOn(level, entry).OrderBy(c => c.Position).ToArray();
        if (uses.Length == 0) return;
        var now = _playheadMs();
        var next = uses.FirstOrDefault(c => c.Position.GetPositionMs(_editor.Beats) > now + 1) ?? uses[0];
        _editor.ExecuteSeek(next.Position.GetPositionMs(_editor.Beats));
    }

    private static IEnumerable<Chord> UsesOn(ChartLevel level, ChordLibraryEntry entry) =>
        level.Chords.Where(c => c.TemplateId == entry.Template.Id);

    private void OnSectionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoundSectionsViewModel.Section) && _sections.IsLibrary) Refresh();
    }

    private void OnHistoryChanged()
    {
        if (_sections.IsLibrary) Pass.Request();
    }

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChordTemplateEditorViewModel.Template)) Follow();
    }

    /// <summary>Another arrangement or level: the list (and the use counts) belong to the old one, so rebuild.</summary>
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ChartEditorViewModel.ActiveArrangement) or nameof(ChartEditorViewModel.ActiveLevel))) return;
        if (_sections.IsLibrary) Pass.Request();
        OnPropertyChanged(nameof(UseCount));
        OnPropertyChanged(nameof(TotalUseCount));
        OnPropertyChanged(nameof(UsesText));
        OnPropertyChanged(nameof(HasUses));
    }

    public void Dispose()
    {
        _sections.PropertyChanged -= OnSectionsChanged;
        _history.HistoryChanged -= OnHistoryChanged;
        _panel.PropertyChanged -= OnPanelChanged;
        _editor.PropertyChanged -= OnEditorChanged;
    }
}
