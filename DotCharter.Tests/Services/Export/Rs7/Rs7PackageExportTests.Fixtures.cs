using DotCharter.Services.Export;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>
///     #736, #777: a 7-string guitar exports in the format the owner's <c>rs7string</c>
///     mod plays — the bytes it relies on are checked one by one (the RS7X block
///     included), the package comes back through the importer with every sound, tuning
///     and extra-string technique intact, and the plain and encrypted containers both
///     import. An 8-string guitar is refused until the mod plays one.
/// </summary>
public sealed partial class Rs7PackageExportTests : IDisposable
{
    private const int BeatCount = 64;
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-rs7-export-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- fixture ------------------------------------------------------------

    /// <summary>Chords that keep native columns AND use an extra string.</summary>
    private const int MixedChordCount = 2;

    /// <summary>7-string: drop A (low B down a whole step); 8-string: low F# down a half step.</summary>
    private static int[] DomainTuning(int strings) =>
        strings == 7 ? [-2, 0, 0, 0, 0, 0, 0] : [-1, 0, 0, 0, 0, 0, 0, 0];

    private static int[] TuningInPackageOrder(int strings)
    {
        var domain = DomainTuning(strings);
        var package = new int[strings];
        for (var d = 0; d < strings; d++) package[Rs7StringLayout.ToSng(d, strings)] = domain[d];
        return package;
    }

    private async Task<string> BuildAsync(ChartProject project, string fileName, bool? plain)
    {
        var wem = Path.Combine(_tempDir, "audio.wem");
        if (!File.Exists(wem))
        {
            var data = new byte[4_000];
            new Random(42).NextBytes(data);
            await File.WriteAllBytesAsync(wem, data, TestContext.Current.CancellationToken);
        }

        var output = Path.Combine(_tempDir, fileName);
        var result = await new RocksmithPackageBuilder(new FakeAppLogger()).BuildPsarcAsync(
            ChartProjectCloner.Clone(project),
            new RocksmithBuildOptions
            {
                OutputPath = output,
                AudioPath = wem,
                DlcKey = Path.GetFileNameWithoutExtension(fileName).Replace("_p", "", StringComparison.Ordinal),
                IncludeVocals = false,
                SongLengthMs = BeatCount * Rs7TestSupport.BeatMs,
                PreviewStartMs = 0,
                PlainPackage = plain
            }, progress: null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("found no place", StringComparison.Ordinal));
        return result.PsarcPath;
    }

    /// <summary>
    ///     A guitar of <paramref name="strings" /> strings in CHART order (0 = lowest):
    ///     single notes on every string, a slide and a bend on the lowest string, two
    ///     chords that mix the lowest string with native ones (one with techniques on the
    ///     extra note), and one chord on the extra strings only.
    /// </summary>
    internal static ChartProject CreateProject(int strings)
    {
        var project = new ChartProject
        {
            Bpm = 80,
            Chart = { SongName = $"RS7 {strings}-string", Artist = "DotCharter", AlbumName = "RS7", AlbumYear = "2026" }
        };
        foreach (var beat in Rs7TestSupport.Beats(BeatCount)) project.Beats.Add(beat);

        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            Name = "Lead",
            StringCount = strings,
            Tuning = TuningUtilities.GetTuningNames(strings, DomainTuning(strings))
        };
        foreach (var offset in DomainTuning(strings)) arrangement.TuningPitches.Add(new TuningOffset(offset));

        var extra = strings - 6;
        var level = new ChartLevel { DifficultyIndex = 0 };
        FractionalPosition At(int beat) => TimelineMath.PositionFromMs(beat * Rs7TestSupport.BeatMs, project.Beats);
        level.Anchors.Add(new FretAnchor(At(4), 1, 5));

        var beatIndex = 4;
        for (var s = 0; s < strings; s++)
            level.Notes.Add(new GuitarNote { Position = At(beatIndex++), StringIndex = s, Fret = 3 });

        beatIndex += 2;
        var slidStart = At(beatIndex);
        level.Notes.Add(new GuitarNote
        {
            Position = slidStart, StringIndex = 0, Fret = 3, SlideTo = 5,
            Sustain = At(beatIndex + 1) - slidStart
        });
        beatIndex += 3;
        var bendStart = At(beatIndex);
        var bent = new GuitarNote { Position = bendStart, StringIndex = 0, Fret = 4, Sustain = At(beatIndex + 2) - bendStart };
        bent.BendValues.Add(new BendValue(At(beatIndex + 1), 1.0));
        level.Notes.Add(bent);
        beatIndex += 4;

        // Mixed chord: low string fret 2 (finger 1) + the two next strings fret 4.
        var template = new ChordTemplate { Id = 0, Name = "X5" };
        template.Frets[0] = 2;
        template.Fingers[0] = 1;
        template.Frets[extra] = 4;
        template.Fingers[extra] = 3;
        template.Frets[extra + 1] = 4;
        template.Fingers[extra + 1] = 4;
        template.Frets[strings - 1] = 0; // the open high e: package string 5, next to the extra ones
        arrangement.ChordTemplates.Add(template);
        for (var i = 0; i < MixedChordCount; i++)
        {
            var position = At(beatIndex);
            var chord = new Chord { Position = position, TemplateId = 0 };
            var sustain = TimelineMath.PositionFromMs(beatIndex * Rs7TestSupport.BeatMs + 1500, project.Beats) - position;
            // The high e FIRST: a writer that folded the extra note onto package string
            // 5 afterwards would overwrite its techniques with the extra note's.
            chord.ChordNotes.Add(new ChordNote { StringIndex = strings - 1, Fret = 0, Sustain = sustain });
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = 0, Fret = 2, Sustain = sustain, Vibrato = i == 0, VibratoStrength = i == 0 ? 80 : 0,
                Mute = i == 0 ? MuteType.Palm : MuteType.None
            });
            chord.ChordNotes.Add(new ChordNote { StringIndex = extra, Fret = 4, Sustain = sustain });
            chord.ChordNotes.Add(new ChordNote { StringIndex = extra + 1, Fret = 4, Sustain = sustain });
            level.Chords.Add(chord);
            beatIndex += 4;
        }

        // A chord on the extra strings only (one string on a 7, two on an 8).
        var extraOnly = new ChordTemplate { Id = 1, Name = "" };
        for (var s = 0; s < extra; s++) extraOnly.Frets[s] = 5;
        arrangement.ChordTemplates.Add(extraOnly);
        var lone = new Chord { Position = At(beatIndex), TemplateId = 1 };
        for (var s = 0; s < extra; s++) lone.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 5 });
        level.Chords.Add(lone);

        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }
}
