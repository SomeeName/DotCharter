namespace DotCharter.Services.Editing.DraftMerge;

/// <summary>
///     One phrase of a chart-from-audio draft held for review against the arrangement it would be
/// merged into: where it is, the draft's notes there, their mean confidence, and what
///     merging them would do to the sounds already in the phrase.
/// </summary>
public sealed class DraftMergePhrase
{
    internal DraftMergePhrase(string name, FractionalPosition start, FractionalPosition? end, double startMs, double? endMs,
        IReadOnlyList<GuitarNote> draft, double confidence, DraftPhraseDiff diff, bool replaces, EventPoint? marker)
    {
        Name = name;
        Start = start;
        End = end;
        StartMs = startMs;
        EndMs = endMs;
        Draft = draft;
        Confidence = confidence;
        Diff = diff;
        Replaces = replaces;
        Marker = marker;
    }

    /// <summary>The phrase's name; empty for the stretch before the first phrase, or for a chart with none.</summary>
    public string Name { get; }

    /// <summary>Where the phrase starts.</summary>
    public FractionalPosition Start { get; }

    /// <summary>Where the next phrase starts (exclusive), or null for "to the end of the song".</summary>
    public FractionalPosition? End { get; }

    /// <summary><see cref="Start" /> in milliseconds.</summary>
    public double StartMs { get; }

    /// <summary><see cref="End" /> in milliseconds, or null.</summary>
    public double? EndMs { get; }

    /// <summary>The draft's notes in the phrase, in song order. Never written as they are: the merge clones them.</summary>
    public IReadOnlyList<GuitarNote> Draft { get; }

    /// <summary>The draft notes' mean confidence, 0–1.</summary>
    public double Confidence { get; }

    /// <summary>What a merge of this phrase would add, change and remove, counted when the review was held.</summary>
    public DraftPhraseDiff Diff { get; }

    /// <summary>The phrase had sounds when the review was held, and the charter chose to replace them.
    /// False: the phrase was empty, and a merge refuses it once it is not.</summary>
    public bool Replaces { get; }

    /// <summary>The phrase's own marker on the arrangement, when it is an editor-authored one; the
    /// review marks it on the timeline and the song map.</summary>
    public EventPoint? Marker { get; }

    /// <summary>True when <paramref name="position" /> lies inside the phrase.</summary>
    public bool Contains(FractionalPosition position) => position.IsWithin(Start, End);
}

/// <summary>
///     A chart-from-audio draft held for review against the LIVE arrangement and level it would be
/// merged into, one <see cref="DraftMergePhrase" /> per phrase the merge would change.
///     <para>
/// <b>The draft's measured note F1 is about 12 %</b>, so by default only phrases the
///         arrangement leaves EMPTY are offered; a phrase that already has sounds is counted in
///         <see cref="Occupied" /> and left alone unless the charter chose to replace existing notes.
///         A phrase the draft heard nothing in is never offered — replacing would only delete.
///     </para>
///     <para>
///         The draft was spelled for this arrangement's tuning and capo; a draft note below the capo
///         (it cannot sound) or on a string the arrangement does not have is dropped here, never
///         written. Structural phrases (COUNT, END, noguitar) take no notes.
///     </para>
/// </summary>
public sealed class DraftMergeReview
{
    private readonly List<DraftMergePhrase> _phrases;

    private DraftMergeReview(ChartProject project, Arrangement arrangement, ChartLevel level, List<DraftMergePhrase> phrases,
        int occupied, bool replaceExisting)
    {
        Project = project;
        Arrangement = arrangement;
        Level = level;
        _phrases = phrases;
        Occupied = occupied;
        ReplaceExisting = replaceExisting;
    }

    /// <summary>The review's project.</summary>
    public ChartProject Project { get; }

    /// <summary>The LIVE arrangement the draft merges into.</summary>
    public Arrangement Arrangement { get; }

    /// <summary>The LIVE level the draft merges into.</summary>
    public ChartLevel Level { get; }

    /// <summary>The phrases still to decide, in song order. A merged or rejected phrase leaves the list.</summary>
    public IReadOnlyList<DraftMergePhrase> Phrases => _phrases;

    /// <summary>Phrases where the draft has notes but the arrangement already has sounds, left alone
    /// because the charter did not choose to replace existing notes.</summary>
    public int Occupied { get; }

    /// <summary>The charter chose "replace existing notes".</summary>
    public bool ReplaceExisting { get; }

    /// <summary>True while <see cref="Level" /> is still part of the open project.</summary>
    public bool IsLive => Project.Arrangements.Contains(Arrangement) && Arrangement.Levels.Contains(Level);

    /// <summary>
    ///     Cuts <paramref name="draft" /> by <paramref name="arrangement" />'s phrases and diffs each
    ///     against <paramref name="level" />. Nothing is written.
    /// </summary>
    /// <param name="project">The open project.</param>
    /// <param name="arrangement">The arrangement the draft was spelled for and merges into.</param>
    /// <param name="level">Its level the draft merges into.</param>
    /// <param name="beats">The beat map, for the phrase times.</param>
    /// <param name="draft">The draft's notes.</param>
    /// <param name="confidences">Each draft note's confidence (0–1), parallel to <paramref name="draft" />; null
    /// when only <paramref name="averageConfidence" /> is known.</param>
    /// <param name="averageConfidence">Stands in for every note when <paramref name="confidences" /> is null.</param>
    /// <param name="replaceExisting">Offer phrases that already have sounds too.</param>
    public static DraftMergeReview Plan(ChartProject project, Arrangement arrangement, ChartLevel level, IList<BeatMarker> beats,
        IReadOnlyList<GuitarNote> draft, IReadOnlyList<double>? confidences, double averageConfidence, bool replaceExisting)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(draft);

        var confidence = new Dictionary<GuitarNote, double>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < draft.Count; i++)
            confidence[draft[i]] = confidences is { } c && i < c.Count ? c[i] : averageConfidence;
        var playable = draft.Where(n => Playable(arrangement, n)).OrderBy(n => n.Position).ThenBy(n => n.StringIndex).ToList();

        var phrases = new List<DraftMergePhrase>();
        var occupied = 0;
        foreach (var (start, end, name) in Chunks(arrangement))
        {
            if (PhraseMarkers.IsStructural(name)) continue;
            var inPhrase = playable.Where(n => n.Position.IsWithin(start, end)).ToList();
            if (inPhrase.Count == 0) continue;

            var notes = level.Notes.Where(n => n.Position.IsWithin(start, end)).ToList();
            var chords = level.Chords.Where(c => c.Position.IsWithin(start, end)).ToList();
            var hasSounds = notes.Count + chords.Count > 0;
            if (hasSounds && !replaceExisting)
            {
                occupied++;
                continue;
            }

            var diff = DraftPhraseDiff.Compute(notes, chords, inPhrase);
            if (diff.IsEmpty) continue;
            var marker = arrangement.EventPoints.FirstOrDefault(e => e.Position == start && !string.IsNullOrWhiteSpace(e.Phrase));
            phrases.Add(new DraftMergePhrase(name, start, end, start.GetPositionMs(beats), end?.GetPositionMs(beats), inPhrase,
                inPhrase.Average(n => confidence.GetValueOrDefault(n, averageConfidence)), diff, hasSounds, marker));
        }

        return new DraftMergeReview(project, arrangement, level, phrases, occupied, replaceExisting);
    }

    /// <summary>Takes <paramref name="phrase" /> off the list (merged or rejected).</summary>
    internal bool Remove(DraftMergePhrase phrase) => _phrases.Remove(phrase);

    /// <summary>A draft note the arrangement can play: on one of its strings, and not below its capo
    /// (fret 0 IS the capo; frets 1 to capo − 1 cannot sound).</summary>
    internal static bool Playable(Arrangement arrangement, GuitarNote note) =>
        note.StringIndex >= 0 && (arrangement.StringCount <= 0 || note.StringIndex < arrangement.StringCount) &&
        note.Fret >= 0 && (note.Fret == 0 || note.Fret >= arrangement.Capo);

    /// <summary>The phrase spans: the stretch before the first marker, then one per marker to the next.
    /// A chart with no phrase markers is one span over the whole song.</summary>
    private static IEnumerable<(FractionalPosition Start, FractionalPosition? End, string Name)> Chunks(Arrangement arrangement)
    {
        var markers = PhraseMarkers.All(arrangement);
        if (markers.Count == 0 || markers[0].Position > FractionalPosition.Zero)
            yield return (FractionalPosition.Zero, markers.Count == 0 ? null : markers[0].Position, "");
        for (var i = 0; i < markers.Count; i++)
            yield return (markers[i].Position, i + 1 < markers.Count ? markers[i + 1].Position : null, markers[i].Name);
    }
}
