using System.Globalization;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Application;

namespace DotCharter.Tests.UI.ViewModels.Application;

/// <summary>
///     The status rail's audio readout and the running-task "+N more" suffix come from the
///     resx, not from string literals (#130): the German and Japanese rails printed
///     "Audio: not ready" next to "Gestoppt". Pinned with <see cref="FakeLocalization.KeysOnly" />
///     so the test names the KEY, not its wording.
/// </summary>
public sealed class StatusBarLocalizedReadoutTests
{
    [Fact]
    public void AnEngineThatIsNotReady_IsDescribedByTheResx()
    {
        var audio = new FakeAudioPlaybackService { ActualBufferFrames = 0 };
        using var vm = Create(audio, FakeLocalization.KeysOnly(), out _);

        Assert.Equal("StatusBar_Audio_NotReady", vm.AudioStatus);
    }

    [Fact]
    public void ARunningEngine_IsDescribedByTheResx()
    {
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48000, ActualBufferFrames = 256 };
        using var vm = Create(audio, FakeLocalization.KeysOnly(), out _);

        Assert.StartsWith("StatusBar_Audio_Fmt(", vm.AudioStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningEngine_ReadsAsBefore_InEnglish()
    {
        var audio = new FakeAudioPlaybackService { ActiveSampleRate = 48000, ActualBufferFrames = 256 };
        using var vm = Create(audio, FakeLocalization.English(CultureInfo.GetCultureInfo("en-US")), out _);

        Assert.Equal("Audio: 48 kHz · 256 fr · buffer 5.3 ms", vm.AudioStatus);
    }

    [Fact]
    public void MoreThanOneTask_AppendsTheResxCount()
    {
        using var vm = Create(new FakeAudioPlaybackService(), FakeLocalization.KeysOnly(), out var service);

        service.BeginTask("a", "Separating stems", indeterminate: true);
        Assert.Equal("Separating stems", vm.ActiveTaskLabel);

        service.BeginTask("b", "Scanning library", indeterminate: true);
        service.BeginTask("c", "Building preview", indeterminate: true);
        // The newest task leads the chip; the count is the rest.
        Assert.Equal("StatusBar_Task_MoreFmt(Building preview, 2)", vm.ActiveTaskLabel);
    }

    private static StatusBarViewModel Create(FakeAudioPlaybackService audio, FakeLocalization localization, out StatusBarService service)
    {
        var dispatcher = new ImmediateDispatcher();
        service = new StatusBarService(dispatcher);
        return new StatusBarViewModel(service, new ProjectState(), audio, dispatcher, new StubProfiles(),
            new FakeFileStorageService(), localization);
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
