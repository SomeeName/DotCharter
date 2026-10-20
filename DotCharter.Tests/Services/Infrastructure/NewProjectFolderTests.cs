using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     Every create-project flow derived its folder name from artist + title and
///     then called <c>Directory.CreateDirectory</c> — a NO-OP on an existing
///     folder. The audio copy and the <c>SaveChart</c> that followed then landed
///     on whatever was already there, so re-importing a song you had imported
///     before destroyed the earlier import's <c>project.rscp</c> and its working
///     audio. The name is derived, so reproducing the collision takes no mistake:
///     the same song yields the same name every time.
/// </summary>
public sealed class NewProjectFolderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_projectfolder_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void AFreeNameIsUsedAsIs()
    {
        Assert.Equal("Metallica - Battery",
            NewProjectFolder.ResolveName("Metallica - Battery", _ => false));
    }

    [Fact]
    public void AnOccupiedNameCountsUpFromTwo()
    {
        var taken = new HashSet<string> { "Metallica - Battery", "Metallica - Battery (2)" };

        Assert.Equal("Metallica - Battery (3)",
            NewProjectFolder.ResolveName("Metallica - Battery", taken.Contains));
    }

    /// <summary>The fallback must still return a name nothing occupies — falling
    /// back to one it was just told is taken would be the original bug.</summary>
    [Fact]
    public void EvenWhenEveryNumberIsTaken_TheResultIsFree()
    {
        var probed = new List<string>();
        var name = NewProjectFolder.ResolveName("Full", candidate =>
        {
            probed.Add(candidate);
            return candidate == "Full" || candidate.StartsWith("Full (", StringComparison.Ordinal)
                   && int.TryParse(candidate[6..^1], out _);
        });

        Assert.DoesNotContain(name, probed[..^1]);
        Assert.StartsWith("Full (", name);
        Assert.NotEqual("Full", name);
    }

    /// <summary>The headline case, on a real filesystem: the previous import is
    /// still there, byte for byte, and the new one went somewhere else.</summary>
    [Fact]
    public void AnExistingProjectFolderIsNeverReturned()
    {
        var existing = Path.Combine(_dir, "Metallica - Battery");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "project.rscp"), "<project>the first import</project>");

        var created = NewProjectFolder.Create(_dir, "Metallica - Battery");

        Assert.Equal(Path.Combine(_dir, "Metallica - Battery (2)"), created);
        Assert.True(Directory.Exists(created));
        Assert.Equal("<project>the first import</project>",
            File.ReadAllText(Path.Combine(existing, "project.rscp")));
    }

    /// <summary>
    ///     An existing but EMPTY folder is not a collision — users pre-create the
    ///     destination in the folder picker, and there is no data behind it to
    ///     protect. Disambiguating that would be a surprise for nothing.
    /// </summary>
    [Fact]
    public void AnExistingEmptyFolderIsReused()
    {
        var empty = Path.Combine(_dir, "Metallica - Battery");
        Directory.CreateDirectory(empty);

        Assert.Equal(empty, NewProjectFolder.Create(_dir, "Metallica - Battery"));
    }

    [Fact]
    public void AFreshImportCreatesTheFolderItReturns()
    {
        var created = NewProjectFolder.Create(_dir, "Ado - Jukkai");

        Assert.Equal(Path.Combine(_dir, "Ado - Jukkai"), created);
        Assert.True(Directory.Exists(created));
    }

    /// <summary>
    ///     All three create-project flows must resolve through this helper. Only
    ///     New Project and opening a <c>.dcsong</c> (<c>DcSongProjectUnpackerTests</c>)
    ///     can be driven end to end in the default gate — the EOF importer needs a
    ///     real <c>.eof</c> file — so the wiring is
    ///     pinned as a source lint against the exact call that caused the loss.
    /// </summary>
    [Fact]
    public void EveryCreateProjectFlow_ResolvesItsFolderThroughTheHelper()
    {
        string[] flows =
        [
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Session", "NewProjectCreator.cs"),
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Import", "Eof", "EofProjectCreator.cs"),
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Package", "DcSong", "DcSongProjectUnpacker.cs")
        ];

        foreach (var flow in flows)
        {
            Assert.True(File.Exists(flow), $"{flow} moved — repoint this lint.");
            var source = File.ReadAllText(flow);
            Assert.Contains("NewProjectFolder.Create", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Directory.CreateDirectory", source, StringComparison.Ordinal);
        }
    }
}
