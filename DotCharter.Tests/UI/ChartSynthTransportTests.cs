using DotCharter.Services.Audio.ChartSynth;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI;

/// <summary>
///     The two chart-synth toggles (editor Mixer ▸ MIDI synth, Play Mode Amp/FX)
///     DRIVE the service — asserted against the service, never against the
///     toggle's own property — and a capability the service cannot honour reads
///     as unavailable instead of live.
/// </summary>
public sealed class ChartSynthTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotcharter-synth-transport-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>The app's wiring in miniature: a profile registry, the instrument
    /// coordinator over it, and a transport whose dispatcher runs inline.</summary>
    private (TransportViewModel Transport, ChartSynthInstrumentCoordinator Instruments, PlayerProfileService Profiles) Wired(
        FakeChartSynthAudio audio, FakeConfigurationService config, DotCharter.Localization.ILocalizationService? loc = null)
    {
        Directory.CreateDirectory(_root);
        var profiles = new PlayerProfileService(FakeLocalization.English(), filePath: Path.Combine(_root, "player-profiles.json"));
        var instruments = new ChartSynthInstrumentCoordinator(audio, profiles, new ProfileChartSynthInstrumentStore(profiles),
            config, new SongToneEditStore(Path.Combine(_root, SongToneEditStore.FolderName)));
        instruments.Start();
        var transport = new TransportViewModel(audio, new ProjectState(), config, loc ?? FakeLocalization.KeysOnly(), new OnboardingWizardRig.InlineDispatcher(), chartSynthInstruments: instruments);
        transport.ChartSynthInstrument.LoadInfoForTest();
        return (transport, instruments, profiles);
    }

    [Fact]
    public void TheToggles_WriteTheService()
    {
        var audio = new FakeChartSynthAudio();
        var transport = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly());
        Assert.True(transport.IsChartSynthAvailable);
        Assert.False(transport.ChartSynthEnabled);

        transport.ChartSynthEnabled = true;
        transport.RouteInstrumentToRig = true;
        Assert.Equal([true], audio.EnabledWrites);
        Assert.Equal([true], audio.RouteWrites);
        Assert.True(transport.ChartSynthEnabled);
        Assert.True(transport.RouteInstrumentToRig);

        transport.ChartSynthEnabled = true;   // no change, no write
        Assert.Single(audio.EnabledWrites);
    }

    [Fact]
    public void ASandboxedInstrument_ShowsTheRouteUnavailable_NotLive()
    {
        var audio = new FakeChartSynthAudio { Sandboxed = true };
        var transport = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly())
        {
            RouteInstrumentToRig = true,
        };
        Assert.Single(audio.RouteWrites);                // the request reached the service…
        Assert.False(transport.CanRouteInstrumentToRig);
        Assert.False(transport.RouteInstrumentToRig);   // the request is kept, but it does not read as live
    }

    [Fact]
    public void AServiceWithNoSynth_ShowsBothTogglesUnavailable()
    {
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly());
        Assert.False(transport.IsChartSynthAvailable);
        Assert.False(transport.CanRouteInstrumentToRig);
        transport.ChartSynthEnabled = true;
        Assert.False(transport.ChartSynthEnabled);
    }

    /// <summary>The instrument pickers (#50) DRIVE the service and SAVE the choice:
    /// a model change keeps the pickup when the new model has it and takes its
    /// default when not, the coil split only exists where a humbucker does, and
    /// every change lands in the PLAYER PROFILE's file (#66) the next start reads.</summary>
    [Fact]
    public void TheInstrumentPickers_WriteTheService_AndSaveTheChoice()
    {
        var audio = new FakeChartSynthAudio();
        var config = new FakeConfigurationService();
        var loc = FakeLocalization.English();
        var (transport, instruments, profiles) = Wired(audio, config, loc);

        // The defaults: the owner's HSS on its bridge humbucker; a Jazz on both pickups.
        Assert.Equal(ChartSynthModel.StratHss, transport.ChartSynthInstrument.SelectedGuitarModel!.Value);
        Assert.Equal(ChartSynthPickup.Bridge, transport.ChartSynthInstrument.SelectedGuitarPickup!.Value);
        Assert.True(transport.ChartSynthInstrument.GuitarHasCoilSplit);
        Assert.Equal(5, transport.ChartSynthInstrument.GuitarPickups.Count);
        Assert.Equal(ChartSynthModel.StratHss, transport.ChartSynthInstrument.GuitarModels[0].Value);
        Assert.All(transport.ChartSynthInstrument.GuitarModels, c => Assert.False((int)c.Value >= 16));
        Assert.All(transport.ChartSynthInstrument.BassModels, c => Assert.True((int)c.Value >= 16));

        // Coil split: written and saved.
        transport.ChartSynthInstrument.GuitarCoilSplit = true;
        Assert.Equal(new ChartSynthInstrument(ChartSynthModel.StratHss, ChartSynthPickup.Bridge, true), audio.GuitarInstrument);
        Assert.False(ChartSynthInstrumentSelection.HasLegacyConfig(config.LoadAppConfig()));   // not the app config any more

        // A Telecaster keeps the bridge, and has nothing to split.
        transport.ChartSynthInstrument.SelectedGuitarModel = transport.ChartSynthInstrument.GuitarModels.Single(c => c.Value == ChartSynthModel.Telecaster);
        Assert.Equal(new ChartSynthInstrument(ChartSynthModel.Telecaster, ChartSynthPickup.Bridge, false), audio.GuitarInstrument);
        Assert.False(transport.ChartSynthInstrument.GuitarHasCoilSplit);
        Assert.Equal(3, transport.ChartSynthInstrument.GuitarPickups.Count);
        transport.ChartSynthInstrument.SelectedGuitarPickup = transport.ChartSynthInstrument.GuitarPickups.Single(c => c.Value == ChartSynthPickup.NeckBridge);
        Assert.Equal(ChartSynthPickup.NeckBridge, audio.GuitarInstrument.Pickup);

        // Back to a Strat: neck+bridge is not on a 5-way, so its default (the bridge) is taken.
        transport.ChartSynthInstrument.SelectedGuitarModel = transport.ChartSynthInstrument.GuitarModels.Single(c => c.Value == ChartSynthModel.StratSss);
        Assert.Equal(new ChartSynthInstrument(ChartSynthModel.StratSss, ChartSynthPickup.Bridge, false), audio.GuitarInstrument);

        // The bass is its own selection: a P bass has one pickup.
        transport.ChartSynthInstrument.SelectedBassModel = transport.ChartSynthInstrument.BassModels.Single(c => c.Value == ChartSynthModel.PrecisionBass);
        Assert.Equal(new ChartSynthInstrument(ChartSynthModel.PrecisionBass, ChartSynthPickup.Middle, false), audio.BassInstrument);
        Assert.Single(transport.ChartSynthInstrument.BassPickups);
        Assert.Equal(ChartSynthModel.StratSss, audio.GuitarInstrument.Model);

        // What the next start reads back: the active player's own file.
        instruments.Flush();
        var saved = new ProfileChartSynthInstrumentStore(profiles).Read(profiles.ActiveProfile.Id);
        Assert.Equal(ProfileInstrumentReadState.Loaded, saved.State);
        Assert.Equal(audio.GuitarInstrument, saved.Guitar);
        Assert.Equal(audio.BassInstrument, saved.Bass);

        // The sound line says what is real: a Strat position is recorded, a Tele simulated.
        Assert.Contains(loc["ChartSynth_Sound_Recorded"], transport.ChartSynthInstrument.GuitarSound, StringComparison.Ordinal);
        transport.ChartSynthInstrument.SelectedGuitarModel = transport.ChartSynthInstrument.GuitarModels.Single(c => c.Value == ChartSynthModel.Telecaster);
        Assert.Contains(loc["ChartSynth_Sound_Simulated"], transport.ChartSynthInstrument.GuitarSound, StringComparison.Ordinal);
    }

    /// <summary>#66 in the pickers: they show the profile's choice after a switch,
    /// offer "Only for this tone" while a song tone plays, then say which tone sets
    /// the instrument and edit THAT; "Use my instrument" gives the profile's back.</summary>
    [Fact]
    public void ThePickers_FollowTheProfile_AndOfferAndDropAToneOverride()
    {
        var audio = new FakeChartSynthAudio();
        var loc = FakeLocalization.English();
        var (transport, instruments, profiles) = Wired(audio, new FakeConfigurationService(), loc);
        var raised = new List<string?>();
        transport.ChartSynthInstrument.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // A second player with a Telecaster: the switch alone moves the pickers.
        var mine = profiles.ActiveProfile.Id;
        profiles.SwitchTo(profiles.Create("Second").Id);
        transport.ChartSynthInstrument.SelectedGuitarModel = transport.ChartSynthInstrument.GuitarModels.Single(c => c.Value == ChartSynthModel.Telecaster);
        raised.Clear();
        profiles.SwitchTo(mine);
        Assert.Contains(nameof(ChartSynthInstrumentViewModel.SelectedGuitarModel), raised);
        Assert.Equal(ChartSynthModel.StratHss, transport.ChartSynthInstrument.SelectedGuitarModel!.Value);

        // No song tone: nothing to offer.
        Assert.False(transport.ChartSynthInstrument.CanOverrideTone);
        Assert.False(transport.ChartSynthInstrument.HasToneOverride);
        Assert.Equal(string.Empty, transport.ChartSynthInstrument.ToneOverrideText);

        instruments.OnSongTone("lib:CallMe", "clean", "Clean Tone");
        Assert.True(transport.ChartSynthInstrument.CanOverrideTone);
        transport.ChartSynthInstrument.OverrideToneCommand.Execute(null);
        Assert.False(transport.ChartSynthInstrument.CanOverrideTone);
        Assert.True(transport.ChartSynthInstrument.HasToneOverride);
        Assert.Contains("Clean Tone", transport.ChartSynthInstrument.ToneOverrideText, StringComparison.Ordinal);

        transport.ChartSynthInstrument.SelectedGuitarPickup = transport.ChartSynthInstrument.GuitarPickups.Single(c => c.Value == ChartSynthPickup.Neck);
        Assert.Equal(ChartSynthPickup.Neck, audio.GuitarInstrument.Pickup);
        Assert.Equal(ChartSynthPickup.Bridge, instruments.ProfileInstrument(bass: false).Pickup);   // the tone's, not mine

        transport.ChartSynthInstrument.UseMyInstrumentCommand.Execute(null);
        Assert.False(transport.ChartSynthInstrument.HasToneOverride);
        Assert.True(transport.ChartSynthInstrument.CanOverrideTone);
        Assert.Equal(ChartSynthPickup.Bridge, audio.GuitarInstrument.Pickup);
        Assert.Equal(ChartSynthPickup.Bridge, transport.ChartSynthInstrument.SelectedGuitarPickup!.Value);
        instruments.Flush();
    }
}
