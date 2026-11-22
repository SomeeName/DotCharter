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
    private TonelabToneFiles? _lastFiles;

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

        Assert.Equal(["status", "tone apply-file " + Path.Combine(_dir, _lastFiles!.FilePrefix + "1.json")], game.Commands());
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
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        game.ReplyAsync = async (line, token) =>
        {
            if (line.StartsWith("tone knob ", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await reply.WaitAsync(token);
            }
            return "ok";
        };
        preview.Apply(WithKnob(Tone(), knob, max));
        _scheduler.Elapse();
        var inFlight = preview.LastTask;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

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
        Assert.Equal([_lastFiles!.FilePrefix + "2.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Stop_DuringAnApplyReply_RestoresAfterTheApply()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var game = new TonelabGameMock
        {
            ReplyAsync = async (line, token) =>
            {
                if (line == "status") return "ok ready v1 48000 128 0";
                if (line == "tone restore") return "ok restored";
                entered.TrySetResult();
                await reply.Task.WaitAsync(token);
                return "ok applied tl_tone";
            }
        };
        using var session = await ReadySession(game);
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var applying = preview.LastTask;
        Assert.True(preview.Stop());
        var restoring = preview.LastTask;
        try
        {
            Assert.DoesNotContain("tone restore", game.Commands());
            Assert.False(restoring.IsCompleted);
            Assert.Single(Directory.GetFiles(_dir));
        }
        finally
        {
            reply.TrySetResult();
        }

        await Task.WhenAll(applying, restoring).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.Equal("tone restore", game.Commands()[^1]);
        Assert.Equal(3, game.Commands().Length);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.False(preview.IsActive);
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
        using var session = new TonelabSession(() => [game.ProcessId], insert, null, TonelabSessionTests.Quick, time: TonelabSessionTests.ReplyClock);
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
        using var session = new TonelabSession(() => [], null, null, TonelabSessionTests.Quick, time: TonelabSessionTests.ReplyClock);
        using var preview = Preview(session);

        Assert.False(preview.IsAvailable);
        preview.Apply(Tone());
        Assert.False(preview.IsActive);
        Assert.Equal(0, _scheduler.Pending);
    }

    [Fact]
    public async Task StopAndApplyDuringAnOutstandingReply_SendsTheNewGenerationAfterRestore()
    {
        var reply = new TaskCompletionSource<TonelabResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready, ApplyReply = () => reply.Task };
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        var oldFlush = preview.LastTask;
        Assert.Single(session.AppliedFiles);
        Assert.True(preview.Stop());
        var restore = preview.LastTask;
        preview.Apply(Tone() with { VolumeDb = -7 });
        _scheduler.Elapse();
        session.ApplyReply = null;
        reply.SetResult(new TonelabResult(TonelabOutcome.Ok));
        await oldFlush.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await restore.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(2, session.AppliedFiles.Count);
        Assert.Equal(1, session.Restores);
        Assert.True(session.AudioRouted);
        Assert.True(preview.Stop());
        await preview.LastTask;
    }

    [Fact]
    public void TwoFileOwnersInOneProcess_NeverShareOrReleaseEachOthersFile()
    {
        using var first = new TonelabToneFiles(_dir);
        using var second = new TonelabToneFiles(_dir);

        var a = first.Write("{\"a\":1}");
        var b = second.Write("{\"b\":2}");
        Assert.NotEqual(a, b);
        Assert.Equal("{\"a\":1}", File.ReadAllText(a));

        first.ReleaseAllBut(null);
        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
        Assert.Equal("{\"b\":2}", File.ReadAllText(b));
    }

    [Fact]
    public async Task TwoPreviews_StoppingOne_LeavesTheOthersToneInTheGame_AndTheLastRestores()
    {
        using var game = new TonelabGameMock();
        using var session = await ReadySession(game);
        using var designer = Preview(session);
        using var editor = Preview(session);
        designer.Apply(Tone());
        await Fire(designer);
        editor.Apply(Tone() with { VolumeDb = Tone().VolumeDb - 1 });
        await Fire(editor);

        Assert.True(editor.Stop());
        await editor.LastTask;
        Assert.DoesNotContain("tone restore", game.Commands());
        Assert.True(session.IsRoutingAudio);
        Assert.True(designer.IsActive);

        Assert.True(designer.Stop());
        await designer.LastTask;
        Assert.Equal("tone restore", game.Commands()[^1]);
        Assert.Single(game.Commands(), c => c == "tone restore");
        Assert.False(session.IsRoutingAudio);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PendingNextOwner_PreventsRestore_AndFailureReleasesTheLastClaim(bool fail, bool dispose)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = "original game tone";
        var applies = 0;
        using var game = new TonelabGameMock
        {
            ReplyAsync = async (command, token) =>
            {
                if (command == "status") return "ok ready v1 48000 128 0";
                if (command == "tone restore")
                {
                    played = "original game tone";
                    return "ok restored";
                }

                Assert.StartsWith("tone apply-file ", command, StringComparison.Ordinal);
                if (++applies == 2)
                {
                    entered.TrySetResult();
                    await BoundedWait.ForAsync(reply.Task, "release the second owner's apply reply", token);
                    if (fail) return "err unknown-gear Amp_Declined";
                }

                played = File.ReadAllText(command["tone apply-file ".Length..]);
                return "ok applied tl_tone";
            }
        };
        using var session = await BoundedWait.ForAsync(ReadySession(game), "ready mock game session", TestContext.Current.CancellationToken);
        using var first = Preview(session);
        using var second = Preview(session);
        first.Apply(Tone());
        _scheduler.Elapse();
        await BoundedWait.ForAsync(first.LastTask, "the first owner's accepted tone", TestContext.Current.CancellationToken);
        var next = Tone() with { VolumeDb = -7 };
        second.Apply(next);
        _scheduler.Elapse();
        var applying = second.LastTask;
        await BoundedWait.ForAsync(entered.Task, "the second owner's pending apply", TestContext.Current.CancellationToken);
        if (dispose) first.Dispose();
        else Assert.True(first.Stop());
        var stopping = first.LastTask;
        try
        {
            Assert.DoesNotContain("tone restore", game.Commands());
        }
        finally
        {
            reply.TrySetResult();
            await BoundedWait.ForAsync(Task.WhenAll(applying, stopping), "pending apply and previous owner stop", TestContext.Current.CancellationToken);
        }

        if (fail)
        {
            Assert.False(second.IsActive);
            Assert.False(session.IsRoutingAudio);
            Assert.False(second.Stop());
            using var vacant = session.TryLeaseAudio();
            Assert.NotNull(vacant);
        }
        else
        {
            Assert.DoesNotContain("tone restore", game.Commands());
            Assert.Equal(RsToneJsonWriter.Write(next), played);
            Assert.True(second.IsActive);
            Assert.True(second.Stop());
            await BoundedWait.ForAsync(second.LastTask, "the final owner's restore", TestContext.Current.CancellationToken);
        }

        Assert.Single(game.Commands(), c => c == "tone restore");
        Assert.Equal("original game tone", played);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ReturningOwner_ResendsItsWholeTone_AndConcurrentStopsRestoreOnce()
    {
        using var game = new TonelabGameMock();
        using var session = await BoundedWait.ForAsync(ReadySession(game), "ready mock game session", TestContext.Current.CancellationToken);
        using var first = Preview(session);
        using var second = Preview(session);
        first.Apply(Tone());
        _scheduler.Elapse();
        await BoundedWait.ForAsync(first.LastTask, "first accepted preview", TestContext.Current.CancellationToken);
        second.Apply(Tone() with { VolumeDb = -7 });
        _scheduler.Elapse();
        await BoundedWait.ForAsync(second.LastTask, "second accepted preview", TestContext.Current.CancellationToken);
        first.Apply(Tone());
        _scheduler.Elapse();
        await BoundedWait.ForAsync(first.LastTask, "returning owner's whole tone", TestContext.Current.CancellationToken);
        Assert.Equal(3, game.AppliedBodies.Count);
        Assert.Equal(RsToneJsonWriter.Write(Tone()), game.AppliedBodies.Last());

        Assert.True(first.Stop());
        var stoppingFirst = first.LastTask;
        Assert.True(second.Stop());
        await BoundedWait.ForAsync(Task.WhenAll(stoppingFirst, second.LastTask), "both owners stopping", TestContext.Current.CancellationToken);
        Assert.Single(game.Commands(), c => c == "tone restore");
        Assert.False(session.IsRoutingAudio);
    }

    [Theory]
    [InlineData(TonelabOutcome.UnknownGear)]
    [InlineData(TonelabOutcome.Timeout)]
    [InlineData(TonelabOutcome.Malformed)]
    public async Task FailedEdit_ReleasesItsRoute_AndRestoresTheSharedSnapshot(TonelabOutcome outcome)
    {
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready };
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        await BoundedWait.ForAsync(preview.LastTask, "initial accepted preview", TestContext.Current.CancellationToken);
        session.ApplyReply = () => Task.FromResult(new TonelabResult(outcome, "failed edit"));
        preview.Apply(Tone() with { VolumeDb = -7 });
        _scheduler.Elapse();
        await BoundedWait.ForAsync(preview.LastTask, "failed edit cleanup", TestContext.Current.CancellationToken);
        Assert.False(preview.IsActive);
        Assert.False(session.IsRoutingAudio);
        Assert.Equal(1, session.Restores);
        Assert.False(preview.Stop());
        Assert.Equal(1, session.Restores);
        Assert.Equal(0, session.Quits);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData(TonelabOutcome.UnknownGear, 0)]
    [InlineData(TonelabOutcome.Timeout, 1)]
    [InlineData(TonelabOutcome.Malformed, 1)]
    public async Task FailedFirstApply_LeavesNoOwner_AndCanBeRetried(TonelabOutcome outcome, int restores)
    {
        var session = new FakeTonelabSession
        {
            State = TonelabSessionState.Ready,
            ApplyReply = () => Task.FromResult(new TonelabResult(outcome, "failed first apply"))
        };
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        await BoundedWait.ForAsync(preview.LastTask, "failed first apply cleanup", TestContext.Current.CancellationToken);
        Assert.False(preview.IsActive);
        Assert.False(session.IsRoutingAudio);
        Assert.Equal(restores, session.Restores);
        session.ApplyReply = null;
        preview.Apply(Tone());
        _scheduler.Elapse();
        await BoundedWait.ForAsync(preview.LastTask, "retry after released owner", TestContext.Current.CancellationToken);
        Assert.True(preview.IsActive);
        Assert.True(preview.Stop());
        await BoundedWait.ForAsync(preview.LastTask, "retried owner's restore", TestContext.Current.CancellationToken);
        Assert.Equal(restores + 1, session.Restores);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionLoss_DropsAcceptedOrPendingOwners_AndLateRepliesCannotRoute(bool pending)
    {
        var reply = new TaskCompletionSource<TonelabResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeTonelabSession { State = TonelabSessionState.Ready };
        if (pending) session.ApplyReply = () => reply.Task;
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        var applying = preview.LastTask;
        Assert.Single(session.AppliedFiles);
        session.Change(TonelabSessionState.Disconnected);
        var cleanup = preview.LastTask;
        Assert.False(preview.IsActive);
        session.Change(TonelabSessionState.Ready);
        reply.TrySetResult(new TonelabResult(TonelabOutcome.Ok));
        await BoundedWait.ForAsync(Task.WhenAll(applying, cleanup), "lost-session reply and cleanup", TestContext.Current.CancellationToken);
        Assert.False(preview.IsActive);
        Assert.False(session.IsRoutingAudio);
        Assert.Equal(0, session.Restores);
        Assert.Equal(0, session.Quits);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task PendingPreview_InterruptsAnOfflineLease_AndWaitsUntilItsRestoreTurnEnds()
    {
        using var game = new TonelabGameMock();
        using var session = await BoundedWait.ForAsync(ReadySession(game), "ready mock game session", TestContext.Current.CancellationToken);
        using var offline = session.TryLeaseAudio();
        Assert.NotNull(offline);
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        var applying = preview.LastTask;
        Assert.True(offline.Interrupted);
        Assert.False(applying.IsCompleted);
        Assert.Equal(["status"], game.Commands());
        Assert.Null(session.TryLeaseAudio());
        offline.Dispose();
        await BoundedWait.ForAsync(applying, "preview after offline lease release", TestContext.Current.CancellationToken);
        Assert.True(preview.IsActive);
        Assert.True(preview.Stop());
        await BoundedWait.ForAsync(preview.LastTask, "post-render preview restore", TestContext.Current.CancellationToken);
        Assert.Single(game.Commands(), c => c == "tone restore");
        using var available = session.TryLeaseAudio();
        Assert.NotNull(available);
    }

    [Fact]
    public async Task InvalidEdit_StopsItsAcceptedOwnerWithoutQuittingTheGame()
    {
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var game = new TonelabGameMock();
        using var session = await BoundedWait.ForAsync(ReadySession(game), "ready invalid-edit test session", TestContext.Current.CancellationToken);
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        await BoundedWait.ForAsync(preview.LastTask, "accepted tone before invalid edit", TestContext.Current.CancellationToken);
        game.ReplyAsync = async (command, token) =>
        {
            Assert.Equal("tone restore", command);
            restoring.TrySetResult();
            await BoundedWait.ForAsync(reply.Task, "invalid-edit restore reply", token);
            return "ok restored";
        };
        preview.Apply(Tone() with { Amp = new RsAmp("Amp_Nope", "Amp", new Dictionary<string, float>()) });
        try
        {
            await BoundedWait.ForAsync(restoring.Task, "invalid edit starts restore", TestContext.Current.CancellationToken);
            Assert.Equal(TonelabOutcome.Invalid, _results[^1].Outcome);
        }
        finally
        {
            reply.TrySetResult();
            await BoundedWait.ForAsync(preview.LastTask, "invalid edit releases its owner", TestContext.Current.CancellationToken);
        }

        Assert.False(preview.IsActive);
        Assert.False(session.IsRoutingAudio);
        Assert.Single(game.Commands(), c => c == "tone restore");
        Assert.Equal(TonelabOutcome.Invalid, _results[^1].Outcome);
        Assert.DoesNotContain("quit", game.Commands());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedStopOrDispose_CancelsAWriteThatHasNotReachedTheGame(bool dispose)
    {
        using var game = new TonelabGameMock();
        using var session = await BoundedWait.ForAsync(ReadySession(game), "ready queued-stop test session", TestContext.Current.CancellationToken);
        using var offline = session.TryLeaseAudio();
        Assert.NotNull(offline);
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        var applying = preview.LastTask;
        Assert.False(applying.IsCompleted);
        if (dispose) preview.Dispose();
        else Assert.True(preview.Stop());
        var stopping = preview.LastTask;
        offline.Dispose();
        await BoundedWait.ForAsync(Task.WhenAll(applying, stopping), "canceled queued preview and stop", TestContext.Current.CancellationToken);
        Assert.Equal(["status"], game.Commands());
        Assert.False(preview.IsActive);
        Assert.False(session.IsRoutingAudio);
        using var available = session.TryLeaseAudio();
        Assert.NotNull(available);
    }

    [Fact]
    public async Task GameExit_ReleasesAcceptedPreviewOwnershipWithoutRestoreOrQuit()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var game = new TonelabGameMock();
        using var session = new TonelabSession(() => [game.ProcessId], null, null, TonelabSessionTests.Quick, time: TonelabSessionTests.ReplyClock)
        {
            WaitForExit = (_, token) => BoundedWait.ForAsync(exited.Task, "fake game exit", token)
        };
        Assert.Equal(TonelabSessionState.Ready,
            await BoundedWait.ForAsync(session.ProbeAsync(TestContext.Current.CancellationToken), "ready exit test session", TestContext.Current.CancellationToken));
        using var preview = Preview(session);
        preview.Apply(Tone());
        _scheduler.Elapse();
        await BoundedWait.ForAsync(preview.LastTask, "accepted tone before game exit", TestContext.Current.CancellationToken);
        exited.SetResult();
        await BoundedWait.ForAsync(session.ExitWatch, "session observes game exit", TestContext.Current.CancellationToken);
        await BoundedWait.ForAsync(preview.LastTask, "preview cleans up after game exit", TestContext.Current.CancellationToken);
        Assert.False(preview.IsActive);
        Assert.False(session.IsRoutingAudio);
        Assert.DoesNotContain("tone restore", game.Commands());
        Assert.DoesNotContain("quit", game.Commands());
        Assert.Empty(Directory.GetFiles(_dir));
        using var available = session.TryLeaseAudio();
        Assert.NotNull(available);
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
        var session = new TonelabSession(() => [game.ProcessId], null, null, TonelabSessionTests.Quick, time: TonelabSessionTests.ReplyClock);
        Assert.Equal(TonelabSessionState.Ready, await session.ProbeAsync());
        return session;
    }

    private TonelabTonePreview Preview(ITonelabSession session)
    {
        _lastFiles = new TonelabToneFiles(_dir);
        var preview = new TonelabTonePreview(session, TonePreviewTargets.Validate, _lastFiles, _scheduler, new FakeAppLogger());
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
