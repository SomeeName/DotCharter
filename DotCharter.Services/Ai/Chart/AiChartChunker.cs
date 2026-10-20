namespace DotCharter.Services.Ai.Chart;

/// <summary>How big a chunk may get, and how much context it carries.</summary>
/// <param name="MinSounds">Consecutive short phrases are merged until a chunk holds at
/// least this many sounds — a two-note phrase is not worth a request of its own.</param>
/// <param name="MaxSounds">No chunk holds more; a longer phrase is cut into even pieces.</param>
/// <param name="ContextBefore">Sounds shown BEFORE the core (never applied).</param>
/// <param name="ContextAfter">Sounds shown AFTER the core — the look-ahead a hand position
/// needs (never applied).</param>
public sealed record AiChartChunkOptions(
    int MinSounds = 24,
    int MaxSounds = 64,
    int ContextBefore = 6,
    int ContextAfter = 10)
{
    /// <summary>The defaults.</summary>
    public static AiChartChunkOptions Default { get; } = new();
}

/// <summary>
/// Cuts a level into requests, by PHRASE: a whole song never goes out in one
///     request, and a phrase is where a charter's hand decisions naturally restart (RS
///     wants an anchor at every phrase start anyway).
///     <para>
///         Pure and deterministic. The CORE of every chunk is disjoint from every other
///         chunk's, so each sound is offered for change exactly once; the context either side
///         is shown to the model and is never part of any plan built from that chunk's reply.
///     </para>
/// </summary>
public static class AiChartChunker
{
    /// <summary>Every sound of the level, time-sorted, with its pass-wide id.</summary>
    /// <param name="level">The level.</param>
    /// <param name="beats">The beat map, for the millisecond echo.</param>
    public static IReadOnlyList<AiChartSound> Sounds(ChartLevel level, IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(beats);

        var raw = new List<(FractionalPosition Position, int Order, GuitarNote? Note, Chord? Chord, int ChordIndex)>(
            level.Notes.Count + level.Chords.Count);
        foreach (var note in level.Notes) raw.Add((note.Position, 0, note, null, -1));
        for (var i = 0; i < level.Chords.Count; i++) raw.Add((level.Chords[i].Position, 1, null, level.Chords[i], i));

        // Stable: the order inside one position is notes first, then chords, then
        // collection order — the same answer on a clone and on the live level.
        var sorted = raw.Select((r, i) => (r, i))
            .OrderBy(x => x.r.Position).ThenBy(x => x.r.Order).ThenBy(x => x.i)
            .Select(x => x.r).ToList();

        var sounds = new List<AiChartSound>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var s = sorted[i];
            sounds.Add(new AiChartSound("s" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                s.Position, Ms(s.Position, beats), s.Note, s.Chord, s.ChordIndex));
        }

        return sounds;
    }

    /// <summary>The chunks for the sounds in <c>[from, to]</c> (both inclusive; null = open).</summary>
    /// <param name="arrangement">Its phrase markers cut the chunks.</param>
    /// <param name="level">The level whose sounds are chunked.</param>
    /// <param name="beats">The beat map.</param>
    /// <param name="from">First position of the selection, or null for the whole level.</param>
    /// <param name="to">Last position of the selection, or null for the whole level.</param>
    /// <param name="options">Sizes; null for <see cref="AiChartChunkOptions.Default" />.</param>
    public static IReadOnlyList<AiChartChunk> Plan(
        Arrangement arrangement,
        ChartLevel level,
        IList<BeatMarker> beats,
        FractionalPosition? from = null,
        FractionalPosition? to = null,
        AiChartChunkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        options ??= AiChartChunkOptions.Default;

        var sounds = Sounds(level, beats);
        var first = -1;
        var last = -1;
        for (var i = 0; i < sounds.Count; i++)
        {
            var p = sounds[i].Position;
            if (from is { } f && p < f) continue;
            if (to is { } t && p > t) continue;
            if (first < 0) first = i;
            last = i;
        }

        if (first < 0) return [];

        var markers = PhraseMarkers.All(arrangement);
        var pieces = Pieces(sounds, first, last, markers, options);
        var chunks = new List<AiChartChunk>(pieces.Count);
        for (var k = 0; k < pieces.Count; k++)
        {
            var (lo, hi, startMarker) = pieces[k];
            // A chunk that opens a phrase starts AT the marker, so the phrase-start
            // anchor Rocksmith wants is the chunk's own first anchor — unless the
            // selection starts later than the marker, which is the charter saying
            // "not before here".
            var start = startMarker is { } m && (from is not { } sel || m >= sel) ? m : sounds[lo].Position;

            // The last chunk stops at whatever comes next — a sound or a phrase marker —
            // so an anchor that governs the rest of the song is never replaced by a
            // selection that ended before it. Inner chunks are closed below.
            var end = EarliestOf(
                hi + 1 < sounds.Count ? sounds[hi + 1].Position : null,
                NextMarker(markers, sounds[hi].Position));

            chunks.Add(new AiChartChunk(
                k, start, end, Ms(start, beats),
                Slice(sounds, lo, hi),
                Slice(sounds, Math.Max(0, lo - options.ContextBefore), lo - 1),
                Slice(sounds, hi + 1, Math.Min(sounds.Count - 1, hi + options.ContextAfter)),
                PhraseNames(markers, sounds[lo].Position, sounds[hi].Position)));
        }

        // Every chunk but the last ends where the next one starts.
        for (var k = 0; k + 1 < chunks.Count; k++) chunks[k] = chunks[k] with { End = chunks[k + 1].Start };
        return chunks;
    }

    private static List<(int Lo, int Hi, FractionalPosition? StartMarker)> Pieces(
        IReadOnlyList<AiChartSound> sounds, int first, int last,
        IReadOnlyList<PhraseMarkers.PhraseMarker> markers, AiChartChunkOptions options)
    {
        // 1. One group per phrase (sounds before the first marker form their own).
        var groups = new List<(int Lo, int Hi, FractionalPosition? Marker)>();
        var markerIndex = -1;
        for (var i = first; i <= last; i++)
        {
            var phrase = PhraseIndexAt(markers, sounds[i].Position);
            if (groups.Count > 0 && phrase == markerIndex)
            {
                groups[^1] = (groups[^1].Lo, i, groups[^1].Marker);
                continue;
            }

            groups.Add((i, i, phrase >= 0 ? markers[phrase].Position : null));
            markerIndex = phrase;
        }

        // 2. A phrase longer than the cap is cut into even pieces; only the first
        //    piece opens the phrase.
        var split = new List<(int Lo, int Hi, FractionalPosition? Marker)>();
        var max = Math.Max(1, options.MaxSounds);
        foreach (var (lo, hi, marker) in groups)
        {
            var count = hi - lo + 1;
            var parts = (count + max - 1) / max;
            for (var p = 0; p < parts; p++)
            {
                var a = lo + (int)((long)count * p / parts);
                var b = lo + (int)((long)count * (p + 1) / parts) - 1;
                split.Add((a, b, p == 0 ? marker : null));
            }
        }

        // 3. Short neighbours merge until the chunk is worth a request.
        var merged = new List<(int Lo, int Hi, FractionalPosition? Marker)>();
        foreach (var piece in split)
        {
            if (merged.Count > 0)
            {
                var (lo, hi, marker) = merged[^1];
                var size = hi - lo + 1;
                var combined = piece.Hi - lo + 1;
                if (size < options.MinSounds && combined <= max)
                {
                    merged[^1] = (lo, piece.Hi, marker);
                    continue;
                }
            }

            merged.Add(piece);
        }

        return merged;
    }

    private static int PhraseIndexAt(IReadOnlyList<PhraseMarkers.PhraseMarker> markers, FractionalPosition position)
    {
        var index = -1;
        for (var i = 0; i < markers.Count && markers[i].Position <= position; i++) index = i;
        return index;
    }

    private static FractionalPosition? NextMarker(IReadOnlyList<PhraseMarkers.PhraseMarker> markers, FractionalPosition after)
    {
        foreach (var m in markers)
        {
            if (m.Position > after) return m.Position;
        }

        return null;
    }

    private static FractionalPosition? EarliestOf(FractionalPosition? a, FractionalPosition? b)
        => a is { } x && b is { } y ? FractionalPosition.Min(x, y) : a ?? b;

    private static string PhraseNames(
        IReadOnlyList<PhraseMarkers.PhraseMarker> markers, FractionalPosition from, FractionalPosition to)
    {
        var lo = PhraseIndexAt(markers, from);
        var hi = PhraseIndexAt(markers, to);
        if (hi < 0) return "";
        var names = new List<string>();
        for (var i = Math.Max(lo, 0); i <= hi; i++)
        {
            if (!string.IsNullOrWhiteSpace(markers[i].Name)) names.Add(markers[i].Name);
        }

        return string.Join(", ", names);
    }

    private static List<AiChartSound> Slice(IReadOnlyList<AiChartSound> sounds, int lo, int hi)
    {
        var list = new List<AiChartSound>(Math.Max(0, hi - lo + 1));
        for (var i = lo; i <= hi; i++) list.Add(sounds[i]);
        return list;
    }

    internal static int Ms(FractionalPosition position, IList<BeatMarker> beats)
        => beats.Count == 0 ? 0 : (int)Math.Round(position.GetPositionMs(beats), MidpointRounding.AwayFromZero);
}
