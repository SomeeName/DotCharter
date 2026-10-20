using DotCharter.Services.Parsing.Eof;

namespace DotCharter.Tests.Services.Parsing.Eof;

/// <summary>
///     An <c>.eof</c> beat carrying <c>EofConstants.BeatFlagAnchor</c> is one
///     whose timing the CHARTER pinned by hand, and the app treats an anchored
///     beat as immovable — <c>TempoService</c> skips it when re-timing a run,
///     <c>RscpWriter</c> persists the flag and <c>EofExporter</c> re-emits it.
///     <para>
///         <c>EofParseSession</c> reads the flag and <c>EofImporter</c> carries it
///         onto the <see cref="BeatMarker"/>s the NOTES are positioned against —
///         but the import DIALOG hand-rolled that projection twice, and both
///         copies left <c>IsAnchor</c> off. So the chart resolved correctly and
///         then SAVED a project that had forgotten every anchor: the first tempo
///         edit dragged beats the charter had deliberately fixed, and a round trip
///         back to <c>.eof</c> lost the flags outright.
///     </para>
/// </summary>
public sealed class EofBeatAnchorImportTests
{
    [Fact]
    public void BuildBeatMarkers_CarriesTheAnchorFlag()
    {
        var meta = new EofImportMetadata();
        meta.Beats.Add(Beat(0, isAnchor: true));
        meta.Beats.Add(Beat(500, isAnchor: false));
        meta.Beats.Add(Beat(1000, isAnchor: true));

        var markers = EofImporter.BuildBeatMarkers(meta);

        Assert.Equal([true, false, true], markers.Select(m => m.IsAnchor));
    }

    /// <summary>The rest of the projection still travels — this must not become
    /// "anchors only".</summary>
    [Fact]
    public void BuildBeatMarkers_CarriesTheRestOfTheBeat()
    {
        var meta = new EofImportMetadata();
        meta.Beats.Add(new EofBeatInfo
        {
            TimeMs = 250,
            Bpm = 140,
            BeatsInMeasure = 3,
            NoteDenominator = 8,
            IsMeasureStart = true,
            IsAnchor = true
        });

        var marker = Assert.Single(EofImporter.BuildBeatMarkers(meta));

        Assert.Equal(250, marker.Time);
        Assert.Equal(140, marker.Bpm);
        Assert.Equal(3, marker.BeatsInMeasure);
        Assert.Equal(8, marker.NoteDenominator);
        Assert.True(marker.IsMeasure);
        Assert.True(marker.IsAnchor);
    }

    /// <summary>
    ///     Source lint for the dialog. The two behavioural tests above pin what
    ///     the shared mapping returns; they cannot pin that the import dialog
    ///     still USES it, and hand-rolling the projection is exactly how the flag
    ///     was lost twice in one file. Same shape as
    ///     <c>ChildProcessEncodingTests</c>: a rule about which expression may
    ///     appear, which no test of the callee can express.
    /// </summary>
    [Fact]
    public void TheImportDialogDoesNotHandRollABeatMarker()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Import", "Eof", "EofProjectCreator.cs");
        Assert.True(File.Exists(path), "EofProjectCreator moved — repoint this lint.");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("IsMeasure = b.IsMeasureStart", source, StringComparison.Ordinal);
        Assert.Contains("EofImporter.BuildBeatMarkers(meta)", source, StringComparison.Ordinal);
    }

    private static EofBeatInfo Beat(double timeMs, bool isAnchor) => new()
    {
        TimeMs = timeMs,
        Bpm = 120,
        BeatsInMeasure = 4,
        NoteDenominator = 4,
        IsMeasureStart = false,
        IsAnchor = isAnchor
    };
}
