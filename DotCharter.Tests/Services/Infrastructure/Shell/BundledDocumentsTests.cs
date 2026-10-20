using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure.Shell;

/// <summary>
///     Where the About window's licence and the Help menu's documents are found:
///     beside the executable in an install, up the folder tree in a dev run.
/// </summary>
public sealed class BundledDocumentsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter-bundled-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void AFileBesideTheExecutable_Wins()
    {
        var exeDir = Directory.CreateDirectory(Path.Combine(_root, "bin", "Debug", "net10.0")).FullName;
        File.WriteAllText(Path.Combine(exeDir, "LICENSE.md"), "beside");
        File.WriteAllText(Path.Combine(_root, "LICENSE.md"), "repo root");

        Assert.Equal(Path.Combine(exeDir, "LICENSE.md"), new BundledDocuments(exeDir).Resolve("LICENSE.md"));
    }

    [Fact]
    public void ADevRun_FindsTheNearestParentThatHasIt()
    {
        var exeDir = Directory.CreateDirectory(Path.Combine(_root, "bin", "Debug", "net10.0")).FullName;
        File.WriteAllText(Path.Combine(_root, "LICENSE.md"), "repo root");

        Assert.Equal(Path.Combine(_root, "LICENSE.md"), new BundledDocuments(exeDir).Resolve("LICENSE.md"));
    }

    [Fact]
    public void NothingAnywhere_FallsBackToThePathBesideTheExecutable()
    {
        var exeDir = Directory.CreateDirectory(Path.Combine(_root, "a", "b")).FullName;

        Assert.Equal(Path.Combine(exeDir, "NOPE.md"), new BundledDocuments(exeDir).Resolve("NOPE.md"));
    }
}
