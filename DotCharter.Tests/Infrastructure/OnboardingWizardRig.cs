using DotCharter.Localization;
using DotCharter.Tone.Audio.Midi;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Library;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Onboarding;
using DotCharter.Presentation.Screens.Onboarding;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Plugins;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     Builds an <see cref="OnboardingWizardViewModel"/> over in-memory fakes —
///     the SAME graph the app resolves (the real <see cref="AudioSettingsViewModel"/>
///     and a recording latency prompt, over the fake engine),
///     so a test drives the product's own commit paths rather than a stand-in.
///     Shared by the default-gate view-model tests and the headless smoke test.
/// </summary>
internal sealed class OnboardingWizardRig
{
    public FakeConfigurationService Config { get; } = new();
    public FakeAudioPlaybackService Engine { get; } = new();
    public FakeDspTonePresetService Presets { get; } = new();
    public StubProfiles Profiles { get; } = new();
    public StubLibrary Library { get; } = new();
    public FakeFileStorageService Storage { get; } = new();
    public FakeAppLogger Logger { get; } = new();
    public AudioSettingsViewModel AudioSettings { get; }
    public DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard Latency { get; } = new();
    public FakeNativeEngineProbe EngineProbe { get; }
    public FakeGpuRenderProbe GpuProbe { get; }
    public SystemCheckViewModel SystemCheck { get; }
    public OnboardingWizardViewModel Vm { get; }

    /// <summary>What the wizard turns the timeline with (the String order on its first page).</summary>
    public ProjectState State { get; } = new();

    public OnboardingWizardRig(string appVersion = "9.9.9", Func<PedalboardViewModel?>? pedalboard = null,
        FakeNativeEngineProbe? engineProbe = null, FakeGpuRenderProbe? gpuProbe = null, IUiDispatcher? systemCheckUi = null,
        IPackageFeatures? packageFeatures = null)
    {
        // The Welcome page COMMITS the language through the process-wide
        // LocalizationService.Instance (private ctor — there is no second one).
        // The config's default is "system", which on a German box would flip
        // every later test in the run to German. Pin the seeded config to the
        // language the test process already runs in.
        Config.AppConfig.LanguageCulture = "en-US";
        // The MIDI enumeration is a winmm P/Invoke over the machine's real
        // drivers. A view-model test must not touch the box's hardware at all —
        // and its cost swings under load, which is exactly what turned these
        // cases into intermittent failures. The fake engine answers instead.
        Engine.MidiOutputs = FakeMidiOutputs;
        AudioSettings = new AudioSettingsViewModel(
            Engine, Engine, Config, new ProjectState(), new AudioSettingsWindows(Latency), Ui, FakeLocalization.English());
        // The first page's system check, over probes that load no DLL and touch no GPU.
        EngineProbe = engineProbe ?? new FakeNativeEngineProbe();
        GpuProbe = gpuProbe ?? new FakeGpuRenderProbe();
        SystemCheck = new SystemCheckViewModel(EngineProbe, GpuProbe, LocalizationService.Instance, systemCheckUi ?? Ui, Logger);
        Vm = new OnboardingWizardViewModel(
            Config, AudioSettings, Latency, Profiles, Presets, Library, Storage,
            LocalizationService.Instance, Engine, Logger, SystemCheck, State, packageFeatures ?? PackageFeatures.All,
            () => appVersion, pedalboard);
    }

    /// <summary>What the wizard's MIDI dropdown sees. The shape winmm returns:
    /// row 0 is always the mapper, at device index -1.</summary>
    public static readonly IReadOnlyList<MidiOutputDeviceInfo> FakeMidiOutputs =
    [
        new(-1, "Default MIDI Mapper"),
        new(0, "Microsoft GS Wavetable Synth"),
    ];

    /// <summary>
    ///     Waits for the audio page's background device probe to finish.
    ///     <para>
    ///         <see cref="InlineDispatcher" /> runs the probe's marshalled
    ///         continuation on whatever thread finishes it, so without this the
    ///         device lists and their selected indexes are written by the POOL
    ///         while the test reads and saves them — a race that reads as a
    ///         flaky assertion. Call it after any step that entered the Audio or
    ///         Input page.
    ///     </para>
    /// </summary>
    public void SettleAudioProbe()
    {
        AudioSettings.DeviceProbe.GetAwaiter().GetResult();
        // The device probe's post starts the output-pair read on the pool as well, and that read
        // rebuilds the pair rows: a view bound before it lands races the rebuild.
        AudioSettings.OutputCountProbe.GetAwaiter().GetResult();
    }

    /// <summary>The rig's stand-in UI thread. Read collections the audio probe
    /// rebuilds through <see cref="InlineDispatcher.Read{T}" />.</summary>
    public InlineDispatcher Ui { get; } = new();

    /// <summary>
    ///     <c>Vm.Next()</c> <paramref name="steps" /> times, settling the audio
    ///     probe after each one. Use this rather than a bare loop for anything
    ///     that walks PAST the Audio page: leaving the pool thread inside
    ///     <c>AudioSettingsViewModel.Open</c>'s probe while the next step runs its
    ///     <c>Save</c> has the probe <c>Clear()</c>ing the device
    ///     <c>ObservableCollection</c>s that Save is indexing — an
    ///     <c>ArgumentOutOfRangeException</c> out of the middle of a wizard step,
    ///     under load, in a test that is about something else entirely.
    /// </summary>
    public void Advance(int steps = 1)
    {
        for (var i = 0; i < steps; i++)
        {
            Vm.Next();
            SettleAudioProbe();
        }
    }

    /// <summary>
    ///     Runs every post inline — the audio page's device probe lands
    ///     synchronously, so a test never races the thread pool.
    ///     <para>
    ///         <b>"Inline" is not "on the test's thread".</b>
    ///         <c>AudioSettingsViewModel.Open</c> enumerates devices in a
    ///         <c>Task.Run</c> and posts the apply back, so the apply runs inline
    ///         on the POOL — and it <c>Clear()</c>s and refills the bound
    ///         <c>ObservableCollection</c>s. A test that reads one of them
    ///         without waiting (deliberately, to check the synchronous half) was
    ///         therefore enumerating a list another thread was rebuilding:
    ///         <c>Open_SeedsTheRowsBeforeTheDeviceProbeReturns</c> failed
    ///         "Collection was modified" in a default-gate run on 2026-09-04.
    ///         Every dispatched action runs under <see cref="_gate" />, so
    ///         <see cref="Read{T}" /> is a legal read of that state; anything
    ///         that can wait should still use <see cref="SettleAudioProbe" />.
    ///     </para>
    /// </summary>
    internal sealed class InlineDispatcher : IUiDispatcher
    {
        private readonly Lock _gate = new();

        public bool CheckAccess() => true;
        public void Post(Action action) => Run(action);
        public void PostNormal(Action action) => Run(action);
        public Task InvokeAsync(Action action) { Run(action); return Task.CompletedTask; }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            lock (_gate) return Task.FromResult(func());
        }

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        /// <summary>Reads view-model state under the same gate the dispatched
        /// work holds, so an in-flight probe cannot mutate it mid-read.</summary>
        public T Read<T>(Func<T> read)
        {
            lock (_gate) return read();
        }

        private void Run(Action action)
        {
            lock (_gate) action();
        }
    }

    /// <summary>In-memory <see cref="IPlayerProfileService"/>: one seeded
    /// profile, Create appends, SwitchTo re-points the active one.</summary>
    internal sealed class StubProfiles : IPlayerProfileService
    {
        private readonly List<PlayerProfile> _profiles = [new() { Name = "Default" }];
        private PlayerProfile _active;

        public StubProfiles() => _active = _profiles[0];

        public IReadOnlyList<PlayerProfile> Profiles => _profiles;
        public PlayerProfile ActiveProfile => _active;
        public event Action<PlayerProfile>? ActiveProfileChanged;
        public List<Guid> SwitchedTo { get; } = [];

        public PlayerProfile Create(string name, string? accentColor = null)
        {
            var p = new PlayerProfile { Name = name };
            _profiles.Add(p);
            return p;
        }

        public void Rename(Guid id, string newName)
        {
            var p = _profiles.FirstOrDefault(x => x.Id == id);
            if (p is not null && !string.IsNullOrWhiteSpace(newName)) p.Name = newName.Trim();
        }
        public void SetLeftyFlip(Guid id, bool lefty) { }
        public void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning) { }

        public bool SwitchTo(Guid id)
        {
            var p = _profiles.FirstOrDefault(x => x.Id == id);
            if (p is null) return false;
            _active = p;
            SwitchedTo.Add(id);
            ActiveProfileChanged?.Invoke(p);
            return true;
        }

        public bool Delete(Guid id) => false;
        public string GetDataDirectory(Guid id) => Path.Combine(Path.GetTempPath(), id.ToString("N"));
        public bool SetAvatar(Guid id, string sourceImagePath) => false;
        public string? GetAvatarPath(Guid id) => null;
        public bool ExportProfile(Guid id, string zipPath) => false;
        public PlayerProfile? ImportProfile(string zipPath) => null;
        public void InvalidateCache() { }
    }

    /// <summary>In-memory <see cref="ILibraryService"/>: every scan returns
    /// <see cref="SongsToReport"/> entries and records the path it was asked for.</summary>
    internal sealed class StubLibrary : ILibraryService
    {
        public int SongsToReport { get; set; } = 3;
        public List<string> ScannedPaths { get; } = [];
        public Exception? ThrowOnScan { get; set; }

        private Task<IReadOnlyList<LibrarySongEntry>> Scan(string rootPath)
        {
            ScannedPaths.Add(rootPath);
            if (ThrowOnScan != null) throw ThrowOnScan;
            IReadOnlyList<LibrarySongEntry> songs = Enumerable.Range(0, SongsToReport)
                .Select(i => new LibrarySongEntry { Title = $"Song {i}", PackagePath = Path.Combine(rootPath, $"song{i}_p.psarc") })
                .ToList();
            return Task.FromResult(songs);
        }

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath) => Scan(rootPath);

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath, IProgress<LibraryScanProgress>? progress,
            CancellationToken cancellationToken) => Scan(rootPath);

        public Task<IReadOnlyList<LibrarySongEntry>> ScanAsync(string rootPath, IProgress<LibraryScanProgress>? progress,
            IProgress<IReadOnlyList<LibrarySongEntry>>? songFound, CancellationToken cancellationToken) => Scan(rootPath);

        public Task<LibraryLoadedSong> LoadSongAsync(LibrarySongEntry song) => throw new NotSupportedException();
        public Task<string> LoadCoverImageAsync(LibrarySongEntry song) => Task.FromResult(string.Empty);
    }
}
