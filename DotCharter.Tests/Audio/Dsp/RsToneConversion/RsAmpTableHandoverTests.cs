using System.Text.RegularExpressions;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
///     JuceSharp #25: the native <c>rsamp</c> table is handed to the engine at run time
///     (<see cref="RsAmpTableHandover" />, owned by <c>JuceAudioPlaybackService</c>). The tables
///     come from the player's install through the optional plugin that reads it; what the engine
///     does with them is covered here, and how they are built in that plugin's own tests.
/// </summary>
public sealed partial class RsAmpTableHandoverTests
{
    /// <summary>The handover is wired where the replay rules say: the constructor watches
    /// the tables being published (they usually arrive after the engine), and the
    /// master-bus replay hands them to EVERY engine build (root AGENTS.md, "replayed on
    /// engine REBUILD"). Comment-stripped source, so a commented-out call does not count.</summary>
    [Fact]
    public void TheService_HandsTheTableOver_OnPublishAndOnEveryEngineBuild()
    {
        var audio = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Audio");
        string Code(string file) => Comments().Replace(File.ReadAllText(Path.Combine(audio, file)), "");

        var replay = Code("JuceAudioPlaybackService.EngineLifecycle.cs");
        var body = replay[replay.IndexOf("void ReapplyMasterBusStateLocked(", StringComparison.Ordinal)..];
        Assert.Contains("_rsAmpTable.Push(RsGameTables.Process", body[..body.IndexOf("void Reapply(", StringComparison.Ordinal)], StringComparison.Ordinal);

        var main = Code("JuceAudioPlaybackService.cs");
        Assert.Contains("_rsAmpTable = new Engine.RsAmpTableHandover(", main, StringComparison.Ordinal);
        Assert.Contains("_rsAmpTable.Dispose();", main, StringComparison.Ordinal);

        var handover = Code(Path.Combine("Engine", "RsAmpTableHandover.cs"));
        Assert.Contains("RsGameTables.Published += OnPublished", handover, StringComparison.Ordinal);
        Assert.Contains("JuceRsAmpTable.TryLoad(", handover, StringComparison.Ordinal);
    }

    /// <summary>A blob the engine refuses is reported, never thrown, and leaves nothing
    /// claimed as loaded: no tables at all hand nothing over.</summary>
    [Fact]
    public void ARefusedTable_IsLogged_AndNoTablesHandNothingOver()
    {
        var log = new FakeAppLogger();
        using var handover = new RsAmpTableHandover(log, () => false);
        handover.Push(RsGameTables.Empty, "engine built");
        Assert.Empty(log.Entries);
        handover.Push(new RsGameTables([], RsGameCurveSet.Empty, [1, 2, 3]), "engine built");
        Assert.True(log.HasEntryContaining("WARN", "refused") || log.HasEntryContaining("WARN", "not handed to the engine"),
            string.Join("\n", log.Entries.Select(e => e.ToString())));
    }

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comments();
}
