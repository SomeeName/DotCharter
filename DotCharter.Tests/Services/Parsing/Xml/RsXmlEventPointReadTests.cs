using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
///     <c>RsXmlSongReader</c>'s section/event merge. It used to find the
///     <c>EventPoint</c> for a position with a LINEAR scan of a growing list,
///     once per <c>&lt;section&gt;</c> and once per <c>&lt;event&gt;</c> — an
///     EOF-exported chart with thousands of events cost millions of comparisons
///     on every load. It is a dictionary now, and these pin that the merge
///     behaviour is unchanged.
/// </summary>
public sealed class RsXmlEventPointReadTests
{
    [Fact]
    public void ASectionAndAnEventAtTheSameTime_ShareOneEventPoint()
    {
        var arr = ReadFirstArrangement(SongXml(
            sections: [("intro", "1.000"), ("verse", "2.000")],
            events: [("B0", "1.000"), ("e0", "3.000")]));

        Assert.Equal(3, arr.EventPoints.Count);

        var shared = arr.EventPoints.Single(ep => ep.Section == "intro");
        Assert.Equal("B0", shared.Events);

        Assert.Equal("verse", arr.EventPoints.Single(ep => ep.Events == "").Section);
        Assert.Equal("e0", arr.EventPoints.Single(ep => ep.Section == "").Events);
    }

    [Fact]
    public void SeveralEventsAtOnePosition_AccumulateCommaSeparated()
    {
        var arr = ReadFirstArrangement(SongXml(
            sections: [],
            events: [("B0", "1.000"), ("B1", "1.000"), ("dc", "1.000")]));

        var ep = Assert.Single(arr.EventPoints);
        Assert.Equal("B0,B1,dc", ep.Events);
    }

    [Fact]
    public void ManySectionsAndEvents_LoadInBoundedTime()
    {
        const int count = 20_000;
        var sections = new List<(string, string)>(count);
        var events = new List<(string, string)>(count);
        for (var i = 0; i < count; i++)
        {
            sections.Add(($"s{i}", $"{i}.{i % 1000:000}"));
            events.Add(($"e{i}", $"{i + count}.{i % 1000:000}"));
        }

        var xml = SongXml(sections, events);

        var sw = Stopwatch.StartNew();
        var arr = ReadFirstArrangement(xml);
        sw.Stop();

        Assert.True(arr.EventPoints.Count > count,
            $"expected more than {count} event points, got {arr.EventPoints.Count}");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"reading took {sw.Elapsed.TotalSeconds:0.0}s — the event-point lookup is scanning again");
    }

    // ---- helpers ------------------------------------------------------------

    private static Arrangement ReadFirstArrangement(string xml)
    {
        var project = new RsXmlSongReader().ReadDocument(XDocument.Parse(xml), "test_lead.xml");
        return Assert.Single(project.Arrangements);
    }

    private static string SongXml(
        IReadOnlyList<(string Name, string Time)> sections,
        IReadOnlyList<(string Code, string Time)> events)
    {
        var sb = new StringBuilder();
        sb.Append("""
                  <?xml version="1.0" encoding="utf-8"?>
                  <song version="8">
                    <title>Bounded Read</title>
                    <arrangement>Lead</arrangement>
                    <offset>0.000</offset>
                    <startBeat>0.000</startBeat>
                    <songLength>60.000</songLength>
                    <tuning string0="0" string1="0" string2="0" string3="0" string4="0" string5="0" strings="6" />
                    <ebeats count="4">
                      <ebeat time="0.000" measure="1" />
                      <ebeat time="0.500" measure="-1" />
                      <ebeat time="1.000" measure="-1" />
                      <ebeat time="1.500" measure="-1" />
                    </ebeats>
                  """);

        sb.Append($"  <sections count=\"{sections.Count}\">\n");
        foreach (var (name, time) in sections)
            sb.Append($"    <section name=\"{name}\" number=\"1\" startTime=\"{time}\" />\n");
        sb.Append("  </sections>\n");

        sb.Append($"  <events count=\"{events.Count}\">\n");
        foreach (var (code, time) in events)
            sb.Append($"    <event time=\"{time}\" code=\"{code}\" />\n");
        sb.Append("  </events>\n");

        sb.Append("  <levels count=\"0\" />\n</song>\n");
        return sb.ToString();
    }
}
