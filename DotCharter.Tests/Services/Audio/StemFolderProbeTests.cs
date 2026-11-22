using System.Security.Cryptography;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordinary_reopen_recognises_the_legacy_completion_boundary(bool completed)
    {
        var path = Write(ProjectDir, "stems", "guitar.wav");
        var directory = Path.GetDirectoryName(path)!;
        var token = new string('a', 32);
        var pending = Path.Combine(directory, ".stemcache-publishing");
        File.WriteAllText(pending, token);
        if (completed) File.WriteAllText(Path.Combine(directory, ".stemcache"), token);

        var probe = StemFolderProbe.Probe(ProjectDir, AudioDir, [], false, null);

        Assert.Equal(completed ? 1 : 0, probe.Recovered.Count);
        Assert.Equal(!completed, File.Exists(pending));
    }

    [Theory]
    [InlineData("complete", true)]
    [InlineData("before-marker", false)]
    [InlineData("missing-file", false)]
    [InlineData("altered-file", false)]
    public void Ordinary_reopen_verifies_every_file_in_a_pending_publication(string boundary, bool completed)
    {
        var guitar = Write(ProjectDir, "stems", "guitar.wav");
        var bass = Write(ProjectDir, "stems", "bass.wav");
        var directory = Path.GetDirectoryName(guitar)!;
        var token = new string('b', 32);
        var proof = string.Join("\n", new[] { token }.Concat(new[] { guitar, bass }.Select(path =>
            $"{Path.GetFileName(path)}\t{new FileInfo(path).Length}\t{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}")));
        var pending = Path.Combine(directory, ".stemcache-publishing");
        File.WriteAllText(pending, proof);
        if (boundary != "before-marker") File.WriteAllText(Path.Combine(directory, ".stemcache"), token);
        if (boundary == "missing-file") File.Delete(bass);
        if (boundary == "altered-file") File.WriteAllText(bass, "y"); // same length, different bytes

        var probe = StemFolderProbe.Probe(ProjectDir, AudioDir, [], false, null);

        Assert.Equal(completed ? 2 : 0, probe.Recovered.Count);
        Assert.Equal(!completed, File.Exists(pending));
    }

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
