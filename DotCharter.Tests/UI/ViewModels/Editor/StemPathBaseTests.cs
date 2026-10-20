using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     A persisted stem path is relative to the PROJECT folder at BOTH ends.
///     <para>
///         Until 2026-09-04 it was not: <c>StemSeparationViewModel</c>
///         relativized against <c>ProjectAudioLocator.ResolveOutputDirectory</c>
///         — the AUDIO's folder, which is where the WAVs are written — while
///         <c>StemMixerViewModel.Rebuild</c> resolved against
///         <c>Path.GetDirectoryName(CurrentProjectPath)</c>. The two agree
///         whenever the audio sits beside the project, which is every ordinary
///         save and the reason this survived; they diverge for a rooted
///         <c>AudioFileName</c>, a PSARC session and a chart in a
///         per-arrangement subfolder, and there every stem silently failed to
///         load on the next open — minutes of separation apparently lost, with
///         the files still sitting on disk.
///     </para>
///     <para>
///         The project folder is the base (the same convention the cover-art
///         name uses — parsing-psarc.md), and the reader still ACCEPTS the old
///         audio-relative form, because a project saved by any earlier build
///         carries it.
///     </para>
/// </summary>
public sealed class StemPathBaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "dotcharter-stembase-" + Guid.NewGuid().ToString("N"));

    private string ProjectDir => Path.Combine(_root, "project");
    private string AudioDir => Path.Combine(_root, "audio");

    public StemPathBaseTests()
    {
        Directory.CreateDirectory(Path.Combine(ProjectDir, "stems"));
        Directory.CreateDirectory(Path.Combine(AudioDir, "stems"));
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    [Fact]
    public void A_project_relative_path_resolves_against_the_project()
    {
        var wav = Path.Combine(ProjectDir, "stems", "vocals.wav");
        File.WriteAllBytes(wav, [0]);

        Assert.Equal(wav, Resolve(Path.Combine("stems", "vocals.wav")));
    }

    [Fact]
    public void An_old_audio_relative_path_still_resolves()
    {
        // Written by any build before the fix: the name is relative to the AUDIO
        // folder, and the project folder has no such file. Silently losing these
        // is what makes a fix that only changes the base a data-loss change.
        var wav = Path.Combine(AudioDir, "stems", "bass.wav");
        File.WriteAllBytes(wav, [0]);

        Assert.Equal(wav, Resolve(Path.Combine("stems", "bass.wav")));
    }

    [Fact]
    public void The_project_base_wins_when_both_exist()
    {
        var canonical = Path.Combine(ProjectDir, "stems", "drums.wav");
        var legacy = Path.Combine(AudioDir, "stems", "drums.wav");
        File.WriteAllBytes(canonical, [0]);
        File.WriteAllBytes(legacy, [0]);

        Assert.Equal(canonical, Resolve(Path.Combine("stems", "drums.wav")));
    }

    [Fact]
    public void A_missing_stem_is_reported_at_the_canonical_location()
    {
        // Neither base has it. The answer must be where the file is SUPPOSED to
        // be, not the compatibility path — otherwise the log points at the
        // location we are moving away from.
        Assert.Equal(Path.Combine(ProjectDir, "stems", "other.wav"),
            Resolve(Path.Combine("stems", "other.wav")));
    }

    [Fact]
    public void A_rooted_path_is_left_alone()
    {
        // Transient sessions (PSARC import, audio-only) store absolute paths;
        // neither base may be prepended to one.
        var rooted = Path.Combine(AudioDir, "stems", "guitar.wav");
        Assert.Equal(rooted, Resolve(rooted));
        Assert.Equal(string.Empty, Resolve(""));
    }

    [Fact]
    public void ThePersistSideRelativizesAgainstTheProjectDirectory()
    {
        // The write half, as source: a full stem-separation run cannot be driven
        // in the default gate, and the failure this pins is precisely the two
        // halves naming different bases.
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels",
            "Shell", "StemSeparationViewModel.cs")));

        var start = source.IndexOf("RegisterAndPersistStemsAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "RegisterAndPersistStemsAsync is the persist site; it must still exist.");

        Assert.Contains("Path.GetRelativePath(projectDir!, s.Path)", source, StringComparison.Ordinal);
        // …and never against the audio/output directory again.
        Assert.DoesNotContain("Path.GetRelativePath(audioDir", source, StringComparison.Ordinal);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>The product's own resolver, the one the stem mixer's probe runs.</summary>
    private string Resolve(string stored) => StemFolderProbe.ResolveStemPath(ProjectDir, AudioDir, stored);
}
