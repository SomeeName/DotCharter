using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Services.Editing.ChordShapes;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The verbs a card hands back to the panel that owns it.</summary>
internal interface IShapeRowOwner
{
    void Edit(ShapePanelRowViewModel row);

    void Place(ShapePanelRowViewModel row);

    void SelectUses(ShapePanelRowViewModel row);

    void NextUse(ShapePanelRowViewModel row);

    void Split(ShapePanelRowViewModel row);

    void BeginMerge(ShapePanelRowViewModel row);

    void MergeHere(ShapePanelRowViewModel row);

    void Delete(ShapePanelRowViewModel row);

    void Fix(ShapePanelRowViewModel row);

    void CommitName(ShapePanelRowViewModel row, string name);

    void SetNoName(ShapePanelRowViewModel row, bool value);

    /// <summary>A card was ticked or unticked.</summary>
    void SelectionChanged();

    /// <summary>The drag-to-merge verbs.</summary>
    ShapesDragVerbs Drag { get; }
}

/// <summary>
///     One template's card. Kept across refreshes for as long as its template is in the
///     arrangement, so an edit updates the card's text in place instead of rebuilding the list.
///     A name being typed survives a refresh: it is replaced only while the box still holds
///     what the shape was last read with.
/// </summary>
public sealed partial class ShapePanelRowViewModel : ObservableObject, IShapePanelRow
{
    private readonly ILocalizationService _localization;
    private readonly IShapeRowOwner _owner;
    private string _readName = "";
    private bool _isNoName;

    [ObservableProperty] private string _nameText = "";
    [ObservableProperty] private string _namePlaceholder = "";
    [ObservableProperty] private string _fretsText = "";
    [ObservableProperty] private ChordTemplate _diagram = new();
    [ObservableProperty] private Chord _diagramChord = new();
    [ObservableProperty] private int _stringCount = 6;
    [ObservableProperty] private string _usesText = "";
    [ObservableProperty] private bool _isUnused;
    [NotifyPropertyChangedFor(nameof(HasIssue))]
    [ObservableProperty] private string _issueText = "";
    [ObservableProperty] private string _fixLabel = "";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isMergeSource;
    [ObservableProperty] private bool _isMergeTarget;
    [ObservableProperty] private bool _canMerge;
    [ObservableProperty] private bool _canMergeHere;
    [ObservableProperty] private bool _canSplitSelection;

    internal ShapePanelRowViewModel(ChordTemplate template, IShapeRowOwner owner, ILocalizationService localization)
    {
        Template = template;
        _owner = owner;
        _localization = localization;
        CommitNameCommand = new RelayCommand(() => _owner.CommitName(this, NameText));
        FixCommand = new RelayCommand(() => _owner.Fix(this));
        EditCommand = new RelayCommand(() => _owner.Edit(this));
        PlaceCommand = new RelayCommand(() => _owner.Place(this));
        SelectUsesCommand = new RelayCommand(() => _owner.SelectUses(this));
        NextUseCommand = new RelayCommand(() => _owner.NextUse(this));
        SplitSelectionCommand = new RelayCommand(() => _owner.Split(this));
        BeginMergeCommand = new RelayCommand(() => _owner.BeginMerge(this));
        MergeHereCommand = new RelayCommand(() => _owner.MergeHere(this));
        BeginDragCommand = new RelayCommand(() => _owner.Drag.Begin(this));
        DragOverCommand = new RelayCommand<bool>(entered => _owner.Drag.Over(this, entered));
        DropCommand = new RelayCommand(() => _owner.Drag.Drop(this));
        EndDragCommand = new RelayCommand(() => _owner.Drag.End());
        DeleteCommand = new RelayCommand(() => _owner.Delete(this));
    }

    /// <summary>The chart's own template; the view only ever sees <see cref="Diagram" />.</summary>
    internal ChordTemplate Template { get; }

    internal ChordShapeEntry? Entry { get; private set; }

    internal ChordShapeIssue Issue { get; private set; } = ChordShapeIssue.None;

    /// <summary>The name, or the fret pattern when the shape has none: how the panel's sentences call it.</summary>
    internal string Title { get; private set; } = "";

    public bool IsNoName
    {
        get => _isNoName;
        set
        {
            if (value == _isNoName) return;
            _owner.SetNoName(this, value);
            OnPropertyChanged();
        }
    }

    public bool HasIssue => IssueText.Length > 0;

    public bool CanTick => true;

    public bool CanEdit => true;

    public ICommand CommitNameCommand { get; }

    public ICommand FixCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand PlaceCommand { get; }

    public ICommand SelectUsesCommand { get; }

    public ICommand NextUseCommand { get; }

    public ICommand SplitSelectionCommand { get; }

    public ICommand BeginMergeCommand { get; }

    public ICommand MergeHereCommand { get; }

    public ICommand BeginDragCommand { get; }

    public ICommand DragOverCommand { get; }

    public ICommand DropCommand { get; }

    public ICommand EndDragCommand { get; }

    public ICommand DeleteCommand { get; }

    /// <summary>Re-reads the card from a fresh catalog entry; a value that did not move raises nothing.</summary>
    internal void Update(ChordShapeEntry entry, ChordShapeIssue issue, string keeperTitle, int stringCount)
    {
        Entry = entry;
        Issue = issue;
        var template = entry.Template;
        StringCount = Math.Max(1, stringCount);
        FretsText = ChordShapeNames.FretPattern(template, StringCount);
        NamePlaceholder = FretsText;
        Title = ChordShapeNames.DisplayName(template, StringCount);

        // A draft the user is typing is theirs: the box follows the shape only while it still holds what was read last.
        var wasDraft = !string.Equals(NameText, _readName, StringComparison.Ordinal);
        _readName = template.Unnamed ? "" : template.Name;
        if (!wasDraft) NameText = _readName;
        if (_isNoName != template.Unnamed)
        {
            _isNoName = template.Unnamed;
            OnPropertyChanged(nameof(IsNoName));
        }

        if (!SameDrawing(Diagram, template))
        {
            Diagram = template.Clone();
            DiagramChord = ChordOf(template);
        }

        var uses = entry.Uses;
        IsUnused = uses.All == 0;
        UsesText = IsUnused ? _localization["ChordShapes_Unused"] : UsesOf(uses.ChordsAll, uses.HandShapesAll);
        CanMerge = !IsUnused && entry.IsDuplicate;

        IssueText = issue.Kind switch
        {
            ChordShapeIssueKind.SameFrets => _localization.Format("Shapes_Issue_SameFrets", keeperTitle),
            ChordShapeIssueKind.NoFingering => _localization["Shapes_Issue_NoFingering"],
            _ => ""
        };
        FixLabel = issue.Kind switch
        {
            ChordShapeIssueKind.SameFrets => _localization["Shapes_Fix_Merge"],
            ChordShapeIssueKind.NoFingering => _localization["Shapes_Fix_Finger"],
            _ => ""
        };
    }

    /// <summary>"6 chords", "6 chords · 2 hand shapes", "2 hand shapes": only the parts that are there.</summary>
    internal string UsesOf(int chords, int handShapes) => UsesWords(_localization, chords, handShapes);

    internal static string UsesWords(ILocalizationService localization, int chords, int handShapes)
    {
        var chordText = localization.Plural("ChordShapes_ChordsOne", "ChordShapes_Chords", chords);
        var shapeText = localization.Plural("ChordShapes_HandShapesOne", "ChordShapes_HandShapes", handShapes);
        return handShapes == 0 ? chordText
            : chords == 0 ? shapeText
            : localization.Format("ChordShapes_UsesPair", chordText, shapeText);
    }

    partial void OnIsSelectedChanged(bool value) => _owner.SelectionChanged();

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
}
