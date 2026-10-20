using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.GuitarRoles;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The stem mixer's disk probe, over a real temp folder: what a project with
///     no declared stems recovers from a `stems/` folder, which declared stems
///     exist, and which one is the guitar the Guitar roles switch splits.
/// </summary>
public sealed class StemFolderProbeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcharter-stem-probe-").FullName;

    private string ProjectDir => Path.Combine(_root, "project");
    private string AudioDir => Path.Combine(_root, "audio");

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void WithNoDeclaredStems_TheProjectsStemsFolderIsRecovered_InNameOrder_ProjectRelative()
    {
        var vocals = Write(ProjectDir, "stems", "vocals.wav");
        var bass = Write(ProjectDir, "stems", "Bass.wav");
        Write(ProjectDir, "stems", "notes.txt");

        var probe = StemFolderProbe.Probe(ProjectDir, AudioDir, [], false, null);

        List<(string Name, string RelativePath)> expected =
            [("Bass", Path.Combine("stems", "Bass.wav")), ("vocals", Path.Combine("stems", "vocals.wav"))];
        Assert.Equal(expected, probe.Recovered);
        Assert.Equal(bass, probe.Resolved[Path.Combine("stems", "Bass.wav")]);
        Assert.Contains(vocals, probe.Existing);
        Assert.Null(probe.GuitarPath);
        Assert.Null(probe.RolePair);
    }

    [Fact]
    public void WithNoStemsBesideTheProject_TheAudioFolderIsTheFallback_StillNamedProjectRelative()
    {
        var drums = Write(AudioDir, "stems", "drums.wav");
        Directory.CreateDirectory(ProjectDir);

        var probe = StemFolderProbe.Probe(ProjectDir, AudioDir, [], false, null);

        var stored = Path.GetRelativePath(ProjectDir, drums);
        Assert.Equal(("drums", stored), Assert.Single(probe.Recovered));
        Assert.Equal(drums, probe.Resolved[stored]);
    }

    [Fact]
    public void WithStemsBesideBoth_OnlyTheProjectsAreRecovered()
    {
        var mine = Write(ProjectDir, "stems", "vocals.wav");
        Write(AudioDir, "stems", "drums.wav");

        var probe = StemFolderProbe.Probe(ProjectDir, AudioDir, [], false, null);

        Assert.Equal(("vocals", Path.GetRelativePath(ProjectDir, mine)), Assert.Single(probe.Recovered));
    }

    [Fact]
    public void DeclaredStems_AreNotRecoveredAgain_AndOnlyTheOnesOnDiskCountAsExisting()
    {
        var guitar = Write(ProjectDir, "stems", "guitar.wav");
        Write(ProjectDir, "stems", "stray.wav");
        Stem[] declared =
        [
            new() { Name = "guitar", Path = Path.Combine("stems", "guitar.wav") },
            new() { Name = "vocals", Path = Path.Combine("stems", "vocals.wav") }
        ];

        var probe = StemFolderProbe.Probe(ProjectDir, AudioDir, declared, false, null);

        Assert.Empty(probe.Recovered);
        Assert.Equal(guitar, Assert.Single(probe.Existing));
        // A missing stem still resolves — to where it is SUPPOSED to be.
        Assert.Equal(Path.Combine(ProjectDir, "stems", "vocals.wav"), probe.Resolved[declared[1].Path]);
        Assert.Equal(guitar, probe.GuitarPath);
    }

    [Fact]
    public void ARecoveredGuitarWav_IsTheGuitar_AndTheDerivedPairIsAskedForOnlyWhenRolesAreWanted()
    {
        var guitar = Write(ProjectDir, "stems", "guitar.wav");
        var pair = new GuitarRolePair("lead.wav", "rhythm.wav");
        var roles = new GuitarRolesViewModelTests.FakeGuitarRoleStems { Derived = pair };

        var wanted = StemFolderProbe.Probe(ProjectDir, AudioDir, [], true, roles);
        var unwanted = StemFolderProbe.Probe(ProjectDir, AudioDir, [], false, roles);

        Assert.Equal(guitar, wanted.GuitarPath);
        Assert.Same(pair, wanted.RolePair);
        Assert.Equal(guitar, unwanted.GuitarPath);
        Assert.Null(unwanted.RolePair);
    }

    [Fact]
    public void WithNeitherFolder_TheProbeFindsNothing_AndDoesNotThrow()
    {
        var probe = StemFolderProbe.Probe(ProjectDir, AudioDir, [], true, null);

        Assert.Empty(probe.Recovered);
        Assert.Empty(probe.Existing);
        Assert.Null(probe.GuitarPath);
    }

    private static string Write(string dir, string sub, string name)
    {
        var folder = Path.Combine(dir, sub);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }
}
