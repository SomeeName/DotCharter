namespace DotCharter.Services.Parsing.Eof;

/// <summary>How faithfully one chart element made it into the EOF project.</summary>
public enum EofConversionKind
{
    /// <summary>Reproduced with a different or reduced representation (e.g. a
    /// complex bend curve folded to EOF's single note-level bend strength).</summary>
    Approximated,

    /// <summary>No EOF equivalent — the element was left out of the file.</summary>
    Dropped
}

/// <summary>
/// WHAT the EOF writer could not keep exactly: one member per decision it reports,
///     so a screen can group the notes and name the item in the user's language
///     (<see cref="EofLosses.ResourceKey" />) instead of printing a count. A new
///     <c>Report.Add</c> site needs its own member and its three resx strings.
/// </summary>
public enum EofLoss
{
    VocalArrangement,
    BassInGuitarSlot,
    NoFreeSlot,
    MeasureStart,
    UnplacedPhrase,
    PhraseMaxDifficulty,
    MoverPhrase,
    ExtraVocalTrack,
    LyricPitch,
    CentOffset,
    TuningStringCount,
    PartialChordTremolo,
    AnchorOnCapo,
    AnchorWidth,
    StartingToneWithoutChange,
    StartingToneReplaced,
    UnusedTone,
    HandShapeShape,
    HandShapeGhostGem,
    NoteStringOutOfRange,
    ChordStringOutOfRange,
    GraceNote,
    ChordGraceNote,
    ChordPartialIgnore,
    ChordMixedHopo,
    ChordMixedHarmonic,
    ChordMixedSlapPop,
    ChordMixedVibrato,
    ChordMixedLinkNext,
    ChordMixedSustain,
    ChordMixedPalmMute,
    ChordMixedSlide,
    ChordMixedBend,
    VibratoStrength,
    FretClamped,
    SlideTargetOutOfRange,
    SlideFromOpenString,
    SlideToSameFret,
    SlideOutOfRange,
    FlatBend,
    BendRounded
}

/// <summary>The resx family behind <see cref="EofLoss" />.</summary>
public static class EofLosses
{
    /// <summary>The <see cref="EofConversionNote.Subject" /> of a note about the beat map, not an arrangement.</summary>
    public const string BeatsSubject = "beats";

    /// <summary>The <see cref="EofConversionNote.Subject" /> of a note about the vocal tracks.</summary>
    public const string VocalsSubject = "vocals";

    /// <summary><c>EofLoss_{member}</c>: the item's name in the report, e.g. "Bend rounded to a quarter step".</summary>
    public static string ResourceKey(EofLoss loss) => "EofLoss_" + loss;
}

/// <summary>
///     One structured note about an export decision. <see cref="Subject" /> is the
///     arrangement's name (or <c>beats</c> / <c>vocals</c> for the song-level parts);
///     <see cref="Message" /> is the English sentence the log keeps. <see cref="TimeMs" /> is
///     where in the song it happened, when it happened at one place, and <see cref="Detail" />
///     the value that names it (a phrase, a tone, a lyric, a number), never a sentence.
/// </summary>
public sealed record EofConversionNote(EofConversionKind Kind, string Subject, string Message)
{
    public EofLoss Loss { get; init; }

    public double? TimeMs { get; init; }

    public string Detail { get; init; } = "";
}

/// <summary>
///     The per-export fidelity record: every dropped / approximated decision
///     the EOF exporter made, so the UI can show "exported with N warnings"
///     and QA can audit coverage. Export never throws over lossy content —
///     it reports instead. Mirrors the RS tone
///     <see cref="DotCharter.Tone.Audio.Dsp.RsToneConversion.ConversionReport"/> pattern.
/// </summary>
public sealed class EofConversionReport
{
    private readonly List<EofConversionNote> _notes = [];

    public IReadOnlyList<EofConversionNote> Notes => _notes;

    public bool IsLossless => _notes.Count == 0;

    public void Add(EofConversionKind kind, EofLoss loss, string subject, string message, double? timeMs = null,
        string detail = "") =>
        _notes.Add(new EofConversionNote(kind, subject, message) { Loss = loss, TimeMs = timeMs, Detail = detail });

    public int Count(EofConversionKind kind)
    {
        var n = 0;
        foreach (var note in _notes)
        {
            if (note.Kind == kind)
                n++;
        }

        return n;
    }

    public int ApproximatedCount => Count(EofConversionKind.Approximated);
    public int DroppedCount => Count(EofConversionKind.Dropped);
}

/// <summary>The exported file bytes plus the fidelity report.</summary>
public sealed class EofExportResult
{
    public required byte[] Bytes { get; init; }
    public required EofConversionReport Report { get; init; }
}
