namespace DotCharter.Tests.Analysis;

/// <summary>
///     One sound of a chart or a draft: a single note or one tone of a chord.
///     <c>PitchCheckable</c> is false for a truth sound whose fretted pitch is
///     not what sounds (harmonics, unpitched slides, pre-bends): it still counts
///     as an ONSET, but is left out of every pitch and string/fret figure.
/// </summary>
internal readonly record struct ScoredSound(double TimeSeconds, int Midi, int StringIndex,
    bool PitchCheckable = true);

/// <summary>
///     The counts one alignment of a draft against its ground truth produced.
///     Every ratio is derived from them, so scores merge by adding counts —
///     never by averaging ratios, which would weigh a 20-note intro like a
///     2,000-note song.
/// </summary>
internal sealed class AutoChartScore
{
    public int TruthOnsets { get; set; }
    public int DraftOnsets { get; set; }
    public int MatchedOnsets { get; set; }

    /// <summary>Pitch-checkable truth sounds, over the whole chart.</summary>
    public int TruthNotes { get; set; }

    /// <summary>Every drafted note.</summary>
    public int DraftNotes { get; set; }

    /// <summary>Pitch-checkable truth sounds at onsets the draft FOUND — the
    /// denominator of pitch accuracy.</summary>
    public int TruthNotesAtMatchedOnsets { get; set; }

    /// <summary>Truth sounds at a matched onset that a drafted note at that
    /// onset spells with the same MIDI pitch (one-to-one).</summary>
    public int PitchHits { get; set; }

    /// <summary>The same, comparing pitch CLASS only — the gap to
    /// <see cref="PitchHits" /> is the octave errors.</summary>
    public int PitchClassHits { get; set; }

    /// <summary>Pitch hits that are also on the charted STRING. Same pitch on the
    /// same string is the same sounding fret, so this is string/fret agreement
    /// without having to reconcile how a capo-stopped "open" string is written.</summary>
    public int PositionHits { get; set; }

    /// <summary>Matched onsets whose truth carries one pitch-checkable note, and
    /// the hits among them; the chord counterparts below.</summary>
    public int SingleNotesAtMatchedOnsets { get; set; }
    public int SinglePitchHits { get; set; }
    public int ChordNotesAtMatchedOnsets { get; set; }
    public int ChordPitchHits { get; set; }

    /// <summary>Draft minus truth, seconds, one per matched onset.</summary>
    public List<double> OnsetErrorsSeconds { get; } = [];

    public double OnsetPrecision => Ratio(MatchedOnsets, DraftOnsets);
    public double OnsetRecall => Ratio(MatchedOnsets, TruthOnsets);
    public double OnsetF1 => F1(OnsetPrecision, OnsetRecall);
    public double PitchAccuracy => Ratio(PitchHits, TruthNotesAtMatchedOnsets);
    public double PitchClassAccuracy => Ratio(PitchClassHits, TruthNotesAtMatchedOnsets);
    public double PositionAgreement => Ratio(PositionHits, PitchHits);
    public double NotePrecision => Ratio(PitchHits, DraftNotes);
    public double NoteRecall => Ratio(PitchHits, TruthNotes);
    public double NoteF1 => F1(NotePrecision, NoteRecall);
    public double SinglePitchAccuracy => Ratio(SinglePitchHits, SingleNotesAtMatchedOnsets);
    public double ChordPitchAccuracy => Ratio(ChordPitchHits, ChordNotesAtMatchedOnsets);

    /// <summary>Median signed onset error in ms (NaN with no match) — the
    /// alignment sanity check: a song whose audio is offset from its chart shows
    /// up here as a large constant, not as a bad detector.</summary>
    public double MedianOnsetErrorMs
    {
        get
        {
            if (OnsetErrorsSeconds.Count == 0) return double.NaN;
            var sorted = OnsetErrorsSeconds.Order().ToList();
            var mid = sorted.Count / 2;
            var median = sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
            return median * 1000.0;
        }
    }

    public void Add(AutoChartScore other)
    {
        TruthOnsets += other.TruthOnsets;
        DraftOnsets += other.DraftOnsets;
        MatchedOnsets += other.MatchedOnsets;
        TruthNotes += other.TruthNotes;
        DraftNotes += other.DraftNotes;
        TruthNotesAtMatchedOnsets += other.TruthNotesAtMatchedOnsets;
        PitchHits += other.PitchHits;
        PitchClassHits += other.PitchClassHits;
        PositionHits += other.PositionHits;
        SingleNotesAtMatchedOnsets += other.SingleNotesAtMatchedOnsets;
        SinglePitchHits += other.SinglePitchHits;
        ChordNotesAtMatchedOnsets += other.ChordNotesAtMatchedOnsets;
        ChordPitchHits += other.ChordPitchHits;
        OnsetErrorsSeconds.AddRange(other.OnsetErrorsSeconds);
    }

    /// <summary>0/0 is 0, never 1: a draft that found nothing has found nothing.</summary>
    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private static double F1(double precision, double recall) =>
        precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
}

/// <summary>
///     Aligns an auto-chart draft with its hand-corrected chart and counts what
///     agrees. Pure and deterministic; pinned by <c>AutoChartScorerTests</c> in
///     the default gate, because the corpus run that reads numbers off it
///     (<c>AutoChartCorpusProbeTests</c>) is env-gated and skipped everywhere else.
///     <para>
///     Two levels. <b>Onsets</b>: sounds closer than <see cref="OnsetGroupSeconds" />
///     are one onset (a chord's tones share a position; so do the up to
///     <c>MaxNotesPerOnset</c> notes the analyzer emits per event), and truth and
///     draft onsets are matched one-to-one within the tolerance. <b>Notes</b>:
///     inside each matched onset pair, truth and draft sounds are paired
///     one-to-one by MIDI pitch, preferring the charted string.
///     </para>
/// </summary>
internal static class AutoChartScorer
{
    /// <summary>Sounds this close together are one onset. Positions on one grid
    /// slot convert to the same millisecond, so this only absorbs rounding.</summary>
    public const double OnsetGroupSeconds = 0.0005;

    // Inclusive window edge, robust to the binary representation of 1.050 - 1.000.
    private const double WindowEpsilon = 1e-9;

    public static AutoChartScore Score(IReadOnlyList<ScoredSound> truth, IReadOnlyList<ScoredSound> draft,
        double toleranceSeconds)
    {
        var truthOnsets = GroupOnsets(truth);
        var draftOnsets = GroupOnsets(draft);
        var score = new AutoChartScore
        {
            TruthOnsets = truthOnsets.Count,
            DraftOnsets = draftOnsets.Count,
            TruthNotes = truth.Count(s => s.PitchCheckable),
            DraftNotes = draft.Count
        };

        foreach (var (t, d) in MatchOnsets(truthOnsets, draftOnsets, toleranceSeconds))
        {
            score.MatchedOnsets++;
            score.OnsetErrorsSeconds.Add(draftOnsets[d].TimeSeconds - truthOnsets[t].TimeSeconds);
            ScoreNotes(truthOnsets[t].Sounds, draftOnsets[d].Sounds, score);
        }

        return score;
    }

    /// <summary>One onset: its time (the first sound's) and every sound in it.</summary>
    internal sealed record Onset(double TimeSeconds, IReadOnlyList<ScoredSound> Sounds);

    internal static List<Onset> GroupOnsets(IEnumerable<ScoredSound> sounds)
    {
        var onsets = new List<Onset>();
        List<ScoredSound>? current = null;
        var currentTime = 0.0;
        foreach (var s in sounds.OrderBy(s => s.TimeSeconds).ThenBy(s => s.StringIndex).ThenBy(s => s.Midi))
        {
            if (current != null && s.TimeSeconds - currentTime < OnsetGroupSeconds)
            {
                current.Add(s);
                continue;
            }

            current = [s];
            currentTime = s.TimeSeconds;
            onsets.Add(new Onset(currentTime, current));
        }

        return onsets;
    }

    /// <summary>
    ///     Maximum one-to-one matching of two sorted onset lists within
    ///     ±<paramref name="toleranceSeconds" /> (inclusive). The two-pointer sweep
    ///     is optimal for points on a line: an unmatched truth onset that lies
    ///     before the current draft onset's window can match no later draft onset
    ///     either, and vice versa. One-to-one is the load-bearing property — a
    ///     many-to-one match would let a burst of drafted onsets claim one charted
    ///     onset several times.
    /// </summary>
    internal static List<(int Truth, int Draft)> MatchOnsets(IReadOnlyList<Onset> truth,
        IReadOnlyList<Onset> draft, double toleranceSeconds)
    {
        var pairs = new List<(int, int)>();
        var i = 0;
        var j = 0;
        while (i < truth.Count && j < draft.Count)
        {
            var diff = draft[j].TimeSeconds - truth[i].TimeSeconds;
            if (Math.Abs(diff) <= toleranceSeconds + WindowEpsilon)
            {
                pairs.Add((i, j));
                i++;
                j++;
            }
            else if (diff > 0)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return pairs;
    }

    private static void ScoreNotes(IReadOnlyList<ScoredSound> truth, IReadOnlyList<ScoredSound> draft,
        AutoChartScore score)
    {
        var checkable = truth.Where(s => s.PitchCheckable).ToList();
        if (checkable.Count == 0) return;

        var exactTaken = new bool[draft.Count];
        var classTaken = new bool[draft.Count];
        var found = new bool[checkable.Count];
        var hits = 0;

        // Same pitch on the charted string first, so a chord holding one pitch
        // twice (a unison) pairs string with string; then same pitch anywhere.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var a = 0; a < checkable.Count; a++)
            {
                if (found[a]) continue;
                for (var k = 0; k < draft.Count; k++)
                {
                    if (exactTaken[k] || draft[k].Midi != checkable[a].Midi) continue;
                    var sameString = draft[k].StringIndex == checkable[a].StringIndex;
                    if (pass == 0 && !sameString) continue;
                    exactTaken[k] = true;
                    found[a] = true;
                    hits++;
                    if (sameString) score.PositionHits++;
                    break;
                }
            }
        }

        foreach (var t in checkable)
        {
            for (var k = 0; k < draft.Count; k++)
            {
                if (classTaken[k] || Mod12(draft[k].Midi) != Mod12(t.Midi)) continue;
                classTaken[k] = true;
                score.PitchClassHits++;
                break;
            }
        }

        score.TruthNotesAtMatchedOnsets += checkable.Count;
        score.PitchHits += hits;
        if (checkable.Count == 1)
        {
            score.SingleNotesAtMatchedOnsets++;
            score.SinglePitchHits += hits;
        }
        else
        {
            score.ChordNotesAtMatchedOnsets += checkable.Count;
            score.ChordPitchHits += hits;
        }
    }

    private static int Mod12(int midi) => ((midi % 12) + 12) % 12;
}
