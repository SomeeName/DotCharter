using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Tests.Services.Export.Rs7;

/// <summary>Golden tuning, mask, template and technique fields of the internal extended model.</summary>
public sealed partial class Rs7ExtendedRangeGoldenTests
{
    private static RocksmithSng Export(string name)
    {
        var (arrangement, beats) = Chart(CaseOf(name));
        return SngFromDomainMapper.BuildInstrumental(arrangement, beats, 40_000,
            new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Unspecified)).Sng;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Tuning_and_section_string_masks_match(string name)
    {
        var golden = LoadGolden(name);
        var sng = Export(name);

        Assert.Equal(golden.Tuning, sng.MetaData.Tuning.Select(t => (int)t));
        Assert.Equal(golden.StringMask, sng.Sections.Aggregate(0, (mask, s) => mask | (s.StringMask[0] & 0xFF)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Chord_templates_match(string name)
    {
        var golden = LoadGolden(name);
        var sng = Export(name);

        Assert.Equal(golden.Chords.Count, sng.Chords.Count);
        for (var i = 0; i < golden.Chords.Count; i++)
        {
            Assert.Equal(golden.Chords[i].Frets, sng.Chords[i].Frets.Select(f => (int)f));
            Assert.Equal(golden.Chords[i].Fingers, sng.Chords[i].Fingers.Select(f => (int)f));
            Assert.Equal(golden.Chords[i].Notes, sng.Chords[i].Notes);
        }

        Assert.Equal(golden.ChordNotes, sng.ChordNotes.Count);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Notes_match_on_string_fret_chord_ids_and_the_rs7x_mask_bits(string name)
    {
        var golden = LoadGolden(name);
        var sng = Export(name);

        const long bits = RsNoteMask.Chord | RsNoteMask.DoubleStop | RsNoteMask.Open | RsNoteMask.ChordNotes;
        var notes = sng.Levels.Single().Notes;
        Assert.Equal(golden.Notes.Select(n => (n.String, n.Fret, n.ChordId, n.ChordNotesId, n.Mask & bits)),
            notes.Select(n => ((int)n.StringIndex, (int)n.Fret, n.ChordId, n.ChordNotesId, n.Mask & bits)));
    }

    /// <summary>Opt-in: builds the two bass cases (single notes plus chords, the low B and the high C in
    /// chords) as in-game test packages for the rs7string mod. Set DOTCHARTER_RS7_INGAME_OUT.</summary>
    [Theory]
    [InlineData("b5", "rs7xbass5chdc_p.psarc")]
    [InlineData("b6", "rs7xbass6chdc_p.psarc")]
    public async Task Write_the_bass_chord_packages(string name, string fileName)
    {
        var outputDir = Environment.GetEnvironmentVariable("DOTCHARTER_RS7_INGAME_OUT");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(outputDir), "Opt-in: set DOTCHARTER_RS7_INGAME_OUT.");

        var (arrangement, beats) = Chart(CaseOf(name));
        var project = new ChartProject { Bpm = 80, Chart = { SongName = $"RS7 {name} chords", Artist = "DotCharter", AlbumName = "RS7", AlbumYear = "2026" } };
        foreach (var beat in beats) project.Beats.Add(beat);
        project.Arrangements.Add(arrangement);

        Directory.CreateDirectory(outputDir);
        var workDir = Directory.CreateTempSubdirectory("rs7bass-audio-").FullName;
        try
        {
            await Rs7TestAudio.BuildPackageAsync(project, Path.Combine(outputDir, fileName),
                Path.GetFileNameWithoutExtension(fileName).Replace("_p", "", StringComparison.Ordinal),
                40 * Rs7TestSupport.BeatMs, workDir, TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
