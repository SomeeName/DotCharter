using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     Saving and reloading a non-standard tuning used to return the arrangement
///     to E STANDARD — every note at a different sounding pitch, silently, on the
///     first save.
///     <para>
///         The chain: <c>Arrangement.TuningType</c> defaults to
///         <c>"E_STANDARD"</c>; five of the six importers fill
///         <c>TuningPitches</c> and never touch <c>TuningType</c>; the writer
///         emitted <c>tuningValues</c> only when the type was <c>"CUSTOM"</c>; and
///         the parser re-derives offsets from the type NAME when it is not. So the
///         actual pitches were never written at all.
///     </para>
///     <para>
///         These assert the PITCHES, not the type string — the type is a shorthand
///         and which spelling the writer picks is an implementation choice, while
///         the offsets are the thing a wrong value transposes the whole chart by.
///     </para>
/// </summary>
public sealed class RscpTuningRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_tuning_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.rscp");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private string ReadProjectXml() => File.ReadAllText(ProjectPath);

    private ChartProject SaveAndReload(ChartProject project)
    {
        new RscpChartSerializer().Save(ProjectPath, project);
        return new RscpChartSerializer().Load(ProjectPath);
    }

    private static ChartProject WithTuning(int[] offsets, string declaredType, int strings = 6)
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = strings, TuningType = declaredType };
        arrangement.Levels.Add(new ChartLevel());
        foreach (var offset in offsets) arrangement.TuningPitches.Add(new TuningOffset(offset));

        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        return project;
    }

    /// <summary>
    ///     The exact shape every Guitar Pro / Songsterr / EOF / RS-XML import
    ///     produces: real offsets, untouched default type name.
    /// </summary>
    [Fact]
    public void DropDSurvivesEvenWhenTheTypeNameStillSaysEStandard()
    {
        var project = WithTuning([-2, 0, 0, 0, 0, 0], "E_STANDARD");

        var reloaded = SaveAndReload(project);

        Assert.Equal([-2, 0, 0, 0, 0, 0],
            reloaded.Arrangements[0].TuningPitches.Select(t => t.Step).ToArray());
    }

    [Theory]
    // Eb standard — every string down a semitone.
    [InlineData(new[] { -1, -1, -1, -1, -1, -1 })]
    // DADGAD.
    [InlineData(new[] { -2, 0, 0, 0, -2, -2 })]
    // Drop C.
    [InlineData(new[] { -4, -2, -2, -2, -2, -2 })]
    public void EveryNonStandardTuningSurvivesTheRoundTrip(int[] offsets)
    {
        var reloaded = SaveAndReload(WithTuning(offsets, "E_STANDARD"));

        Assert.Equal(offsets, reloaded.Arrangements[0].TuningPitches.Select(t => t.Step).ToArray());
    }

    /// <summary>A bass or extended-range name is written as CUSTOM + offsets (rscharter cannot read
    ///     it) and comes back from the offsets on load.</summary>
    [Theory]
    [InlineData("7_STRING_DROP_A", new[] { -2, 0, 0, 0, 0, 0, 0 })]
    [InlineData("5_STRING_BASS_STANDARD", new[] { -5, -5, -5, -5, -4 })]
    [InlineData("6_STRING_BASS_DROP_A", new[] { -7, -5, -5, -5, -4, -4 })]
    [InlineData("8_STRING_DROP_E", new[] { -2, 0, 0, 0, 0, 0, 0, 0 })]
    public void AnExtendedRangeName_IsCustomInTheFile_AndComesBackOnLoad(string typeName, int[] offsets)
    {
        var project = WithTuning(offsets, typeName, offsets.Length);
        new RscpChartSerializer().Save(ProjectPath, project);

        Assert.Contains("type=\"CUSTOM\"", ReadProjectXml());
        var reloaded = new RscpChartSerializer().Load(ProjectPath).Arrangements[0];
        Assert.Equal(typeName, reloaded.TuningType);
        Assert.Equal(offsets, reloaded.TuningPitches.Select(t => t.Step).ToArray());
    }

    [Fact]
    public void SevenStringTuningsSurviveToo()
    {
        var reloaded = SaveAndReload(WithTuning([-2, -2, 0, 0, 0, 0, 0], "E_STANDARD", strings: 7));

        Assert.Equal([-2, -2, 0, 0, 0, 0, 0],
            reloaded.Arrangements[0].TuningPitches.Select(t => t.Step).ToArray());
    }

    /// <summary>
    ///     A genuinely standard tuning must still be written by NAME. Otherwise
    ///     every existing chart's tuning node changes shape on the next save, and
    ///     the human-readable type — which other tools and the chart's own UI read
    ///     — is replaced by an opaque "CUSTOM".
    /// </summary>
    [Fact]
    public void AnHonestTypeNameIsPreservedRatherThanRewrittenAsCustom()
    {
        var reloaded = SaveAndReload(WithTuning([0, 0, 0, 0, 0, 0], "E_STANDARD"));

        Assert.Equal("E_STANDARD", reloaded.Arrangements[0].TuningType);
        Assert.Equal([0, 0, 0, 0, 0, 0],
            reloaded.Arrangements[0].TuningPitches.Select(t => t.Step).ToArray());
    }

    /// <summary>
    ///     <b>A chart states its tuning TWICE</b> — note LETTERS and per-string
    ///     OFFSETS — and an arrangement may carry only the letters (an RS XML with
    ///     no <c>&lt;tuning&gt;</c> element, anything that builds an
    ///     <see cref="Arrangement" /> and sets <c>Tuning</c>). The writer read
    ///     <c>TuningPitches</c> directly, so such an arrangement wrote
    ///     <c>type="CUSTOM" tuningValues=""</c> — an empty vector the parser then
    ///     filled with zeros. <see cref="TuningOffsetResolution" /> exists for
    ///     exactly this and four exporters already go through it; the PROJECT
    ///     format did not, which makes it the worst place for the gap.
    /// </summary>
    [Fact]
    public void ATuningStatedOnlyAsLetters_IsResolvedRatherThanZeroed()
    {
        var arrangement = new Arrangement
        { Name = "Lead", StringCount = 6, Tuning = ["D", "A", "D", "G", "B", "e"] };
        arrangement.Levels.Add(new ChartLevel());
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);

        var reloaded = SaveAndReload(project).Arrangements[0];

        Assert.Equal([-2, 0, 0, 0, 0, 0], reloaded.TuningPitches.Select(t => t.Step).ToArray());
        Assert.Equal(["D", "A", "D", "G", "B", "E"], reloaded.Tuning);
    }

    /// <summary>
    ///     …and the other half: letters that ARE standard must still write the
    ///     honest type name, not an opaque CUSTOM with an empty vector.
    /// </summary>
    [Fact]
    public void StandardLettersWithNoOffsets_StillWriteTheHonestTypeName()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        arrangement.Levels.Add(new ChartLevel());
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);

        var reloaded = SaveAndReload(project).Arrangements[0];

        Assert.Equal("E_STANDARD", reloaded.TuningType);
        Assert.Equal([0, 0, 0, 0, 0, 0], reloaded.TuningPitches.Select(t => t.Step).ToArray());
    }

    /// <summary>An already-CUSTOM arrangement keeps working exactly as before.</summary>
    [Fact]
    public void AnExplicitlyCustomTuningStillRoundTrips()
    {
        var reloaded = SaveAndReload(WithTuning([-2, -2, -2, -2, -2, -2], "CUSTOM"));

        Assert.Equal([-2, -2, -2, -2, -2, -2],
            reloaded.Arrangements[0].TuningPitches.Select(t => t.Step).ToArray());
    }
}
