using DotCharter.Services.Export;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     The save companions: an Editor on Fire .eof on every save, and a Rocksmith
///     .psarc in the background. Both are DERIVED — the .rscp is the source of
///     truth — so the contract is that they are honest about being disabled, and
///     that they never destroy a good previous artifact.
/// </summary>
public sealed partial class SaveCompanionServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_companion_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private async Task<string> BuildSevenStringCompanionAsync()
    {
        WriteWav(Path.Combine(_dir, "song.wav"));
        var service = Create(new AppConfig { SaveCompanionPsarc = true, SaveCompanionPsarcCodec = "Vorbis" });

        var project = CreateProject();
        project.Arrangements[0].StringCount = 7;
        project.Arrangements[0].Levels[0].Anchors.Add(
            new FretAnchor(TimelineMath.PositionFromMs(1000, project.Beats), 1, 4));
        project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(2000, project.Beats),
            StringIndex = 0, // the low B
            Fret = 3
        });
        var chord = new Chord { Position = TimelineMath.PositionFromMs(3000, project.Beats) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 4 });
        project.Arrangements[0].Levels[0].Chords.Add(chord);

        var path = await service.BuildPsarcAsync(
            Path.Combine(_dir, "MySong.rscp"), project, audioLengthMs: null, CancellationToken.None);
        Assert.NotNull(path);
        return path;
    }

    // ---- helpers ------------------------------------------------------------

    private static SaveCompanionService Create(AppConfig config, FakeAppLogger? logger = null) =>
        new(new FakeConfigurationService { AppConfig = config }, logger ?? new FakeAppLogger());

    private static ChartProject CreateProject()
    {
        var project = new ChartProject
        {
            Bpm = 120,
            AudioFileName = "song.wav",
            Chart = { SongName = "My Song", Artist = "Tester" }
        };

        for (var i = 0; i < 32; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        var arrangement = new Arrangement { Name = "Lead" };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arrangement);

        return project;
    }

    private static void WriteWav(string path)
    {
        const int rate = 48000, channels = 2, frames = rate; // 1 second

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(file);

        var dataBytes = frames * channels * sizeof(short);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(rate);
        writer.Write(rate * channels * sizeof(short));
        writer.Write((ushort)(channels * sizeof(short)));
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (var f = 0; f < frames; f++)
        {
            var v = (short)(Math.Sin(2 * Math.PI * 220 * f / rate) * 10000);
            for (var c = 0; c < channels; c++) writer.Write(v);
        }
    }
}
