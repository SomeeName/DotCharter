using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DotCharter.Analysis.AudioToChart;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Analysis;

/// <summary>One scored arrangement of the corpus run. <c>Sweep</c> is aligned
/// with <see cref="AutoChartCorpus.SweepSensitivities" />; <c>BiasCorrected</c>
/// is a DIAGNOSTIC — the raw onset times minus this arrangement's own median
/// offset <c>BiasMs</c> (an oracle), to tell a constant lead or lag from scatter.</summary>
internal sealed record AutoChartRow(
    string Song,
    string Arrangement,
    string Kind,
    AutoChartScore Score,
    AutoChartScore Score30,
    AutoChartScore Score100,
    AutoChartScore RawOnsets,
    AutoChartScore BiasCorrected,
    IReadOnlyList<AutoChartScore> Sweep,
    string DraftSha,
    double BiasMs = double.NaN);

/// <summary>
///     The pure halves of the auto-chart corpus score: chart → ground-truth
///     sounds, draft → scored sounds, a draft fingerprint for the determinism
///     check, and the CSV + markdown report. Kept out of the env-gated harness
///     (<c>AutoChartCorpusProbeTests</c>) so the default gate runs them
///     (<c>AutoChartScorerTests</c>).
/// </summary>
internal static class AutoChartCorpus
{
    /// <summary>The headline onset window. ±50 ms is the MIREX / mir_eval onset
    /// convention, and it covers the draft's own grid snap (a 1/12-beat slot is
    /// 25–50 ms wide at 200–100 BPM, so the snap moves a note at most half that).</summary>
    public const double ToleranceSeconds = 0.050;

    public static readonly double[] SweepSensitivities = [0, 25, 50, 75, 100];

    /// <summary>
    ///     The arrangement's HARDEST level as sounds — the level the editor opens on
    ///     and Play Mode draws (the import fills every level dense, see
    ///     <c>RsLevelLadder</c>). Full mutes have no pitch and no reliable attack
    ///     and are dropped; harmonics, unpitched slides and pre-bends stay as
    ///     onsets but are not pitch-checkable (what sounds is not the fretted pitch).
    /// </summary>
    public static List<ScoredSound> GroundTruth(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var sounds = new List<ScoredSound>();
        var level = arrangement.Levels.MaxBy(l => l.DifficultyIndex);
        if (level == null) return sounds;

        foreach (var note in level.Notes)
            AddTruth(sounds, arrangement, note, note.Position.GetPositionMs(beats) / 1000.0, note.Position);

        foreach (var chord in level.Chords)
        {
            var t = chord.Position.GetPositionMs(beats) / 1000.0;
            foreach (var cn in chord.ChordNotes)
                AddTruth(sounds, arrangement, cn, t, chord.Position);
        }

        sounds.Sort(static (a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        return sounds;
    }

    /// <summary>The draft as the user receives it: <paramref name="snapped" />
    /// places each note at its grid POSITION (what Apply writes into the chart);
    /// otherwise at the detector's raw onset time, a diagnostic for how much the
    /// snap costs or saves.</summary>
    public static List<ScoredSound> DraftSounds(AudioToChartDraft draft, IList<BeatMarker> beats, bool snapped) =>
        draft.Notes
            .Select(n => new ScoredSound(
                snapped ? n.Note.Position.GetPositionMs(beats) / 1000.0 : n.TimeSeconds,
                n.Midi,
                n.Note.StringIndex))
            .ToList();

    /// <summary>A hash of everything the draft says, bit for bit, so two runs can
    /// be compared for determinism by comparing one CSV column.</summary>
    public static string Fingerprint(AudioToChartDraft draft)
    {
        var sb = new StringBuilder();
        foreach (var n in draft.Notes)
        {
            sb.Append(BitConverter.DoubleToInt64Bits(n.TimeSeconds)).Append(',')
                .Append(n.Midi).Append(',')
                .Append(BitConverter.SingleToInt32Bits(n.FrequencyHz)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(n.Confidence)).Append(',')
                .Append(n.Note.StringIndex).Append(',')
                .Append(n.Note.Fret).Append(',')
                .Append(n.Note.Position.BeatId).Append(',')
                .Append(n.Note.Position.Fraction.Numerator).Append('/')
                .Append(n.Note.Position.Fraction.Denominator).Append(',')
                .Append((int)n.Note.Hopo).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public static string Csv(IEnumerable<AutoChartRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("song,arrangement,kind,truth_onsets,draft_onsets,matched_onsets,onset_p,onset_r,onset_f1,")
            .Append("truth_notes,draft_notes,pitch_hits,pitch_acc,pitch_class_acc,string_fret_agree,")
            .Append("note_p,note_r,note_f1,median_onset_err_ms,onset_f1_30ms,onset_f1_100ms,raw_onset_f1,")
            .Append("bias_ms,bias_corrected_onset_f1,bias_corrected_note_f1,draft_sha\n");
        foreach (var r in rows)
        {
            var s = r.Score;
            sb.Append(CsvField(r.Song)).Append(',').Append(CsvField(r.Arrangement)).Append(',').Append(r.Kind)
                .Append(',').Append(s.TruthOnsets).Append(',').Append(s.DraftOnsets).Append(',').Append(s.MatchedOnsets)
                .Append(',').Append(F(s.OnsetPrecision)).Append(',').Append(F(s.OnsetRecall)).Append(',').Append(F(s.OnsetF1))
                .Append(',').Append(s.TruthNotes).Append(',').Append(s.DraftNotes).Append(',').Append(s.PitchHits)
                .Append(',').Append(F(s.PitchAccuracy)).Append(',').Append(F(s.PitchClassAccuracy))
                .Append(',').Append(F(s.PositionAgreement))
                .Append(',').Append(F(s.NotePrecision)).Append(',').Append(F(s.NoteRecall)).Append(',').Append(F(s.NoteF1))
                .Append(',').Append(s.MedianOnsetErrorMs.ToString("0.0", CultureInfo.InvariantCulture))
                .Append(',').Append(F(r.Score30.OnsetF1)).Append(',').Append(F(r.Score100.OnsetF1))
                .Append(',').Append(F(r.RawOnsets.OnsetF1))
                .Append(',').Append(r.BiasMs.ToString("0.0", CultureInfo.InvariantCulture))
                .Append(',').Append(F(r.BiasCorrected.OnsetF1)).Append(',').Append(F(r.BiasCorrected.NoteF1))
                .Append(',').Append(r.DraftSha).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The value at <paramref name="q" /> (0..1) of a sorted list, nearest rank.</summary>
    public static double Quantile(IReadOnlyList<double> sorted, double q) =>
        sorted.Count == 0 ? double.NaN : sorted[(int)Math.Round(q * (sorted.Count - 1))];

    public static AutoChartScore Sum(IEnumerable<AutoChartScore> scores)
    {
        var total = new AutoChartScore();
        foreach (var s in scores) total.Add(s);
        return total;
    }

    /// <summary>The draft's raw onsets shifted by the median offset a ±100 ms
    /// match finds (draft minus truth), and that offset in ms (NaN: no match).</summary>
    public static (List<ScoredSound> Shifted, double BiasMs) RemoveBias(IReadOnlyList<ScoredSound> truth,
        List<ScoredSound> raw)
    {
        var bias = AutoChartScorer.Score(truth, raw, 0.100).MedianOnsetErrorMs;
        return double.IsNaN(bias)
            ? (raw, bias)
            : (raw.Select(s => s with { TimeSeconds = s.TimeSeconds - bias / 1000.0 }).ToList(), bias);
    }

    public static string Pct(double ratio) => (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    private static void AddTruth(List<ScoredSound> sounds, Arrangement arrangement, IGuitarSound sound,
        double timeSeconds, FractionalPosition position)
    {
        if (sound.Mute == MuteType.Full) return;
        if (LivePitchAcceptance.SoundingMidi(arrangement, sound.StringIndex, sound.Fret) is not { } midi) return;

        var checkable = sound.Harmonic == HarmonicType.None && !sound.UnpitchedSlide && !IsPreBent(sound, position);
        sounds.Add(new ScoredSound(timeSeconds, midi, sound.StringIndex, checkable));
    }

    /// <summary>The rule <c>AudioChartLintService.IsPreBent</c> spells: a bend
    /// point with a non-zero step AT the onset means the string is already bent
    /// when struck, so the fretted pitch never sounds.</summary>
    private static bool IsPreBent(IGuitarSound sound, FractionalPosition position)
    {
        foreach (var bend in sound.BendValues)
        {
            if (bend.Step != 0 && bend.Position.CompareTo(position) <= 0)
                return true;
        }

        return false;
    }

    private static string F(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string CsvField(string s) => s.Contains(',') || s.Contains('"')
        ? "\"" + s.Replace("\"", "\"\"") + "\""
        : s;
}
