namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Import-only side channel carrying Guitar Pro's AUTHORED chord NAME from
///     the beat reader to the chord-template builder, keyed by chart position.
///     <para>
///     Guitar Pro hangs a chord DIAGRAM on the beat a strum is written over, and
///     that diagram's first real field is the name the arranger typed — "Am",
///     "Cadd9", "F#m7b5", "N.C.". Both readers must keep it, not throw it away: the GP3-5
///     binary path must not skip the block (its only job was to keep the beat
///     stream in sync), and the gpif path must look at the diagram collection
/// (it does, through <see cref="GpChordDiagrams" />).
///     <see cref="ChordHandShapeBuilder" /> then NAMED every imported chord with
///     <c>ChordNameSuggester</c>'s guess from the sounding pitches — which is a
///     fine default and a poor substitute: a suggester works from pitch classes,
///     so it cannot know the arranger meant a slash chord, an inversion, a
///     "no-chord" marker, or which enharmonic spelling the key calls for. Losing
///     the author's name is exactly the kind of fidelity regression this repo
///     treats as a bug.
///     </para>
///     <para>
///     Deliberately NOT a domain field (like <see cref="GpAuthoredFingering" />
///     and <c>GpLegacyImporter._onBeatGraces</c>): it is fully consumed during
///     import, where it lands in <see cref="ChordTemplate.Name" />. It follows
///     the SAME two rules the authored fingering does — applied at template
///     CREATION only (a template found by shape is shared and must never be
///     mutated, so the first name a shape is seen with wins), and per TRACK,
///     because the key is a position and two tracks strum at the same instant all
///     the time.
///     </para>
/// </summary>
public sealed class GpAuthoredChordNames
{
    /// <summary>
    ///     Cap on a name taken verbatim from a file. Guitar Pro's own binary
    ///     field is 20 characters and gpif's is unbounded; a chord name is a
    ///     label rendered in the chart editor and the sheet-music export, so a
    ///     megabyte of it is a rendering problem, not a chord.
    /// </summary>
    public const int MaxNameLength = 32;

    private readonly Dictionary<FractionalPosition, string> _map = [];

    public int Count => _map.Count;

    public void Clear() => _map.Clear();

    /// <summary>
    ///     Records the authored name of the strum at <paramref name="position" />.
    ///     A blank name is NOT a name — Guitar Pro writes empty diagrams (a shape
    ///     with no label) and storing one would replace the suggester's useful
    ///     guess with nothing. The FIRST value recorded for a position wins, so a
    ///     re-read cannot silently rename an already-built template.
    /// </summary>
    public void Set(FractionalPosition position, string? name)
    {
        var trimmed = Normalize(name);
        if (trimmed == null) return;
        _map.TryAdd(position, trimmed);
    }

    public bool TryGet(FractionalPosition position, out string name)
        => _map.TryGetValue(position, out name!);

    /// <summary>
    ///     Trims a raw file name and rejects the blank one. Also drops the NUL
    ///     padding the binary format leaves behind a short name inside its
    ///     fixed-width field, and any other control character — the string is
    ///     Latin-1 decoded straight off the wire, so it is not guaranteed to be
    ///     printable, and it ends up in the chart editor and the sheet export.
    /// </summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        Span<char> buffer = stackalloc char[MaxNameLength];
        var length = 0;
        foreach (var ch in name)
        {
            if (char.IsControl(ch)) continue;
            if (length == MaxNameLength) break;
            buffer[length++] = ch;
        }

        var trimmed = new string(buffer[..length]).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
