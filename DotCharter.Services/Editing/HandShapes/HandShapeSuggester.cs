using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     The detector's limits. The defaults were tuned with <c>tools/chart-eval/HandShapeEval.cs</c>
///     on the owner's finished charts (train split); the held-out numbers are in that tool's README.
/// </summary>
/// <param name="MinSounds">Fewest sounds a run must hold. Fewer is a figure, not a grip.</param>
/// <param name="MinStrings">Fewest strings the grip must use when the chart already declares a chord
/// shape containing it (the arpeggio of a chord played elsewhere).</param>
/// <param name="MinStringsAlone">Fewest strings a grip no declared template contains must use.</param>
/// <param name="MaxFretSpread">Widest distance between the lowest and the highest FRETTED fret
/// (3 = a four-fret box, one finger per fret).</param>
/// <param name="MaxGap">Longest silence (quarter-note beats) between one sound's end and the next
/// sound's start that a bar may carry across.</param>
/// <param name="OneChordShape">A run holds ONE chord shape (repeated, or strummed in part): a chord
/// that is neither inside nor around the first one starts a new bar, the planner's rule for chords.</param>
public sealed record HandShapeSuggestOptions(
    int MinSounds = 3,
    int MinStrings = 3,
    int MinStringsAlone = 4,
    int MaxFretSpread = 3,
    double MaxGap = 2.0,
    bool OneChordShape = true)
{
    public static HandShapeSuggestOptions Default { get; } = new();
}

/// <summary>
///     One proposed bar: its half-open span, the grip the run plays (string → fret, absolute frets)
///     and the template it would reuse. <see cref="Existing" /> is a PLAIN template the arrangement
///     already declares with exactly these frets, or else the closest one CONTAINING them
///     (<see cref="ChordShapeSuperset" />); null means accepting it mints one.
/// </summary>
public sealed record SuggestedHandShape(
    FractionalPosition Position,
    FractionalPosition EndPosition,
    IReadOnlyDictionary<int, int> Frets,
    ChordTemplate? Existing,
    int SoundCount,
    int NoteCount)
{
    /// <summary>True when the span holds <paramref name="position" /> — the bar's own
    /// half-open rule (<see cref="HandShape.Covers" />).</summary>
    public bool Covers(FractionalPosition position) => position.IsWithin(Position, EndPosition);
}

/// <summary>
///     SUGGESTS hand shapes over runs of single notes that all belong to one grip — an arpeggiated
/// chord, a let-ring figure, a riff played from one barre. The finished charts the owner
///     vouches for put 516 of 2,909 single notes under a bar against 74 in our import, and every one
///     of those bars was PLAIN, so the detector proposes plain bars; arpeggio is the accepting
///     gesture's choice.
///     <para>
///         Pure: it reads the level and the arrangement's templates and never writes either. A run
///         is a maximal stretch of sounds no hand shape covers, in which
///         <list type="bullet">
///             <item>no string is played at two different frets (that is a second grip);</item>
///             <item>the fretted frets stay inside <see cref="HandShapeSuggestOptions.MaxFretSpread" />
///             and <see cref="ChordTemplateFingerSetter" /> can finger the whole grip (no position shift);</item>
///             <item>no gap is longer than <see cref="HandShapeSuggestOptions.MaxGap" />, no slide leaves
///             the shape before the run ends, and no FHP anchor or existing bar starts inside it
///             (an anchor inside a bar is I20).</item>
///         </list>
///         A run becomes a suggestion when it holds at least
///         <see cref="HandShapeSuggestOptions.MinSounds" /> sounds on
///         <see cref="HandShapeSuggestOptions.MinStrings" /> strings and at least one single note —
///         a run of chords alone is <see cref="ChordHandShapePlanner" />'s job. A fret-hand-muted
///         sound neither adds to the grip nor breaks it.
///     </para>
/// </summary>
public static class HandShapeSuggester
{
    public static List<SuggestedHandShape> Suggest(
        Arrangement arrangement, ChartLevel level, HandShapeSuggestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        options ??= HandShapeSuggestOptions.Default;
        // Bass charters never bar single notes: 0 of 1,101 proposed bass notes sat under one of
        // their bars (tools/chart-eval/HandShapeEval.cs --kind bass, both splits).
        if (arrangement.ArrangementType == ArrangementType.Bass) return [];

        using var pass = new HandShapeSuggestPass(arrangement, level, options);
        return pass.Run();
    }

    /// <summary>
    ///     The plain template to reuse: one with exactly these frets, else the closest declared
    ///     shape that CONTAINS them — the full chord an arpeggio picks a few strings of.
    /// </summary>
    public static ChordTemplate? ExistingTemplate(Arrangement arrangement, IReadOnlyDictionary<int, int> grip)
    {
        if (ChordTemplateResolver.FindByShape(arrangement, grip, ArpeggioFlavour.Plain) is { } exact) return exact;
        var seed = new ChordTemplate();
        foreach (var (stringIndex, fret) in grip) seed.Frets[stringIndex] = fret;
        var containing = ChordShapeSuperset.FindContaining(arrangement, seed, 1);
        return containing.Count > 0 ? containing[0] : null;
    }

    /// <summary>
    ///     The grip fits one hand position: its fretted frets span at most
    ///     <paramref name="maxSpread" />, and the fingering heuristic gives every fretted string a
    ///     finger (it gives up on a shape it cannot finger, e.g. five separate press points).
    /// </summary>
    internal static bool CanHold(IReadOnlyDictionary<int, int> grip, int capo, int stringCount, int maxSpread)
    {
        var strings = new int[grip.Count];
        var frets = new int[grip.Count];
        var slot = 0;
        foreach (var (stringIndex, fret) in grip)
        {
            strings[slot] = stringIndex;
            frets[slot++] = fret;
        }

        return CanHold(strings, frets, slot, capo, stringCount, maxSpread, new ChordTemplate());
    }

    /// <summary><see cref="CanHold(IReadOnlyDictionary{int, int}, int, int, int)" /> over parallel
    /// string and fret arrays, fingering on <paramref name="probe" /> (cleared first) so a caller
    /// judging many grips reuses one template.</summary>
    internal static bool CanHold(int[] strings, int[] frets, int count, int capo, int stringCount, int maxSpread,
        ChordTemplate probe)
    {
        int low = int.MaxValue, high = int.MinValue;
        for (var k = 0; k < count; k++)
        {
            if (frets[k] <= capo) continue;
            low = Math.Min(low, frets[k]);
            high = Math.Max(high, frets[k]);
        }

        if (high == int.MinValue) return true;
        if (high - low > maxSpread) return false;

        probe.Frets.Clear();
        probe.Fingers.Clear();
        for (var k = 0; k < count; k++) probe.Frets[strings[k]] = frets[k];
        ChordTemplateFingerSetter.SetSuggestedFingers(probe, stringCount);
        for (var k = 0; k < count; k++)
        {
            if (frets[k] <= capo) continue;
            if (!probe.Fingers.TryGetValue(strings[k], out var finger) || finger is < 0 or > 4) return false;
        }

        return true;
    }
}
