using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Input;
using DotCharter.Tone.Infrastructure.Configuration;

namespace DotCharter.Presentation.Screens.Dialogs;

/// <summary>
///     The Pattern builder, one instance per open. Its job is COMPARISON: every
///     pattern in the rail is counted live against the current selection, every
///     pattern can be run without a key, and a key another owner already holds is
///     reported, because the keybind registry resolves a collision
///     last-registration-wins with nothing but a log line.
/// </summary>
public sealed class PatternBuilderViewModel : DialogViewModel<bool>, IPatternBuilderScreen, ICancelableDialog
{
    private readonly ChartEditDialogServices _edit;
    private readonly MatchPatternStore _store;
    private readonly IGlobalEditingCoordinator _editing;
    private readonly ISelectionService _selection;
    private readonly IKeybindDispatch _keybinds;
    private readonly RelayCommand[] _needsPattern;
    private MatchPatternRowViewModel? _selectedPattern;
    private string _matchSummary = "";
    private string _seedSummary = "";
    private bool _hasSeed;
    private bool _seedIsFromAnotherArrangement;
    private bool _hasConflict;
    private string _conflictSummary = "";

    public PatternBuilderViewModel(ChartEditDialogServices edit, MatchPatternStore store, IGlobalEditingCoordinator editing,
        ISelectionService selection, IKeybindDispatch keybinds)
    {
        _edit = edit;
        _store = store;
        _editing = editing;
        _selection = selection;
        _keybinds = keybinds;
        Choices = PatternChoiceSet.Build(edit.Localization);

        AddPatternCommand = new RelayCommand(AddPattern);
        RestoreBuiltInsCommand = new RelayCommand(RestoreBuiltIns);
        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(() => TryCancel());
        var duplicate = new RelayCommand(DuplicatePattern, () => HasSelectedPattern);
        var delete = new RelayCommand(DeletePattern, () => HasSelectedPattern);
        var runSelect = new RelayCommand(RunSelect, CanRun);
        var runNext = new RelayCommand(() => Step(1), CanRun);
        var runPrevious = new RelayCommand(() => Step(-1), CanRun);
        (DuplicatePatternCommand, DeletePatternCommand, RunSelectCommand, RunNextCommand, RunPreviousCommand) =
            (duplicate, delete, runSelect, runNext, runPrevious);
        _needsPattern = [duplicate, delete, runSelect, runNext, runPrevious];

        foreach (var pattern in store.EnsureSeeded()) Patterns.Add(NewRow(pattern));
        SelectedPattern = Patterns.FirstOrDefault();
        if (SelectedPattern is null) RefreshPreview();
        edit.Logger.Info(AppLogCategory.Input, $"Pattern builder opened with {Patterns.Count} pattern(s).");
    }

    public ObservableCollection<MatchPatternRowViewModel> Patterns { get; } = [];

    public PatternChoiceSet Choices { get; }

    IReadOnlyList<IMatchPatternRow> IPatternBuilderScreen.Patterns => Patterns;

    public MatchPatternRowViewModel? SelectedPattern
    {
        get => _selectedPattern;
        set
        {
            if (!SetProperty(ref _selectedPattern, value)) return;
            OnPropertyChanged(nameof(HasSelectedPattern));
            OnPropertyChanged(nameof(NoPatternSelectedReason));
            foreach (var command in _needsPattern) command.NotifyCanExecuteChanged();
            RefreshPreview();
        }
    }

    IMatchPatternRow? IPatternBuilderScreen.SelectedPattern
    {
        get => SelectedPattern;
        set => SelectedPattern = value as MatchPatternRowViewModel;
    }

    public bool HasSelectedPattern => SelectedPattern is not null;

    public string NoPatternSelectedReason => HasSelectedPattern ? "" : Loc("Pattern_NoPatternSelected");

    public string MatchSummary { get => _matchSummary; private set => SetProperty(ref _matchSummary, value); }

    public string SeedSummary { get => _seedSummary; private set => SetProperty(ref _seedSummary, value); }

    public bool HasSeed
    {
        get => _hasSeed;
        private set
        {
            if (SetProperty(ref _hasSeed, value)) OnSeedChanged();
        }
    }

    public bool SeedIsFromAnotherArrangement
    {
        get => _seedIsFromAnotherArrangement;
        private set
        {
            if (SetProperty(ref _seedIsFromAnotherArrangement, value)) OnSeedChanged();
        }
    }

    public bool SeedIsUsable => HasSeed && !SeedIsFromAnotherArrangement;

    /// <summary>The try-bar verbs find nothing without a usable seed; the bar's own text says why.</summary>
    private bool CanRun() => HasSelectedPattern && SeedIsUsable;

    private void OnSeedChanged()
    {
        OnPropertyChanged(nameof(SeedIsUsable));
        foreach (var command in _needsPattern) command.NotifyCanExecuteChanged();
    }

    public bool HasConflict { get => _hasConflict; private set => SetProperty(ref _hasConflict, value); }

    public string ConflictSummary { get => _conflictSummary; private set => SetProperty(ref _conflictSummary, value); }

    public ICommand AddPatternCommand { get; }

    public ICommand DuplicatePatternCommand { get; }

    public ICommand DeletePatternCommand { get; }

    public ICommand RestoreBuiltInsCommand { get; }

    public ICommand RunSelectCommand { get; }

    public ICommand RunNextCommand { get; }

    public ICommand RunPreviousCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public bool TryCancel()
    {
        if (!IsClosed) _edit.Logger.Info(AppLogCategory.Input, "Pattern builder canceled; nothing was saved.");
        Close(false);
        return true;
    }

    /// <summary>
    ///     Runs every EDITED pattern (not the stored one) against the live chart and
    ///     reports the counts without touching the selection. Asked on every change
    ///     that could move a count, so the dialog draws no refresh button.
    /// </summary>
    public void RefreshPreview()
    {
        var seedCount = _selection.SelectedNotes.Count + _selection.SelectedChords.Count +
                        _selection.SelectedHandShapes.Count + _selection.SelectedAnchors.Count;
        HasSeed = seedCount > 0;
        SeedSummary = _edit.Localization.Format("Pattern_SeedSummary", seedCount);
        RefreshConflicts();

        if (!HasSeed)
        {
            SeedIsFromAnotherArrangement = false;
            MatchSummary = Loc("Pattern_NoSeed");
            foreach (var row in Patterns) row.MatchCountLabel = "";
            return;
        }

        SeedIsFromAnotherArrangement = _editing.PatternSeedIsFromAnotherArrangement();
        if (SeedIsFromAnotherArrangement)
        {
            MatchSummary = Loc("Pattern_SeedFromAnotherArrangement");
            foreach (var row in Patterns) row.MatchCountLabel = "0";
            return;
        }

        foreach (var row in Patterns)
        {
            var count = _editing.CountPatternMatches(row.ToPattern());
            row.MatchCountLabel = count.ToString(System.Globalization.CultureInfo.CurrentCulture);
            if (ReferenceEquals(row, SelectedPattern)) MatchSummary = _edit.Localization.Format("Pattern_MatchSummary", count);
        }

        if (SelectedPattern is null) MatchSummary = NoPatternSelectedReason;
    }

    private MatchPatternRowViewModel NewRow(MatchPattern pattern)
    {
        var row = new MatchPatternRowViewModel(pattern.Normalized(), Choices);
        row.Summary = PatternSummary.Describe(row.ToPattern(), _edit.Localization);
        row.FacetChanged += OnFacetChanged;
        return row;
    }

    private void OnFacetChanged(object? sender, PatternFacetChangedEventArgs e)
    {
        if (sender is MatchPatternRowViewModel row) row.Summary = PatternSummary.Describe(row.ToPattern(), _edit.Localization);
        if (e.AffectsMatchCount && ReferenceEquals(sender, SelectedPattern)) RefreshPreview();
    }

    /// <summary>A key on the selected pattern owned by another pattern, or by a built-in keybind other than this pattern's own.</summary>
    private void RefreshConflicts()
    {
        var row = SelectedPattern;
        var conflicts = new List<string>();
        if (row is not null)
        {
            Check(row, row.SelectKey, "Pattern_Verb_Select", conflicts);
            Check(row, row.NextKey, "Pattern_Verb_Next", conflicts);
            Check(row, row.PreviousKey, "Pattern_Verb_Previous", conflicts);
        }

        HasConflict = conflicts.Count > 0;
        ConflictSummary = HasConflict ? _edit.Localization.Format("Pattern_ConflictSummary", string.Join("; ", conflicts)) : "";
    }

    private void Check(MatchPatternRowViewModel row, KeyChord chord, string verbKey, List<string> conflicts)
    {
        if (!chord.IsBound) return;
        var other = Patterns.FirstOrDefault(p =>
            !ReferenceEquals(p, row) && (p.SelectKey == chord || p.NextKey == chord || p.PreviousKey == chord));
        if (other is not null)
        {
            conflicts.Add($"{chord} ({Loc(verbKey)}) → {other.Name}");
            return;
        }

        var owner = _keybinds.DescribeBinding(chord);
        if (owner is null || owner.StartsWith($"pattern:{row.Id}:", StringComparison.Ordinal)) return;
        conflicts.Add($"{chord} ({Loc(verbKey)}) → {owner}");
    }

    private void AddPattern()
    {
        var row = NewRow(new MatchPattern { Name = Loc("Pattern_NewName") });
        Patterns.Add(row);
        OnPropertyChanged(nameof(Patterns));
        SelectedPattern = row;
    }

    /// <summary>Keeps the facets and drops the keys: two patterns on one chord is the silent override this dialog warns about.</summary>
    private void DuplicatePattern()
    {
        if (SelectedPattern is null) return;
        var copy = SelectedPattern.ToPattern();
        copy.Id = string.Empty;
        copy.Name = _edit.Localization.Format("Pattern_CopyName", SelectedPattern.Name);
        copy.SelectKey = KeyChord.None;
        copy.NextKey = KeyChord.None;
        copy.PreviousKey = KeyChord.None;
        var row = NewRow(copy);
        Patterns.Add(row);
        OnPropertyChanged(nameof(Patterns));
        SelectedPattern = row;
    }

    private void DeletePattern()
    {
        if (SelectedPattern is null) return;
        var index = Patterns.IndexOf(SelectedPattern);
        SelectedPattern.FacetChanged -= OnFacetChanged;
        Patterns.Remove(SelectedPattern);
        OnPropertyChanged(nameof(Patterns));
        SelectedPattern = Patterns.Count == 0 ? null : Patterns[Math.Min(index, Patterns.Count - 1)];
    }

    private void RestoreBuiltIns()
    {
        var known = Patterns.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var missing = MatchPatternCatalog.BuiltIns().Where(b => !known.Contains(b.Id)).ToList();
        foreach (var builtIn in missing) Patterns.Add(NewRow(builtIn));
        if (missing.Count > 0) OnPropertyChanged(nameof(Patterns));
        _edit.Logger.Info(AppLogCategory.Input, $"Pattern builder restored {missing.Count} built-in pattern(s).");
        RefreshPreview();
    }

    private void RunSelect()
    {
        if (SelectedPattern is null) return;
        var added = _editing.SelectByPattern(SelectedPattern.ToPattern());
        RefreshPreview();
        MatchSummary = _edit.Localization.Format("Pattern_SelectedSummary", added);
    }

    private void Step(int direction)
    {
        if (SelectedPattern is null) return;
        var target = _editing.MoveToPatternMatch(SelectedPattern.ToPattern(), _edit.State.CurrentTimeMs, direction);
        if (target.HasValue) _edit.Editor.ExecuteSeek(target.Value);
        RefreshPreview();
    }

    /// <summary>Persists every row, then refreshes the dispatch table so a new or rebound pattern is live on the next keypress.</summary>
    private void Save()
    {
        if (IsClosed) return;
        var saved = _store.ReplaceAll(Patterns.Select(p => p.ToPattern()));
        _keybinds.RefreshBinds();
        _edit.Logger.Info(AppLogCategory.Input,
            $"Pattern builder saved {saved.Count} pattern(s); {saved.Count(p => p.HasAnyKey)} of them carry a keybind.");
        Close(true);
    }

    private string Loc(string key) => _edit.Localization[key];
}
