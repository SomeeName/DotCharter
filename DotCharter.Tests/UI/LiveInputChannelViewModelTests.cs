using System.Diagnostics;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig footer's guitar-input jack picker (2026-08-30: "when picking an
///     amp you cannot select the channel"). It must offer the device's jacks,
///     write the SAME config fields Settings ▸ Audio writes, re-apply the engine
///     through <c>ApplyAudioConfiguration</c> (the one writer of the live seat
///     baseline), follow the seat, and ship disabled with its reason where a
///     jack cannot be chosen.
/// </summary>
public sealed class LiveInputChannelViewModelTests
{
    private static (LiveInputChannelViewModel Vm, FakeAudioPlaybackService Audio, FakeConfigurationService Config) Build(
        Action<AppConfig>? configure = null, int jacks = 4)
    {
        var audio = new FakeAudioPlaybackService { InputDeviceChannelCount = jacks };
        var config = new FakeConfigurationService();
        config.AppConfig.EnableInput = true;
        config.AppConfig.AudioDriver = "ASIO";
        config.AppConfig.AsioInputMono = true;
        config.AppConfig.AsioInputChannelLeft = 0;
        config.AppConfig.AsioInputChannelRight = 1;
        configure?.Invoke(config.AppConfig);
        return (new LiveInputChannelViewModel(audio, config, FakeLocalization.English()), audio, config);
    }

    [Fact]
    public void Asio_OffersEveryJackOfTheDevice_WithTheConfiguredOneSelected()
    {
        var (vm, _, _) = Build(c => c.AsioInputChannelLeft = 1);
        Assert.True(vm.IsAvailable);
        Assert.Equal([0, 1, 2, 3], vm.Options.Select(o => o.Jack).ToArray());
        Assert.Equal(1, vm.Selected!.Jack);
        Assert.Equal(GuitarInputChannels.JackLabel(FakeLocalization.English(), 1), vm.Selected.Label);
    }

    [Fact]
    public void PickingAJack_SavesTheLeftBox_AndReappliesTheEngine()
    {
        var (vm, audio, config) = Build();
        vm.Selected = vm.Options.Single(o => o.Jack == 1);

        Assert.Equal(1, config.AppConfig.AsioInputChannelLeft);
        Assert.Equal(1, vm.AppliedCount);
        var applied = Assert.Single(audio.AppliedConfigs);
        Assert.Equal(1, applied.AsioInputChannelLeft);
        // It is the same engine path Settings takes, and the layout the engine
        // derives from it is the config's own — no second writer.
        Assert.Equal(LiveInputSeatLayout.FromInputConfig(config.AppConfig).Channels[0],
            LiveInputSeatLayout.FromInputConfig(applied).Channels[0]);
    }

    [Fact]
    public void PickingTheSameJack_DoesNothing()
    {
        var (vm, audio, _) = Build();
        vm.Selected = vm.Options.Single(o => o.Jack == 0);
        Assert.Empty(audio.AppliedConfigs);
        Assert.Equal(0, vm.AppliedCount);
    }

    [Fact]
    public void TwoChannelsOpen_PickingTheOtherBoxsJack_SwapsRatherThanCollapses()
    {
        var (vm, _, config) = Build(c => c.AsioInputMono = false);
        vm.Selected = vm.Options.Single(o => o.Jack == 1);
        Assert.Equal(1, config.AppConfig.AsioInputChannelLeft);
        Assert.Equal(0, config.AppConfig.AsioInputChannelRight);
    }

    [Fact]
    public void SeatOne_ReadsAndWritesTheRightBox()
    {
        var (vm, _, config) = Build(c =>
        {
            c.AsioInputMono = false;
            c.InputChannelsAreSeparateGuitars = true;
            c.AsioInputChannelRight = 2;
        });
        vm.Seat = 1;
        Assert.Equal(2, vm.Selected!.Jack);

        vm.Selected = vm.Options.Single(o => o.Jack == 3);
        Assert.Equal(3, config.AppConfig.AsioInputChannelRight);
        Assert.Equal(0, config.AppConfig.AsioInputChannelLeft);
    }

    [Fact]
    public void Wasapi_ShipsDisabledWithItsReason()
    {
        var (vm, audio, _) = Build(c => c.AudioDriver = "WASAPI");
        Assert.False(vm.IsAvailable);
        Assert.Empty(vm.Options);
        Assert.Equal(FakeLocalization.English()["Rig_Input_Unavailable_Wasapi"], vm.Hint);
        Assert.Empty(audio.AppliedConfigs);
    }

    [Fact]
    public void InputOff_ShipsDisabledWithItsReason()
    {
        var (vm, _, _) = Build(c => c.EnableInput = false);
        Assert.False(vm.IsAvailable);
        Assert.Equal(FakeLocalization.English()["Rig_Input_Unavailable_Off"], vm.Hint);
    }

    [Fact]
    public void NoServices_IsUnavailable_NotNull()
    {
        var vm = new LiveInputChannelViewModel(null, null, FakeLocalization.English());
        Assert.False(vm.IsAvailable);
        Assert.NotEmpty(vm.Hint);
    }

    [Fact]
    public void UnknownJackCount_FallsBackToAGenericList_ThatStillHoldsTheConfiguredJack()
    {
        var (vm, _, _) = Build(c => c.AsioInputChannelLeft = 11, jacks: 0);
        Assert.Equal(GuitarInputChannels.FallbackJackCount + 1, vm.Options.Count);
        Assert.Equal(11, vm.Selected!.Jack);
    }

    [Fact]
    public async Task WithADispatcher_TheDeviceCountLandsAfterThePost()
    {
        var audio = new FakeAudioPlaybackService { InputDeviceChannelCount = 2 };
        var config = new FakeConfigurationService();
        config.AppConfig.EnableInput = true;
        config.AppConfig.AudioDriver = "ASIO";
        var ui = new RecordingDispatcher();
        var vm = new LiveInputChannelViewModel(audio, config, FakeLocalization.English(), ui);
        // Synchronous half: the generic list, so the first frame is never blank.
        Assert.Equal(GuitarInputChannels.FallbackJackCount, vm.Options.Count);
        // A YIELDING poll under a DEADLOCK-DETECTOR cap. It was
        // `SpinWait.SpinUntil(…, 5 s)`, which was wrong twice over: five seconds
        // is a budget for work the view model hands to the thread pool, and a
        // hot spin starves the very pool thread it is waiting on — worst on the
        // loaded box where the budget was already tight. And the return value
        // was DISCARDED, so an expiry fell through to a no-op `Drain()` and
        // failed on `Expected 2, Actual 8`, blaming the device-count code for a
        // probe that simply had not been scheduled yet.
        var posted = await SettleAsync(() => !ui.Posted.IsEmpty);
        Assert.True(posted,
            "the device probe never posted its apply — the assertion below would blame the " +
            "device count for a callback that never ran.");

        ui.Drain();
        Assert.Equal(2, vm.Options.Count);
    }

    /// <summary>Condition-driven, with a cap that is a deadlock detector and not
    /// a budget: a green run leaves on its first or second turn and never reads
    /// it. Returns whether the condition was met, so the caller can say so.</summary>
    private static async Task<bool> SettleAsync(Func<bool> until)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (until()) return true;
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        return until();
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public System.Collections.Concurrent.ConcurrentQueue<Action> Posted { get; } = new();
        public bool CheckAccess() => true;
        public void Post(Action action) => Posted.Enqueue(action);
        public void PostNormal(Action action) => Posted.Enqueue(action);
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
        public void Drain() { while (Posted.TryDequeue(out var a)) a(); }
    }
}
