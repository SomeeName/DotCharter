using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

/// <summary>
///     One resolver, because there used to be four that disagreed. The project
///     LOADER walked up the parent directories; stem separation only probed the
///     project folder; the run-video exporter called File.Exists on the raw
///     CurrentAudioFileName — a bare file name for every saved project, so it
///     never existed. Symptom: a song that plays fine but whose stems or replay
///     video report "no audio file".
/// </summary>
public sealed class ProjectAudioLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter_audioloc_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_root);

    private string Touch(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "");
        return full;
    }

    [Fact]
    public void FindsAudioNextToTheProject()
    {
        var audio = Touch("song.ogg");
        var project = Path.Combine(_root, "project.rscp");

        Assert.Equal(audio, ProjectAudioLocator.TryResolve(project, "song.ogg"));
    }

    [Fact]
    public void WalksUpToTheSongRoot_WhenTheChartSitsInASubfolder()
    {
        // The behaviour the loader had and the other three callers lacked.
        var audio = Touch("song.ogg");
        var project = Path.Combine(_root, "lead", "project.rscp");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);

        Assert.Equal(audio, ProjectAudioLocator.TryResolve(project, "song.ogg"));
    }

    [Fact]
    public void AcceptsARootedAudioReference_TheUnsavedPsarcSessionCase()
    {
        var audio = Touch("cache/psarc-audio.wav");

        // No project path at all — a PSARC session that was never saved.
        Assert.Equal(audio, ProjectAudioLocator.TryResolve(null, audio));
    }

    [Fact]
    public void ReturnsNull_ForARootedReferenceThatDoesNotExist()
    {
        var missing = Path.Combine(_root, "gone.wav");

        Assert.Null(ProjectAudioLocator.TryResolve(null, missing));
    }

    /// <summary>
    ///     A project whose audio reference is ABSOLUTE — a PSARC session, an
    ///     imported audio file, a Save As out of the audio's folder — and which
    ///     the user then MOVED, folder and all. The absolute path names nothing
    ///     any more, and the audio is sitting right next to the project. Stopping
    ///     dead at the rooted check is how "I moved my song folder and it lost
    ///     its audio" happened; the bare name is tried after the path, never
    ///     instead of it.
    /// </summary>
    [Fact]
    public void AStaleRootedReference_FallsBackToTheNameBesideTheProject()
    {
        var moved = Path.Combine(_root, "moved");
        Directory.CreateDirectory(moved);
        var project = Path.Combine(moved, "project.rscp");
        var audio = Path.Combine(moved, "song.ogg");
        File.WriteAllText(audio, "audio");

        var staleReference = Path.Combine(_root, "where-it-used-to-live", "song.ogg");

        Assert.Equal(audio, ProjectAudioLocator.TryResolve(project, staleReference));
    }

    /// <summary>The path still wins when it resolves — the fallback must never
    /// silently swap in a same-named file from somewhere else.</summary>
    [Fact]
    public void ARootedReferenceThatExists_BeatsASameNamedFileBesideTheProject()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        var real = Path.Combine(elsewhere, "song.ogg");
        File.WriteAllText(real, "the real one");

        var beside = Path.Combine(_root, "beside");
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(beside, "song.ogg"), "an impostor");

        Assert.Equal(real, ProjectAudioLocator.TryResolve(Path.Combine(beside, "project.rscp"), real));
    }

    [Fact]
    public void ReturnsNull_WhenTheAudioIsMissingEntirely()
    {
        var project = Path.Combine(_root, "project.rscp");

        Assert.Null(ProjectAudioLocator.TryResolve(project, "nope.ogg"));
        Assert.Null(ProjectAudioLocator.TryResolve(project, ""));
        Assert.Null(ProjectAudioLocator.TryResolve(null, "song.ogg"));
    }

    [Fact]
    public void ResolveOrDefault_ReportsTheExpectedLocation_WhenMissing()
    {
        // The loader logs this path in its "references missing audio" warning,
        // so it must stay a concrete location rather than null or empty.
        var project = Path.Combine(_root, "project.rscp");

        Assert.Equal(Path.Combine(_root, "nope.ogg"),
            ProjectAudioLocator.ResolveOrDefault(project, "nope.ogg"));
    }

    [Fact]
    public void ResolveOutputDirectory_PrefersTheResolvedAudiosOwnFolder()
    {
        // Stems belong next to the audio for a rooted (PSARC) reference, and
        // next to the project otherwise.
        var rooted = Touch("cache/psarc-audio.wav");
        Assert.Equal(Path.GetDirectoryName(rooted), ProjectAudioLocator.ResolveOutputDirectory(null, rooted));

        Touch("song.ogg");
        var project = Path.Combine(_root, "lead", "project.rscp");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        Assert.Equal(_root.TrimEnd(Path.DirectorySeparatorChar),
            ProjectAudioLocator.ResolveOutputDirectory(project, "song.ogg")!
                .TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ResolveOutputDirectory_FallsBackToTheProjectFolder_WhenAudioIsMissing()
    {
        var project = Path.Combine(_root, "project.rscp");

        Assert.Equal(_root.TrimEnd(Path.DirectorySeparatorChar),
            ProjectAudioLocator.ResolveOutputDirectory(project, "nope.ogg")!
                .TrimEnd(Path.DirectorySeparatorChar));
    }
}
