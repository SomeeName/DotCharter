using DotCharter.Analysis.AudioToChart;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Export.Audio;

namespace DotCharter.Services.Editing.Linting;

/// <summary>Result of one audio-lint run. <see cref="Ran"/> false = the run
/// could not happen (no audio, undecodable) with <see cref="Error"/> saying
/// why; findings are the analyzer's, verbatim.</summary>
public sealed record AudioChartLintOutcome(
    bool Ran,
    string? Error,
    int SoundsChecked,
    int SoundsVerified,
    IReadOnlyList<ChartAudioFinding> Findings)
{
    public static AudioChartLintOutcome Failed(string error) => new(false, error, 0, 0, []);
}

public interface IAudioChartLintService
{
    /// <summary>Offline audio-verified chart lint (v0.29): decodes the song
    /// audio, projects the arrangement's HIGHEST level to sounding pitches
    /// (the fully-leveled convention gameplay uses) and asks the offline CQT
    /// verifier about every pitched sound. Background-threaded; cancellable;
    /// never throws except for cancellation.</summary>
    Task<AudioChartLintOutcome> LintAsync(Arrangement arrangement, IList<BeatMarker> beats,
        string? audioPath, CancellationToken cancellationToken = default);
}

/// <summary>
///     Service wrapper over the pure <see cref="ChartAudioLintAnalyzer" />.
///     <para>
///     AUDIO SOURCE POLICY (owner-approved): the ORIGINAL MIX, decoded via
///     <see cref="PcmAudioLoader" /> (the editor's decoder, 48 kHz stereo, downmixed
///     to mono here). Opus-decoded stems are NEVER used — the bright-line
///     stem rule — and the mix always exists, so coverage doesn't suffer.
///     A lossless (WAV-manifest) guitar-stem source can slot in later for
///     better isolation; findings are advisory either way.
///     </para>
///     Whole-clip memory note: the decode plus the CQT matrix hold the full
///     song in memory (~tens of MB for a 5-minute track) — acceptable for an
///     explicit user-invoked action, not for a per-tick path.
/// </summary>
public sealed class AudioChartLintService : IAudioChartLintService
{
    private readonly IAppLogger? _logger;

    public AudioChartLintService(IAppLogger? logger = null) => _logger = logger;

    public Task<AudioChartLintOutcome> LintAsync(Arrangement arrangement, IList<BeatMarker> beats,
        string? audioPath, CancellationToken cancellationToken = default)
        => Task.Run(() => LintCore(arrangement, beats, audioPath, cancellationToken), cancellationToken);

    private AudioChartLintOutcome LintCore(Arrangement arrangement, IList<BeatMarker> beats,
        string? audioPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return AudioChartLintOutcome.Failed("no-audio");

        PcmAudio pcm;
        try
        {
            pcm = PcmAudioLoader.Load(audioPath, []);
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Audio lint: decode failed: {ex.Message}");
            return AudioChartLintOutcome.Failed("decode-failed");
        }

        ct.ThrowIfCancellationRequested();

        // Downmix the 48 kHz stereo decode to mono for the CQT.
        var frames = pcm.FrameCount;
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
            mono[f] = (pcm.Samples[f * 2] + pcm.Samples[f * 2 + 1]) / (2f * 32768f);

        var sounds = ProjectExpectedSounds(arrangement, beats);
        ct.ThrowIfCancellationRequested();

        var result = ChartAudioLintAnalyzer.Analyze(mono, pcm.SampleRate, sounds);
        _logger?.Info(AppLogCategory.Audio,
            $"Audio lint: {result.SoundsVerified}/{result.SoundsChecked} sounds verified, " +
            $"{result.Findings.Count} finding(s) ('{Path.GetFileName(audioPath)}').");
        return new AudioChartLintOutcome(true, null,
            result.SoundsChecked, result.SoundsVerified, result.Findings);
    }

    /// <summary>
    ///     The HIGHEST level's notes/chords as lint sounds. Skips what has no
    ///     verifiable pitch at the FRETTED pitch: full mutes, unpitched slides,
    ///     HARMONICS — a natural/pinch harmonic rings at an overtone, not the
    ///     fretted pitch — and PRE-BENDS.
    ///     <para>
    ///     A pre-bend is bent before it is struck, so it sounds at
    ///     <c>fret + step</c> from its very first frame and the verifier's
    ///     window (t−40 ms … t+140 ms) never sees the un-bent pitch at all: every
    ///     pre-bent note in a correct chart filed a finding. Ordinary bends are
    ///     fine — they start at the fretted pitch and move later — which is why
    ///     the test is the bend point AT the sound's own position, not "has
    ///     bends". (<c>RsTechniques.IsPreBend</c> spells the same rule for the
    ///     Rocksmith writer; bend positions are absolute, see
    ///     <c>BendGeometry.ClampBendsToSustain</c>.)
    ///     </para>
    /// </summary>
    internal static List<LintExpectedSound> ProjectExpectedSounds(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var sounds = new List<LintExpectedSound>();
        if (arrangement.Levels.Count == 0) return sounds;
        var level = arrangement.Levels[^1];

        foreach (var note in level.Notes)
        {
            var pitched = note.Mute != MuteType.Full &&
                          note.Harmonic == HarmonicType.None &&
                          !note.UnpitchedSlide &&
                          !IsPreBent(note, note.Position);
            if (LivePitchAcceptance.SoundingMidi(arrangement, note.StringIndex, note.Fret) is not { } midi)
                continue;
            sounds.Add(new LintExpectedSound(
                note.Position.GetPositionMs(beats), [midi], IsChord: false, IsPitched: pitched));
        }

        foreach (var chord in level.Chords)
        {
            var midis = new List<int>();
            // A chord tone that does not sound its fretted pitch makes the whole
            // chord unverifiable, the same way a harmonic already did: the
            // verifier is asked per pitch and there is no way to say "all but
            // this one". Unpitched slides and pre-bends were missing here while
            // the single-note arm above rejected both.
            var unpitchable = false;
            foreach (var cn in chord.ChordNotes)
            {
                unpitchable |= cn.Harmonic != HarmonicType.None ||
                               cn.UnpitchedSlide ||
                               IsPreBent(cn, chord.Position);
                if (cn.Mute == MuteType.Full) continue;
                if (LivePitchAcceptance.SoundingMidi(arrangement, cn.StringIndex, cn.Fret) is { } m)
                    midis.Add(m);
            }

            if (midis.Count == 0) continue;
            sounds.Add(new LintExpectedSound(
                chord.Position.GetPositionMs(beats), midis,
                IsChord: midis.Count > 1, IsPitched: !unpitchable));
        }

        sounds.Sort(static (a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return sounds;
    }

    /// <summary>A bend point sitting ON the sound's own onset with a non-zero
    /// step — i.e. the string is already bent when it is struck, so the pitch
    /// that sounds is never the fretted one.</summary>
    private static bool IsPreBent(IGuitarSound sound, FractionalPosition position)
    {
        foreach (var bend in sound.BendValues)
        {
            if (bend.Step != 0 && bend.Position.CompareTo(position) <= 0)
                return true;
        }

        return false;
    }
}
