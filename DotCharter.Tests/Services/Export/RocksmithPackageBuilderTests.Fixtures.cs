using System.Text.Json;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Export;
using DotCharter.Services.Export.Audio;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Sng;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

public sealed partial class RocksmithPackageBuilderTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-export-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- fixture project ----------------------------------------------------

    private static List<BeatMarker> CreateBeats(int count = 64)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 500.0,
                IsMeasure = i % 4 == 0,
                Bpm = 120,
                BeatsInMeasure = 4,
                NoteDenominator = 4
            });
        }

        return beats;
    }

    private static FractionalPosition Pos(IList<BeatMarker> beats, double ms) => TimelineMath.PositionFromMs(ms, beats);

    internal static ChartProject CreateProject()
    {
        var project = new ChartProject
        {
            Bpm = 120,
            Chart =
            {
                SongName = "Golden Test Song",
                Artist = "DotCharter",
                AlbumName = "Test Album",
                AlbumYear = "2026"
            }
        };

        foreach (var beat in CreateBeats()) project.Beats.Add(beat);
        var beats = project.Beats;

        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Name = "Lead",
            StringCount = 6
        };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(i == 0 ? -2 : 0));
        arrangement.Tuning = TuningUtilities.GetTuningNames(6, [-2, 0, 0, 0, 0, 0]);

        arrangement.Phrases.AddRange(["COUNT", "verse", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 0), 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 2000), 1));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(beats, 30000), 2));
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 2000), Section = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = Pos(beats, 30000), Section = "noguitar" });

        arrangement.ToneBase = "clean";
        arrangement.ToneA = "clean";
        arrangement.ToneChanges.Add(new ToneChange { Position = Pos(beats, 2000), ToneName = "clean" });

        var template = new ChordTemplate { Id = 0, Name = "E5" };
        template.Frets[0] = 0;
        template.Frets[1] = 2;
        template.Fingers[1] = 1;
        arrangement.ChordTemplates.Add(template);

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(Pos(beats, 2000), 1, 4));

        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 2000),
            StringIndex = 0,
            Fret = 3,
            Sustain = Pos(beats, 500)
        });
        level.Notes.Add(new GuitarNote
        {
            Position = Pos(beats, 3000),
            StringIndex = 2,
            Fret = 7,
            Hopo = HopoType.HammerOn,
            Mute = MuteType.Palm,
            Accent = true
        });
        var bendNote = new GuitarNote
        {
            Position = Pos(beats, 4000),
            StringIndex = 1,
            Fret = 9,
            Sustain = Pos(beats, 1000)
        };
        bendNote.BendValues.Add(new BendValue(Pos(beats, 4400), 1.0));
        level.Notes.Add(bendNote);

        var chord = new Chord { Position = Pos(beats, 6000), TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 0, Sustain = Pos(beats, 500) });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2, Sustain = Pos(beats, 500) });
        level.Chords.Add(chord);

        level.HandShapes.Add(new HandShape
        {
            Position = Pos(beats, 6000),
            EndPosition = Pos(beats, 7000),
            TemplateId = 0,
            Name = "E5"
        });

        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        var vocals = new VocalTrack { Name = "Vocals" };
        var phrase = new LyricPhrase { Position = Pos(beats, 8000) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = Pos(beats, 8000),
            EndPosition = Pos(beats, 8400),
            // No trailing space: since #324 a word end is the IsWordPart flag, not
            // a space in the text, on export and on psarc import alike.
            Text = "test",
            IsPhraseEnd = true,
            Note = 60
        });
        vocals.Phrases.Add(phrase);
        project.VocalTracks.Add(vocals);

        return project;
    }

    private string CreateFakeWem()
    {
        // A real .wem when fixtures are available, otherwise deterministic bytes.
        var fixture = FindFixtureFile("guitar.wem");
        if (fixture != null) return fixture;

        var path = Path.Combine(_tempDir, "audio.wem");
        var data = new byte[80_000];
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private string WriteWav(int seconds)
    {
        var path = Path.Combine(_tempDir, $"audio_{seconds}s.wav");
        var samples = new short[48_000 * seconds * 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(Math.Sin(2 * Math.PI * 220 * (i / 2) / 48_000.0) * 8000);
        PcmAudioLoader.WriteWav(path, new PcmAudio(samples, 2, 48_000));
        return path;
    }

    private static string? FindFixtureFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData", "EOF");
            if (Directory.Exists(candidate))
            {
                return Directory
                    .EnumerateFiles(candidate, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static void AssertBankNamedFor(byte[] bank, string name)
    {
        var chunks = new Dictionary<string, byte[]>();
        for (var position = 0; position < bank.Length;)
        {
            var length = BitConverter.ToInt32(bank, position + 4);
            chunks[System.Text.Encoding.ASCII.GetString(bank, position, 4)] = bank.AsSpan(position + 8, length).ToArray();
            position += 8 + length;
        }

        var stid = chunks["STID"];
        Assert.Equal($"Song_{name}", System.Text.Encoding.ASCII.GetString(stid, 13, stid[12]));
        var eventId = BitConverter.GetBytes(SoundBankWriter.FnvHash($"Play_{name}"));
        Assert.True(chunks["HIRC"].AsSpan().IndexOf(eventId) >= 0, $"HIRC has no Play_{name} event");
    }

    /// <summary>Reports SYNCHRONOUSLY, unlike <see cref="Progress{T}" />, which
    /// posts to the thread pool when there is no synchronization context — a
    /// test that cancels from a progress callback needs the cancel to land
    /// before the builder moves on.</summary>
    private sealed class SyncProgress(Action<RocksmithBuildProgress> onReport)
        : IProgress<RocksmithBuildProgress>
    {
        public void Report(RocksmithBuildProgress value) => onReport(value);
    }

    /// <summary>
    ///     The manifest drops NAMELESS chord templates, which since 0.41 includes
    ///     every double stop. This proves the list is still populated by a shape
    ///     that keeps its name, so the zero above means "correctly excluded" rather
    ///     than "the field broke".
    /// </summary>
    private static void AThreeStringNamedTemplateStillReachesTheManifest(RsPackageMetadata meta)
    {
        var project = CreateProject();
        var arrangement = project.Arrangements[0];

        var named = new ChordTemplate { Id = 1, Name = "Amin" };
        named.Frets[1] = 0;
        named.Frets[2] = 2;
        named.Frets[3] = 2;
        arrangement.ChordTemplates.Add(named);

        var chord = new Chord { Position = Pos(project.Beats, 8000), TemplateId = 1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 0 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 2 });
        arrangement.Levels[0].Chords.Add(chord);

        var build = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 32_000);
        var packageArr = new RsPackageArrangement
        {
            ArrangementName = "Lead",
            PartitionName = "lead",
            SongPartition = 1,
            PersistentId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            MasterId = 12345,
            Sng = build.Sng,
            NoteCounts = build.NoteCounts,
            Domain = arrangement,
            ToneBase = "clean",
            ToneSlots = ["clean"]
        };

        using var doc = JsonDocument.Parse(RsManifestGenerator.CreateArrangementManifest(meta, packageArr));
        var templates = doc.RootElement
            .GetProperty("Entries").GetProperty("11111111222233334444555555555555")
            .GetProperty("Attributes").GetProperty("ChordTemplates");

        Assert.Equal(1, templates.GetArrayLength());
        Assert.Equal("Amin", templates[0].GetProperty("ChordName").GetString());
    }
}
