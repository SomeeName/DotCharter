namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The Placement page's staged values for one note; null leaves a field alone. <see cref="SlideSet" />
///     says the slide end was staged (<see cref="SlideTo" /> null then clears it).
/// </summary>
public sealed record PlacementDraft
{
    public int? StringIndex { get; init; }

    public int? Fret { get; init; }

    public FractionalPosition? Sustain { get; init; }

    public bool SlideSet { get; init; }

    public int? SlideTo { get; init; }

    public bool IsEmpty => StringIndex is null && Fret is null && Sustain is null && !SlideSet;

    /// <summary>The same draft without the fields <paramref name="note" /> already holds (a re-read baseline).</summary>
    public PlacementDraft Without(GuitarNote note) => this with
    {
        StringIndex = StringIndex == note.StringIndex ? null : StringIndex,
        Fret = Fret == note.Fret ? null : Fret,
        Sustain = Sustain == note.Sustain ? null : Sustain,
        SlideSet = SlideSet && SlideTo != note.SlideTo,
        SlideTo = SlideSet && SlideTo != note.SlideTo ? SlideTo : null
    };
}

/// <summary>
///     The Placement page, pure: a single note's string, fret, sustain and slide end. A chord is not
///     placed here (its frets are the Shape page's). A fret move CARRIES the slide end the charter did not
///     stage, by the same number of frets (behaviour 5): the keyboard's fret verbs leave it behind. A string
///     move carries everything else by itself, since a note's techniques and bend live on the note. A
///     placement whose fret or carried slide would leave the neck writes nothing and says so.
/// </summary>
internal static class StagedPlacement
{
    internal static StagedEditPlan Plan(ITimedElement? source, PlacementDraft draft, int capo, int stringCount)
    {
        if (draft.IsEmpty || source is not GuitarNote note) return StagedEditPlan.Empty;
        if (draft.StringIndex is { } s && (s < 0 || s >= stringCount) ||
            draft.Fret is { } fret && !(fret == 0 || fret >= capo && fret <= FretLimits.MaxFret))
        {
            return StagedEditPlan.From([], [new StagedSkip(note, StagedSkipReason.LeavesNeck)], []);
        }

        return SlideEnd(note, draft) is { } end && !StagedTechnique.OnNeck(end, capo)
            ? StagedEditPlan.From([], [new StagedSkip(note, StagedSkipReason.SlideOffNeck)], [])
            : StagedEditPlan.From([note], [], []);
    }

    /// <summary>Where the note's slide ends after the placement: the staged end, or the old one carried with the fret.</summary>
    internal static int? SlideEnd(GuitarNote note, PlacementDraft draft) => draft.SlideSet
        ? draft.SlideTo
        : note.SlideTo is { } end && draft.Fret is { } fret ? end + fret - note.Fret : note.SlideTo;

    internal static Action<GuitarNote> Write(GuitarNote source, PlacementDraft draft)
    {
        var slide = SlideEnd(source, draft);
        return note =>
        {
            if (draft.StringIndex is { } s) note.StringIndex = s;
            if (draft.Fret is { } fret) note.Fret = fret;
            if (draft.Sustain is { } sustain) note.Sustain = sustain;
            note.SlideTo = slide;
        };
    }
}
