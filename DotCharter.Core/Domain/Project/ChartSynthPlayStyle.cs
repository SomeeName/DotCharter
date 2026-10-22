namespace DotCharter.Core.Domain.Project;

/// <summary>
/// How the built-in chart synth PLAYS a song's notes, per song: one rule set
///     for guitar arrangements and one for bass. The defaults are the rules the synth
///     always followed, so a project that never touches this sounds exactly as before.
///     Saved in the .dcproj as <c>dcSynthPlayStyle</c>, only when it differs from the
///     defaults.
/// </summary>
public sealed class ChartSynthPlayStyle
{
    public ChartSynthPlayRules Guitar { get; set; } = new();
    public ChartSynthPlayRules Bass { get; set; } = new();

    /// <summary>The rule set for an arrangement: bass arrangements use <see cref="Bass" />.</summary>
    public ChartSynthPlayRules For(bool bass) => bass ? Bass : Guitar;

    public bool IsDefault => Guitar.IsDefault && Bass.IsDefault;

    public ChartSynthPlayStyle Clone() => new() { Guitar = Guitar.Clone(), Bass = Bass.Clone() };

    /// <summary>Copies <paramref name="other" /> INTO this instance (the restorer keeps
    /// the live object, so bindings and history stay attached).</summary>
    public void CopyFrom(ChartSynthPlayStyle other)
    {
        Guitar.CopyFrom(other.Guitar);
        Bass.CopyFrom(other.Bass);
    }
}

/// <summary>
///     One instrument's playing rules. A "gap" rule holds a note (or chord) legato into
///     the next note when the silence it would leave is SHORTER than the given note
///     value; the value is a divisor of a whole note (4 = a quarter note, 16 = a
///     sixteenth), read off the local tempo.
/// </summary>
public sealed class ChartSynthPlayRules
{
    /// <summary>The note values a gap rule offers, longest first (1/2 … 1/32).</summary>
    public static readonly int[] GapDivisors = [2, 4, 8, 16, 32];

    /// <summary>The divisor every rule starts at: a quarter note.</summary>
    public const int DefaultGapDivisor = 4;

    /// <summary>A note inside a hand shape rings across later notes, to the shape's end.</summary>
    public bool HandShapesRing { get; set; } = true;

    /// <summary>A single note is held into the next note across a short gap.</summary>
    public bool HoldSingleNoteGaps { get; set; } = true;

    /// <summary>What counts as a short gap after a single note (see <see cref="GapDivisors" />).</summary>
    public int SingleNoteGapDivisor { get; set; } = DefaultGapDivisor;

    /// <summary>A chord is held into the next note across a short gap.</summary>
    public bool HoldChordGaps { get; set; } = true;

    /// <summary>What counts as a short gap after a chord (see <see cref="GapDivisors" />).</summary>
    public int ChordGapDivisor { get; set; } = DefaultGapDivisor;

    public bool IsDefault =>
        HandShapesRing && HoldSingleNoteGaps && HoldChordGaps &&
        SingleNoteGapDivisor == DefaultGapDivisor && ChordGapDivisor == DefaultGapDivisor;

    /// <summary>A gap divisor the rules accept: one of <see cref="GapDivisors" />,
    /// anything else (a hand-edited file) becomes the default.</summary>
    public static int Sanitize(int divisor) => Array.IndexOf(GapDivisors, divisor) >= 0 ? divisor : DefaultGapDivisor;

    public ChartSynthPlayRules Clone() => (ChartSynthPlayRules)MemberwiseClone();

    public void CopyFrom(ChartSynthPlayRules other)
    {
        HandShapesRing = other.HandShapesRing;
        HoldSingleNoteGaps = other.HoldSingleNoteGaps;
        SingleNoteGapDivisor = other.SingleNoteGapDivisor;
        HoldChordGaps = other.HoldChordGaps;
        ChordGapDivisor = other.ChordGapDivisor;
    }

    /// <summary>The same rules as <paramref name="other" /> (a change that does nothing
    /// is not an undo step).</summary>
    public bool SameAs(ChartSynthPlayRules other) =>
        HandShapesRing == other.HandShapesRing && HoldSingleNoteGaps == other.HoldSingleNoteGaps &&
        SingleNoteGapDivisor == other.SingleNoteGapDivisor && HoldChordGaps == other.HoldChordGaps &&
        ChordGapDivisor == other.ChordGapDivisor;
}
