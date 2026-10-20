using DotCharter.Services.Export.Sheet;

namespace DotCharter.Tests.Services.Export.Sheet;

/// <summary>
///     What lets the sheet strip skip a render.
///     <para>
///         <c>SheetMusicView.RebuildAsync</c> runs on every committed edit while
///         the strip is open, and its export has to run on the UI thread (it
///         reads the live graph). The AlphaTab layout and the AlphaSkia raster
///         behind it do not — but only if "the notation did not change" is a fact
///         and not a hope. Two halves:
///     </para>
///     <list type="number">
///         <item>
///             The exporter is DETERMINISTIC — same inputs, byte-identical string
///             — and blind to the fields it does not emit. A hash-of-the-string
///             cache over a non-deterministic exporter would skip nothing; one
///             over an exporter that leaked a timestamp or a hash code would skip
///             at random.
///         </item>
///         <item>
///             <see cref="SheetRenderKey.ShouldRerender" /> — the geometry is in
///             the key too, so a resize / zoom / DPI / layout change still
///             re-renders.
///         </item>
///     </list>
///     No Avalonia and no AlphaTab: both halves are pure.
/// </summary>
public sealed class AlphaTexExportStabilityTests
{
    private static (ChartProject Project, Arrangement Arr, ChartLevel Level, List<BeatMarker> Beats) Fixture()
    {
        var project = new ChartProject();
        var arr = new Arrangement { Name = "Lead", StringCount = 6 };
        var level = new ChartLevel();
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);

        var beats = new List<BeatMarker>();
        for (var i = 0; i < 16; i++)
            beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Name = "Am",
            Frets = new Dictionary<int, int> { { 1, 0 }, { 2, 2 }, { 3, 2 }, { 4, 1 }, { 5, 0 } },
            Fingers = new Dictionary<int, int> { { 2, 2 }, { 3, 3 }, { 4, 1 } }
        });

        for (var i = 0; i < 8; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i),
                StringIndex = i % 6,
                Fret = 1 + i % 12,
                Sustain = new FractionalPosition(new Fraction(1, 2)),
                Vibrato = i % 3 == 0,
                Hopo = i % 4 == 0 ? HopoType.HammerOn : HopoType.None
            });
        }

        var chord = new Chord { Position = new FractionalPosition(9), TemplateId = 0 };
        foreach (var (s, f) in new[] { (1, 0), (2, 2), (3, 2), (4, 1), (5, 0) })
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        level.Chords.Add(chord);

        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(9),
            EndPosition = new FractionalPosition(10),
            TemplateId = 0
        });

        return (project, arr, level, beats);
    }

    private static string Export((ChartProject Project, Arrangement Arr, ChartLevel Level, List<BeatMarker> Beats) f) =>
        AlphaTexExporter.Export(f.Project, f.Arr, f.Level, f.Beats).AlphaTex;

    /// <summary>
    ///     Same snapshot in, byte-identical string out. The whole skip rests on
    ///     this: an exporter that folded in a hash code, a timestamp or a
    ///     dictionary enumeration order would differ run to run and the cache
    ///     would never hit.
    /// </summary>
    [Fact]
    public void Export_IsByteIdenticalForTheSameSnapshot()
    {
        var f = Fixture();

        var first = Export(f);
        var second = Export(f);

        Assert.NotEmpty(first);
        Assert.Equal(first, second, StringComparer.Ordinal);
    }

    /// <summary>
    ///     …and blind to a field it does not emit. <c>Ignore</c> and
    ///     <c>PassOtherNotes</c> are Rocksmith scoring flags with no notation at
    ///     all, and they are bound to keyboard verbs — so toggling one over a
    ///     selection is exactly the edit the strip should not re-render for.
    /// </summary>
    [Fact]
    public void Export_IsUnchangedByAFieldTheNotationDoesNotShow()
    {
        var f = Fixture();
        var before = Export(f);

        foreach (var note in f.Level.Notes)
        {
            note.Ignore = !note.Ignore;
            note.PassOtherNotes = !note.PassOtherNotes;
        }

        Assert.Equal(before, Export(f), StringComparer.Ordinal);
    }

    /// <summary>The other direction, so the test above cannot pass by the
    /// exporter having stopped looking at anything: a real notation change DOES
    /// move the string.</summary>
    [Fact]
    public void Export_ChangesWhenTheNotationDoes()
    {
        var f = Fixture();
        var before = Export(f);

        f.Level.Notes[0].Fret += 3;

        Assert.NotEqual(before, Export(f), StringComparer.Ordinal);
    }

    private static SheetRenderKey Key(
        string tex = "tex", double width = 800, double height = 220,
        double dpi = 1.0, double zoom = 1.0, bool pageMode = false, int staves = 0) =>
        new(tex, width, height, dpi, zoom, pageMode, staves);

    [Fact]
    public void ShouldRerender_IsTrueWhenNothingHasBeenRenderedYet() => Assert.True(SheetRenderKey.ShouldRerender(null, Key()));

    [Fact]
    public void ShouldRerender_IsFalseForAnIdenticalKey() => Assert.False(SheetRenderKey.ShouldRerender(Key(), Key()));

    /// <summary>
    ///     Every input, one at a time. A key of the AlphaTex ALONE would make the
    ///     strip stop re-rendering on a resize, a zoom or a page/strip flip —
    ///     which is the failure this theory exists to prevent, not a hypothetical
    ///     one: `RebuildAsync` is the single method all six go through.
    /// </summary>
    [Theory]
    [InlineData("alphaTex")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("dpi")]
    [InlineData("zoom")]
    [InlineData("pageMode")]
    [InlineData("staves")]
    public void ShouldRerender_IsTrueWhenAnyRenderInputMoves(string what)
    {
        // Built here rather than passed in: SheetRenderKey is internal to the UI
        // assembly and may not appear in a public test signature.
        var next = what switch
        {
            "alphaTex" => Key(tex: "tex2"),
            "width" => Key(width: 801),
            "height" => Key(height: 221),
            "dpi" => Key(dpi: 1.5),
            "zoom" => Key(zoom: 1.25),
            "pageMode" => Key(pageMode: true),
            "staves" => Key(staves: 1),
            _ => throw new ArgumentOutOfRangeException(nameof(what), what, "unknown render input")
        };

        Assert.True(SheetRenderKey.ShouldRerender(Key(), next), $"a changed {what} must re-render.");
    }
}
