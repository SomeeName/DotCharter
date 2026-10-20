using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>The queue's filters, in the dropdown's order.</summary>
public enum SoundQueueFilter
{
    AnySound,
    MissingFingering,
    UnnamedChord,
    Untemplated,
    NoAnchorCoverage,
    HasIssue,
    BlocksExport
}

/// <summary>
///     One filter row in the picker, with its live count.
///     <para>
///         Observable and MUTABLE rather than a record, deliberately. A refresh
///         changes only the seven counts, and this list is the
///         <c>ItemsSource</c> of a bound <c>ComboBox</c>: rebuilding it meant
///         <c>Clear()</c> plus seven <c>Add</c>s — eight <c>CollectionChanged</c>
///         notifications and a torn-down popup — per committed edit. Worse, the
///         records were VALUE-equal, so a changed count made
///         <c>SelectedFilter</c> compare unequal to itself and re-entered
///         <c>OnSelectedFilterChanged</c> mid-refresh. One stable instance per
///         filter for the life of the view model fixes both: the selection is
///         the same object it was, and only <see cref="Display" /> changes.
///     </para>
/// </summary>
public sealed partial class SoundQueueFilterOption : ObservableObject
{
    public SoundQueueFilterOption(SoundQueueFilter filter, string label, int count)
    {
        Filter = filter;
        _label = label;
        _count = count;
    }

    public SoundQueueFilter Filter { get; }

    /// <summary>Localized name. Settable because a language switch re-runs the
    /// refresh and the label has to follow it.</summary>
    [ObservableProperty] private string _label;

    [ObservableProperty] private int _count;

    public string Display => $"{Label} ({Count})";

    partial void OnLabelChanged(string value)
    {
        _ = value;
        OnPropertyChanged(nameof(Display));
    }

    partial void OnCountChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(Display));
    }
}

/// <summary>
///     The work queue (04-inspector-redesign.md §8) — EOF's <c>F</c> dialog,
///     generalised. <c>‹ ›</c> step sounds, <c>‹‹ ››</c> step the ones matching a
///     swappable filter.
///     <para>
///         A STANDALONE view model with injected seams, exactly the shape of
///         <c>PhrasesInspectorViewModel</c> and <c>AudioLintViewModel</c> — which
///         is what keeps the inspector's VM exception small: the host only
///         composes it.
///     </para>
///     <para>
///         Two deliberate differences from the <c>SelectNextMissingFingering</c>
///         button it subsumes: it SELECTS the sound rather than only seeking, so
///         the panel's fields are live and the technique verbs have a target;
///         and it wraps with a visible end-of-list state instead of silently
///         doing nothing.
///     </para>
/// </summary>
public partial class SoundQueueViewModel : ViewModelBase
{
    private readonly Func<Arrangement?> _arrangement;
    private readonly Func<IList<BeatMarker>> _beats;
    private readonly Func<IReadOnlyList<ArrangementIssue>> _issues;
    private readonly Func<ChartLevel?> _level;
    private readonly ISelectionService _selection;
    private readonly Action<double> _seek;
    private readonly ILocalizationService _localization;

    /// <summary>
    ///     <c>level</c> is the level being EDITED, not the arrangement's first
    ///     one — see <see cref="ActiveLevelOf" />. It is a seam rather than a
    ///     snapshot for the same reason the arrangement is: the host re-reads it
    ///     on every refresh.
    /// </summary>
    public SoundQueueViewModel(
        Func<Arrangement?> arrangement,
        Func<ChartLevel?> level,
        Func<IList<BeatMarker>> beats,
        Func<IReadOnlyList<ArrangementIssue>> issues,
        ISelectionService selection,
        Action<double> seek,
        ILocalizationService localization)
    {
        _localization = localization;
        _arrangement = arrangement;
        _level = level;
        _beats = beats;
        _issues = issues;
        _selection = selection;
        _seek = seek;
        Refresh();
        // A highway click, a marquee or a sheet click changes the selection without an edit, so
        // nothing schedules a refresh: the position has to follow the selection itself.
        _selection.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>
    ///     The match list the position was last computed against (refresh or step). A selection
    ///     change re-locates against it rather than rebuilding the level's ordered sound list: the
    ///     list changes only with an edit, and every edit already refreshes it.
    /// </summary>
    private IReadOnlyList<ITimedElement> _lastMatches = [];

    /// <summary>
    /// The counter follows the CURRENT selection: a sound clicked in the chart shows its
    ///     own place, and a multi-selection or a sound outside the filter shows "—". O(1) for any
    ///     selection that is not exactly one sound, so a marquee adding hundreds is not quadratic;
    ///     no seam is read, so a queue deferred while the tab is hidden stays deferred.
    /// </summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_stale || _stepping) return;
        Wrapped = false;
        CurrentIndex = PositionOfSelection(_lastMatches);
    }

    /// <summary>Set while a step selects its target, which then sets the position itself.</summary>
    private bool _stepping;

    [ObservableProperty] private SoundQueueFilterOption? _selectedFilter;

    /// <summary>Position of the CURRENT selection inside the filtered list,
    /// 1-based. 0 = the selection is not in the queue.</summary>
    [ObservableProperty] private int _currentIndex;

    [ObservableProperty] private int _matchCount;

    /// <summary>True after a step reached the end and wrapped — the visible
    /// end-of-list state the old "silently do nothing" lacked.</summary>
    [ObservableProperty] private bool _wrapped;

    public ObservableCollection<SoundQueueFilterOption> Filters { get; } = [];

    /// <summary>"3 / 17" — or "— / 17" when nothing in the queue is selected.</summary>
    public string PositionLabel => MatchCount == 0
        ? _localization["Inspector_Queue_Empty"]
        : _localization.Format("Inspector_Queue_Position",
            CurrentIndex == 0 ? "—" : CurrentIndex.ToString(), MatchCount);

    public bool HasMatches => MatchCount > 0;

    /// <summary>Live counts for the three empty-state tiles (§4.5). The tiles
    /// ARE the queue's filters, pre-counted — clicking one sets the filter and
    /// jumps to the first match.</summary>
    public int MissingFingeringCount => CountFor(SoundQueueFilter.MissingFingering);
    public int UnnamedChordCount => CountFor(SoundQueueFilter.UnnamedChord);
    public int BlocksExportCount => CountFor(SoundQueueFilter.BlocksExport);

    /// <summary>How many lint FINDINGS sit on the <see cref="BlocksExportCount" />
    /// sounds: the tile counts sounds, the Issues badge counts findings over
    /// every level, and the tile's tooltip states both so the two numbers can be
    /// reconciled.</summary>
    public int BlocksExportFindingCount { get; private set; }

    /// <inheritdoc cref="Inspector.ProblemCountText.BlockingSoundsTip" />
    public string BlocksExportTileTip =>
        Inspector.ProblemCountText.BlockingSoundsTip(_localization, BlocksExportCount,
            BlocksExportFindingCount);

    private int CountFor(SoundQueueFilter filter) =>
        Filters.FirstOrDefault(f => f.Filter == filter)?.Count ?? 0;

    /// <summary>
    ///     Whether an edit landed while the Sound tab was hidden, so the counts
    ///     and the position are behind the chart.
    ///     <para>
    ///         Everything this view model feeds is bound by
    ///         <c>SoundInspectorView.axaml</c> and nothing else, so a refresh
    ///         while another tab is showing is a whole-level rebuild nobody can
    ///         see — the same reasoning (and the same shape) as the FEHLER tab's
    ///         <c>_issueGroupsStale</c>. The two KEYBIND entry points
    ///         (<c>QueuePrevMatch</c> / <c>QueueNextMatch</c>) work from any tab,
    ///         so <see cref="Step" /> and <see cref="TriageFilter" /> bring it
    ///         current first — a deferral may not change what a gesture does.
    ///     </para>
    /// </summary>
    private bool _stale;

    /// <summary>Read by the tests that pin the deferral.</summary>
    internal bool IsStale => _stale;

    /// <summary>Record that the work set is behind, without rebuilding it.</summary>
    public void MarkStale() => _stale = true;

    /// <summary>Refresh only if an edit landed while the tab was hidden.</summary>
    public void EnsureCurrent()
    {
        if (_stale) Refresh();
    }

    /// <summary>Rebuild the filter counts and re-locate the selection. Called on
    /// selection / history / arrangement changes; the host owns the debounce.</summary>
    public void Refresh()
    {
        _stale = false;
        var loc = _localization;

        // ONE work set for the whole refresh. Building the level's
        // ordered sound list from scratch inside every Matches() call — seven
        // filter counts plus RecomputePosition, i.e. EIGHT Cast+Concat+OrderBy
        // +ToList passes over 6,500 sounds — and three of those filters would then run
        // `issues.Any(Backreferences)` per sound, which is O(sounds x issues)
        // with a closure allocated per sound. All of it on the UI thread, once
        // per committed edit, whether or not the Queue tab is visible.
        var work = BuildWork();
        // Held for the whole refresh because assigning SelectedFilter below can
        // re-enter RecomputePosition() through OnSelectedFilterChanged. That
        // re-entry is part of this refresh and must not read the seams a second
        // time.
        _refreshWork = work;
        try
        {
            // In PLACE after the first build. Clear + seven Adds is eight
            // CollectionChanged notifications into a bound ComboBox per
            // committed edit, and it also replaced SelectedFilter's instance —
            // see SoundQueueFilterOption. Only Display changes now.
            var i = 0;
            foreach (var filter in Enum.GetValues<SoundQueueFilter>())
            {
                var label = loc[$"Inspector_Queue_Filter_{filter}"];
                var count = work.Matches(filter).Count;
                if (i < Filters.Count)
                {
                    Filters[i].Label = label;
                    Filters[i].Count = count;
                }
                else
                {
                    Filters.Add(new SoundQueueFilterOption(filter, label, count));
                }

                i++;
            }

            SelectedFilter ??= Filters[0];
            BlocksExportFindingCount = work.BlockingFindingCount;
            RecomputePosition(work);
        }
        finally
        {
            _refreshWork = null;
        }

        OnPropertyChanged(nameof(MissingFingeringCount));
        OnPropertyChanged(nameof(UnnamedChordCount));
        OnPropertyChanged(nameof(BlocksExportCount));
        OnPropertyChanged(nameof(BlocksExportFindingCount));
        OnPropertyChanged(nameof(BlocksExportTileTip));
    }

    /// <summary>Non-null only for the duration of <see cref="Refresh" />.</summary>
    private SoundQueueWork? _refreshWork;

    partial void OnSelectedFilterChanged(SoundQueueFilterOption? value)
    {
        _ = value;
        Wrapped = false;
        RecomputePosition();
    }

    private void RecomputePosition() => RecomputePosition(_refreshWork ?? BuildWork());

    private void RecomputePosition(SoundQueueWork work)
    {
        var matches = work.Matches(SelectedFilter?.Filter ?? SoundQueueFilter.AnySound);
        _lastMatches = matches;
        MatchCount = matches.Count;
        CurrentIndex = PositionOfSelection(matches);

        OnPropertyChanged(nameof(PositionLabel));
        OnPropertyChanged(nameof(HasMatches));
    }

    /// <summary>
    ///     Where the CURRENT selection sits in <paramref name="matches" />,
    ///     1-based; 0 when nothing selected is in the queue. This IS the
    ///     definition of <see cref="CurrentIndex" />, which is why both the
    ///     refresh and every step derive it here rather than each keeping its own
    ///     idea of where the walk had got to.
    /// </summary>
    private int LocateSelection(IReadOnlyList<ITimedElement> matches)
    {
        if (_selection.SelectedNotes.Count + _selection.SelectedChords.Count == 1)
            return IndexOf(matches, _selection.SelectedNotes.Count == 1 ? _selection.SelectedNotes[0] : _selection.SelectedChords[0]);

        for (var i = 0; i < matches.Count; i++)
        {
            var sound = matches[i];
            var selected = (sound is GuitarNote n && _selection.SelectedNotes.Contains(n))
                           || (sound is Chord c && _selection.SelectedChords.Contains(c));
            if (selected) return i + 1;
        }

        return 0;
    }

    /// <summary>
    ///     The position the COUNTER shows: the place of a single selected sound, 0 ("—") for a
    ///     multi-selection, nothing, or a sound outside the filter. A step still starts from the
    ///     first selected sound of a multi-selection (<see cref="LocateSelection" />).
    /// </summary>
    private int PositionOfSelection(IReadOnlyList<ITimedElement> matches) =>
        _selection.SelectedNotes.Count + _selection.SelectedChords.Count == 1 ? LocateSelection(matches) : 0;

    private static int IndexOf(IReadOnlyList<ITimedElement> matches, ITimedElement sound)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            if (ReferenceEquals(matches[i], sound)) return i + 1;
        }

        return 0;
    }

    partial void OnCurrentIndexChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(PositionLabel));
    }

    partial void OnMatchCountChanged(int value)
    {
        _ = value;
        OnPropertyChanged(nameof(PositionLabel));
        OnPropertyChanged(nameof(HasMatches));
    }

    /// <summary>«« — the previous sound MATCHING the filter.</summary>
    [RelayCommand]
    public void PreviousMatch() => Step(-1);

    /// <summary>»» — the next sound matching the filter.</summary>
    [RelayCommand]
    public void NextMatch() => Step(1);

    /// <summary>Set the filter and jump to its first match — what a health tile
    /// does. The entry point to the triage loop, which would otherwise be discoverable
    /// only as a button inside a collapsed expander inside another tab.
    /// <para>
    ///     Called DIRECTLY from <c>SoundInspectorView.axaml.cs</c>: the three
    ///     tiles are <c>Border</c>s with <c>PointerPressed</c> handlers, not
    ///     <c>Button</c>s, so there is nothing to bind a <c>Command</c> to. It
    ///     carries no <c>[RelayCommand]</c>, since the generated property would be
    ///     bound by nothing.
    /// </para></summary>
    public void TriageFilter(SoundQueueFilter filter)
    {
        EnsureCurrent();
        SelectedFilter = Filters.FirstOrDefault(f => f.Filter == filter) ?? SelectedFilter;
        // FROM THE START, explicitly: this means "jump to the FIRST match", and
        // Step re-locates the selection when it is not told where to start.
        Step(1, from: 0);
    }

    private void Step(int direction, int? from = null)
    {
        // The two step keybinds fire from any tab, so a deferred refresh has to
        // be settled here or CurrentIndex would step from a stale position.
        EnsureCurrent();
        var matches = BuildWork().Matches(SelectedFilter?.Filter ?? SoundQueueFilter.AnySound);
        _lastMatches = matches;
        MatchCount = matches.Count;
        if (matches.Count == 0)
        {
            CurrentIndex = 0;
            return;
        }

        // RE-LOCATE, never trust the cached index. CurrentIndex is DEFINED as
        // the current selection's place in the queue, and the selection changes
        // without an edit: a highway click raises no history/project event, so
        // nothing schedules the debounced refresh that recomputes it. Stepping
        // from the stale value walked off from wherever the last step had
        // stopped, skipping or repeating everything in between. This is one pass
        // over the match list that was built either way — no extra seam read.
        CurrentIndex = from ?? LocateSelection(matches);

        // 1-based CurrentIndex, 0 = "not in the queue", so a forward step from
        // 0 lands on the first match rather than the second.
        var next = CurrentIndex + direction;
        var wrapped = next < 1 || next > matches.Count;
        if (next < 1) next = matches.Count;
        if (next > matches.Count) next = 1;

        var target = matches[next - 1];
        _stepping = true;
        try
        {
            SelectAndSeek(target);
        }
        finally
        {
            _stepping = false;
        }

        Wrapped = wrapped;
        CurrentIndex = next;
    }

    internal static FractionalPosition PositionOf(ITimedElement sound) => sound switch
    {
        GuitarNote n => n.Position,
        Chord c => c.Position,
        _ => FractionalPosition.Zero
    };

    private void SelectAndSeek(ITimedElement sound)
    {
        var beats = _beats();
        if (beats.Count > 0) _seek(PositionOf(sound).GetPositionMs(beats));

        _selection.ClearSelections();
        switch (sound)
        {
            case GuitarNote note:
                _selection.SelectedNotes.Add(note);
                _selection.SelectedNote = note;
                break;
            case Chord chord:
                _selection.SelectedChords.Add(chord);
                _selection.SelectedChord = chord;
                break;
        }
    }

    /// <summary>
    ///     The filtered, position-ordered sound list. Pure over the arrangement:
    ///     every predicate reuses a rule that already exists elsewhere rather
    ///     than inventing a second definition of "needs work".
    ///     <para>
    ///         Each call reads both seams and rebuilds the derived state, so it is
    ///         the ONE-filter entry point. A refresh wants seven counts plus a
    ///         position, and goes through <see cref="BuildWork" /> once instead.
    ///     </para>
    /// </summary>
    internal IReadOnlyList<ITimedElement> Matches(SoundQueueFilter filter) => BuildWork().Matches(filter);

    /// <summary>Reads the two seams ONCE and derives everything the seven filters
    /// need from them.</summary>
    private SoundQueueWork BuildWork()
    {
        var arr = _arrangement();
        var level = ActiveLevelOf(arr);
        return arr == null || level == null
            ? SoundQueueWork.Empty
            : new SoundQueueWork(arr, level, _issues());
    }

    /// <summary>
    ///     One refresh's worth of derived state: the level's ordered sound list,
    ///     the arrangement's templates indexed by id, and the issue
    ///     back-references indexed by the sound they point at.
    ///     <para>
    ///         All three are computed once per refresh rather than per <c>Matches</c> call, of which a
    ///         refresh makes eight. The issue back-references are the expensive
    ///         half — three of the seven filters would run <c>issues.Any(i =&gt;
    ///         Backreferences(i, sound))</c> for EVERY sound, i.e. O(sounds ×
    ///         issues) with a closure allocated per sound, which on a 6,500-sound
    ///         level with 300 findings is ~5.85 M predicate calls per refresh, on
    ///         the UI thread, per committed edit, tab visible or not.
    ///     </para>
    ///     <para>
    ///         The sets are reference-keyed because the per-sound back-reference test
    ///         is <see cref="object.ReferenceEquals" /> — a value comparison would
    ///         claim findings for a different sound that happens to look the same.
    ///     </para>
    /// </summary>
    private sealed class SoundQueueWork
    {
        public static readonly SoundQueueWork Empty = new();

        private readonly List<ITimedElement> _sounds = [];
        private readonly Dictionary<int, ChordTemplate> _templatesById = [];
        private readonly HashSet<object> _hasIssue = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _noAnchorCoverage = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _blocksExport = new(ReferenceEqualityComparer.Instance);

        /// <summary>Export-blocking findings on THIS level's sounds — the
        /// blockers tile's second number.</summary>
        public int BlockingFindingCount { get; }

        private SoundQueueWork()
        {
        }

        public SoundQueueWork(Arrangement arr, ChartLevel level, IReadOnlyList<ArrangementIssue> issues)
        {
            // ITimedElement, not IGuitarSound: a Chord does NOT implement
            // IGuitarSound (that interface is the per-STRING contract, which
            // GuitarNote and ChordNote satisfy and a strum does not).
            // ITimedElement is the shared base, and Position comes off the
            // concrete type.
            _sounds = level.Notes.Cast<ITimedElement>()
                .Concat(level.Chords.Cast<ITimedElement>())
                .OrderBy(PositionOf)
                .ToList();

            // First id wins, which is what FirstOrDefault(x => x.Id == …) did.
            foreach (var template in arr.ChordTemplates)
                _templatesById.TryAdd(template.Id, template);

            // Findings sweep EVERY level; the tile counts this one.
            var onLevel = new HashSet<object>(_sounds, ReferenceEqualityComparer.Instance);
            foreach (var issue in issues)
            {
                Index(_hasIssue, issue);
                if (issue.Type is ArrangementIssueType.NoteWithoutAnchor
                    or ArrangementIssueType.NoteOutsideAnchorRange)
                {
                    Index(_noAnchorCoverage, issue);
                }

                if (Inspector.InspectorIssueFieldMap.BlocksExport(issue.Type))
                {
                    Index(_blocksExport, issue);
                    if ((issue.Note != null && onLevel.Contains(issue.Note))
                        || (issue.Chord != null && onLevel.Contains(issue.Chord)))
                    {
                        BlockingFindingCount++;
                    }
                }
            }
        }

        private static void Index(HashSet<object> set, ArrangementIssue issue)
        {
            if (issue.Note != null) set.Add(issue.Note);
            if (issue.Chord != null) set.Add(issue.Chord);
        }

        public IReadOnlyList<ITimedElement> Matches(SoundQueueFilter filter)
        {
            if (_sounds.Count == 0) return Array.Empty<ITimedElement>();
            if (filter == SoundQueueFilter.AnySound) return _sounds;

            var matches = new List<ITimedElement>();
            foreach (var sound in _sounds)
            {
                if (IsMatch(sound, filter))
                    matches.Add(sound);
            }

            return matches;
        }

        private bool IsMatch(ITimedElement sound, SoundQueueFilter filter)
        {
            switch (filter)
            {
                case SoundQueueFilter.MissingFingering:
                {
                    if (sound is not Chord chord) return false;
                    var t = TemplateOf(chord);
                    // A FRETTED string with no finger. An open string legitimately
                    // has none (that is inspector rule L06 the other way round).
                    return t != null && t.Frets.Any(kv => kv.Value > 0 && !t.Fingers.ContainsKey(kv.Key));
                }
                case SoundQueueFilter.UnnamedChord:
                {
                    if (sound is not Chord chord) return false;
                    var t = TemplateOf(chord);
                    return t != null && string.IsNullOrWhiteSpace(t.Name);
                }
                case SoundQueueFilter.Untemplated:
                    return sound is Chord { TemplateId: -1 };
                case SoundQueueFilter.NoAnchorCoverage:
                    return _noAnchorCoverage.Contains(sound);
                case SoundQueueFilter.HasIssue:
                    return _hasIssue.Contains(sound);
                case SoundQueueFilter.BlocksExport:
                    return _blocksExport.Contains(sound);
                default:
                    return true;
            }
        }

        private ChordTemplate? TemplateOf(Chord chord) =>
            _templatesById.TryGetValue(chord.TemplateId, out var template) ? template : null;
    }

    /// <summary>
    ///     The level the charter is looking at.
    ///     <para>
    ///         This is not <c>arr.Levels.FirstOrDefault()</c> — level 0, the
    ///         THINNEST difficulty, not the one being edited. On any multi-level
    ///         chart (every multi-level package import, and everything after Tools →
    ///         Generate Levels, which deliberately leaves the editor on level
    ///         N-1) the queue's stepping, its filter counts and the three health
    ///         tiles would all describe a level nobody has on screen — and because
    ///         stepping SELECTS, the technique verbs would then act on a sound from
    ///         the wrong level.
    ///     </para>
    ///     <para>
    ///         Falls back to the first level when the ambient level is null or
    ///         belongs to some OTHER arrangement: the two seams are read
    ///         independently and can disagree for a dispatcher turn during an
    ///         arrangement switch, and a queue over a level this arrangement does
    ///         not contain would report matches that cannot be selected.
    ///     </para>
    /// </summary>
    private ChartLevel? ActiveLevelOf(Arrangement? arr)
    {
        if (arr == null) return null;
        var level = _level();
        return level != null && arr.Levels.Contains(level) ? level : arr.Levels.FirstOrDefault();
    }
}
