using DotCharter.Services.Audio.ChartSynth;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Audio.Dsp.Tones;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Parsing.Psarc;
using DotCharter.Tests.UI;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The chart synth's instrument follows the PLAYER PROFILE (#66): a switch puts
///     that player's guitar and bass on the synth; a playing song tone's override
///     wins while that tone plays and the profile's comes back after it; the
///     app-wide choice #50 saved is moved into the active profile ONCE; and all of
///     it survives a restart (a new coordinator over the same files). Asserted
///     against what the audio service was told, never the coordinator's own word.
/// </summary>
public sealed class ChartSynthInstrumentCoordinatorTests : IDisposable
{
    private const string Song = "lib:CallMe";

    private static readonly ChartSynthInstrument TeleNeck = new(ChartSynthModel.Telecaster, ChartSynthPickup.Neck, false);
    private static readonly ChartSynthInstrument LesPaulBridge = new(ChartSynthModel.LesPaul, ChartSynthPickup.Bridge, true);
    private static readonly ChartSynthInstrument StratNeck = new(ChartSynthModel.StratSss, ChartSynthPickup.Neck, false);
    private static readonly ChartSynthInstrument PBass = new(ChartSynthModel.PrecisionBass, ChartSynthPickup.Middle, false);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dotcharter-synth-profile-" + Guid.NewGuid().ToString("N"));
    private readonly FakeConfigurationService _config = new();
    private readonly PlayerProfileService _profiles;
    private readonly SongToneEditStore _songTones;

    public ChartSynthInstrumentCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
        _profiles = new PlayerProfileService(FakeLocalization.English(), filePath: Path.Combine(_root, "player-profiles.json"));
        _songTones = new SongToneEditStore(Path.Combine(_root, SongToneEditStore.FolderName));
    }

    public void Dispose() => BestEffortDelete.Directory(_root);

    /// <summary>A coordinator over the test's files — a second one is "the next start".</summary>
    private ChartSynthInstrumentCoordinator Start(FakeChartSynthAudio audio, Func<RsTonePlaybackCoordinator?>? tones = null)
    {
        var c = new ChartSynthInstrumentCoordinator(audio, _profiles, new ProfileChartSynthInstrumentStore(_profiles),
            _config, _songTones, tones);
        c.Start();
        return c;
    }

    private string ProfileFile(Guid id) => Path.Combine(_profiles.GetDataDirectory(id), ProfileChartSynthInstrumentStore.FileName);

    [Fact]
    public void AFreshProfile_PlaysTheOwnersStratHss()
    {
        var audio = new FakeChartSynthAudio { GuitarInstrument = TeleNeck };   // whatever the service held before
        using var c = Start(audio);

        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, audio.GuitarInstrument);
        Assert.Equal(new ChartSynthInstrument(ChartSynthModel.StratHss, ChartSynthPickup.Bridge, false), audio.GuitarInstrument);
        Assert.Equal(ChartSynthInstrumentSelection.DefaultBass, audio.BassInstrument);
    }

    /// <summary>
    ///     The exit paths flush with a bound and then terminate the process (#82): a
    ///     pick made just before closing must be on disk once the bounded flush says
    ///     so, before any Dispose runs.
    /// </summary>
    [Fact]
    public void ABoundedFlush_PutsAPickMadeJustBeforeClosingOnDisk()
    {
        var audio = new FakeChartSynthAudio();
        var id = _profiles.ActiveProfile.Id;
        var c = Start(audio);
        try
        {
            Assert.True(c.SetInstrument(TeleNeck));

            // 60 s is a deadlock detector here, never a budget.
            Assert.True(c.Flush(TimeSpan.FromSeconds(60)), "the queued save never landed");
            Assert.True(File.Exists(ProfileFile(id)), "the flush returned but the pick is not on disk");
            Assert.Equal(TeleNeck, new ProfileChartSynthInstrumentStore(_profiles).Read(id).Guitar);
        }
        finally
        {
            c.Dispose();
        }
    }

    [Fact]
    public void AProfileSwitch_PutsThatPlayersInstrumentOnTheSynth_AndItSurvivesARestart()
    {
        var audio = new FakeChartSynthAudio();
        var first = _profiles.ActiveProfile.Id;
        using (var c = Start(audio))
        {
            Assert.True(c.SetInstrument(TeleNeck));
            Assert.True(c.SetInstrument(PBass));
            Assert.Equal(TeleNeck, audio.GuitarInstrument);

            var second = _profiles.Create("Second").Id;
            Assert.True(_profiles.SwitchTo(second));
            // The new player has chosen nothing: the defaults, not the first player's Tele.
            Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, audio.GuitarInstrument);
            Assert.Equal(ChartSynthInstrumentSelection.DefaultBass, audio.BassInstrument);
            Assert.True(c.SetInstrument(LesPaulBridge));
            Assert.Equal(LesPaulBridge, audio.GuitarInstrument);

            Assert.True(_profiles.SwitchTo(first));
            Assert.Equal(TeleNeck, audio.GuitarInstrument);
            Assert.Equal(PBass, audio.BassInstrument);
            c.Flush();
        }

        // The next start reads each player's file.
        var restarted = new FakeChartSynthAudio();
        using (Start(restarted))
        {
            Assert.Equal(TeleNeck, restarted.GuitarInstrument);
            Assert.Equal(PBass, restarted.BassInstrument);
            _profiles.SwitchTo(_profiles.Profiles.Single(p => p.Name == "Second").Id);
            Assert.Equal(LesPaulBridge, restarted.GuitarInstrument);
            Assert.Equal(ChartSynthInstrumentSelection.DefaultBass, restarted.BassInstrument);
        }
    }

    [Fact]
    public void AToneOverride_WinsWhileItsTonePlays_AndTheProfilesComesBackAfter()
    {
        var audio = new FakeChartSynthAudio();
        using (var c = Start(audio))
        {
            Assert.True(c.SetInstrument(TeleNeck));                 // the player's own
            Assert.False(c.OverrideTone());                          // no song tone playing: nothing to override

            c.OnSongTone(Song, "clean", "Clean Tone");
            Assert.True(c.CanOverrideTone);
            Assert.False(c.HasToneOverride);
            Assert.True(c.OverrideTone());
            Assert.True(c.HasToneOverride);
            Assert.Equal(TeleNeck, audio.GuitarInstrument);         // an empty override changes nothing yet
            Assert.True(c.SetInstrument(StratNeck));                 // the pickers now edit the TONE's
            Assert.Equal(StratNeck, audio.GuitarInstrument);
            Assert.True(c.IsOverridden(bass: false));
            Assert.False(c.IsOverridden(bass: true));                // the bass still follows the profile
            Assert.Equal(TeleNeck, c.ProfileInstrument(bass: false)); // …and the profile was not touched

            c.OnSongTone(Song, "heavy", "heavy");                    // another tone of the song
            Assert.Equal(TeleNeck, audio.GuitarInstrument);
            c.OnSongTone(Song, "clean", "Clean Tone");               // back to the overriding tone
            Assert.Equal(StratNeck, audio.GuitarInstrument);
            c.OnSongTone(null, null, null);                          // the run ended
            Assert.Equal(TeleNeck, audio.GuitarInstrument);
            Assert.False(c.CanOverrideTone);
            c.Flush();
        }

        // Saved with the song's tones: the next start plays it again for that tone only.
        var restarted = new FakeChartSynthAudio();
        using (var c = Start(restarted))
        {
            Assert.Equal(TeleNeck, restarted.GuitarInstrument);
            c.OnSongTone(Song, "CLEAN", "Clean Tone");               // keys are case-insensitive
            Assert.Equal(StratNeck, restarted.GuitarInstrument);
            Assert.Equal("Clean Tone", c.ActiveToneName);

            // "Use my instrument": the override goes, the profile's plays, and stays gone.
            Assert.True(c.UseProfileInstrument());
            Assert.Equal(TeleNeck, restarted.GuitarInstrument);
            Assert.False(c.HasToneOverride);
            c.Flush();
        }

        Assert.Empty(_songTones.ReadInstruments(Song));
    }

    [Fact]
    public void TheAppWideChoice_IsMovedIntoTheActiveProfile_Once()
    {
        ChartSynthInstrumentSelection.WriteTo(_config.AppConfig, LesPaulBridge);
        ChartSynthInstrumentSelection.WriteTo(_config.AppConfig, PBass);
        var owner = _profiles.ActiveProfile.Id;
        var other = _profiles.Create("Guest").Id;   // not active: gets nothing

        var audio = new FakeChartSynthAudio();
        using (Start(audio))
        {
            Assert.Equal(LesPaulBridge, audio.GuitarInstrument);
            Assert.Equal(PBass, audio.BassInstrument);
        }

        Assert.True(File.Exists(ProfileFile(owner)));
        Assert.False(File.Exists(ProfileFile(other)));
        Assert.False(ChartSynthInstrumentSelection.HasLegacyConfig(_config.AppConfig));   // one-shot

        // Once: a later switch to the guest plays the defaults, not the moved choice.
        using (Start(audio))
        {
            _profiles.SwitchTo(other);
            Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, audio.GuitarInstrument);
        }
    }

    [Fact]
    public void TheMove_KeepsAProfilesOwnChoice_AndRetriesWhenTheFileCannotBeRead()
    {
        var id = _profiles.ActiveProfile.Id;
        var store = new ProfileChartSynthInstrumentStore(_profiles);
        Assert.True(store.Save(id, TeleNeck, PBass, out _));
        ChartSynthInstrumentSelection.WriteTo(_config.AppConfig, LesPaulBridge);

        // Unreadable: nothing is cleared, so the next start tries again.
        var path = ProfileFile(id);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
        using (Start(new FakeChartSynthAudio())) { }
        Assert.True(ChartSynthInstrumentSelection.HasLegacyConfig(_config.AppConfig));

        // Readable: the profile already has its own, which wins; the legacy is dropped.
        var audio = new FakeChartSynthAudio();
        using (Start(audio)) Assert.Equal(TeleNeck, audio.GuitarInstrument);
        Assert.False(ChartSynthInstrumentSelection.HasLegacyConfig(_config.AppConfig));
    }

    [Fact]
    public void AnUnreadableProfileFile_PlaysTheDefaults_WithoutCachingThem()
    {
        var id = _profiles.ActiveProfile.Id;
        Assert.True(new ProfileChartSynthInstrumentStore(_profiles).Save(id, TeleNeck, PBass, out _));
        var audio = new FakeChartSynthAudio();
        ChartSynthInstrumentCoordinator c;
        using (new FileStream(ProfileFile(id), FileMode.Open, FileAccess.Read, FileShare.Write | FileShare.Delete))
            c = Start(audio);
        using (c)
        {
            Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, audio.GuitarInstrument);
            // The hold is gone; the next apply re-reads instead of trusting the stand-in.
            c.OnSongTone(null, null, null);
            Assert.Equal(TeleNeck, audio.GuitarInstrument);
        }
    }

    /// <summary>End to end through the REAL tone coordinator: its song-tone edit
    /// raises the tone with its song and package key (the chart names it "Clean
    /// Tone", the package key is "clean"), and closing the edit gives the
    /// profile's instrument back.</summary>
    [Fact]
    public async Task TheToneCoordinatorsEvents_DriveTheOverride()
    {
        Assert.True(_songTones.SaveInstrument(Song, "clean", new ChartSynthToneOverride(StratNeck, null)).Ok);
        var tones = ToneCoordinator();
        tones.PrepareForSong("song.psarc", new Arrangement { ToneBase = "Clean Tone" }, Song);
        var audio = new FakeChartSynthAudio();
        using var c = Start(audio, () => tones);

        Assert.True(tones.BeginToneEdit("Clean Tone"));
        Assert.Equal(StratNeck, audio.GuitarInstrument);
        Assert.Equal("Clean Tone", c.ActiveToneName);

        tones.EndToneEdit();
        await tones.RestoreCompletion;
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, audio.GuitarInstrument);

        Assert.True(tones.BeginToneEdit("heavy"));   // a tone without one: the profile's
        Assert.Equal(ChartSynthInstrumentSelection.DefaultGuitar, audio.GuitarInstrument);
        tones.EndToneEdit();
        await tones.RestoreCompletion;
    }

    /// <summary>
    ///     A pick's save is queued; a profile switch re-reads the profile. Switching
    ///     away and back before the save has landed must still play the pick, not
    ///     the stale file's defaults. That was a race the full gate lost under load.
    /// </summary>
    [Fact]
    public void AProfileSwitchBack_BeforeThePicksSaveLands_StillPlaysThePick()
    {
        var held = new HeldWrites();
        var audio = new FakeChartSynthAudio();
        var first = _profiles.ActiveProfile.Id;
        using var c = StartHeld(audio, held);

        Assert.True(c.SetInstrument(TeleNeck));
        Assert.True(c.SetInstrument(PBass));
        Assert.True(held.Count > 0, "the saves were expected to be held");
        var second = _profiles.Create("Second").Id;
        Assert.True(_profiles.SwitchTo(second));
        Assert.True(_profiles.SwitchTo(first));

        Assert.Equal(TeleNeck, audio.GuitarInstrument);
        Assert.Equal(PBass, audio.BassInstrument);

        held.RunAll();
        Assert.True(c.Flush(TimeSpan.FromSeconds(60)), "the released saves never landed");
        var onDisk = new ProfileChartSynthInstrumentStore(_profiles).Read(first);
        Assert.Equal(TeleNeck, onDisk.Guitar);
        Assert.Equal(PBass, onDisk.Bass);
    }

    /// <summary>The same race for a song tone's override: the run ends and the
    /// tone comes back before its save has landed.</summary>
    [Fact]
    public void ASongToneComingBack_BeforeItsOverridesSaveLands_StillPlaysTheOverride()
    {
        var held = new HeldWrites();
        var audio = new FakeChartSynthAudio();
        using var c = StartHeld(audio, held);

        c.OnSongTone(Song, "clean", "Clean Tone");
        Assert.True(c.OverrideTone());
        Assert.True(c.SetInstrument(StratNeck));
        c.OnSongTone(null, null, null);                 // the run ended: overrides are re-read next time
        c.OnSongTone(Song, "clean", "Clean Tone");

        Assert.Equal(StratNeck, audio.GuitarInstrument);
        Assert.True(c.HasToneOverride);

        held.RunAll();
        Assert.True(c.Flush(TimeSpan.FromSeconds(60)), "the released saves never landed");
        Assert.Equal(StratNeck, _songTones.ReadInstruments(Song)["clean"].Guitar);

        // "Use my instrument" before ITS save lands: the override stays gone.
        Assert.True(c.UseProfileInstrument());
        c.OnSongTone(null, null, null);
        c.OnSongTone(Song, "clean", "Clean Tone");
        Assert.False(c.HasToneOverride);
        held.RunAll();
        Assert.True(c.Flush(TimeSpan.FromSeconds(60)));
    }

    private ChartSynthInstrumentCoordinator StartHeld(FakeChartSynthAudio audio, HeldWrites held)
    {
        var c = new ChartSynthInstrumentCoordinator(audio, _profiles, new ProfileChartSynthInstrumentStore(_profiles),
            _config, _songTones)
        {
            WriteScheduler = held
        };
        c.Start();
        return c;
    }

    /// <summary>Runs queued saves only when told to, so a test can hold one in flight.</summary>
    private sealed class HeldWrites : TaskScheduler
    {
        private readonly Queue<Task> _queue = new();

        public int Count
        {
            get
            {
                lock (_queue) return _queue.Count;
            }
        }

        public void RunAll()
        {
            while (true)
            {
                Task next;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    next = _queue.Dequeue();
                }

                TryExecuteTask(next);
            }
        }

        protected override void QueueTask(Task task)
        {
            lock (_queue) _queue.Enqueue(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (_queue) return _queue.ToArray();
        }
    }

    private static RsTonePlaybackCoordinator ToneCoordinator()
    {
        const string clean = """{"Key":"clean","Name":"Clean Tone","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";
        const string heavy = """{"Key":"heavy","Name":"heavy","GearList":{"Amp":{"Type":"Amps","Key":"Amp_5150","KnobValues":{}}}}""";
        var catalog = new[] { "amp", "cab", "gain", "overdrive", "distortion", "eq", "reverb" }
            .Select(id => new DspModuleType(id, id, new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();
        var chain = new FakeDspChainService(catalog);
        var state = new ProjectState();
        for (var i = 0; i < 8; i++) state.Beats.Add(new BeatMarker { Time = i * 1000, IsMeasure = i % 4 == 0 });
        return new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), state,
            new FakeConfigurationService { AppConfig = { UseInGameTones = true } })
        {
            ToneDefinitionSource = _ =>
            [
                new PsarcToneDefinition("clean", "Clean Tone", clean),
                new PsarcToneDefinition("heavy", "heavy", heavy)
            ],
            SongTonesSource = _ => new Dictionary<string, DcRigPreset>()
        };
    }
}
