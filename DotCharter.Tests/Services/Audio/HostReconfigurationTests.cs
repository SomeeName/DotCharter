using DotCharter.Tone.Audio.Engine;
using DotCharter.Tone.Audio.Vst;

namespace DotCharter.Tests.Services.Audio;

/// <summary>Effect order and media recovery survive host reconfiguration without native I/O.</summary>
public sealed class HostReconfigurationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void UnbypassingTheFirstEffect_RestoresItBeforeTheSecondEffect(int player)
    {
        using var first = new RecordingRebuildPlugin([1]);
        using var second = new RecordingRebuildPlugin([2]);
        var live = new List<RecordingRebuildPlugin> { first, second };
        void Detach(RecordingRebuildPlugin plugin) => live.Remove(plugin);
        void Attach(RecordingRebuildPlugin plugin, int seat)
        {
            Assert.Equal(player, seat);
            live.Add(plugin);
        }
        VstInputOrder.Apply(new[] { (first, player, true), (second, player, false) }, Detach, Attach);
        Assert.Equal(new[] { second }, live);
        VstInputOrder.Apply(new[] { (first, player, false), (second, player, false) }, Detach, Attach);
        Assert.Equal(new[] { first, second }, live);
    }

    [Fact]
    public void ReorderingAndRestoringBypass_PreservesInstancesStateAndSeatOrder()
    {
        using var first = new RecordingRebuildPlugin([1]);
        using var second = new RecordingRebuildPlugin([2]);
        using var otherSeat = new RecordingRebuildPlugin([3]);
        var live = new List<(RecordingRebuildPlugin Plugin, int Player)> { (first, 0), (second, 0), (otherSeat, 1) };
        void Detach(RecordingRebuildPlugin plugin) => live.RemoveAll(s => ReferenceEquals(s.Plugin, plugin));
        void Attach(RecordingRebuildPlugin plugin, int player) => live.Add((plugin, player));

        VstInputOrder.Apply(new[] { (otherSeat, 1, false), (second, 0, false), (first, 0, true) }, Detach, Attach);
        Assert.Equal(new[] { otherSeat, second }, live.Select(s => s.Plugin));
        VstInputOrder.Apply(new[] { (otherSeat, 1, false), (second, 0, false), (first, 0, false) }, Detach, Attach);
        Assert.Equal(new[] { otherSeat, second, first }, live.Select(s => s.Plugin));
        Assert.Equal(new[] { 1, 0, 0 }, live.Select(s => s.Player));
        Assert.Equal(new byte[] { 1 }, first.State);
        Assert.Equal(new byte[] { 2 }, second.State);
        Assert.All(new[] { first, second, otherSeat }, p => Assert.False(p.IsDisposed));
    }

    [Fact]
    public void FailedEngineOrPartialMediaRestore_RetainsTheWholeRecoverySnapshot()
    {
        var recovery = new MediaRebuildRecovery<string[]>();
        var request = new MediaLoadRequest("song.wav", 1);
        var snapshot = recovery.Capture("song.wav", request, request, ["drums", "bass"]);
        Assert.NotNull(snapshot);
        Assert.Same(snapshot, recovery.Capture(null, request, request, null));
        recovery.Complete(snapshot, false);
        Assert.Same(snapshot, recovery.Capture(null, request, request, ["drums"]));
        Assert.Equal(new[] { "drums", "bass" }, snapshot.Stems);
        recovery.Complete(snapshot, true);
        Assert.Null(recovery.Capture(null, request, request, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("other.wav")]
    public void ClearedOrReplacedSong_DoesNotInheritPendingRecovery(string? identity)
    {
        var recovery = new MediaRebuildRecovery<string[]>();
        var original = new MediaLoadRequest("song.wav", 1);
        recovery.Capture("song.wav", original, original, ["drums"]);
        var newer = new MediaLoadRequest(identity, 2);
        Assert.Null(recovery.Capture(null, original, newer, null));
    }

    [Fact]
    public void AnExplicitClearThenReloadOfTheSameSong_DoesNotResurrectTheOldMix()
    {
        var recovery = new MediaRebuildRecovery<string[]>();
        var original = new MediaLoadRequest("song.wav", 1);
        recovery.Capture("song.wav", original, original, ["drums"]);
        recovery.Observe(new MediaLoadRequest(null, 2));
        var reload = new MediaLoadRequest("song.wav", 3);
        Assert.Null(recovery.Capture(null, reload, reload, null));
    }

    [Fact]
    public void AFailedMasterReopen_IsRetriedWithItsOriginalStemMixOnTheNextEngine()
    {
        var recovery = new MediaRebuildRecovery<string[]>();
        var request = new MediaLoadRequest("song.wav", 1);
        var snapshot = recovery.Capture("song.wav", request, request, ["drums", "bass"])!;
        var engine = new object();
        string[]? restored = null;
        Assert.Throws<IOException>(() => MediaRebuildReplay.Replay<MemoryStream, string[]>(
            new object(), new object(), snapshot.Request, () => request, engine, () => engine,
            () => throw new IOException("open failed"), _ => Assert.Fail("A failed decode cannot publish."),
            () => true, snapshot.Stems, stems => restored = stems));
        Assert.Equal(new[] { "drums", "bass" }, restored);

        snapshot = recovery.Capture(null, request, request, restored)!;
        using var reopened = new MemoryStream();
        MemoryStream? published = null;
        MediaRebuildReplay.Replay(new object(), new object(), snapshot.Request, () => request, engine, () => engine,
            () => reopened, source => published = source, () => true, snapshot.Stems, stems => restored = stems);
        Assert.Same(reopened, published);
        Assert.Equal(new[] { "drums", "bass" }, restored);
        recovery.Complete(snapshot, true);
        Assert.Null(recovery.Capture(null, request, request, null));
    }

    [Fact]
    public async Task ADelayedOlderChainWorker_CannotResurrectARemovedEffect()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var latest = 2;
        var chain = new List<string> { "removed" };
        await VstChainWork.RunAsync(gate, () => latest == 2, () =>
        {
            chain.Clear();
            return Task.CompletedTask;
        });
        await VstChainWork.RunAsync(gate, () => latest == 1, () =>
        {
            chain.Add("removed");
            return Task.CompletedTask;
        });
        Assert.Empty(chain);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task AWorkerWaitingForTheGate_RechecksOwnershipAfterTheWait()
    {
        using var gate = new SemaphoreSlim(0, 1);
        var latest = 1;
        var applied = false;
        var pending = VstChainWork.RunAsync(gate, () => latest == 1, () =>
        {
            applied = true;
            return Task.CompletedTask;
        });
        latest = 2;
        gate.Release();
        await pending;
        Assert.False(applied);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public void ChainWorkers_UseTheSerializedOwnershipCheckBeforeEitherBackend()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Tone/Audio/JuceAudioPlaybackService.VstChain.cs"));
        var worker = source.IndexOf("private void QueueGuitarVstChain", StringComparison.Ordinal);
        Assert.True(worker >= 0);
        var entered = source.IndexOf("VstChainWork.RunAsync(_vstGate", worker, StringComparison.Ordinal);
        Assert.True(entered >= 0);
        var stale = source.IndexOf("generation == _guitarChainGeneration", entered, StringComparison.Ordinal);
        var apply = source.IndexOf("ApplySandboxGuitarChainCoreAsync(snapshot)", entered, StringComparison.Ordinal);
        Assert.True(stale > entered && apply > stale,
            "An older worker must be rejected after acquiring the gate and before applying either backend.");
    }

    [Fact]
    public void RebuildRecovery_IsCapturedBeforeTeardownAndCompletedAfterReplay()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Tone/Audio/JuceAudioPlaybackService.EngineLifecycle.cs"));
        var capture = source.IndexOf("_mediaRebuildRecovery.Capture(", StringComparison.Ordinal);
        var teardown = source.IndexOf("FreeAudioLocked();", StringComparison.Ordinal);
        var replay = source.IndexOf("MediaRebuildReplay.Replay<", StringComparison.Ordinal);
        var complete = source.IndexOf("_mediaRebuildRecovery.Complete(", StringComparison.Ordinal);
        Assert.True(capture >= 0 && teardown > capture && replay > teardown && complete > replay);
    }

    [Theory]
    [InlineData("EngineLifecycle")]
    [InlineData("Sandbox")]
    public void EffectRehydration_PreservesRequestedOrderInsteadOfDictionaryOrder(string partial)
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            $"DotCharter.Tone/Audio/JuceAudioPlaybackService.{partial}.cs"));
        Assert.Contains(".OrderBy(kv => _guitarChainOrder.IndexOf(kv.Key))", source, StringComparison.Ordinal);
    }
}
