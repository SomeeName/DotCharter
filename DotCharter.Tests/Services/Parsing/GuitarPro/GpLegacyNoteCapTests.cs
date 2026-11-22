using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     The GP3-5 binary importer clones a repeated bar's notes once per later
///     pass, so nested repeats multiply notes the file never spells out. It
///     must honour the same emitted-note cap the GPIF path has
///     (<see cref="GpFormatBounds.MaxNotesPerTrack" />) and refuse with an
///     <see cref="InvalidDataException" /> (shown as <c>Status_GpImportFailed</c>).
/// </summary>
public sealed class GpLegacyNoteCapTests
{
    private static FxMeasure BusyBar(int notes, Action<FxMeasure>? configure = null)
    {
        var measure = new FxMeasure().AddVoice(
            Enumerable.Range(0, notes).Select(i => FxBeat.Note(2, stringIndex: 0, fret: i % 20)).ToArray());
        configure?.Invoke(measure);
        return measure;
    }

    private static int ImportedNoteCount(string version, FxMeasure[] measures, int cap)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version, measures);
        try
        {
            var tracks = new GpLegacyImporter { MaxNotesPerTrack = cap }.GetTracks(path);
            var result = new GpLegacyImporter { MaxNotesPerTrack = cap }
                .Import(path, tracks.Select(t => t.Id).ToList());
            return Assert.Single(result.Arrangements).Levels.Sum(l => l.Notes.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("5.00")]
    [InlineData("4.06")]
    public void ARepeatThatExpandsPastTheCap_IsRefusedBeforeCloning(string version)
    {
        // 8 notes x 10 passes = 80 emitted notes against a cap of 50.
        var measures = new[]
        {
            BusyBar(8, m => { m.RepeatStart = true; m.RepeatTotalPasses = 10; })
        };

        var ex = Assert.Throws<InvalidDataException>(() => ImportedNoteCount(version, measures, cap: 50));

        Assert.Contains("more than 50 notes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleHugeBarWithNoRepeat_IsRefused()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            ImportedNoteCount("5.00", [BusyBar(16)], cap: 10));

        Assert.Contains("more than 10 notes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedScoreInsideTheCap_ImportsEveryPass()
    {
        var count = ImportedNoteCount("5.00",
            [BusyBar(8, m => { m.RepeatStart = true; m.RepeatTotalPasses = 3; })], cap: 24);

        Assert.Equal(24, count);
    }

    [Fact]
    public void TheDefaultCapIsTheSharedBound() =>
        Assert.Equal(GpFormatBounds.MaxNotesPerTrack, new GpLegacyImporter().MaxNotesPerTrack);
}
