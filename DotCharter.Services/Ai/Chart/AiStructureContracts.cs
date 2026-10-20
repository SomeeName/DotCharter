namespace DotCharter.Services.Ai.Chart;

/// <summary>Which ribbon a structure change is on.</summary>
public enum AiStructureKind
{
    /// <summary>A Rocksmith section (<see cref="EventPoint.Section" />), from <c>RsSectionCatalog</c>.</summary>
    Section = 0,

    /// <summary>A phrase (<see cref="EventPoint.Phrase" />).</summary>
    Phrase = 1
}

/// <summary>The CLOSED set of things the structure job may do. Nothing else exists:
/// the reader refuses any other operation, and the applier writes only these.</summary>
public enum AiStructureOp
{
    Add = 0,
    Rename = 1,
    Move = 2,
    Remove = 3
}

/// <summary>
///     One section or phrase marker as the structure job lists it to the model.
///     <see cref="Id" /> is <c>s</c>/<c>p</c> + its 1-based place in song order.
///     <see cref="Editable" /> is false for a phrase that exists only in the imported phrase list
///     (<c>Arrangement.PhraseIterations</c>): it is shown as context and may not be changed.
/// </summary>
public sealed record AiStructureMarker(
    string Id,
    AiStructureKind Kind,
    FractionalPosition Position,
    string Name,
    bool Editable);

/// <summary>
/// One validated change to the section/phrase ribbon, on the beat grid.
///     <paramref name="From" /> and <paramref name="OldName" /> are the live marker a rename, move or
///     remove acts on — the GUARD the write re-checks, so a marker the charter edited while the
///     proposal waited is never written over. <paramref name="To" /> is where an add or a move puts
///     it; <paramref name="Name" /> is an add's or a rename's new name.
/// </summary>
public sealed record AiStructureChange(
    AiStructureOp Op,
    AiStructureKind Kind,
    FractionalPosition? From,
    string? OldName,
    FractionalPosition? To,
    string? Name)
{
    /// <summary>Where the change shows: the destination of an add or a move, else the marker.</summary>
    public FractionalPosition At => To ?? From ?? FractionalPosition.Zero;

    /// <summary>The name the marker carries after the change (null for a remove).</summary>
    public string? NameAfter => Op switch
    {
        AiStructureOp.Add or AiStructureOp.Rename => Name,
        AiStructureOp.Move => OldName,
        _ => null
    };

    /// <summary>
    /// The hand positions an added or moved PHRASE brings with it: a phrase that plays
    ///     notes must own an anchor from its start to its first note (inspector rule E06), so where
    ///     the chart has none the gate (<see cref="AiStructureGate" />) attaches the one the phrase
    ///     needs (<c>PhraseStartAnchors.Needed</c>) and judges the change WITH it. Never the model's:
    ///     the reply cannot name an anchor. Empty for every other change and wherever an anchor
    ///     already is. Written in the change's own undo step; an existing anchor is never moved
    ///     or removed.
    /// </summary>
    public IReadOnlyList<AiStructureAnchor> Anchors { get; init; } = [];
}

/// <summary>
/// One hand position (FHP anchor) a phrase change adds, by VALUE: the
///     <paramref name="Level" />'s index in the arrangement's levels (the gate judges a copy, the
///     write goes to the live chart), where, the fret and the width.
/// </summary>
public sealed record AiStructureAnchor(int Level, FractionalPosition Position, int Fret, int Width);

/// <summary>A hand position the review marks: the live level it would be added to and the
/// anchor (not on the chart yet).</summary>
public sealed record AiStructureAnchorMark(ChartLevel Level, FretAnchor Anchor);

/// <summary>
/// What a structure write changes on the live ribbon — the review's marks, read off
///     the SAME composition <see cref="AiStructureApplier.Build" /> writes.
///     <paramref name="Sections" /> and <paramref name="Phrases" /> are LIVE event points whose
///     section or phrase the write renames, moves away or removes (drawn faded);
///     <paramref name="Proposed" /> are new event points that are not on the chart (each carries
///     only the name it would get), drawn in the review colour; <paramref name="Anchors" /> the hand
/// positions the write adds for a phrase that needs one.
/// </summary>
public sealed record AiStructureApplyTargets(
    IReadOnlyList<EventPoint> Sections,
    IReadOnlyList<EventPoint> Phrases,
    IReadOnlyList<EventPoint> Proposed,
    IReadOnlyList<AiStructureAnchorMark> Anchors)
{
    /// <summary>Nothing to mark.</summary>
    public static readonly AiStructureApplyTargets None = new([], [], [], []);

    /// <summary>How many marks.</summary>
    public int Count => Sections.Count + Phrases.Count + Proposed.Count + Anchors.Count;
}

/// <summary>
/// The beat grid as the structure job reads and writes it: bars from
///     <see cref="BeatMarker.IsMeasure" />, numbered from 1 (a pickup before the first bar line is
///     bar 0), beats 1-based inside their bar. A position the model names is ALWAYS a beat of this
///     grid — sections on a bar line — so nothing it proposes can sit between beats.
/// </summary>
public sealed class AiStructureGrid
{
    private readonly int[] _barOfBeat;

    private AiStructureGrid(IReadOnlyList<GridBar> bars, int[] barOfBeat)
    {
        Bars = bars;
        _barOfBeat = barOfBeat;
    }

    /// <summary>One bar: its number, its first beat's index in the beat map, its beats and start.</summary>
    public sealed record GridBar(int Number, int FirstBeat, int BeatCount, double StartMs);

    /// <summary>The bars, in song order.</summary>
    public IReadOnlyList<GridBar> Bars { get; }

    /// <summary>The grid of <paramref name="beats" />.</summary>
    public static AiStructureGrid Of(IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        var bars = new List<GridBar>();
        var barOfBeat = new int[beats.Count];
        var number = beats.Count > 0 && beats[0].IsMeasure ? 1 : 0;
        var first = 0;
        for (var i = 1; i <= beats.Count; i++)
        {
            if (i < beats.Count && !beats[i].IsMeasure) continue;
            bars.Add(new GridBar(number, first, i - first, beats.Count == 0 ? 0 : beats[first].Time));
            for (var b = first; b < i; b++) barOfBeat[b] = bars.Count - 1;
            number++;
            first = i;
        }

        if (beats.Count == 0) bars.Clear();
        return new AiStructureGrid(bars, barOfBeat);
    }

    /// <summary>The bar numbered <paramref name="number" />, or null.</summary>
    public GridBar? Bar(int number)
    {
        if (Bars.Count == 0) return null;
        var index = number - Bars[0].Number;
        return index >= 0 && index < Bars.Count ? Bars[index] : null;
    }

    /// <summary>Beat <paramref name="beat" /> (1-based) of bar <paramref name="bar" />, or null
    /// when the grid has no such beat.</summary>
    public FractionalPosition? At(int bar, int beat)
        => Bar(bar) is { } b && beat >= 1 && beat <= b.BeatCount ? new FractionalPosition(b.FirstBeat + beat - 1) : null;

    /// <summary>The bar and 1-based beat <paramref name="position" /> falls in, and whether it sits
    /// exactly ON that beat; null outside the grid.</summary>
    public (int Bar, int Beat, bool OnBeat)? Locate(FractionalPosition position)
    {
        var beat = position.BeatId;
        if (beat < 0 || beat >= _barOfBeat.Length) return null;
        var bar = Bars[_barOfBeat[beat]];
        return (bar.Number, beat - bar.FirstBeat + 1, position.Fraction == Fraction.Zero);
    }
}
