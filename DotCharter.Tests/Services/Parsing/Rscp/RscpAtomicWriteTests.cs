using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>Project serialization replaces a destination without writing through its existing handle.</summary>
public sealed class RscpAtomicWriteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dc-rscp-atomic-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void Saving_ReplacesTheDirectoryEntryInsteadOfWritingThroughTheOldFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows share-delete rename semantics.");
        var path = Path.Combine(_dir, "song.dcproj.saving");
        File.WriteAllText(path, "previous contents");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(held);
        var project = new ChartProject();

        new RscpChartSerializer().Save(path, project);

        Assert.Equal("previous contents", reader.ReadToEnd());
        Assert.Equal(project.ProjectId, new RscpChartSerializer().Load(path).ProjectId);
        Assert.Equal([path], Directory.GetFiles(_dir));
    }
}
