using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     A `.rscp` capo and string index are untrusted input just like its positions
///     (see <see cref="RscpHostilePositionTests" />). The capo feeds arithmetic and
///     a string index lands in arrays sized by the arrangement's string count, so
///     an out-of-range value is clamped on parse: the project still loads, and
///     nothing downstream sees a capo of two billion or string 500 of 6.
/// </summary>
public sealed class RscpHostileIndexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_index_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private ChartProject RoundTripWithRewrite(Action<XDocument> rewrite)
    {
        var ser = new RscpChartSerializer();
        var path = Path.Combine(_dir, "hostile.rscp");
        ser.Save(path, RichProjectFactory.Build());
        var doc = XDocument.Load(path);
        rewrite(doc);
        doc.Save(path);
        return ser.Load(path);
    }

    [Theory]
    [InlineData("2147483647", 24)]
    [InlineData("99", 24)]
    [InlineData("-7", 0)]
    [InlineData("-2147483648", 0)]
    [InlineData("5", 5)]
    public void ACapoOutsideTheFretboard_IsClamped(string capo, int expected)
    {
        var rewritten = 0;
        var project = RoundTripWithRewrite(doc =>
        {
            foreach (var arrangement in doc.Descendants("arrangement"))
            {
                arrangement.SetAttributeValue("capo", capo);
                rewritten++;
            }
        });

        Assert.True(rewritten > 0, "The fixture has no arrangement - the test would be vacuous.");
        Assert.All(project.Arrangements, a => Assert.Equal(expected, a.Capo));
    }

    [Theory]
    [InlineData("500")]
    [InlineData("2147483647")]
    [InlineData("-1")]
    [InlineData("-2147483648")]
    public void AStringIndexOutsideTheArrangement_IsClampedIntoIt(string hostile)
    {
        var rewritten = 0;
        var project = RoundTripWithRewrite(doc =>
        {
            foreach (var element in doc.Descendants().Where(e => e.Name.LocalName is "soundNote" or "chordNote"))
            {
                element.SetAttributeValue("string", hostile);
                rewritten++;
            }
        });

        Assert.True(rewritten > 0, "The fixture has no notes - the test would be vacuous.");
        foreach (var arrangement in project.Arrangements)
        {
            var indexes = arrangement.Levels
                .SelectMany(l => l.Notes.Select(n => n.StringIndex)
                    .Concat(l.Chords.SelectMany(c => c.ChordNotes.Select(n => n.StringIndex))))
                .ToList();
            Assert.All(indexes, i => Assert.InRange(i, 0, arrangement.StringCount - 1));
        }
    }

    [Fact]
    public void AChordTemplateStringOutsideTheArrangement_IsDropped()
    {
        var rewritten = 0;
        var project = RoundTripWithRewrite(doc =>
        {
            foreach (var template in doc.Descendants("chordTemplate"))
            {
                template.SetAttributeValue("frets", "0=3,1=2,500=7,-1=4,2147483647=1");
                template.SetAttributeValue("fingers", "0=1,500=2,-1=3");
                rewritten++;
            }
        });

        Assert.True(rewritten > 0, "The fixture has no chord template - the test would be vacuous.");
        foreach (var arrangement in project.Arrangements)
        {
            foreach (var template in arrangement.ChordTemplates)
            {
                Assert.All(template.Frets.Keys, s => Assert.InRange(s, 0, arrangement.StringCount - 1));
                Assert.All(template.Fingers.Keys, s => Assert.InRange(s, 0, arrangement.StringCount - 1));
                Assert.Equal(3, template.Frets[0]);
            }
        }
    }
}
