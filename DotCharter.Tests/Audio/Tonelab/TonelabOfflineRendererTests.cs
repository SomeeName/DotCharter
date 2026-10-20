using System.Runtime.Versioning;
using DotCharter.Tests.Ai.Generate;
using DotCharter.Tone.Audio.Tonelab;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The AI tone check's real-game renderer: the DI pushed OFFLINE through tonelab's shared-memory exchange (the
///     loopback mock renders input × gain, delayed), each tone applied first and the game's own tone restored after; and
///     the refusals — a live preview routing audio, a game on its own device, a session that is not ready, a route
///     claimed mid-batch — and the session's lease that keeps the native insert closed meanwhile.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TonelabOfflineRendererTests
{
    private static readonly string ToneFiles = Path.Combine(Path.GetTempPath(), "dotcharter-tonelab-render-tests");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_tone_is_applied_then_the_di_comes_back_through_the_exchange_and_the_games_tone_is_restored()
    {
        using var game = new TonelabGameMock { Gain = 0.5f, DelaySamples = 3 };
        using var audio = game.CreateAudio(128);
        var session = ReadyFor(game);
        var di = Enumerable.Range(0, 3000).Select(i => (float)Math.Sin(i * 0.03)).ToArray();

        var results = await new TonelabOfflineRenderer(session, null, ToneFiles)
            .RenderAsync(di, [RsToneLoopFakes.MakeTone(RsToneLoopFakes.AmpA, 10), RsToneLoopFakes.MakeTone(RsToneLoopFakes.AmpB, 50)], Ct);

        Assert.Equal(2, results.Count);
        var tail = (int)(TonelabOfflineRenderer.Tail.TotalSeconds * 48000);
        foreach (var result in results)
        {
            Assert.Null(result.Refusal);
            Assert.Equal(di.Length + tail, result.Samples!.Length);
            // The lead-in is cut exactly, so the game's own latency stays in the render: input × gain, 3 samples late.
            for (var i = 3; i < di.Length; i += 37) Assert.Equal(di[i - 3] * 0.5f, result.Samples[i], 5);
        }

        Assert.Equal(2, session.AppliedFiles.Count);
        Assert.Equal(1, session.Restores);
        Assert.True(session.Lease!.Ended);
    }

    [Fact]
    public async Task A_live_preview_routing_audio_refuses_the_whole_batch_and_nothing_reaches_the_game()
    {
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready, GameHoldsAudioDevice = false };
        session.RouteAudio(new object(), true);
        var renderer = new TonelabOfflineRenderer(session, null, ToneFiles);

        Assert.Equal(GameRenderRefusals.LivePreview, renderer.Availability().Refusal);
        var results = await renderer.RenderAsync([0f, 1f], [RsToneLoopFakes.MakeTone(RsToneLoopFakes.AmpA, 10)], Ct);

        Assert.Equal(GameRenderRefusals.LivePreview, Assert.Single(results).Refusal);
        Assert.Empty(session.AppliedFiles);
        Assert.Null(session.Lease);
    }

    [Theory]
    [InlineData(TonelabSessionState.Disconnected, false, GameRenderRefusals.NotReady)]
    [InlineData(TonelabSessionState.Starting, false, GameRenderRefusals.NotReady)]
    [InlineData(TonelabSessionState.Ready, true, GameRenderRefusals.GameOwnDevice)]
    [InlineData(TonelabSessionState.Ready, false, null)]
    public void Availability_names_why_the_game_cannot_render(TonelabSessionState state, bool ownDevice, string? refusal) =>
        Assert.Equal(refusal, new TonelabOfflineRenderer(new FakeTonelabSession { State = state, GameHoldsAudioDevice = ownDevice }, null, ToneFiles)
            .Availability().Refusal);

    [Fact]
    public async Task A_route_claimed_mid_batch_marks_the_render_interrupted_and_the_rest_are_not_sent()
    {
        using var game = new TonelabGameMock();
        using var audio = game.CreateAudio(128);
        var session = ReadyFor(game);
        session.Applied = () => session.RouteAudio(new object(), true);
        var tones = new[] { RsToneLoopFakes.MakeTone(RsToneLoopFakes.AmpA, 10), RsToneLoopFakes.MakeTone(RsToneLoopFakes.AmpA, 20) };

        var results = await new TonelabOfflineRenderer(session, null, ToneFiles).RenderAsync(new float[512], tones, Ct);

        Assert.All(results, r => Assert.Equal(GameRenderRefusals.Interrupted, r.Refusal));
        Assert.Single(session.AppliedFiles);
        Assert.Equal(1, session.Restores);
    }

    [Fact]
    public async Task No_exchange_is_a_refusal_with_the_fault()
    {
        using var game = new TonelabGameMock();
        var session = ReadyFor(game);

        var result = Assert.Single(await new TonelabOfflineRenderer(session, null, ToneFiles)
            .RenderAsync([0f], [RsToneLoopFakes.MakeTone(RsToneLoopFakes.AmpA, 10)], Ct));

        Assert.Equal(GameRenderRefusals.NoAudioRing, result.Refusal);
        Assert.Contains("no shared memory", result.Detail, StringComparison.Ordinal);
        Assert.Empty(session.AppliedFiles);
    }

    [Fact]
    public async Task The_sessions_lease_is_refused_while_routed_and_holds_a_route_claim_until_it_ends()
    {
        using var game = new TonelabGameMock();
        var insert = new TonelabSessionTests.FakeInsert();
        using var session = new TonelabSession(() => [game.ProcessId], insert, null, TonelabSessionTests.Quick);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(Ct));
        var preview = new object();

        session.RouteAudio(preview, true);
        Assert.True(insert.IsOpen);
        Assert.Null(session.TryLeaseAudio());
        session.RouteAudio(preview, false);

        var lease = session.TryLeaseAudio()!;
        Assert.Null(session.TryLeaseAudio());   // one at a time
        session.RouteAudio(preview, true);
        Assert.True(lease.Interrupted);
        Assert.False(insert.IsOpen);            // queued: the exchange is the render's until the lease ends
        lease.Dispose();
        Assert.True(insert.IsOpen);
        Assert.True(session.IsRoutingAudio);
    }

    private static FakeTonelabSession ReadyFor(TonelabGameMock game) =>
        new() { State = TonelabSessionState.Ready, GameHoldsAudioDevice = false, Pid = game.ProcessId };
}
