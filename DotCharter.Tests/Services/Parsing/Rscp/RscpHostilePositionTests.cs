using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     A `.rscp` position attribute is untrusted input: it arrives off disk AND
///     over the unauthenticated LAN session transfer
///     (<c>Multiplayer/LanSessionClient</c> writes the received project to disk
///     and loads it). Every reader of one goes through
///     <see cref="FractionalPosition.TryParse" /> —
///     <c>RscpParsingUtilities.ParseFraction</c>, <c>RscpArrangementParser</c>,
///     <c>RscpVocalsParser</c> — whose whole job is to answer <c>false</c> for a
///     value it cannot read.
///     <para>
///         It used to THROW instead: <c>1/-2147483648</c> and
///         <c>-2147483648/-1</c> parse as two ints but reduce outside int range,
///         and <see cref="Fraction" />'s constructor raised
///         <see cref="ArgumentOutOfRangeException" /> out of the <c>bool</c>
///         -returning method, aborting the whole load. One attribute, no project.
///     </para>
/// </summary>
public sealed class RscpHostilePositionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_hostile_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Theory]
    [InlineData("1/-2147483648")]
    [InlineData("-2147483648/-1")]
    [InlineData("0 1/-2147483648")]
    [InlineData("-2147483648 -2147483648/-1")]
    public void AHostilePositionAttribute_LoadsAsZeroInsteadOfAbortingTheProject(string hostile)
    {
        var ser = new RscpChartSerializer();
        var path = Path.Combine(_dir, "hostile.rscp");
        ser.Save(path, RichProjectFactory.Build());

        // Every element that carries a position attribute gets the hostile
        // value — notes, chords, beats, phrases, sections, anchors, lyrics.
        var doc = XDocument.Load(path);
        var rewritten = 0;
        foreach (var element in doc.Descendants())
        {
            foreach (var name in new[] { "p", "ep" })
            {
                var attribute = element.Attribute(name);
                if (attribute is null) continue;
                attribute.Value = hostile;
                rewritten++;
            }
        }

        Assert.True(rewritten > 0, "The fixture has no position attributes — the test would be vacuous.");
        doc.Save(path);

        var project = ser.Load(path);

        // Loaded, not thrown. Every position read the documented fallback.
        Assert.NotNull(project);
        Assert.All(
            project.Arrangements.SelectMany(a => a.Levels).SelectMany(l => l.Notes),
            note => Assert.Equal(FractionalPosition.Zero, note.Position));
    }
}
