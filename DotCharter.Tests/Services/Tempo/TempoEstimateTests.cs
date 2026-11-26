using DotCharter.Services.Editing.Tempo.Estimate;
using DotCharter.Tests.Analysis;
using JuceSharp.AudioIo;

namespace DotCharter.Tests.Services.Tempo;

public sealed class TempoEstimateTests
{
    [Fact]
    public void CancellationWinsEvenWhenTheClipIsTooShortToEstimate()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => TempoEstimateService.EstimateSamples(
            new float[1], 44100, null, null, cancelled.Token));
    }
    private static List<BeatMarker> Beats(int count, double bpm = 120, params int[] anchors)
    {
        var list = new List<BeatMarker>();
        for (var i = 0; i < count; i++)
        {
            list.Add(new BeatMarker
            {
                Time = i * 60000.0 / bpm,
                Bpm = bpm,
                IsMeasure = i % 4 == 0,
                IsAnchor = anchors.Contains(i)
            });
        }

        return list;
    }

    private static TempoService Tempo() => new(new FakeAppLogger());

    private static float[] Clicks(double seconds, double bpm)
    {
        var buffer = new float[(int)(seconds * AnalysisTestSignals.SampleRate)];
        var interval = 60.0 / bpm;
        var index = 0;
        for (var t = 0.5; t < seconds - interval; t += interval, index++)
            AnalysisTestSignals.AddClick(buffer, t, index % 4 == 0 ? 0.9f : 0.45f);
        return buffer;
    }

    [Fact]
    public void TheWholeSong_SetsTheTempoFromTheFirstBeat_ToTheEnd()
    {
        var beats = Beats(16);

        var plan = TempoEstimateApplier.Plan(Tempo(), beats, 150, 0)!;

        Assert.All(plan.Take(15), b => Assert.Equal(150, b.Bpm));
        Assert.Equal(400.0, plan[1].Time, 3);
        Assert.Equal(120, beats[0].Bpm);
        Assert.Equal(500.0, beats[1].Time, 3);
    }

    [Fact]
    public void TheTempo_RunsUpToTheNextAnchor_AndNoFurther()
    {
        var beats = Beats(16, 120, 8);

        var plan = TempoEstimateApplier.Plan(Tempo(), beats, 240, 0)!;

        Assert.Equal(240, plan[7].Bpm);
        Assert.Equal(beats[8].Time, plan[8].Time, 3);
        Assert.Equal(120, plan[8].Bpm);
        Assert.Equal(120, plan[12].Bpm);
    }

    [Fact]
    public void AMarkedPart_StartsOnTheBeatNearestItsStart()
    {
        var beats = Beats(16);

        Assert.Equal(6, TempoEstimateApplier.StartBeatIndex(beats, 3010));
        var plan = TempoEstimateApplier.Plan(Tempo(), beats, 60, 3010)!;

        Assert.Equal(120, plan[5].Bpm);
        Assert.Equal(60, plan[6].Bpm);
        Assert.Equal(beats[6].Time, plan[6].Time, 3);
        Assert.Equal(beats[6].Time + 1000, plan[7].Time, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void ANonsenseTempo_PlansNothing(double bpm) => Assert.Null(TempoEstimateApplier.Plan(Tempo(), Beats(8), bpm, 0));

    [Theory]
    [InlineData(3, 2)]
    [InlineData(5, 4)]
    public void ASlowerTempoThatReachesOrPassesTheNextAnchor_IsRejected(int count, int anchor)
    {
        var beats = Beats(count, 120, anchor);
        var times = beats.Select(b => b.Time).ToArray();

        var plan = TempoEstimateApplier.Plan(Tempo(), beats, 60, 0);

        Assert.Null(plan);
        Assert.Equal(times, beats.Select(b => b.Time).ToArray());
        Assert.All(beats, b => Assert.Equal(120, b.Bpm));
        Assert.True(beats[anchor].IsAnchor);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void APlanWithNonFiniteBeatTimes_IsRejected(double time)
    {
        var beats = Beats(3);
        beats[0].Time = time;

        Assert.Null(TempoEstimateApplier.Plan(Tempo(), beats, 120, 0));
    }

    [Fact]
    public void TheAlternatives_AreHalfAndDouble_AndDropWhenOutOfRange()
    {
        var normal = TempoEstimate.From(128.004, 0, 50);
        Assert.Equal(128.0, normal.Bpm);
        Assert.Equal(64.0, normal.Half);
        Assert.Equal(256.0, normal.Double);

        Assert.Null(TempoEstimate.From(50, 0, 10).Half);
        Assert.Null(TempoEstimate.From(250, 0, 10).Double);
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(100.0)]
    [InlineData(140.0)]
    [InlineData(160.0)]
    [InlineData(200.0)]
    public void ThePureEstimate_ReadsAClickTrack(double bpm)
    {
        var estimate = TempoEstimateService.EstimateSamples(Clicks(30, bpm), AnalysisTestSignals.SampleRate, null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(estimate);
        Assert.InRange(estimate.Bpm, bpm - 1, bpm + 1);
    }

    [Fact]
    public void OnlyTheMarkedPart_IsListenedTo()
    {
        var first = Clicks(20, 100);
        var second = Clicks(20, 150);
        var both = first.Concat(second).ToArray();

        var late = TempoEstimateService.EstimateSamples(both, AnalysisTestSignals.SampleRate, 21000, 40000, TestContext.Current.CancellationToken)!;
        var early = TempoEstimateService.EstimateSamples(both, AnalysisTestSignals.SampleRate, 0, 19500, TestContext.Current.CancellationToken)!;

        Assert.InRange(late.Bpm, 149, 151);
        Assert.InRange(early.Bpm, 99, 101);
        Assert.Equal(21000, late.FromMs);
    }

    [Fact]
    public void ASliverOrSilence_FindsNothing()
    {
        Assert.Null(TempoEstimateService.EstimateSamples(Clicks(30, 120), AnalysisTestSignals.SampleRate, 1000, 2000, TestContext.Current.CancellationToken));
        Assert.Null(TempoEstimateService.EstimateSamples(new float[30 * AnalysisTestSignals.SampleRate], AnalysisTestSignals.SampleRate, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoAudioFile_IsReportedNotThrown()
    {
        var service = new TempoEstimateService(new AudioTempoEstimateStore(), new FakeAppLogger());

        var outcome = await service.EstimateAsync(null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(TempoEstimateFailure.NoAudio, outcome.Failure);
        Assert.Null(outcome.Estimate);
    }

    [Fact]
    public async Task AWholeSongEstimate_IsRememberedForValidation_APartIsNot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dc-tempo-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "song.wav");
            WavIo.WriteFloat32(path, new AudioBuffer(Clicks(30, 120), AnalysisTestSignals.SampleRate, 1));
            var store = new AudioTempoEstimateStore();
            var service = new TempoEstimateService(store, new FakeAppLogger());

            await service.EstimateAsync(path, 0, 20_000, TestContext.Current.CancellationToken);
            Assert.Null(store.Find("song.wav"));

            var outcome = await service.EstimateAsync(path, null, null, TestContext.Current.CancellationToken);
            Assert.NotNull(outcome.Estimate);
            Assert.Equal(outcome.Estimate.Bpm, store.Find("song.wav")?.Bpm);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
