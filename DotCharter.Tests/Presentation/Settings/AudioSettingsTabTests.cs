using System.Text.RegularExpressions;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using static DotCharter.Tests.Presentation.Settings.GraphicsStubs;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     Settings ▸ Audio as a tab of the settings shell: its unsaved counter, its
///     search, and the key agreement between the search catalogue and the view.
///     The load / save / device behaviour has its own classes under
///     <c>UI/ViewModels/Settings/AudioSettings*Tests</c>.
/// </summary>
public sealed class AudioSettingsTabTests
{
    /// <summary>
    ///     Contract setters that are NOT settings, each with its reason. The
    ///     device rows persist as NAMES and land on their value when the probe
    ///     returns; the jack row mirrors <c>AsioInputChannelLeft</c> and the output
    ///     pair row mirrors <c>AsioOutputChannelLeft</c> / <c>Right</c>, which are
    ///     tracked; the disclosure is layout.
    /// </summary>
    private static readonly HashSet<string> NotSettings = new(StringComparer.Ordinal)
    {
        nameof(IAudioSettingsScreen.SelectedWasapiOutputIndex), nameof(IAudioSettingsScreen.SelectedAsioOutputIndex),
        nameof(IAudioSettingsScreen.SelectedWasapiInputIndex), nameof(IAudioSettingsScreen.SelectedAsioInputIndex),
        nameof(IAudioSettingsScreen.SelectedStreamingMirrorIndex), nameof(IAudioSettingsScreen.SelectedMidiOutputIndex),
        nameof(IAudioSettingsScreen.SelectedInputJackIndex), nameof(IAudioSettingsScreen.SelectedOutputPairIndex),
        nameof(IAudioSettingsScreen.IsAdvancedOpen)
    };

    private static AudioSettingsViewModel OpenedVm()
    {
        var config = new FakeConfigurationService();
        var engine = new FakeAudioPlaybackService();
        var vm = new AudioSettingsViewModel(
            engine, engine, config, new ProjectState(), new AudioSettingsWindows(new FakeWizard()), new GatedInlineDispatcher(), FakeLocalization.English());
        vm.Open();
        vm.DeviceProbe.Wait(TimeSpan.FromSeconds(60));
        return vm;
    }

    /// <summary>
    ///     The readouts say what the ENGINE runs. An ASIO device fixed at 48 kHz ignores a
    ///     44.1 kHz request, and the readout printed the request (2.9 ms) while the status bar
    ///     printed the engine (2.7 ms) on the same screen.
    /// </summary>
    [Fact]
    public async Task TheReadouts_ReportWhatTheEngineNegotiated_NotTheRequest()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.AudioSampleRate = 44100;
        var engine = new FakeAudioPlaybackService { ActiveSampleRate = 48000, ActualBufferFrames = 128 };
        var vm = new AudioSettingsViewModel(
            engine, engine, config, new ProjectState(), new AudioSettingsWindows(new FakeWizard()), new GatedInlineDispatcher(), FakeLocalization.English());
        vm.Open();
        await vm.DeviceProbe.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(AudioSettingsRules.RateLabel(48000), vm.ReadoutRate);
        Assert.Equal(AudioSettingsRules.LatencyLabel(128, 48000), vm.ReadoutLatency);
    }

    [Fact]
    public void AFreshlyOpenedTab_HasNothingUnsaved()
    {
        var vm = OpenedVm();

        Assert.Empty(vm.UnsavedSettings);
    }

    /// <summary>
    ///     Every setter on the contract either counts once or is named in
    ///     <see cref="NotSettings" /> with a reason. Derived by reflection, so a
    ///     new row added without its tracker entry fails here instead of reading
    ///     "Everything saved" while its edit is lost on leaving the tab.
    /// </summary>
    [Fact]
    public void EverySettableContractMember_CountsOnce_OrIsNamedAsNotASetting()
    {
        var settable = typeof(IAudioSettingsScreen).GetProperties()
            .Where(p => p.CanWrite && !NotSettings.Contains(p.Name))
            .ToList();
        Assert.True(settable.Count >= 15, $"only {settable.Count} settable members; the walk went vacuous");
        Assert.All(NotSettings, n => Assert.NotNull(typeof(IAudioSettingsScreen).GetProperty(n)));

        foreach (var property in settable)
        {
            var vm = OpenedVm();
            property.SetValue(vm, Different(property.PropertyType, property.GetValue(vm)));
            Assert.True(vm.UnsavedSettings.Count == 1,
                $"{property.Name}: {vm.UnsavedSettings.Count} unsaved change(s) after one edit");
        }
    }

    [Fact]
    public void Save_ClearsTheCount()
    {
        var vm = OpenedVm();
        vm.EnableHighProcessPriority = !vm.EnableHighProcessPriority;
        Assert.Single(vm.UnsavedSettings);

        ((ISettingsTab)vm).Save();

        Assert.Empty(vm.UnsavedSettings);
    }

    /// <summary>Reset reloads the persisted values — through the same Open a page reload runs.</summary>
    [Fact]
    public void Reset_PutsThePersistedValuesBack()
    {
        var vm = OpenedVm();
        var persisted = vm.OutOfProcessVst;
        vm.OutOfProcessVst = !persisted;

        ((ISettingsTab)vm).Reset();

        Assert.Equal(persisted, vm.OutOfProcessVst);
        Assert.Empty(vm.UnsavedSettings);
    }

    [Fact]
    public void Search_HidesWhatDoesNotMatch_AndOpensAdvancedForAMidiHit()
    {
        var vm = OpenedVm();
        var midi = vm.SearchGroups.SelectMany(g => g.Rows).Single(r => r.Key == "audio.midiDevice");

        vm.ApplySearch(midi.Label);

        Assert.DoesNotContain("audio.midiDevice", vm.HiddenKeys);
        Assert.Contains("audio.driver", vm.HiddenKeys);
        Assert.Contains(AudioSettingsCatalog.GroupKey(0), vm.HiddenKeys);
        Assert.True(vm.IsAdvancedOpen);

        vm.ApplySearch("");
        Assert.Empty(vm.HiddenKeys);
    }

    [Fact]
    public void TheSearchCatalogue_HasNoRawResourceKeys()
    {
        var rawKey = new Regex(@"^[A-Za-z]+_[A-Za-z0-9_]+$");
        foreach (var group in AudioSettingsCatalog.Groups(FakeLocalization.English()))
        {
            Assert.DoesNotMatch(rawKey, group.Title);
            Assert.DoesNotMatch(rawKey, group.Subtitle ?? "");
            foreach (var row in group.Rows)
            {
                Assert.DoesNotMatch(rawKey, row.Label);
                Assert.DoesNotMatch(rawKey, row.Description ?? "");
            }
        }
    }

    /// <summary>
    ///     The catalogue and the view name the same keys, both ways: a row the
    ///     view hides by a key the catalogue never emits can never be hidden,
    ///     and a catalogue row the view does not bind is a search hit that
    ///     filters nothing.
    /// </summary>
    [Fact]
    public void TheCatalogueAndTheView_NameTheSameKeys()
    {
        var axaml = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "AudioSettingsView.axaml")));
        var inView = Regex.Matches(axaml, @"ConverterParameter=""?(audio\.[A-Za-z0-9.]+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var groups = AudioSettingsCatalog.Groups(FakeLocalization.English());
        var inCatalogue = groups.SelectMany(g => g.Rows).Select(r => r.Key)
            .Concat(Enumerable.Range(0, groups.Count).Select(AudioSettingsCatalog.GroupKey))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(inCatalogue.Order(), inView.Order());
    }

    private static object Different(Type type, object? current) => type switch
    {
        _ when type == typeof(bool) => !(bool)current!,
        _ when type == typeof(int) => (int)current! + 1,
        _ when type == typeof(double) => (double)current! + 5,
        _ when type == typeof(string) => (string?)current == "ASIO" ? "WASAPI" : "ASIO",
        _ => throw new NotSupportedException(type.Name)
    };
}
