using DotCharter.Analysis.Tuning;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Services.Audio.Tuning;
using DotCharter.Services.Session;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The A440 check's two doors (#821): the arrangement dialog shows the measured value beside the
///     authored one and Use fills the field only (Save is the write), and the post-attach offer
///     corrects every arrangement whose residual disagrees as ONE undo step, keeping each one's
///     whole semitones. Below the threshold or the confidence nothing is offered.
/// </summary>
public sealed class ReferencePitchCheckTests
{
    [Fact]
    public async Task TheDialog_ShowsMeasuredBesideAuthored_AndUseFillsTheFieldOnly()
    {
        var chart = new ChartEditHarness();
        var lead = chart.Project.Arrangements[0];
        var vm = new ArrangementSettingsViewModel(chart.Services, new StubService(15.2, 0.9));
        vm.Configure(new ArrangementSettingsRequest());

        Assert.False(vm.ReferencePitch.HasSuggestion);
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ReferencePitch.MeasureCommand).ExecuteAsync(null);

        Assert.True(vm.ReferencePitch.HasSuggestion);
        Assert.Contains("+15", vm.ReferencePitch.StatusText);
        Assert.Contains("443", vm.ReferencePitch.StatusText);
        Assert.Contains("+15", vm.ReferencePitch.ApplyLabel);

        vm.ReferencePitch.ApplyCommand.Execute(null);
        Assert.Equal(15, vm.CentOffset);
        Assert.Equal(0, lead.CentOffset);
        Assert.False(vm.ReferencePitch.HasSuggestion);

        vm.SaveCommand.Execute(null);
        Assert.Equal(15, lead.CentOffset);
        chart.History.Undo();
        Assert.Equal(0, lead.CentOffset);
    }

    [Theory]
    [InlineData(4.0, 0.9)]
    [InlineData(25.0, 0.1)]
    public async Task ASmallOrUnsureReading_OffersNothing(double cents, double confidence)
    {
        var chart = new ChartEditHarness();
        var vm = new ArrangementSettingsViewModel(chart.Services, new StubService(cents, confidence));
        vm.Configure(new ArrangementSettingsRequest());

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ReferencePitch.MeasureCommand).ExecuteAsync(null);

        Assert.False(vm.ReferencePitch.HasSuggestion);
        Assert.False(vm.ReferencePitch.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheOffer_CorrectsEveryArrangement_KeepingTheirSemitones_AsOneUndoStep()
    {
        var chart = new ChartEditHarness();
        var bass = new Arrangement { Name = "Bass", ArrangementType = ArrangementType.Bass, CentOffset = -1200 };
        chart.Project.Arrangements.Add(bass);
        var lead = chart.Project.Arrangements[0];
        var offer = new ReferencePitchOffer(new StubService(-31, 0.8), chart.Services);

        Assert.True(await offer.OfferAsync(TestContext.Current.CancellationToken));
        var action = Assert.IsType<StatusMessageAction>(chart.State.MainDisplayAction);
        action.Run();

        Assert.Equal(-31, lead.CentOffset);
        Assert.Equal(-1231, bass.CentOffset);
        chart.History.Undo();
        Assert.Equal(0, lead.CentOffset);
        Assert.Equal(-1200, bass.CentOffset);
    }

    [Fact]
    public async Task TheOffer_IsDropped_WhenTheProjectChangedDuringTheMeasurement()
    {
        var chart = new ChartEditHarness();
        var gate = new TaskCompletionSource<ReferencePitchCheck>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offer = new ReferencePitchOffer(new GatedService(gate.Task), chart.Services);
        var original = chart.Project;

        var pending = offer.OfferAsync(TestContext.Current.CancellationToken);
        chart.State.ActiveProject = new ChartProject();
        gate.SetResult(ReferencePitchCheck.From(new ReferencePitchEstimate(-31, 0.8, 500, 60), 0, RecordingAlignmentSource.Mix));

        Assert.False(await pending);
        Assert.Null(chart.State.MainDisplayAction);
        Assert.Equal(0, offer.Apply(original, -31));
        Assert.Equal(0, original.Arrangements[0].CentOffset);
    }

    [Fact]
    public async Task TheOffer_StaysAway_WhenTheChartAlreadyMatches()
    {
        var chart = new ChartEditHarness();
        var offer = new ReferencePitchOffer(new StubService(3, 0.95), chart.Services);

        Assert.False(await offer.OfferAsync(TestContext.Current.CancellationToken));
        Assert.Null(chart.State.MainDisplayAction);
    }

    [Fact]
    public void TheInstrumentsOwnStem_IsPicked_AndNoStemMeansTheMix()
    {
        string[] stems = [@"E:\Song\stems\guitar.opus", @"E:\Song\stems\bass.opus", @"E:\Song\stems\other.opus"];

        Assert.EndsWith("bass.opus", ReferencePitchService.PickStem(stems, bass: true));
        Assert.EndsWith("guitar.opus", ReferencePitchService.PickStem(stems, bass: false));
        Assert.Null(ReferencePitchService.PickStem([@"E:\Song\stems\other.opus"], bass: false));
    }

    [Fact]
    public async Task AttachingFromYoutube_OffersTheSameCorrection()
    {
        var chart = new ChartEditHarness();
        var offer = new ReferencePitchOffer(new StubService(-31, 0.8), chart.Services);
        var (session, _) = KeepingSession.Create();
        var launcher = new YoutubeAudioLauncher(new AnsweringDialogs("yt.mp3"), chart.State, session, FakeLocalization.English(),
            new FakeAppLogger(), offer);

        Assert.True(await launcher.OpenAsync());

        Assert.IsType<StatusMessageAction>(chart.State.MainDisplayAction);
    }

    [Fact]
    public void OpeningASongPackage_OffersTheSameCorrection()
    {
        var chart = new ChartEditHarness();
        var offer = new ReferencePitchOffer(new StubService(-31, 0.8), chart.Services);
        var session = RaisingSession.Create(out var raise);
        offer.Watch(session);

        raise();

        Assert.IsType<StatusMessageAction>(chart.State.MainDisplayAction);
    }

    /// <summary>A session whose <c>SongPackageOpened</c> the test raises by hand.</summary>
    private class RaisingSession : RecordingProxy
    {
        private Action? _handler;

        public static IProjectSessionService Create(out Action raise)
        {
            var proxy = Create<IProjectSessionService, RaisingSession>();
            var self = (RaisingSession)(object)proxy;
            raise = () => self._handler?.Invoke();
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "add_" + nameof(IProjectSessionService.SongPackageOpened)) return base.Invoke(method, args);
            _handler += (Action)args![0]!;
            return null;
        }
    }

    private sealed class AnsweringDialogs(string answer) : IDialogs
    {
        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed { add { } remove { } }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult> => Task.FromResult((TResult?)(object?)answer);

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

        public bool CloseTop() => false;
    }

    /// <summary>A session whose attach succeeds; every other call answers the default.</summary>
    private class KeepingSession : RecordingProxy
    {
        public static (IProjectSessionService Proxy, RecordingProxy Recorder) Create()
        {
            var proxy = Create<IProjectSessionService, KeepingSession>();
            return (proxy, (RecordingProxy)(object)proxy);
        }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(IProjectSessionService.AttachAudioAsync) ? Task.FromResult(true) : base.Invoke(method, args);
    }

    private sealed class GatedService(Task<ReferencePitchCheck> result) : IReferencePitchService
    {
        public Task<ReferencePitchCheck> MeasureAsync(bool bass, double authoredCentOffset, CancellationToken cancellationToken) => result;
    }

    private sealed class StubService(double cents, double confidence) : IReferencePitchService
    {
        public Task<ReferencePitchCheck> MeasureAsync(bool bass, double authoredCentOffset, CancellationToken cancellationToken) =>
            Task.FromResult(ReferencePitchCheck.From(new ReferencePitchEstimate(cents, confidence, 500, 60), authoredCentOffset,
                RecordingAlignmentSource.Mix));
    }
}
