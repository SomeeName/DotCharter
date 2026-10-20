using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The verbs a row hands back to the list that owns it.</summary>
internal interface IChordShapeRowOwner
{
    void Place(ChordShapeRowViewModel row);

    void SelectUses(ChordShapeRowViewModel row);

    void NextUse(ChordShapeRowViewModel row);

    void Rename(ChordShapeRowViewModel row, string name);

    void Merge(ChordShapeRowViewModel row, ChordTemplate target);

    void Split(ChordShapeRowViewModel row);
}

/// <summary>
///     One template's row. Kept across refreshes for as long as its template is in the
///     arrangement, so an edit updates the row's text in place instead of rebuilding the list.
/// </summary>
public sealed partial class ChordShapeRowViewModel : ObservableObject, IChordShapeRow
{
    private readonly ILocalizationService _localization;
    private readonly IChordShapeRowOwner _owner;
    private IReadOnlyList<ChordTemplate> _duplicates = [];

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isUnnamed;
    [ObservableProperty] private string _fretsText = "";
    [ObservableProperty] private ChordTemplate _diagram = new();
    [ObservableProperty] private Chord _diagramChord = new();
    [ObservableProperty] private int _stringCount = 6;
    [ObservableProperty] private bool _isArpeggio;
    [ObservableProperty] private string _fingerStateText = "";
    [ObservableProperty] private bool _isFingeringMissing;
    [ObservableProperty] private bool _isFingeringSuggested;
    [ObservableProperty] private string _levelUsesText = "";
    [ObservableProperty] private string _allUsesText = "";
    [ObservableProperty] private bool _isUnused;
    [ObservableProperty] private string _duplicateText = "";
    [ObservableProperty] private bool _isDuplicate;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _nameDraft = "";
    [ObservableProperty] private IReadOnlyList<IChordShapeMergeTarget> _mergeTargets = [];
    [ObservableProperty] private bool _isChoosingMergeTarget;
    [ObservableProperty] private bool _canSplitSelection;

    internal ChordShapeRowViewModel(ChordTemplate template, IChordShapeRowOwner owner, ILocalizationService localization)
    {
        Template = template;
        _owner = owner;
        _localization = localization;
        PlaceCommand = new RelayCommand(() => _owner.Place(this));
        SelectUsesCommand = new RelayCommand(() => _owner.SelectUses(this));
        NextUseCommand = new RelayCommand(() => _owner.NextUse(this));
        BeginRenameCommand = new RelayCommand(BeginRename);
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        SplitSelectionCommand = new RelayCommand(() => _owner.Split(this));
        ChooseMergeTargetCommand = new RelayCommand(() => IsChoosingMergeTarget = !IsChoosingMergeTarget);
    }

    /// <summary>The chart's own template; the view only ever sees <see cref="Diagram" />.</summary>
    internal ChordTemplate Template { get; }

    internal ChordShapeEntry? Entry { get; private set; }

    public ICommand PlaceCommand { get; }

    public ICommand SelectUsesCommand { get; }

    public ICommand NextUseCommand { get; }

    public ICommand BeginRenameCommand { get; }

    public ICommand CommitRenameCommand { get; }

    public ICommand CancelRenameCommand { get; }

    public ICommand SplitSelectionCommand { get; }

    public ICommand ChooseMergeTargetCommand { get; }

    /// <summary>Re-reads the row from a fresh catalog entry; a value that did not move raises nothing.</summary>
    internal void Update(ChordShapeEntry entry, int stringCount, IReadOnlyDictionary<ChordTemplate, int> usesByTemplate)
    {
        Entry = entry;
        var template = entry.Template;
        StringCount = Math.Max(1, stringCount);
        FretsText = ChordShapeNames.FretPattern(template, StringCount);
        IsUnnamed = ChordShapeNames.IsUnnamed(template);
        Name = ChordShapeNames.DisplayName(template, StringCount);
        if (!SameDrawing(Diagram, template))
        {
            Diagram = template.Clone();
            DiagramChord = ChordOf(template);
        }

        IsArpeggio = template.IsArpeggio;

        IsFingeringMissing = entry.FingerState == ChordFingerState.Missing;
        IsFingeringSuggested = entry.FingerState == ChordFingerState.Suggested;
        FingerStateText = FingerWord(entry.FingerState);

        var uses = entry.Uses;
        IsUnused = uses.All == 0;
        LevelUsesText = IsUnused
            ? _localization["ChordShapes_Unused"]
            : _localization.Format("ChordShapes_LevelUses", UsesText(uses.ChordsOnLevel, uses.HandShapesOnLevel));
        // Only when other levels add to it: on a one-level chart the second line repeated the first.
        AllUsesText = uses.OnOtherLevels == 0
            ? ""
            : _localization.Format("ChordShapes_AllUses", UsesText(uses.ChordsAll, uses.HandShapesAll));

        IsDuplicate = entry.IsDuplicate;
        DuplicateText = entry.IsDuplicate
            ? _localization.Plural("ChordShapes_DuplicateOne", "ChordShapes_Duplicate", entry.Duplicates.Count)
            : "";
        UpdateMergeTargets(entry.Duplicates, stringCount, usesByTemplate);
        if (!entry.IsDuplicate) IsChoosingMergeTarget = false;
    }

    internal string UsesText(int chords, int handShapes) =>
        _localization.Format("ChordShapes_UsesPair",
            _localization.Plural("ChordShapes_ChordsOne", "ChordShapes_Chords", chords),
            _localization.Plural("ChordShapes_HandShapesOne", "ChordShapes_HandShapes", handShapes));

    private void UpdateMergeTargets(
        IReadOnlyList<ChordTemplate> duplicates, int stringCount, IReadOnlyDictionary<ChordTemplate, int> usesByTemplate)
    {
        var labels = duplicates.Select(t => Label(t, stringCount, usesByTemplate)).ToList();
        var same = duplicates.Count == _duplicates.Count
                   && duplicates.Zip(_duplicates).All(p => ReferenceEquals(p.First, p.Second))
                   && labels.SequenceEqual(MergeTargets.Select(m => m.Label), StringComparer.Ordinal);
        if (same) return;

        _duplicates = duplicates;
        MergeTargets = duplicates
            .Select((t, i) => (IChordShapeMergeTarget)new ChordShapeMergeTargetViewModel(labels[i], () => _owner.Merge(this, t)))
            .ToList();
    }

    private string Label(ChordTemplate template, int stringCount, IReadOnlyDictionary<ChordTemplate, int> usesByTemplate)
    {
        var name = ChordShapeNames.DisplayName(template, stringCount);
        var uses = usesByTemplate.TryGetValue(template, out var count) ? count : 0;
        return _localization.Format("ChordShapes_MergeTarget", name,
            FingerWord(ChordShapeCatalog.FingerStateOf(template, stringCount)),
            _localization.Plural("ChordShapes_UseCountOne", "ChordShapes_UseCount", uses));
    }

    private string FingerWord(ChordFingerState state) => _localization[state switch
    {
        ChordFingerState.Missing => "ChordShapes_Finger_Missing",
        ChordFingerState.Suggested => "ChordShapes_Finger_Suggested",
        _ => "ChordShapes_Finger_Complete"
    }];

    private static Chord ChordOf(ChordTemplate template)
    {
        var chord = new Chord { TemplateId = template.Id };
        foreach (var (stringIndex, fret) in template.Frets.OrderBy(f => f.Key))
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    private static bool SameDrawing(ChordTemplate drawn, ChordTemplate template) =>
        drawn.HasSameShapeAs(template) && drawn.IsArpeggio == template.IsArpeggio
                                       && string.Equals(drawn.Name, template.Name, StringComparison.Ordinal);

    private void BeginRename()
    {
        NameDraft = Template.Name;
        IsRenaming = true;
    }

    private void CommitRename()
    {
        IsRenaming = false;
        _owner.Rename(this, NameDraft);
    }
}

/// <summary>One "merge into" choice of a row.</summary>
public sealed class ChordShapeMergeTargetViewModel(string label, Action merge) : ObservableObject, IChordShapeMergeTarget
{
    public string Label { get; } = label;

    public ICommand MergeCommand { get; } = new RelayCommand(merge);
}
