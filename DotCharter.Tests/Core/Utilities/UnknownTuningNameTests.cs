using System.Xml.Linq;
using DotCharter.Tone.Audio.Tuner;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     <b>An unrecognised tuning NAME may not render as a confident E standard.</b>
///     <para>
///         <c>TuningUtilities.GetOffsetsForTuningType</c> ended in
///         <c>_ =&gt; new[] { 0,0,0,0,0,0,0,0,0 }</c>, so every name the table did
///         not know — and the table uses Rocksmith's low-note spelling, where Drop
///         C# is <c>E_FLAT_DROP_D_FLAT</c> and Drop B is <c>D_FLAT_DROP_B</c>,
///         which almost nothing else calls them — became standard tuning, silently
///         and identically to a real "E_STANDARD". That defeated the string
///         gutter's careful <c>?</c> one layer down, certified an undecodable name
///         as honest in the <c>.rscp</c> writer, and let the phrase catalogue keep
///         it.
///     </para>
///     <para>
///         <c>TryGetOffsetsForTuningType</c> is the honest primitive.
///         <c>GetOffsetsForTuningType</c> survives as the DELIBERATE default for
///         the two callers that need some vector to do arithmetic against.
///     </para>
/// </summary>
public sealed class UnknownTuningNameTests
{
    /// <summary>Names the table really does know, including the two whose
    /// Rocksmith spelling is the trap: Drop C# and Drop B.</summary>
    [Theory]
    [InlineData("E_STANDARD", new[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData("e_standard", new[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData("", new[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData("E_DROP_D", new[] { -2, 0, 0, 0, 0, 0 })]
    // Drop C# — the tuning of the CDLC that opened this report.
    [InlineData("E_FLAT_DROP_D_FLAT", new[] { -3, -1, -1, -1, -1, -1 })]
    [InlineData("D_FLAT_DROP_B", new[] { -5, -3, -3, -3, -3, -3 })]
    [InlineData("C_SHARP_STANDARD", new[] { -3, -3, -3, -3, -3, -3 })] // legacy alias
    public void AKnownName_Resolves(string name, int[] expected)
    {
        Assert.True(TuningUtilities.TryGetOffsetsForTuningType(name, 6, out var offsets), name);
        Assert.Equal(expected, offsets);
        Assert.Equal(expected, TuningUtilities.GetOffsetsForTuningType(name, 6));
    }

    /// <summary>
    ///     Names that are NOT in the table — including "CUSTOM", which is what the
    ///     PSARC importer stamps on every Rocksmith chart, and the common-usage
    ///     spellings the table deliberately does not carry.
    /// </summary>
    [Theory]
    [InlineData("CUSTOM")]
    [InlineData("DROP_D")]
    [InlineData("DROP_C_SHARP")]
    [InlineData("Drop C#")]
    [InlineData("NOT_A_TUNING")]
    public void AnUnknownName_IsRefused_NotAnsweredAsStandard(string name)
    {
        Assert.False(TuningUtilities.TryGetOffsetsForTuningType(name, 6, out var offsets), name);
        Assert.Empty(offsets);
    }

    /// <summary>The deliberate default still exists, and still answers.</summary>
    [Fact]
    public void TheDeliberateDefault_StillReturnsStandard()
    {
        Assert.Equal([0, 0, 0, 0, 0, 0], TuningUtilities.GetOffsetsForTuningType("NOT_A_TUNING", 6));
        Assert.Equal([0, 0, 0, 0], TuningUtilities.GetOffsetsForTuningType("NOT_A_TUNING", 4));
    }

    // ── what the change is FOR: the gutter says "?" ──────────────────────────

    /// <summary>
    ///     An arrangement that states no offsets and names a tuning nothing can
    ///     decode gets <c>?</c> in every lane — not six invented letters.
    /// </summary>
    [Fact]
    public void AnUndecodableArrangement_NamesNoString()
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead,
            StringCount = 6,
            TuningType = "CUSTOM"
        };
        Assert.Empty(arrangement.TuningPitches);

        Assert.False(TuningCheckTargets.TryBuild(
            arrangement, TuningCheckCapoMode.TuneOpenThenPlaceCapo, out var targets));
        Assert.Empty(targets);

        Assert.Equal(["?", "?", "?", "?", "?", "?"],
            TimelineGutterControl.StringNamesFor(arrangement, 6));
    }

    /// <summary>
    ///     …while a chart that DOES state its tuning is unaffected, by name or by
    ///     offsets. This is the regression half: the real CDLC's vector must keep
    ///     naming its lanes.
    /// </summary>
    [Fact]
    public void AChartThatSaysItsTuning_StillNamesEveryString()
    {
        var byOffsets = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, StringCount = 6, TuningType = "CUSTOM"
        };
        foreach (var step in new[] { -3, -1, -1, -1, -1, -1 })
            byOffsets.TuningPitches.Add(new TuningOffset(step));
        Assert.Equal(["C#", "G#", "C#", "F#", "A#", "D#"],
            TimelineGutterControl.StringNamesFor(byOffsets, 6));

        var byName = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, StringCount = 6, TuningType = "E_FLAT_DROP_D_FLAT"
        };
        Assert.Equal(["C#", "G#", "C#", "F#", "A#", "D#"],
            TimelineGutterControl.StringNamesFor(byName, 6));
    }

    /// <summary>
    ///     The GUIDED tuner keeps its default — it has to aim somewhere, and
    ///     <c>GuidedTuningCheck</c> throws on an empty target table. Pinned so the
    ///     honest half above cannot be "tidied" into the tuner and crash Get Ready.
    /// </summary>
    [Fact]
    public void TheGuidedTuner_KeepsItsDeliberateDefault()
    {
        var arrangement = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, StringCount = 6, TuningType = "NOT_A_TUNING"
        };
        var targets = TuningCheckTargets.Build(arrangement);
        Assert.Equal(6, targets.Length);
        Assert.Equal(40, targets[0].Midi); // E2
        _ = new GuidedTuningCheck(arrangement);
    }

    // ── the .rscp round trip ────────────────────────────────────────────────

    /// <summary>
    ///     <c>type="CUSTOM"</c> with no <c>tuningValues</c> is the shape an older
    ///     writer could leave on disk, and it means "this file does not say". It
    ///     used to read back as six zeros — a chart that opened as E standard with
    ///     no way to tell it apart from a real one.
    /// </summary>
    [Fact]
    public void CustomWithoutValues_ReadsBackAsUnknown_NotAsStandard()
    {
        var arrangement = ParseTuningOf("""<tuning type="CUSTOM" strings="6" />""");

        Assert.Empty(arrangement.TuningPitches);
        Assert.Equal(["?", "?", "?", "?", "?", "?"], arrangement.Tuning);
        Assert.Equal(["?", "?", "?", "?", "?", "?"],
            TimelineGutterControl.StringNamesFor(arrangement, 6));

        // Nothing downstream may turn those back into offsets.
        Assert.Empty(TuningOffsetResolution.Resolve(arrangement));
    }

    /// <summary>A named tuning the table knows still round-trips by name alone.</summary>
    [Fact]
    public void ANamedTuning_StillResolvesOnRead()
    {
        var arrangement = ParseTuningOf("""<tuning type="E_FLAT_DROP_D_FLAT" strings="6" />""");
        Assert.Equal([-3, -1, -1, -1, -1, -1], arrangement.TuningPitches.Select(t => t.Step));
        Assert.Equal(["C#", "G#", "C#", "F#", "Bb", "Eb"], arrangement.Tuning);
    }

    /// <summary>
    ///     …and an explicit vector always wins, which is the real CDLC's shape
    ///     after the PSARC importer stamps <c>CUSTOM</c> on it.
    /// </summary>
    [Fact]
    public void CustomWithValues_IsUnchanged()
    {
        var arrangement = ParseTuningOf(
            """<tuning type="CUSTOM" strings="6" tuningValues="-3,-1,-1,-1,-1,-1" />""");
        Assert.Equal([-3, -1, -1, -1, -1, -1], arrangement.TuningPitches.Select(t => t.Step));
        Assert.Equal(["C#", "G#", "C#", "F#", "Bb", "Eb"], arrangement.Tuning);
    }

    /// <summary>
    ///     The writer may not certify an undecodable name as a valid shorthand.
    ///     An all-standard arrangement carrying an unknown name used to compare
    ///     equal to the table's zeros and be written as that name, which no reader
    ///     can expand.
    /// </summary>
    [Fact]
    public void TheWriter_NeverCallsAnUndecodableNameHonest()
    {
        var arrangement = new Arrangement
        {
            Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6,
            TuningType = "DROP_D" // common-usage spelling; not in the table
        };
        foreach (var step in new[] { 0, 0, 0, 0, 0, 0 })
            arrangement.TuningPitches.Add(new TuningOffset(step));

        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, project);

        var tuning = doc.Descendants("tuning").Single();
        Assert.Equal("CUSTOM", tuning.Attribute("type")?.Value);
        Assert.Equal("0,0,0,0,0,0", tuning.Attribute("tuningValues")?.Value);
    }

    /// <summary>The riff catalogue reaches the same verdict for the same reason.</summary>
    [Fact]
    public void ThePhraseCatalogue_MarksAnUndecodableNameCustom()
    {
        var arrangement = new Arrangement
        {
            Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6,
            TuningType = "DROP_D"
        };
        foreach (var step in new[] { 0, 0, 0, 0, 0, 0 })
            arrangement.TuningPitches.Add(new TuningOffset(step));

        var fragment = PhraseCatalogFragment.Build(
            "riff", arrangement,
            [new GuitarNote { Position = FractionalPosition.Zero, StringIndex = 0, Fret = 3 }],
            [], [], []);

        Assert.NotNull(fragment);
        Assert.Equal("CUSTOM", fragment.Arrangements.Single().TuningType);
    }

    private static Arrangement ParseTuningOf(string tuningXml)
    {
        var doc = XDocument.Parse($"""
                                   <project><arrangements><arrangement>
                                     <name>Lead</name>
                                     {tuningXml}
                                   </arrangement></arrangements></project>
                                   """);
        var project = new ChartProject();
        new RscpArrangementParser().ParseArrangements(doc, project);
        return project.Arrangements.Single();
    }
}
