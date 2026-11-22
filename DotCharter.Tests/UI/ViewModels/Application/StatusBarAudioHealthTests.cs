using System.Globalization;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Application;

namespace DotCharter.Tests.UI.ViewModels.Application;

/// <summary>
///     the status rail's audio readout is one health dot while audio is fine (the detail is its
///     tooltip) and prints in full only while something is wrong: no device, a lost device, or dropouts
///     in the last 30 seconds.
/// </summary>
public sealed class StatusBarAudioHealthTests
{
    [Fact]
    public void AHealthyEngine_IsOneDotWithTheDetailForATooltip()
    {
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48000, ActualBufferFrames = 128 };
        using var vm = Create(audio, out _);

        Assert.False(vm.HasAudioProblem);
        Assert.True(vm.AudioHealthy);
        Assert.Equal("Audio: 48 kHz · 128 fr · buffer 2.7 ms", vm.AudioStatus);
    }

    [Fact]
    public void NoDevice_IsAProblemAndSaysSo()
    {
        using var vm = Create(new FakeAudioPlaybackService { ActualBufferFrames = 0 }, out _);

        Assert.True(vm.HasAudioProblem);
        Assert.False(vm.AudioHealthy);
        Assert.Equal("Audio: not ready", vm.AudioStatus);
    }

    [Fact]
    public void ALostDevice_IsAProblemAndSaysSo()
    {
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48000, ActualBufferFrames = 128, IsDeviceLost = true };
        using var vm = Create(audio, out _);

        Assert.True(vm.HasAudioProblem);
        Assert.Equal("Audio: device lost", vm.AudioStatus);
    }

    [Fact]
    public void RecentDropouts_AreAProblemWithTheFullDetail()
    {
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48000, ActualBufferFrames = 128 };
        using var vm = Create(audio, out _);
        Assert.False(vm.HasAudioProblem);

        audio.DropoutCount = 2;
        audio.RaiseBufferChanged(128);

        Assert.True(vm.HasAudioProblem);
        Assert.Equal("Audio dropouts: 48 kHz · 128 fr · buffer 2.7 ms", vm.AudioStatus);
    }

    [Fact]
    public void TheTrackerForgetsADropoutAfterItsMemory()
    {
        var health = new AudioRailHealth();
        Assert.Equal(AudioRailProblem.None, health.Evaluate(48000, 128, false, 0, 1_000));
        Assert.Equal(AudioRailProblem.Dropouts, health.Evaluate(48000, 128, false, 1, 2_000));
        Assert.Equal(AudioRailProblem.Dropouts, health.Evaluate(48000, 128, false, 1, 2_000 + AudioRailHealth.DropoutMemoryMs - 1));
        Assert.Equal(AudioRailProblem.None, health.Evaluate(48000, 128, false, 1, 2_000 + AudioRailHealth.DropoutMemoryMs));
        // A new burst starts the memory again.
        Assert.Equal(AudioRailProblem.Dropouts, health.Evaluate(48000, 128, false, 2, 50_000));
    }

    [Fact]
    public void ADeviceErrorOutranksDropoutsAndNoDeviceOutranksHealthy()
    {
        var health = new AudioRailHealth();
        Assert.Equal(AudioRailProblem.DeviceLost, health.Evaluate(48000, 128, true, 3, 10));
        Assert.Equal(AudioRailProblem.NoDevice, health.Evaluate(0, 0, false, 3, 10));
    }

    private static StatusBarViewModel Create(FakeAudioPlaybackService audio, out StatusBarService service)
    {
        var dispatcher = new ImmediateDispatcher();
        service = new StatusBarService(dispatcher);
        return new StatusBarViewModel(service, new ProjectState(), audio, dispatcher, new StubProfiles(),
            new FakeFileStorageService(), FakeLocalization.English(CultureInfo.GetCultureInfo("en-US")), new FakeConfigurationService());
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private sealed class StubProfiles : IPlayerProfileService
    {
        private readonly PlayerProfile _active = new() { Id = Guid.NewGuid(), Name = "Test" };
        public IReadOnlyList<PlayerProfile> Profiles => [_active];
        public PlayerProfile ActiveProfile => _active;
        public event Action<PlayerProfile>? ActiveProfileChanged { add { } remove { } }
        public string GetDataDirectory(Guid id) => Path.GetTempPath();
        public void InvalidateCache() { }
        public PlayerProfile Create(string name, string? accentColor = null) => _active;
        public void Rename(Guid id, string newName) { }
        public void SetLeftyFlip(Guid id, bool lefty) { }
        public void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning) { }
        public bool SwitchTo(Guid id) => false;
        public bool Delete(Guid id) => false;
        public bool SetAvatar(Guid id, string sourceImagePath) => false;
        public string? GetAvatarPath(Guid id) => null;
        public bool ExportProfile(Guid id, string zipPath) => false;
        public PlayerProfile? ImportProfile(string zipPath) => null;
    }
}
