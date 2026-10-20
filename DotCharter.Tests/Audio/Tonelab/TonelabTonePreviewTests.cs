using System.Globalization;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Tonelab;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio.Tonelab;

/// <summary>
///     The in-game preview target over the mock game: the clone's contract (apply,
///     latest wins, restore on stop) mapped onto apply-file / tone knob / tone restore,
///     with the gear-catalogue gate the app wires (<see cref="TonePreviewTargets.Validate" />)
///     in front and the temp files cleaned up behind.
/// </summary>
public sealed class TonelabTonePreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter-tonelab-").FullName;
    private readonly ManualTonelabScheduler _scheduler = new();
    private readonly List<TonelabResult> _results = [];

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public async Task ABurst_SendsOneFile_WithTheLatestTone()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var preview = Preview(session);
        var (knob, min, max) = AmpKnob(Tone());

        preview.Apply(WithKnob(Tone(), knob, min));
        preview.Apply(WithKnob(Tone(), knob, max));
        var latest = WithKnob(Tone(), knob, (min + max) / 2);
        preview.Apply(latest);
        Assert.Equal(1, _scheduler.Pending);
        await Fire(preview);

        Assert.Equal(["status", "tone apply-file " + Path.Combine(_dir, $"tone-{Environment.ProcessId}-1.json")], game.Commands());
        Assert.Equal(RsToneJsonWriter.Write(latest), Assert.Single(game.AppliedBodies));
        Assert.True(Assert.Single(_results).IsOk);
        Assert.True(preview.IsActive);
    }

    [Fact]
    public async Task OnlyKnobsMoved_SendsToneKnob_NotAWholeFile()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var preview = Preview(session);
        var (knob, min, max) = AmpKnob(Tone());

        preview.Apply(WithKnob(Tone(), knob, min));
        await Fire(preview);
        preview.Apply(WithKnob(Tone(), knob, max));
        await Fire(preview);

        var value = max.ToString("0.######", CultureInfo.InvariantCulture);
        Assert.Equal($"tone knob Amp {knob} {value}", game.Commands()[^1]);
        Assert.Single(game.AppliedBodies);
    }

    [Fact]
    public async Task KnobMovesWhileAReplyIsPending_Coalesce_IntoOneMoreSend()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var preview = Preview(session);
        var (knob, min, max) = AmpKnob(Tone());
        preview.Apply(WithKnob(Tone(), knob, min));
        await Fire(preview);

        using var reply = new SemaphoreSlim(0);
        game.Responder = line =>
        {
            if (line.StartsWith("tone knob ", StringComparison.Ordinal)) reply.Wait(TimeSpan.FromSeconds(10));
            return "ok";
        };
        preview.Apply(WithKnob(Tone(), knob, max));
        _scheduler.Elapse();
        var inFlight = preview.LastTask;
        Assert.True(SpinWait.SpinUntil(() => game.Commands().Any(c => c.StartsWith("tone knob ", StringComparison.Ordinal)), TimeSpan.FromSeconds(10)));

        // three more drags while the game has not answered: none of them may queue a command
        var latest = (min + max) / 2;
        foreach (var value in new[] { max - 1, max - 2, latest })
        {
            preview.Apply(WithKnob(Tone(), knob, value));
            _scheduler.Elapse();
        }

        Assert.Single(game.Commands(), c => c.StartsWith("tone knob ", StringComparison.Ordinal));
        reply.Release(2);
        await inFlight;

        var knobs = game.Commands().Where(c => c.StartsWith("tone knob ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, knobs.Length);
        Assert.EndsWith(" " + latest.ToString("0.######", CultureInfo.InvariantCulture), knobs[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewVolume_SendsAWholeFile_AndTheOldFileIsDeleted()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var preview = Preview(session);

        preview.Apply(Tone());
        await Fire(preview);
        preview.Apply(Tone() with { VolumeDb = Tone().VolumeDb - 1 });
        await Fire(preview);

        Assert.Equal(2, game.AppliedBodies.Count);
        Assert.Equal([$"tone-{Environment.ProcessId}-2.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Stop_AsksTheGameToRestore_AndCleansUp()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var preview = Preview(session);
        preview.Apply(Tone());
        await Fire(preview);

        Assert.True(preview.Stop());
        await preview.LastTask;

        Assert.Equal("tone restore", game.Commands()[^1]);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.False(preview.IsActive);
        Assert.False(preview.Stop());
    }

    [Fact]
    public async Task TheGuitarGoesThroughTheGame_FromTheFirstAcceptedTone_UntilRestore()
    {
        var insert = new TonelabSessionTests.FakeInsert();
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [game.ProcessId], insert, null, TonelabSessionTests.Quick);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync(TestContext.Current.CancellationToken));
        using var preview = Preview(session);

        preview.Apply(Tone());
        Assert.False(insert.IsOpen);   // debounce pending: the game has not got the tone yet
        await Fire(preview);
        Assert.True(insert.IsOpen);

        Assert.True(preview.Stop());
        await preview.LastTask;
        Assert.False(insert.IsOpen);
    }

    [Fact]
    public async Task Stop_BeforeTheDebounceFires_SendsNoTone()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var preview = Preview(session);

        preview.Apply(Tone());
        Assert.True(preview.Stop());
        await preview.LastTask;
        _scheduler.Elapse();

        Assert.Equal(["status"], game.Commands());   // the game accepted nothing: nothing to restore
    }

    [Fact]
    public async Task GearOffTheCatalogue_IsNeverSent()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var preview = Preview(session);

        preview.Apply(Tone() with { Amp = new RsAmp("Amp_Nope", "Amp", new Dictionary<string, float>()) });

        Assert.Equal(0, _scheduler.Pending);
        var refused = Assert.Single(_results);
        Assert.Equal(TonelabOutcome.Invalid, refused.Outcome);
        Assert.Contains("Amp_Nope", refused.Detail, StringComparison.Ordinal);
        Assert.Equal(["status"], game.Commands());
    }

    [Fact]
    public async Task GearTheGameRefuses_IsReported_AndTheNextEditResendsTheWholeTone()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var preview = Preview(session);
        game.UnknownGear.Add(Tone().Amp.GearKey);
        var (knob, min, max) = AmpKnob(Tone());

        preview.Apply(WithKnob(Tone(), knob, min));
        await Fire(preview);
        Assert.Equal(TonelabOutcome.UnknownGear, _results[^1].Outcome);
        Assert.Equal(Tone().Amp.GearKey, _results[^1].Detail);

        game.UnknownGear.Clear();
        preview.Apply(WithKnob(Tone(), knob, max));
        await Fire(preview);
        Assert.StartsWith("tone apply-file ", game.Commands()[^1], StringComparison.Ordinal);
        Assert.True(_results[^1].IsOk);
    }

    [Fact]
    public void WithoutAReadySession_NothingIsTaken()
    {
        using var session = new TonelabSession(() => [], null, null, TonelabSessionTests.Quick);
        using var preview = Preview(session);

        Assert.False(preview.IsAvailable);
        preview.Apply(Tone());
        Assert.False(preview.IsActive);
        Assert.Equal(0, _scheduler.Pending);
    }

    private static RsTone Tone() => RsToneAuthoring.CreateDefaultTone("tl_tone", "Tonelab", RsGearCatalog.Shared);

    private static (string Key, float Min, float Max) AmpKnob(RsTone tone)
    {
        Assert.True(RsGearCatalog.Shared.TryGetGear(tone.Amp.GearKey, out var gear));
        var knob = gear.Knobs!.First(k => k.EnumValues is null && k.MaxValue > k.MinValue && tone.Amp.KnobValues.ContainsKey(k.Key));
        return (knob.Key, knob.MinValue, knob.MaxValue);
    }

    private static RsTone WithKnob(RsTone tone, string key, float value) =>
        tone with { Amp = tone.Amp with { KnobValues = new Dictionary<string, float>(tone.Amp.KnobValues) { [key] = value } } };

    private static async Task<TonelabSession> ReadySession(TonelabGameMock game)
    {
        var session = new TonelabSession(() => [game.ProcessId], null, null, TonelabSessionTests.Quick);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync());
        return session;
    }

    private TonelabTonePreview Preview(ITonelabSession session)
    {
        var preview = new TonelabTonePreview(session, TonePreviewTargets.Validate, new TonelabToneFiles(_dir), _scheduler, new FakeAppLogger());
        preview.Completed += (_, result) =>
        {
            lock (_results) _results.Add(result);
        };
        return preview;
    }

    private async Task Fire(TonelabTonePreview preview)
    {
        _scheduler.Elapse();
        await preview.LastTask;
    }
}
