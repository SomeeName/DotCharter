namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The source files the song lists' filter, facet and sort work lives in, for the lints that
///     read it BY PATH: the shared engine (<c>DotCharter.Presentation/Shared/Library/</c>), the
///     Library's <c>SongBrowserViewModel</c>, Song Select's <c>SongSelectViewModel</c> and every
///     <c>PlayModeViewModel*.cs</c> partial.
///     <para>
///         One place, so a file moved out of the set fails here ("moved — repoint") instead of
///         leaving a lint that names one file passing over a file that no longer holds its subject.
///     </para>
/// </summary>
internal static class SongQuerySources
{
    /// <summary>The engine files, each by name — every one must exist.</summary>
    public static readonly string[] EngineFiles =
    [
        "ILibrarySong.cs", "LibraryFacets.cs", "LibraryFilter.cs", "LibraryQuery.cs",
        "LibraryFacetOptions.cs", "LibrarySort.cs", "PlayRecordFilter.cs"
    ];

    public static string EngineDirectory() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Presentation", "Shared", "Library");

    public static string LibraryViewModelPath() =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Panels", "SongBrowserViewModel.cs");

    /// <summary>One engine file's source.</summary>
    public static string Engine(string file)
    {
        Assert.Contains(file, EngineFiles);
        var path = Path.Combine(EngineDirectory(), file);
        Assert.True(File.Exists(path), $"{path} moved — repoint SongQuerySources.");
        return File.ReadAllText(path);
    }

    /// <summary>The Library's view model.</summary>
    public static string LibraryViewModel()
    {
        var path = LibraryViewModelPath();
        Assert.True(File.Exists(path), $"{path} moved — repoint SongQuerySources.");
        return UiSourceTree.ReadAllText(path);
    }

    /// <summary>Song Select's view model and every partial of the Play Mode view model that owns it.</summary>
    public static IReadOnlyList<(string File, string Source)> SongSelectViewModel()
    {
        var folder = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode");
        var files = UiSourceTree.GetFiles(folder, "PlayModeViewModel*.cs");
        Assert.True(files.Length > 5, $"PlayModeViewModel*.cs moved out of {folder} — repoint SongQuerySources.");
        var songSelect = Path.Combine(folder, "SongSelectViewModel.cs");
        Assert.True(File.Exists(songSelect), $"{songSelect} moved — repoint SongQuerySources.");
        return [.. files.Append(songSelect).Select(f => (Path.GetFileName(f), UiSourceTree.ReadAllText(f)))];
    }

    /// <summary>Both view models: the Library's, then each Song Select partial.</summary>
    public static IReadOnlyList<(string File, string Source)> ViewModels() =>
        [("SongBrowserViewModel.cs", LibraryViewModel()), .. SongSelectViewModel()];

    /// <summary>The engine and both view models.</summary>
    public static IReadOnlyList<(string File, string Source)> All() =>
        [.. EngineFiles.Select(f => (f, Engine(f))), .. ViewModels()];
}
