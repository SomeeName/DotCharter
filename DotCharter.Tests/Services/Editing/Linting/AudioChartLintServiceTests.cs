using DotCharter.Services.Editing.Linting;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Editing.Linting;

/// <summary>
///     v0.29 audio lint service: decode (original mix ONLY — never Opus
///     stems), highest-level chart projection through the matcher's own
///     sounding-pitch math, and the analyzer verdicts end to end against a
///     synthetic WAV. Headless; in-process WAV decode needs no ffmpeg.
/// </summary>
public sealed class AudioChartLintServiceTests : IDisposable
{
    private const int Sr = 48000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    /// <summary>Standard tuning, one level, one note: string 0 fret 5 —
    /// sounding MIDI 45 (A2, 110 Hz) by the matcher's math.</summary>
    private static Arrangement ArrangementWithNote(int fret)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote
        {
            StringIndex = 0, Fret = fret,
            Position = new FractionalPosition(2, new Fraction(0, 1)) // beat 2 → 1000 ms below
        });
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static List<BeatMarker> Beats()
        => Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0 }).ToList();

    /// <summary>1 s silence + 1.5 s of a 110 Hz-rooted tone + tail silence,
    /// as a 16-bit mono WAV (the in-process decoder conforms it to 48 kHz
    /// stereo; the service downmixes back).</summary>
    private string WriteToneWav(double midi = 45)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "mix.wav");
        var n = Sr * 3;
        var f0 = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        var dataBytes = n * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Sr);
        writer.Write(Sr * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < n; i++)
        {
            double value = 0;
            if (i is >= Sr and < ((int)(Sr * 2.5)))
            {
                var t = (i - Sr) / (double)Sr;
                var envelope = Math.Min(1.0, t * 50.0) * Math.Exp(-t * 0.8);
                value = envelope * (0.6 * Math.Sin(2 * Math.PI * f0 * t) +
                                    0.25 * Math.Sin(2 * Math.PI * f0 * 2 * t) +
                                    0.1 * Math.Sin(2 * Math.PI * f0 * 3 * t));
            }

            writer.Write((short)(value * 24000));
        }

        return path;
    }

    [Fact]
    public async Task ACorrectChart_VerifiesAgainstTheMix()
    {
        var outcome = await new AudioChartLintService().LintAsync(
            ArrangementWithNote(fret: 5), Beats(), WriteToneWav(),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Ran);
        Assert.Equal(1, outcome.SoundsChecked);
        Assert.Equal(1, outcome.SoundsVerified);
        Assert.Empty(outcome.Findings);
    }

    [Fact]
    public async Task AWrongFret_ProducesAFindingNamingTheSoundingPitch()
    {
        // Audio plays fret 5 (MIDI 45); the chart claims fret 7 (MIDI 47).
        var outcome = await new AudioChartLintService().LintAsync(
            ArrangementWithNote(fret: 7), Beats(), WriteToneWav(),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Ran);
        var finding = Assert.Single(outcome.Findings);
        Assert.Equal(47, finding.ChartedMidi);
        Assert.Equal(45, finding.ObservedMidi);
        Assert.Equal(1000, finding.TimeMs, 3);
    }

    [Fact]
    public async Task MissingAudio_FailsTyped_NeverThrows()
    {
        var outcome = await new AudioChartLintService().LintAsync(
            ArrangementWithNote(5), Beats(), Path.Combine(_dir, "nope.wav"),
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Ran);
        Assert.Equal("no-audio", outcome.Error);
    }

    [Fact]
    public void Projection_SkipsUnverifiablePitches()
    {
        var arrangement = ArrangementWithNote(5);
        var level = arrangement.Levels[0];
        level.Notes[0].Harmonic = HarmonicType.Normal; // rings at an overtone
        level.Notes.Add(new GuitarNote
        {
            StringIndex = 1, Fret = 3, Mute = MuteType.Full,
            Position = new FractionalPosition(4, new Fraction(0, 1))
        });

        var sounds = AudioChartLintService.ProjectExpectedSounds(arrangement, Beats());

        Assert.Equal(2, sounds.Count);
        Assert.All(sounds, s => Assert.False(s.IsPitched));
    }

    /// <summary>
    ///     A PRE-BEND sounds at <c>fret + step</c> from its first frame, so the
    ///     verifier's window (t−40 ms … t+140 ms) never contains the fretted
    ///     pitch: every pre-bent note in a correct chart used to file a finding.
    ///     An ORDINARY bend starts at the fretted pitch and moves later, so it
    ///     must keep being checked — the test is the bend point AT the onset,
    ///     not "has bends".
    /// </summary>
    [Fact]
    public void Projection_SkipsAPreBend_ButStillChecksAnOrdinaryBend()
    {
        var arrangement = ArrangementWithNote(5);
        var note = arrangement.Levels[0].Notes[0];
        note.Sustain = new FractionalPosition(1, new Fraction(0, 1));

        note.BendValues.Add(new BendValue(note.Position + new FractionalPosition(0, new Fraction(1, 2)), 2.0));
        var ordinary = AudioChartLintService.ProjectExpectedSounds(arrangement, Beats());
        Assert.True(Assert.Single(ordinary).IsPitched);

        note.BendValues.Insert(0, new BendValue(note.Position, 2.0));
        var preBent = AudioChartLintService.ProjectExpectedSounds(arrangement, Beats());
        Assert.False(Assert.Single(preBent).IsPitched);
    }

    /// <summary>The chord arm rejected harmonics only; an unpitched slide and a
    /// pre-bend on a chord tone are just as unverifiable, and the verifier is
    /// asked per pitch so there is no way to exclude one tone of a chord.</summary>
    [Theory]
    [InlineData("slide")]
    [InlineData("prebend")]
    public void Projection_SkipsAChordWhoseToneDoesNotSoundItsFret(string technique)
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        var position = new FractionalPosition(2, new Fraction(0, 1));
        var chord = new Chord { Position = position };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        var second = new ChordNote { StringIndex = 1, Fret = 5 };
        if (technique == "slide") second.UnpitchedSlide = true;
        else second.BendValues.Add(new BendValue(position, 1.0));
        chord.ChordNotes.Add(second);
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);

        var sounds = AudioChartLintService.ProjectExpectedSounds(arrangement, Beats());

        Assert.False(Assert.Single(sounds).IsPitched);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
