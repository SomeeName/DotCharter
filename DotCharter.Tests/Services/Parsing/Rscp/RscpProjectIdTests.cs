using System.Text;
using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     <c>&lt;dcProjectId&gt;</c> (#562): the stable project identity the AI chat
///     history keys conversations by. Minted at creation, saved on every write,
///     derived — and stable until the next save — for a file that predates it, and
///     never taken from the file when it is not id-shaped (it names a directory).
/// </summary>
public sealed class RscpProjectIdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public RscpProjectIdTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void ANewProject_HasAWellFormedIdAtCreation_AndTwoNewProjectsDiffer()
    {
        var a = new ChartProject();
        var b = new ChartProject();

        Assert.True(ProjectIdentity.IsWellFormed(a.ProjectId), a.ProjectId);
        Assert.True(ProjectIdentity.IsWellFormed(b.ProjectId), b.ProjectId);
        Assert.NotEqual(a.ProjectId, b.ProjectId);
    }

    [Fact]
    public void TheId_SurvivesSaveAndReopen_AndIsWrittenAsOneRootElement()
    {
        var project = Sample();
        var path = Save(project, "a.rscp");

        var root = XDocument.Load(path).Root!;
        Assert.Equal(project.ProjectId, Assert.Single(root.Elements("dcProjectId")).Value);
        Assert.Equal(project.ProjectId, new RscpChartSerializer().Load(path).ProjectId);
    }

    [Fact]
    public void AClone_IsTheSameProject()
    {
        var project = Sample();
        Assert.Equal(project.ProjectId, ChartProjectCloner.Clone(project).ProjectId);
    }

    [Fact]
    public void ALegacyFile_GetsADerivedId_ThatIsStableUntilTheNextSaveWritesIt()
    {
        var legacy = WriteLegacy(Sample(), "legacy.rscp");
        var serializer = new RscpChartSerializer();

        var first = serializer.Load(legacy);
        var second = serializer.Load(legacy);

        Assert.True(ProjectIdentity.IsWellFormed(first.ProjectId), first.ProjectId);
        Assert.Equal(first.ProjectId, second.ProjectId);

        // The save writes the derived id, so the identity a chat was keyed by
        // before the first save is the identity after it.
        serializer.Save(legacy, first);
        Assert.Equal(first.ProjectId, serializer.Load(legacy).ProjectId);
    }

    [Fact]
    public void ALegacyFile_OpensUnchanged_TheIdIsTheOnlyDifference()
    {
        var project = Sample();
        var current = Save(project, "current.rscp");
        var legacy = WriteLegacy(project, "legacy.rscp");
        var serializer = new RscpChartSerializer();

        var fromCurrent = serializer.Load(current);
        var fromLegacy = serializer.Load(legacy);
        Assert.NotEqual(fromCurrent.ProjectId, fromLegacy.ProjectId);

        // The same project read from both files, written back out: identical
        // except for the one id line, so nothing ELSE about a legacy file changes.
        var a = Path.Combine(_dir, "a-resaved.rscp");
        var b = Path.Combine(_dir, "b-resaved.rscp");
        serializer.Save(a, fromCurrent);
        serializer.Save(b, fromLegacy);
        var textA = File.ReadAllText(a, Encoding.UTF8);
        var textB = File.ReadAllText(b, Encoding.UTF8);
        Assert.Contains($"<dcProjectId>{fromLegacy.ProjectId}</dcProjectId>", textB, StringComparison.Ordinal);
        Assert.Equal(StripId(textA), StripId(textB));
    }

    [Fact]
    public void TwoDifferentLegacyFiles_GetDifferentIds()
    {
        var one = Sample();
        var other = Sample();
        other.Chart.SongName = "Another Song";

        var serializer = new RscpChartSerializer();
        Assert.NotEqual(serializer.Load(WriteLegacy(one, "one.rscp")).ProjectId,
            serializer.Load(WriteLegacy(other, "other.rscp")).ProjectId);
    }

    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef")]
    [InlineData("")]
    public void AMalformedId_IsNotTaken_AndReadsAsALegacyFile(string malformed)
    {
        var project = Sample();
        var path = Save(project, "a.rscp");
        var doc = XDocument.Load(path);
        doc.Root!.Element("dcProjectId")!.Value = malformed;
        doc.Save(path);

        var loaded = new RscpChartSerializer().Load(path);

        Assert.True(ProjectIdentity.IsWellFormed(loaded.ProjectId), loaded.ProjectId);
        Assert.NotEqual(malformed, loaded.ProjectId);
        Assert.Equal(RscpChartSerializer.LegacyProjectId(path), loaded.ProjectId);
    }

    private static ChartProject Sample()
    {
        var project = new ChartProject { AudioFileName = "song.wav", Bpm = 120 };
        project.Chart.Artist = "Artist";
        project.Chart.SongName = "Song";
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 });

        var arrangement = new Arrangement { Name = "Lead Guitar", ArrangementType = ArrangementType.Lead };
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), Fret = 5, StringIndex = 2 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        return project;
    }

    private string Save(ChartProject project, string name)
    {
        var path = Path.Combine(_dir, name);
        new RscpChartSerializer().Save(path, project);
        return path;
    }

    /// <summary>A file exactly as a pre-#562 build wrote it: the same writer, no id.</summary>
    private string WriteLegacy(ChartProject project, string name)
    {
        var path = Save(project, name);
        File.WriteAllText(path, StripId(File.ReadAllText(path, Encoding.UTF8)), new UTF8Encoding(false));
        Assert.DoesNotContain("dcProjectId", File.ReadAllText(path, Encoding.UTF8), StringComparison.Ordinal);
        return path;
    }

    private static string StripId(string rscp)
    {
        var start = rscp.IndexOf("  <dcProjectId>", StringComparison.Ordinal);
        if (start < 0) return rscp;
        var end = rscp.IndexOf('\n', start);
        return rscp.Remove(start, end - start + 1);
    }
}
