using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>The Sound tab's sections — one rail entry each.</summary>
public enum SoundSection
{
    /// <summary>Nothing selected: the next thing that needs work, and the playhead.</summary>
    Task,

    /// <summary>Nothing selected: the chord library (the read-only template browser).</summary>
    Library,

    /// <summary>One note: the fretboard, string, fret, length, slide target.</summary>
    Placement,

    /// <summary>A chord or a hand shape: the chord box, fingering, name — and a hand shape's span.</summary>
    Shape,

    /// <summary>Several sounds: what is selected and the bulk verbs.</summary>
    Selection,

    /// <summary>The techniques — a chord's per-string set AND its whole-chord flags.</summary>
    Technique,

    /// <summary>One note or one chord: the bend curve.</summary>
    Bends,

    /// <summary>The findings on the selection.</summary>
    Findings
}

/// <summary>
/// Which section of the Sound tab is on screen ("Rail (dense)").
///     <para>
///         The tab is an icon-only RAIL of sections beside ONE section at a time. Which sections
///         the rail offers is a function of the selection type (<see cref="For" />); the one on
///         screen is remembered PER TYPE for the session, so stepping from chord to chord stays on
///         Technique while a note still opens on Placement. Playback is not a section: the
///         whole-chord flags are a group inside Technique.
///     </para>
///     <para>
///         The visibility matrix lives here, as named booleans, so it reads in one place and the
///         default gate pins it without a view (<c>SoundSectionsViewModelTests</c>). It replaced
///         the earlier segments (<c>SoundSegmentsViewModel</c>), whose two jobs it keeps: what the
///         pinned "Apply to" row governs (<see cref="ApplyFamily" />), and whether a chord's edits
///         are reached by the ladder at all (<see cref="HasChordScope" />).
///     </para>
/// </summary>
public sealed partial class SoundSectionsViewModel : ObservableObject
{
    private readonly Dictionary<SoundSelectionKind, SoundSection> _chosen = [];
    private SoundSelectionKind _kind;

    /// <summary>What is selected — the inspector forwards <c>InspectorViewModel.SelectionKind</c>.</summary>
    public SoundSelectionKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            NotifyLayout();
        }
    }

    /// <summary>The sections the rail offers for the current selection, in rail order.</summary>
    public IReadOnlyList<SoundSection> Sections => For(_kind);

    /// <summary>The section on screen: the one last chosen for this selection type, else the first.</summary>
    public SoundSection Section =>
        _chosen.TryGetValue(_kind, out var chosen) && Sections.Contains(chosen) ? chosen : Sections[0];

    /// <summary>
    ///     The rail for each selection type. The first is the default. A section a type does not
    ///     have is never offered: a hand shape has no Technique, a multi-selection no Bends (a
    ///     curve editor over several curves cannot tell the truth about any of them), nothing
    ///     selected has no Findings.
    /// </summary>
    public static IReadOnlyList<SoundSection> For(SoundSelectionKind kind) => kind switch
    {
        SoundSelectionKind.Empty => [SoundSection.Task, SoundSection.Library],
        SoundSelectionKind.Note => [SoundSection.Placement, SoundSection.Technique, SoundSection.Bends, SoundSection.Findings],
        SoundSelectionKind.Chord => [SoundSection.Shape, SoundSection.Technique, SoundSection.Bends, SoundSection.Findings],
        SoundSelectionKind.HandShape => [SoundSection.Shape, SoundSection.Findings],
        SoundSelectionKind.ShapeAndSounds => [SoundSection.Shape, SoundSection.Technique, SoundSection.Findings],
        _ => [SoundSection.Selection, SoundSection.Technique, SoundSection.Findings]
    };

    /// <summary>
    ///     Whether the ONE pinned "Apply to" row shows over a section. The ladder governs a chord's
    /// shape, its technique and whole-chord edits and its bend curve,
    ///     so it pins over Shape of a chord or hand shape and over Technique and Bends of a chord; a
    ///     note, a multi-selection and Findings have none — an edit is never broader than the bar
    ///     above it. A chord's bend edits followed the rung before the row showed over them; that
    ///     was the one edit broader than its bar.
    /// </summary>
    public static bool ShowsApplyBar(SoundSelectionKind kind, SoundSection section) => section switch
    {
        SoundSection.Shape => kind is SoundSelectionKind.Chord or SoundSelectionKind.HandShape or SoundSelectionKind.ShapeAndSounds,
        SoundSection.Technique or SoundSection.Bends => IsChordScope(kind),
        _ => false
    };

    /// <summary>A chord is in the selection, so the ladder reaches its technique edits.</summary>
    public bool HasChordScope => IsChordScope(_kind);

    public bool ShowApplyBar => ShowsApplyBar(_kind, Section);

    /// <summary>
    /// Which edits the pinned ladder counts and previews: the techniques' while a chord's
    /// Technique or Bends section is on screen (a bend is written like a technique — the
    ///     chords only, "selected" the whole selection), the shape's otherwise.
    /// </summary>
    public ChordApplyFamily ApplyFamily =>
        HasChordScope && Section is SoundSection.Technique or SoundSection.Bends
            ? ChordApplyFamily.Technique
            : ChordApplyFamily.Shape;

    public bool OffersTask => Offers(SoundSection.Task);

    public bool OffersLibrary => Offers(SoundSection.Library);

    public bool OffersPlacement => Offers(SoundSection.Placement);

    public bool OffersShape => Offers(SoundSection.Shape);

    public bool OffersSelection => Offers(SoundSection.Selection);

    public bool OffersTechnique => Offers(SoundSection.Technique);

    public bool OffersBends => Offers(SoundSection.Bends);

    public bool OffersFindings => Offers(SoundSection.Findings);

    public bool IsTask => Section == SoundSection.Task;

    public bool IsLibrary => Section == SoundSection.Library;

    public bool IsPlacement => Section == SoundSection.Placement;

    public bool IsShape => Section == SoundSection.Shape;

    public bool IsSelection => Section == SoundSection.Selection;

    public bool IsTechnique => Section == SoundSection.Technique;

    public bool IsBends => Section == SoundSection.Bends;

    public bool IsFindings => Section == SoundSection.Findings;

    /// <summary>Technique for a chord: the per-string set behind the string mask, then the whole-chord flags.</summary>
    public bool ShowChordTechnique => IsTechnique && HasChordScope;

    /// <summary>Technique for one note or a multi-selection without a chord's scope.</summary>
    public bool ShowNoteTechnique => IsTechnique && !HasChordScope;

    /// <summary>
    ///     A click on a rail entry. Re-announces the flags even when the section did not change: a
    ///     click on the lit entry unchecks it locally, and its OneWay binding only re-reads on a
    ///     notification.
    /// </summary>
    [RelayCommand]
    private void Choose(SoundSection section)
    {
        if (Sections.Contains(section)) _chosen[_kind] = section;
        NotifyLayout();
    }

    private static bool IsChordScope(SoundSelectionKind kind) => kind is SoundSelectionKind.Chord or SoundSelectionKind.ShapeAndSounds;

    private bool Offers(SoundSection section) => Sections.Contains(section);

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(HasChordScope));
        OnPropertyChanged(nameof(ShowApplyBar));
        OnPropertyChanged(nameof(ApplyFamily));
        OnPropertyChanged(nameof(OffersTask));
        OnPropertyChanged(nameof(OffersLibrary));
        OnPropertyChanged(nameof(OffersPlacement));
        OnPropertyChanged(nameof(OffersShape));
        OnPropertyChanged(nameof(OffersSelection));
        OnPropertyChanged(nameof(OffersTechnique));
        OnPropertyChanged(nameof(OffersBends));
        OnPropertyChanged(nameof(OffersFindings));
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsPlacement));
        OnPropertyChanged(nameof(IsShape));
        OnPropertyChanged(nameof(IsSelection));
        OnPropertyChanged(nameof(IsTechnique));
        OnPropertyChanged(nameof(IsBends));
        OnPropertyChanged(nameof(IsFindings));
        OnPropertyChanged(nameof(ShowChordTechnique));
        OnPropertyChanged(nameof(ShowNoteTechnique));
    }
}
