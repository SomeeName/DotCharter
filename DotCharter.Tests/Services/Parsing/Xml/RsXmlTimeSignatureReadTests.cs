using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
///     RS-XML records the meter in the EVENT stream (<c>TS:n/d</c>), not on the
///     ebeats — and nothing read it back, so every <c>.xml</c> import was silently
///     4/4 whatever the file said.
///     <para>
///         It is not a display detail: <c>RsXmlSongWriter</c> re-emits the meter it
///         finds on the beat markers, <c>SngFromDomainMapper</c> ships it into the
///         psarc, <c>GpExporter</c> draws bar lines from it and
///         <c>ChartSpacingEnforcer.GetBeatsInMeasure</c> computes the min-note
///         distance off it. The SAME song imported from a psarc arrived with the
///         real meter (the package importer derives it from the
///         measure mask), so the two import paths disagreed about the song.
///     </para>
///     <para>
///         The corpus says the events are really there: 468 <c>TS:</c> events over
///         <c>TestData/EOF</c> in eleven distinct meters, from 2/4 to 11/8.
///     </para>
/// </summary>
public sealed class RsXmlTimeSignatureReadTests
{
    [Fact]
    public void AThreeFourChart_ImportsAsThreeFour()
    {
        var project = Read(SongXml(("TS:3/4", "0.000")));

        Assert.NotEmpty(project.Beats);
        Assert.All(project.Beats, b => Assert.Equal(3, b.BeatsInMeasure));
        Assert.All(project.Beats, b => Assert.Equal(4, b.NoteDenominator));
    }

    /// <summary>Round trip: what comes back out has to say 3/4 too, because the
    /// writer derives the event stream from these two fields.</summary>
    [Fact]
    public void AThreeFourChart_IsStillThreeFourAfterAWrite()
    {
        var project = Read(SongXml(("TS:3/4", "0.000")));

        var written = new RsXmlSongWriter().Write(project);
        var codes = written.Root!.Element("events")!.Elements("event")
            .Select(e => e.Attribute("code")!.Value)
            .Where(c => c.StartsWith("TS:", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(["TS:3/4"], codes);
    }

    /// <summary>A change applies from its own beat onward — which is exactly the
    /// shape the writer emits (one event per beat whose meter differs from the
    /// beat before it).</summary>
    [Fact]
    public void AMeterChange_AppliesFromItsOwnBeatOnward()
    {
        // Beats sit at 0.000 / 0.500 / 1.000 / 1.500.
        var project = Read(SongXml(("TS:4/4", "0.000"), ("TS:7/8", "1.000")));

        Assert.Equal([4, 4, 7, 7], project.Beats.Select(b => b.BeatsInMeasure));
        Assert.Equal([4, 4, 8, 8], project.Beats.Select(b => b.NoteDenominator));
    }

    /// <summary>No event stream at all — an older or hand-written file — keeps the
    /// 4/4 default rather than becoming 1/1 or throwing.</summary>
    [Fact]
    public void AChartWithNoTimeSignatureEvents_StaysFourFour()
    {
        var project = Read(SongXml(("B0", "0.000")));

        Assert.All(project.Beats, b => Assert.Equal(4, b.BeatsInMeasure));
        Assert.All(project.Beats, b => Assert.Equal(4, b.NoteDenominator));
    }

    /// <summary>
    ///     A downloaded CDLC is an untrusted file, and both halves size a bar and
    ///     drive emit loops downstream (<c>GpBeatMapBuilder</c> allocates a marker
    ///     per beat in a measure). Bounded, never thrown away — same treatment
    ///     <c>ReadTuning</c> gives its string count.
    /// </summary>
    [Theory]
    [InlineData("TS:2000000000/4", 32, 4)]
    [InlineData("TS:4/2000000000", 4, 64)]
    [InlineData("TS:0/0", 1, 1)]
    [InlineData("TS:-3/-4", 1, 1)]
    public void AnAbsurdTimeSignature_IsClampedInsteadOfTrusted(string code, int beats, int denominator)
    {
        var project = Read(SongXml((code, "0.000")));

        Assert.All(project.Beats, b => Assert.Equal(beats, b.BeatsInMeasure));
        Assert.All(project.Beats, b => Assert.Equal(denominator, b.NoteDenominator));
    }

    [Theory]
    [InlineData("TS:")]
    [InlineData("TS:4")]
    [InlineData("TS:x/y")]
    [InlineData("TSX:3/4")]
    public void AMalformedTimeSignatureCode_IsIgnored(string code)
    {
        var project = Read(SongXml((code, "0.000")));

        Assert.All(project.Beats, b => Assert.Equal(4, b.BeatsInMeasure));
    }

    // ---- helpers ------------------------------------------------------------

    private static ChartProject Read(string xml) =>
        new RsXmlSongReader().ReadDocument(XDocument.Parse(xml), "meter_lead.xml");

    private static string SongXml(params (string Code, string Time)[] events)
    {
        var sb = new StringBuilder();
        sb.Append("""
                  <?xml version="1.0" encoding="utf-8"?>
                  <song version="8">
                    <title>Meter</title>
                    <arrangement>Lead</arrangement>
                    <offset>0.000</offset>
                    <startBeat>0.000</startBeat>
                    <songLength>60.000</songLength>
                    <tuning string0="0" string1="0" string2="0" string3="0" string4="0" string5="0" strings="6" />
                    <ebeats count="4">
                      <ebeat time="0.000" measure="1" />
                      <ebeat time="0.500" measure="-1" />
                      <ebeat time="1.000" measure="2" />
                      <ebeat time="1.500" measure="-1" />
                    </ebeats>

                  """);

        sb.Append($"  <events count=\"{events.Length}\">\n");
        foreach (var (code, time) in events)
            sb.Append($"    <event time=\"{time}\" code=\"{code}\" />\n");
        sb.Append("  </events>\n");

        sb.Append("  <levels count=\"0\" />\n</song>\n");
        return sb.ToString();
    }
}
