using System.Xml.Linq;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
///     Rocksmith's <c>ChordMask.Nop</c> is <see cref="ChordTemplate.NopInRs" />, a
///     template FIELD (#489). Every interchange format spells it as a <c>-nop</c>
///     suffix on the display name: each reader folds the suffix into the field and
///     takes it out of the name, each writer spells it back from the field.
/// </summary>
public sealed partial class ChordNopFlagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-nop-" + Guid.NewGuid().ToString("N"));

    public ChordNopFlagTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static Arrangement Guitar() => new()
    {
        Name = "Lead",
        StringCount = 6,
        Tuning = ["E", "A", "D", "G", "B", "E"],
        Levels = { new ChartLevel() }
    };

    private static ChordTemplate Shape(string name, bool nop = false) => new()
    {
        Id = 0,
        Name = name,
        NopInRs = nop,
        // Three strings, so the double-stop name suppression leaves the name alone.
        Frets = new Dictionary<int, int> { [3] = 4, [4] = 3, [5] = 2 }
    };

    private static ChartProject ProjectWith(ChordTemplate template)
    {
        var arrangement = Guitar();
        arrangement.ChordTemplates.Add(template);
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        project.Beats.Add(new BeatMarker { Time = 0, Bpm = 120 });
        project.Beats.Add(new BeatMarker { Time = 500, Bpm = 120 });
        return project;
    }

    private static XElement WrittenTemplate(ChartProject project) =>
        new RsXmlSongWriter().Write(project).Descendants("chordTemplate").Single();

    private static RsChordTemplate SngTemplate(ChartProject project) =>
        SngFromDomainMapper.BuildInstrumental(project.Arrangements[0], project.Beats, 1000).Sng.Chords.Single();
}
