using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.Tests.Analysis;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Services.Editing.Tempo;

/// <summary>A real chart editor over <see cref="RecordingAlignmentFixture" />'s chart (its notes on the low string,
///     a section marker every 16 beats) and a WAV of its recording, for the align-to-recording service and dialog.</summary>
internal static class RecordingAlignmentProject
{
    public static (ChartEditHarness Harness, RecordingAlignmentService Service, string Audio) Build(RecordingAlignmentFixture fx, string dir)
    {
        var h = new ChartEditHarness();
        h.Project.Beats.Clear();
        for (var i = 0; i < RecordingAlignmentFixture.Beats; i++)
            h.Project.Beats.Add(new BeatMarker { Time = fx.ChartTimes[i], Bpm = 120, IsMeasure = i % 4 == 0 });

        var level = h.Project.Arrangements[0].Levels[0];
        foreach (var e in fx.Events)
        {
            var beat = (int)e.BeatPosition;
            var position = e.BeatPosition > beat ? new FractionalPosition(beat, new Fraction(1, 2)) : new FractionalPosition(beat);
            level.Notes.Add(new GuitarNote { Position = position, StringIndex = 0, Fret = e.Midi[0] - 40 });
        }

        foreach (var start in fx.SectionStarts)
            h.Project.Arrangements[0].EventPoints.Add(new EventPoint { Position = new FractionalPosition(start), Section = "verse" });

        var audio = Path.Combine(dir, $"song-{Guid.NewGuid():N}.wav");
        WriteWav(audio, fx.Samples, AnalysisTestSignals.SampleRate);
        var service = new RecordingAlignmentService(new BeatResyncService(), h.History, new FakeConfigurationService(), h.Transport,
            h.Editor, new FakeAppLogger());
        return (h, service, audio);
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(36 + samples.Length * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
    }
}
