using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <para>
///         <c>StemWaveformCoordinator</c> is an app-wide SINGLETON holding a
///         <c>ConcurrentDictionary&lt;string, WaveformSamples&gt;</c> of per-stem peak
///         pyramids keyed by PATH, plus the song's own file waveform. Only
///         <c>StemMixerViewModel</c> ever resets it, and two of its four clear sites —
///         the no-project and no-stems early returns of <c>RebuildOnceAsync</c> — cleared
///         the ENGINE's stems and left the cache. Open a project with stems, then one
///         without, and the previous song's pyramids stayed for the life of the process.
///     </para>
///     <para>
///         It was also a correctness bug, which is the half a memory audit would miss:
///         the coordinator latched the file waveform with
///         <c>_fileWaveform ??= _state.WaveformData</c>, so the stale entry meant
///         turning "waveform follows mix" off on project B restored project A's
///         waveform onto B's timeline. That latch is gone — the coordinator re-reads
///         the song's waveform every pass and refuses the one it published itself
///         (<c>StemWaveformCoordinator._published</c>, pinned by
///         <see cref="StemWaveformFileLatchTests" />) — but the MEMORY half is
///         unchanged and is what this class still guards.
///     </para>
///     <para>
///         Linted rather than exercised: reaching the two branches needs the real
///         Avalonia dispatcher and a disk probe, and the invariant here is a STRUCTURAL
///         one — the clear of the engine and the clear of the cache are one decision, so
///         they live in one method and nothing else may clear only half.
///     </para>
/// </summary>
public sealed class StemWaveformCacheLifetimeTests
{
    private const string ViewModel = "DotCharter.UI/ViewModels/Editor/StemMixerViewModel.cs";

    /// <summary>
    ///     Exactly one call site clears the engine's stems, and it is the method that
    ///     also drops the waveform cache.
    /// </summary>
    [Fact]
    public void EveryStemClearInTheMixerAlsoDropsTheWaveformCache()
    {
        var source = SourceText.StripComments(UiSourceTree.ReadAllText(RepoPath(ViewModel)));

        var clears = Regex.Matches(source, @"_audio\.ClearStems\(\)").Count;
        Assert.True(clears == 1,
            $"{ViewModel}: found {clears} calls to _audio.ClearStems(). Exactly one is allowed — the one inside "
            + "ClearStemsAndTheirWaveforms — because clearing the engine's stems without resetting "
            + "StemWaveformCoordinator strands a peak pyramid per stem, for the life of the process, and leaves "
            + "the NEXT project drawing the previous song's waveform. Route the new call site through the helper.");

        var helper = source.IndexOf("private void ClearStemsAndTheirWaveforms()", StringComparison.Ordinal);
        Assert.True(helper >= 0,
            $"{ViewModel}: ClearStemsAndTheirWaveforms is gone. If it was renamed, repoint this lint; if the "
            + "pairing was inlined again, the two early-return branches will silently stop resetting the cache.");

        var clear = source.IndexOf("_audio.ClearStems()", StringComparison.Ordinal);
        var reset = source.IndexOf("_waveform?.Reset()", helper, StringComparison.Ordinal);
        Assert.True(clear > helper, $"{ViewModel}: the only ClearStems call is outside the helper.");
        Assert.True(reset > clear && reset - clear < 200,
            $"{ViewModel}: ClearStemsAndTheirWaveforms no longer resets the waveform cache beside the engine clear.");
    }

    private static string RepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.False(dir is null, "Could not locate repository root (DotCharter.sln).");
        return Path.Combine(dir!, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
