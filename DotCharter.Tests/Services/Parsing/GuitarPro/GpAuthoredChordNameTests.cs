using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
///     Guitar Pro hangs a chord DIAGRAM on the beat a strum is written over, and
///     that diagram's first real field is the name the arranger typed. The GP3-5
///     binary reader used to consume the block purely to keep the beat stream in
///     sync (<c>SkipChordDiagram</c>) and throw the name away, so
///     <c>ChordHandShapeBuilder</c> renamed every imported chord from
///     <c>ChordNameSuggester</c>'s pitch-class guess — which cannot know the
///     arranger meant a slash chord, an inversion, a "no-chord" marker or a
///     particular enharmonic spelling.
///     <para>
///     The name now travels on the import-only <see cref="GpAuthoredChordNames" />
///     side channel — per TRACK, applied at template CREATION only — exactly like
///     the authored fingering it sits beside.
///     </para>
/// </summary>
public sealed class GpAuthoredChordNameTests
{
    /// <summary>The chord shape the fixtures strum. Deliberately a shape the
    /// suggester names something OTHER than the authored label, which is what
    /// makes the assertions below mean anything (proved by the control test).</summary>
    private static FxBeat ChordBeat(string? diagramName) => new()
    {
        Duration = 0,
        ChordDiagramName = diagramName,
        Notes =
        {
            new FxNote(0, 3),
            new FxNote(1, 2),
            new FxNote(2, 0)
        }
    };

    private static Arrangement ImportLegacy(string path)
    {
        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            return Assert.Single(importer.ImportTracks(path, tracks.Select(t => t.Id).ToList()));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- Normalize ---------------------------------------------------------

    [Theory]
    [InlineData("Am", "Am")]
    [InlineData("  Cadd9  ", "Cadd9")]
    [InlineData("F#m7b5", "F#m7b5")]
    [InlineData("N.C.", "N.C.")]
    [InlineData("D/F#", "D/F#")]
    public void Normalize_KeepsARealName(string raw, string expected)
        => Assert.Equal(expected, GpAuthoredChordNames.Normalize(raw));

    /// <summary>A blank name is NOT a name: Guitar Pro writes diagrams with no
    /// label, and storing one would replace the suggester's useful guess with
    /// nothing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\0\0\0")]
    [InlineData(null)]
    public void Normalize_RejectsTheBlankName(string? raw)
        => Assert.Null(GpAuthoredChordNames.Normalize(raw));

    /// <summary>The string is Latin-1 decoded straight off the wire into a label
    /// the chart editor and the sheet export both render, so it is capped and
    /// stripped of control characters.</summary>
    [Fact]
    public void Normalize_CapsAndStripsWhatTheWireCanCarry()
    {
        var normalized = GpAuthoredChordNames.Normalize("A" + new string('m', 500));
        Assert.NotNull(normalized);
        Assert.InRange(normalized!.Length, 1, GpAuthoredChordNames.MaxNameLength);

        Assert.Equal("Am", GpAuthoredChordNames.Normalize("Am\0"));
    }

    /// <summary>The FIRST name recorded for a position wins — a template found by
    /// shape is shared and must never be renamed under an existing chord.</summary>
    [Fact]
    public void Set_KeepsTheFirstNameForAPosition()
    {
        var names = new GpAuthoredChordNames();
        names.Set(new FractionalPosition(4), "Am");
        names.Set(new FractionalPosition(4), "C");

        Assert.True(names.TryGet(new FractionalPosition(4), out var name));
        Assert.Equal("Am", name);
    }

    // ---- End to end: the GP3-5 binary reader --------------------------------

    /// <summary>
    ///     GP4 and GP5 write the "new format" 107-byte diagram; the name sits
    ///     behind a fixed 16-byte prefix in a 21-byte field.
    /// </summary>
    [Theory]
    [InlineData("4.06")]
    [InlineData("5.00")]
    [InlineData("5.10")]
    public void Legacy_TheAuthoredChordName_SurvivesImport(string version)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version,
            new[] { new FxMeasure().AddVoice(ChordBeat("Cadd9")) });

        var arr = ImportLegacy(path);

        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal("Cadd9", template.Name);
    }

    /// <summary>
    ///     The control: the SAME shape with no diagram still gets the suggester's
    ///     name, and that name is NOT the authored one — otherwise the test above
    ///     would pass without the reader ever looking at the diagram.
    /// </summary>
    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void Legacy_WithNoDiagram_TheSuggesterStillNamesTheChordAndDisagrees(string version)
    {
        var path = LegacyGpFixtureBuilder.WriteFile(version,
            new[] { new FxMeasure().AddVoice(ChordBeat(null)) });

        var arr = ImportLegacy(path);

        var template = Assert.Single(arr.ChordTemplates);
        Assert.NotEqual("Cadd9", template.Name);
    }

    /// <summary>A diagram with an EMPTY label must not blank the template's
    /// name — the suggester's guess is better than nothing.</summary>
    [Fact]
    public void Legacy_AnUnlabelledDiagram_LeavesTheSuggestedNameAlone()
    {
        var withLabel = ImportLegacy(LegacyGpFixtureBuilder.WriteFile("5.10",
            new[] { new FxMeasure().AddVoice(ChordBeat("")) }));
        var without = ImportLegacy(LegacyGpFixtureBuilder.WriteFile("5.10",
            new[] { new FxMeasure().AddVoice(ChordBeat(null)) }));

        Assert.NotEmpty(Assert.Single(withLabel.ChordTemplates).Name);
        Assert.Equal(Assert.Single(without.ChordTemplates).Name,
            Assert.Single(withLabel.ChordTemplates).Name);
    }

    /// <summary>GP3's OLD-format diagram spells the name as a length-prefixed
    /// block instead, and the reader has to take it from there too.</summary>
    [Fact]
    public void Legacy_Gp3OldFormatDiagram_IsConsumedWithoutDesyncingTheBeatStream()
    {
        // GP3 writes the old (format-0) diagram, which the fixture builder does
        // not author — so this pins the shape GP3 CAN produce: a beat whose
        // diagram bit is set must still leave the following notes readable.
        var path = LegacyGpFixtureBuilder.WriteFile("3.00",
            new[] { new FxMeasure().AddVoice(FxBeat.Note(0, 0, 5), FxBeat.Note(0, 0, 7)) });

        var arr = ImportLegacy(path);

        Assert.Equal(new[] { 5, 7 }, arr.Levels[0].Notes.OrderBy(n => n.Position).Select(n => n.Fret));
    }

    // ---- Per TRACK, not per file -------------------------------------------

    /// <summary>
    ///     Both authored side channels are keyed by chart POSITION, and two tracks
    ///     of one score strum together constantly — so a file-wide table let the
    ///     FIRST track's chord name (and fingering) win on the second track's
    ///     template. <c>GpAuthoredChordNames.Set</c>/<c>GpAuthoredFingering.Set</c>
    ///     both keep the first value for a key, so the leak was silent.
    /// </summary>
    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void Legacy_TwoTracksStrummingTogether_EachKeepItsOwnAuthoredChordName(string version)
    {
        var path = LegacyGpFixtureBuilder.WriteMultiTrackFile(version,
        [
            ("Rhythm", new[] { new FxMeasure().AddVoice(ChordBeat("Am")) }),
            ("Second", new[] { new FxMeasure().AddVoice(ChordBeat("Fmaj7")) })
        ]);

        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            Assert.Equal(2, tracks.Count);

            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());
            Assert.Equal(2, arrangements.Count);

            Assert.Equal("Am", Assert.Single(arrangements[0].ChordTemplates).Name);
            Assert.Equal("Fmaj7", Assert.Single(arrangements[1].ChordTemplates).Name);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>The same leak, on the fingering channel that shipped first.</summary>
    [Theory]
    [InlineData("4.06")]
    [InlineData("5.10")]
    public void Legacy_TwoTracksStrummingTogether_EachKeepsItsOwnAuthoredFingering(string version)
    {
        static FxBeat Fingered(int a, int b, int c) => new()
        {
            Duration = 0,
            Notes =
            {
                new FxNote(0, 3, LeftFinger: a),
                new FxNote(1, 2, LeftFinger: b),
                new FxNote(2, 0, LeftFinger: c)
            }
        };

        var path = LegacyGpFixtureBuilder.WriteMultiTrackFile(version,
        [
            ("Rhythm", new[] { new FxMeasure().AddVoice(Fingered(3, 2, 1)) }),
            ("Second", new[] { new FxMeasure().AddVoice(Fingered(1, 2, 4)) })
        ]);

        try
        {
            var importer = new GpImporter();
            var tracks = importer.GetAvailableTracks(path);
            var arrangements = importer.ImportTracks(path, tracks.Select(t => t.Id).ToList());

            var first = Assert.Single(arrangements[0].ChordTemplates);
            var second = Assert.Single(arrangements[1].ChordTemplates);

            Assert.Equal(3, first.Fingers[0]);
            Assert.Equal(1, second.Fingers[0]);
            Assert.Equal(4, second.Fingers[2]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
