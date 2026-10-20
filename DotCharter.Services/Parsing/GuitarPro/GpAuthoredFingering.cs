using System.Globalization;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Import-only side channel carrying Guitar Pro's AUTHORED left-hand
///     fingering from the note readers to the chord-template builders, keyed by
///     (string, chart position).
///     <para>
///     Guitar Pro stores a per-note left-hand finger in both of its formats and
///     both of our readers keep it — the binary path does not
///     <c>Skip(2)</c> the two fingering bytes, and the gpif path looks for
///     the <c>LeftFingering</c> property. Otherwise the template builders GUESS the
///     fingering with <see cref="ChordTemplateFingerSetter" />, whose measured
///     accuracy against the hand-fingered corpus is 74.6% against a hard ceiling
///     of 88.8% for any context-free function — while the file has the answer.
///     </para>
///     <para>
///     Deliberately NOT a domain field (like <c>GpLegacyImporter._onBeatGraces</c>
///     and <c>_pendingTrills</c>): it is fully consumed during import, where it
///     lands in <see cref="ChordTemplate.Fingers" />. And it does NOT make the
///     suggester context-dependent — <see cref="ChordTemplateFingerSetter" />
///     stays a pure function of the fret shape, per the Core invariant; the
///     builder simply prefers an authored finger over the suggested one, per
///     string, at template CREATION only. A shape already backed by a template is
///     reused verbatim and never mutated, so the first fingering a shape is seen
///     with wins — the same rule its name follows.
///     </para>
///     <para>
///     It also remembers WHICH templates took an authored finger
///     (<see cref="IsAuthoredTemplate" />), because the import's FHP pass
///     re-fingers a chord whose fingering equals the suggestion
/// (<c>ChordFingeringFollower</c>) — and a finger the tab states is
///     authored even when the suggester happens to agree with it.
///     </para>
/// </summary>
public sealed class GpAuthoredFingering
{
    private readonly Dictionary<(int StringIndex, FractionalPosition Position), int> _map = [];
    private readonly HashSet<ChordTemplate> _authoredTemplates = new(ReferenceEqualityComparer.Instance);

    public int Count => _map.Count;

    public void Clear()
    {
        _map.Clear();
        _authoredTemplates.Clear();
    }

    /// <summary>Records the authored finger for one string of one sound. The
    /// FIRST value recorded for a (string, position) wins, so a re-read cannot
    /// silently change an already-built template.</summary>
    public void Set(int stringIndex, FractionalPosition position, int finger) => _map.TryAdd((stringIndex, position), finger);

    public bool TryGet(int stringIndex, FractionalPosition position, out int finger)
        => _map.TryGetValue((stringIndex, position), out finger);

    /// <summary>Records that a template builder CREATED <paramref name="template" />
    /// with at least one finger from the file (by instance, not by value).</summary>
    public void MarkAuthored(ChordTemplate template) => _authoredTemplates.Add(template);

    /// <summary>True when <paramref name="template" /> (this instance) took an
    /// authored finger at creation — so its fingering is never free to follow the
    /// hand, even where it equals the suggestion.</summary>
    public bool IsAuthoredTemplate(ChordTemplate template) => _authoredTemplates.Contains(template);

    /// <summary>
    ///     Maps Guitar Pro's BINARY left-hand fingering byte to our finger id.
    ///     GP: <c>-1</c> (and the unsigned <c>255</c>) = none, <c>0</c> = thumb,
    ///     <c>1..4</c> = index/middle/ring/little — which is already our
    ///     convention (<c>0</c> = T, <c>1..4</c>), so the mapping is identity for
    ///     every legal value. Anything else reads as "no fingering authored".
    /// </summary>
    public static int? MapBinaryFinger(int gpFinger)
        => gpFinger is >= 0 and <= 4 ? gpFinger : null;

    /// <summary>
    ///     Maps a gpif <c>&lt;Fingering&gt;</c> value to our finger id. GP7/GP8
    ///     spell the fingers with the classical-guitar letters
    ///     (<c>P</c> pouce/thumb, <c>I</c> index, <c>M</c> middle, <c>A</c>
    ///     annular/ring, <c>C</c> little), and "Open"/"None" means the string is
    ///     not fingered. Numeric spellings (<c>0..4</c>, seen in some exporters)
    ///     are accepted too. Anything else reads as "no fingering authored".
    /// </summary>
    public static int? MapGpifFinger(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        switch (value.Trim().ToUpperInvariant())
        {
            case "P": return 0; // thumb
            case "I": return 1;
            case "M": return 2;
            case "A": return 3;
            case "C": return 4;
            case "OPEN":
            case "NONE": return null;
        }

        return int.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var n) ? MapBinaryFinger(n) : null;
    }

    /// <summary>
    ///     Maps the <c>finger</c> attribute of a gpif chord DIAGRAM's
    ///     <c>&lt;Fingering&gt;&lt;Position&gt;</c> to our finger id. Diagrams
    ///     spell the fingers as English words — GP7/GP8 write <c>Thumb</c>,
    ///     <c>Index</c>, <c>Middle</c>, <c>Ring</c> and <c>Pinky</c> (every one of
    ///     the 258 diagrams in the owner's tab set), and <c>Annular</c> /
    ///     <c>Little</c> are accepted as their classical spellings. <c>None</c> is
    ///     an unfingered string. The note property's letters and digits are
    ///     accepted too (<see cref="MapGpifFinger" />).
    /// </summary>
    public static int? MapDiagramFinger(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToUpperInvariant() switch
        {
            "THUMB" => 0,
            "INDEX" => 1,
            "MIDDLE" => 2,
            "RING" or "ANNULAR" => 3,
            "PINKY" or "LITTLE" => 4,
            _ => MapGpifFinger(value)
        };
    }
}
