using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     The Pattern builder: what "alike" means as data the charter authors, tries
///     against the live chart and binds to keys. Every pattern in the rail carries
///     its live match count against the current selection, so definitions can be
///     compared side by side. <see cref="IDialogScreen{TResult}.Result" /> is true
///     once saved.
/// </summary>
public interface IPatternBuilderScreen : IDialogScreen<bool>
{
    IReadOnlyList<IMatchPatternRow> Patterns { get; }

    IMatchPatternRow? SelectedPattern { get; set; }

    /// <summary>Duplicate, Delete and the three try-bar verbs need a picked pattern.</summary>
    bool HasSelectedPattern { get; }

    /// <summary>Why those five are greyed; empty while a pattern is picked.</summary>
    string NoPatternSelectedReason { get; }

    /// <summary>What the selected pattern finds right now, or why it finds nothing.</summary>
    string MatchSummary { get; }

    /// <summary>"3 items selected as the seed".</summary>
    string SeedSummary { get; }

    bool HasSeed { get; }

    /// <summary>The seed sits on another arrangement than the track the patterns run against.</summary>
    bool SeedIsFromAnotherArrangement { get; }

    /// <summary>A seed on the track the patterns run against: the try bar's OK state.</summary>
    bool SeedIsUsable { get; }

    /// <summary>A key on the selected pattern that another pattern or a built-in keybind already owns.</summary>
    bool HasConflict { get; }

    string ConflictSummary { get; }

    ICommand AddPatternCommand { get; }

    /// <summary>Copies the facets and drops the keys.</summary>
    ICommand DuplicatePatternCommand { get; }

    ICommand DeletePatternCommand { get; }

    /// <summary>Brings back the built-ins that are missing; an edited built-in keeps its edits.</summary>
    ICommand RestoreBuiltInsCommand { get; }

    ICommand RunSelectCommand { get; }

    ICommand RunNextCommand { get; }

    ICommand RunPreviousCommand { get; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}

/// <summary>One editable pattern: a working copy nothing persists before Save.</summary>
public interface IMatchPatternRow : INotifyPropertyChanged
{
    string Name { get; set; }

    /// <summary>The live count against the current seed; empty without a seed.</summary>
    string MatchCountLabel { get; }

    /// <summary>The pattern in one line, for the rail.</summary>
    string Summary { get; }

    /// <summary>The bound chords, "Ctrl+F1 · Shift+F2"; empty with none.</summary>
    string KeyLabel { get; }

    bool HasKeys { get; }

    bool ScopeNotes { get; set; }

    bool ScopeChords { get; set; }

    bool ScopeHandShapes { get; set; }

    bool ScopeAnchors { get; set; }

    IPatternChoices Choices { get; }

    IPatternChoice SelectedGrouping { get; set; }

    IPatternChoice SelectedRange { get; set; }

    IPatternChoice SelectedFretRule { get; set; }

    IPatternChoice SelectedStringRule { get; set; }

    /// <summary>Only a run has internal spacing; see <see cref="IsRun" />.</summary>
    IPatternChoice SelectedRhythm { get; set; }

    IPatternChoice SelectedSustain { get; set; }

    int MaxFretDistance { get; set; }

    bool MatchSize { get; set; }

    bool MatchSoundKind { get; set; }

    bool MatchBeatOffset { get; set; }

    bool MatchFingering { get; set; }

    bool MatchChordName { get; set; }

    bool MatchArpeggio { get; set; }

    bool MatchAnchorWidth { get; set; }

    bool MatchAnchorSpan { get; set; }

    bool IsRun { get; }

    /// <summary>Fingering, chord name and arpeggio come from a template: chords and hand shapes only.</summary>
    bool ShowsTemplateFacets { get; }

    bool ShowsAnchorFacets { get; }

    IReadOnlyList<IPatternToggle> Techniques { get; }

    /// <summary>What a match drags along: its hand shape, its anchor, the notes inside it.</summary>
    IReadOnlyList<IPatternToggle> Companions { get; }

    KeyChord SelectKey { get; set; }

    KeyChord NextKey { get; set; }

    KeyChord PreviousKey { get; set; }
}

/// <summary>The picker lists, shared by every row.</summary>
public interface IPatternChoices
{
    IReadOnlyList<IPatternChoice> Frets { get; }

    IReadOnlyList<IPatternChoice> Strings { get; }

    IReadOnlyList<IPatternChoice> Grouping { get; }

    IReadOnlyList<IPatternChoice> Rhythm { get; }

    IReadOnlyList<IPatternChoice> Sustain { get; }

    IReadOnlyList<IPatternChoice> Range { get; }
}

public interface IPatternChoice
{
    string Label { get; }
}

public interface IPatternToggle : INotifyPropertyChanged
{
    string Label { get; }

    /// <summary>Empty for a technique; a companion explains itself.</summary>
    string Description { get; }

    bool IsSelected { get; set; }
}
