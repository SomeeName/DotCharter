using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Catalog;

/// <summary>
///     The arrangement context a phrase was captured FROM. It travels with the
///     entry because a catalog is per-user and outlives the project it was
///     captured in: without it a paste cannot tell whether the frets it is about
///     to write still mean the same pitches (see
///     <see cref="PhrasePasteCompatibility" />).
/// </summary>
public sealed class PhraseSourceContext
{
    /// <summary>The source arrangement's display name, for the catalog list only.</summary>
    public string ArrangementName { get; init; } = "";

    public ArrangementType ArrangementType { get; init; } = ArrangementType.Lead;

    /// <summary>Number of strings the source arrangement had. A phrase using a
    /// string the destination does not have cannot be pasted at all.</summary>
    public int StringCount { get; init; } = 6;

    /// <summary>The source capo. Frets are ABSOLUTE with <c>0</c> = the capo, so
    /// this only changes what an OPEN string sounds — but that is enough to make
    /// a paste into a differently-capoed arrangement wrong.</summary>
    public int Capo { get; init; }

    /// <summary>Canonical tuning-type name (<c>E_STANDARD</c>, <c>CUSTOM</c>, …).</summary>
    public string TuningType { get; init; } = "E_STANDARD";

    /// <summary>Per-string semitone offsets from standard. This — not
    /// <see cref="TuningType" /> — is what the compatibility check compares.</summary>
    public IReadOnlyList<int> TuningOffsets { get; init; } = Array.Empty<int>();

    /// <summary>Per-string note names, derived from the offsets. Display only.</summary>
    public IReadOnlyList<string> TuningNames { get; init; } = Array.Empty<string>();

    /// <summary>Where the source arrangement's content came from. Play-only
    /// content pastes only into another play-only arrangement.</summary>
    public ChartProvenance Provenance { get; init; }

    /// <summary>What a paste is checked against: the arrangement the content came from.
    /// Shared by the phrase catalog and the clipboard.</summary>
    public static PhraseSourceContext From(Arrangement arrangement) => new()
    {
        ArrangementName = arrangement.Name,
        ArrangementType = arrangement.ArrangementType,
        StringCount = arrangement.StringCount,
        Capo = arrangement.Capo,
        TuningType = arrangement.TuningType,
        TuningOffsets = arrangement.TuningPitches.Select(t => t.Step).ToArray(),
        TuningNames = arrangement.Tuning.ToArray(),
        Provenance = arrangement.Provenance
    };
}

/// <summary>
///     One named, reusable chart phrase in the per-user catalog.
///     <para>
///         The <see cref="Payload" /> is a normal <see cref="ClipboardPayload" />
///         normalized so its <c>BasePosition</c> is <see cref="FractionalPosition.Zero" />
///         — i.e. every element's position is an offset in BEATS from the start of
///         the phrase. That is what lets one phrase land correctly in a song with a
///         different tempo map: the rhythm is stored, not the milliseconds.
///     </para>
/// </summary>
public sealed class PhraseCatalogEntry
{
    /// <summary>Stable, filesystem-safe id — the entry file's base name. NOT the
    /// display name: a phrase may be called anything, including characters no
    /// file system accepts.</summary>
    public required string Id { get; init; }

    /// <summary>The user-facing name. Free text; matched case-insensitively by
    /// the paste-by-name command.</summary>
    public required string Name { get; init; }

    public required PhraseSourceContext Source { get; init; }

    /// <summary>Origin-normalized content. Never mutate it — pastes go through
    /// <see cref="ClipboardPayload.ShiftedTo" />, which deep-copies.</summary>
    public required ClipboardPayload Payload { get; init; }

    public DateTime CreatedUtc { get; init; }

    public int NoteCount => Payload.Notes.Count;
    public int ChordCount => Payload.Chords.Count;
    public int HandShapeCount => Payload.HandShapes.Count;
    public int AnchorCount => Payload.Anchors.Count;

    /// <summary>Total span of the phrase in beats (0 for a single instant).</summary>
    public double LengthInBeats
    {
        get
        {
            var end = FractionalPosition.Zero;
            foreach (var n in Payload.Notes) end = FractionalPosition.Max(end, n.EndPosition);
            foreach (var c in Payload.Chords) end = FractionalPosition.Max(end, c.EndPosition);
            foreach (var h in Payload.HandShapes) end = FractionalPosition.Max(end, h.EndPosition);
            foreach (var a in Payload.Anchors) end = FractionalPosition.Max(end, a.Position);
            return end.DoubleValue;
        }
    }
}
