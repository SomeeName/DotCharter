using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export.Rsxr;

public sealed class RsxrXmlCompatibilityTests
{
    [Theory]
    [InlineData(false, 7, false)]
    [InlineData(false, 8, false)]
    [InlineData(true, 5, false)]
    [InlineData(true, 6, false)]
    [InlineData(false, 7, true)]
    [InlineData(false, 8, true)]
    [InlineData(true, 5, true)]
    [InlineData(true, 6, true)]
    public void Frozen_XML_preserves_every_string_tuning_and_sound(bool bass, int strings, bool marked)
    {
        var built = RsxrTestSupport.Chart(bass, strings);
        var fixture = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tests/Services/Export/Rsxr/Fixtures",
            $"{(bass ? "bass" : "guitar")}{strings}-{(marked ? "marked" : "slot")}.xml");
        var xml = XDocument.Load(fixture);
        Assert.Equal(marked ? "1" : null, (string?)xml.Root!.Element("tuning")!.Attribute("extendedRange"));
        var read = new RsXmlSongReader().ReadDocument(xml, Path.GetFileName(fixture));
        var expected = built.Project.Arrangements[0];
        var actual = Assert.Single(read.Arrangements);
        Assert.Equal(expected.StringCount, actual.StringCount);
        Assert.Equal(expected.TuningPitches.Select(t => t.Step), actual.TuningPitches.Select(t => t.Step));
        Assert.Equal(Sounds(expected, built.Project.Beats), Sounds(actual, read.Beats));
    }

    private static IEnumerable<string> Sounds(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var level = arrangement.Levels.OrderBy(l => l.DifficultyIndex).Last();
        return level.Notes.Select(n => $"{TimelineMath.ToWholeMs(n.Position.GetPositionMs(beats))}:{n.StringIndex}:{n.Fret}")
            .Concat(level.Chords.SelectMany(c => c.ChordNotes.Select(n =>
                $"{TimelineMath.ToWholeMs(c.Position.GetPositionMs(beats))}:{n.StringIndex}:{n.Fret}")))
            .Order(StringComparer.Ordinal);
    }
}
