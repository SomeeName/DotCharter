using System.Globalization;
using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Shell;
using DotCharter.Presentation.Screens.Home;
using DotCharter.Presentation.Shared;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library.Index;
using DotCharter.Services.Session;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The Home screen over its real seams, with every service faked.
///     <para>
///         Assertions are about SHAPE, never about English: the localization
///         fake echoes the key and its arguments, so a translation change cannot
///         break these and a wrong key or a missing figure still does.
///     </para>
/// </summary>
public sealed class HomeScreenTests
{
    [Fact]
    public void AFreshInstall_SaysSoAndOffersTheLibraryAsThePrimaryCard()
    {
        var rig = new Rig();

        var home = rig.Screen();

        Assert.Equal("Home_Library_Empty", home.LibrarySummary);
        Assert.False(home.HasRecentSong);
        Assert.False(home.HasJumpBackIn);
        Assert.False(home.CanOpenRecentChart);

        // Exactly one card is lit, and on an empty install it is the one that
        // fixes the emptiness.
        var primary = Assert.Single(home.Destinations, d => d.IsPrimary);
        Assert.Equal("Home_Dest_Library", primary.Title);
    }

    [Fact]
    public void AScannedLibrary_IsCountedByDistinctSong_AndLightsPlay()
    {
        var rig = new Rig { Library = new LibraryIndexFacts(312, 41) };

        var home = rig.Screen();

        Assert.Equal("Home_Library_Summary(312)", home.LibrarySummary);
        var primary = Assert.Single(home.Destinations, d => d.IsPrimary);
        Assert.Equal("Home_Dest_Play", primary.Title);
    }

    /// <summary>The library count is said ONCE, under the title: the Play and Library cards
    /// repeated it word for word, three times on one screen (2026-09-23). The cards say what they do.</summary>
    [Fact]
    public void TheLibraryCount_IsSaidOnce_AndTheCardsSayWhatTheyDo()
    {
        var home = new Rig { Library = new LibraryIndexFacts(312, 41) }.Screen();

        Assert.DoesNotContain(home.Destinations, d => d.Subtitle == home.LibrarySummary);
        Assert.Equal("Home_Dest_Play_Sub", home.Destinations.Single(d => d.Title == "Home_Dest_Play").Subtitle);
        Assert.Equal("Home_Dest_Library_Sub", home.Destinations.Single(d => d.Title == "Home_Dest_Library").Subtitle);
    }

    [Fact]
    public void TheHero_IsTheLastPlayedSong_WithEveryArrangementOfIt()
    {
        var rig = new Rig
        {
            Stats = new ProfileStatistics
            {
                RecentlyPlayed =
                [
                    Song("take-my-bones", "Lead", 0.96, 88),
                    Song("take-my-bones", "Bass", 0, 12),
                    Song("everlong", "Lead", 0.71, 40)
                ]
            }
        };

        var home = rig.Screen();

        Assert.True(home.HasRecentSong);
        Assert.Equal("take-my-bones", home.RecentTitle);
        Assert.Equal("Home_PlayArrangement(Lead)", home.PlayRecentCaption);

        // Both arrangements of the HERO song, best first, and only the played
        // one carries a figure. `everlong` is a different song and stays out.
        Assert.Equal(
            [$"Home_Arrangement_Best(Lead, {PercentText.Format(0.96, 0)})", "Home_Arrangement_NotPlayed(Bass)"],
            home.RecentArrangements.Select(a => a.Label));
        Assert.True(home.RecentArrangements[0].IsBest);
        Assert.False(home.RecentArrangements[1].IsBest);
    }

    [Fact]
    public void TheHero_MovesOnlyWhenTheSettingSaysSo()
    {
        var rig = new Rig();
        Assert.False(rig.Screen().AnimateHero);

        rig.Config.AppConfig.AnimatedHomeHero = true;
        Assert.True(rig.Screen().AnimateHero);
    }

    /// <summary>
    ///     The hero and the jump-back-in rows show a library song's cover from
    ///     the index's cover cache (the stats key is "lib:" plus the song key),
    ///     and an own chart, keyed by its project path, asks for none.
    /// </summary>
    [Fact]
    public void ALibrarySong_BringsItsCover_AndAnOwnChartAsksForNone()
    {
        var rig = new Rig
        {
            Stats = new ProfileStatistics
            {
                RecentlyPlayed =
                [
                    Song("lib:TakeMyBones", "Lead", 0.96, 88),
                    Song(@"C:\charts\mine.rscp", "Lead", 0.5, 30)
                ]
            },
            Covers = { ["TakeMyBones"] = @"C:\cache	mb.png" }
        };

        var home = rig.Screen();

        Assert.True(home.HasRecentArt);
        Assert.Equal(@"C:\cache	mb.png", home.RecentArtPath);
        Assert.Equal([true, false], home.JumpBackIn.Select(r => r.HasArt));
        Assert.DoesNotContain(rig.CoverQueries, q => q.Contains("rscp"));
    }

    /// <summary>
    ///     A song played from a chart project is keyed by its project path; its
    ///     cover is the one beside the project, and a project with none gives
    ///     the strings fallback (#815).
    /// </summary>
    [Fact]
    public void AProjectSong_BringsTheCoverBesideItsProject_AndOneWithoutGivesNone()
    {
        var root = Path.Combine(Path.GetTempPath(), "dc-home-" + Guid.NewGuid().ToString("N"));
        try
        {
            var withCover = Path.Combine(root, "a", "a.dcproj");
            var without = Path.Combine(root, "b", "b.dcproj");
            foreach (var project in new[] { withCover, without })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(project)!);
                File.WriteAllText(project, "<project><title>T</title><artistName>A</artistName><beats/></project>");
            }

            var cover = Path.Combine(root, "a", "cover.png");
            File.WriteAllBytes(cover, [1]);

            var covered = new Rig { Stats = new ProfileStatistics { RecentlyPlayed = [Song(withCover, "Lead", 0.5, 30)] } }
                .Screen();
            var bare = new Rig { Stats = new ProfileStatistics { RecentlyPlayed = [Song(without, "Lead", 0.5, 30)] } }
                .Screen();

            Assert.Equal(cover, covered.RecentArtPath);
            Assert.Equal([true], covered.JumpBackIn.Select(r => r.HasArt));
            Assert.False(bare.HasRecentArt);
            Assert.Equal([false], bare.JumpBackIn.Select(r => r.HasArt));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>
    ///     Home has no facts strip (the v2-stage layout dropped it), so the one
    ///     figure on this screen that can be WRONG rather than informative says
    ///     so on the device chip itself.
    /// </summary>
    [Fact]
    public void ALostDevice_MarksTheDeviceChip_AndSettingsIsOneClickAway()
    {
        var rig = new Rig { Device = { IsDeviceLost = true } };

        var home = rig.Screen();

        Assert.Equal("Home_Device_Lost", home.DeviceSummary);
        Assert.True(home.DeviceNeedsAttention);

        // …and this screen is FULL-BLEED, so its own header is the only route
        // to Settings there is.
        home.OpenSettingsCommand.Execute(null);
        Assert.Equal(ShellDestination.Settings, rig.Shell.Went[^1]);
    }

    /// <summary>
    ///     An unmeasured latency is left OFF rather than printed as a confident
    ///     "0.0 ms" — the engine reports 0 with no device open, and a readout
    ///     that states it is lying about something the user can act on.
    /// </summary>
    [Fact]
    public void AClosedEngine_NamesTheDeviceWithoutInventingALatency()
    {
        var rig = new Rig();
        rig.Config.AppConfig.WasapiOutputDeviceName = "Focusrite 2i2";

        Assert.Equal("Focusrite 2i2", rig.Screen().DeviceSummary);

        rig.Device.EstimatedOutputLatencyMs = 4.2;
        // In the Windows region's format, as every other number on screen.
        Assert.Equal($"Home_Device_Summary(Focusrite 2i2, {4.2.ToString("0.0", CultureInfo.CurrentCulture)})",
            rig.Screen().DeviceSummary);
    }

    /// <summary>
    ///     Four cards, in the v2-stage order, and the rig's is the one that
    ///     opens an OVERLAY rather than navigating — so it goes through
    ///     `OpenRig`, not through a destination.
    /// </summary>
    [Fact]
    public void TheFourDestinations_IncludeTheRig_WhichOpensTheOverlay()
    {
        var rig = new Rig { Library = new LibraryIndexFacts(312, 41) };

        var cards = rig.Screen().Destinations;

        Assert.Equal(
            ["Home_Dest_Play", "Home_Dest_Chart", "Home_Dest_Rig", "Home_Dest_Library"],
            cards.Select(c => c.Title));

        cards[2].OpenCommand.Execute(null);
        Assert.True(rig.Shell.RigOpened);
        Assert.Empty(rig.Shell.Went);
    }

    /// <summary>
    ///     Home reads LIVE figures, so arriving on its tab has to re-read them.
    ///     Its host holds one screen and therefore never navigates again after
    ///     the first show — <c>ScreenHost.NotifyReshown</c> is what calls this,
    ///     and without it the numbers are whatever they were at startup.
    /// </summary>
    [Fact]
    public void OnShown_ReReadsEveryFigure()
    {
        var rig = new Rig();
        var home = rig.Screen();
        Assert.Equal("Home_Library_Empty", home.LibrarySummary);

        rig.Library = new LibraryIndexFacts(7, 2);
        home.OnShown();

        Assert.Equal("Home_Library_Summary(7)", home.LibrarySummary);
    }

    /// <summary>
    ///     A return to Home reads the library index and the play history OFF the
    ///     caller's thread and publishes when the read lands (#234): the show itself
    ///     queries nothing, and the figures move only when the result arrives.
    /// </summary>
    [Fact]
    public void AShow_ReadsOffTheCallersThread_AndPublishesWhenTheReadLands()
    {
        var rig = new Rig();
        var home = rig.Screen();
        Func<HomeSnapshot>? pending = null;
        var landed = new TaskCompletionSource<HomeSnapshot>();
        rig.OffThread = read =>
        {
            pending = read;
            return landed.Task;
        };
        rig.Library = new LibraryIndexFacts(7, 2);
        var readsBefore = rig.FactReads;

        home.OnShown();

        Assert.Equal(readsBefore, rig.FactReads);
        Assert.Equal("Home_Library_Empty", home.LibrarySummary);

        landed.SetResult(pending!());

        Assert.Equal("Home_Library_Summary(7)", home.LibrarySummary);
    }

    /// <summary>
    ///     A return that finds nothing changed announces nothing and keeps every
    ///     card: re-raising every binding and rebuilding the cards on each return
    ///     was most of what a Home switch cost.
    /// </summary>
    [Fact]
    public void AShowWithNothingChanged_RaisesNothing_AndKeepsTheCards()
    {
        var rig = new Rig { Library = new LibraryIndexFacts(312, 41) };
        var home = rig.Screen();
        var cards = home.Destinations;
        var raised = 0;
        home.PropertyChanged += (_, _) => raised++;

        home.OnShown();
        home.OnShown();

        Assert.Equal(0, raised);
        Assert.Same(cards, home.Destinations);

        rig.Library = new LibraryIndexFacts(313, 41);
        home.OnShown();

        Assert.True(raised > 0);
        Assert.NotSame(cards, home.Destinations);
    }

    /// <summary>With nothing loaded, the Chart card names the last saved project and
    /// a click OPENS it (it used to show the name and open an empty editor).</summary>
    [Fact]
    public void TheChartCard_WithNothingLoaded_NamesAndOpensTheLastSavedProject()
    {
        var rig = new Rig();
        rig.Session.Recent = [new RecentProjectEntry { Path = @"C:\songs\a.rscp", DisplayName = "A" }];

        var home = rig.Screen();
        var chart = home.Destinations.Single(d => d.Title == "Home_Dest_Chart");

        Assert.Equal("A", chart.Subtitle);
        chart.OpenCommand.Execute(null);
        Assert.Equal(ShellDestination.Chart, rig.Shell.Went[^1]);
        Assert.Equal(@"C:\songs\a.rscp", rig.Session.Loaded[^1]);
    }

    /// <summary>The Chart card names what a click opens. A song played from the
    /// library is loaded in the editor, and the card said "Sakasma" (the last SAVED
    /// project) while a click opened The Offspring (owner report, 2026-09-29).</summary>
    [Fact]
    public void TheChartCard_NamesTheChartTheEditorHasOpen_NotTheLastSavedProject()
    {
        var rig = new Rig();
        rig.Session.Recent = [new RecentProjectEntry { Path = @"C:\songs\sakasma.rscp", DisplayName = "Sakasma" }];
        rig.Session.LoadedChartName = "The Offspring - Self Esteem";

        var home = rig.Screen();
        var chart = home.Destinations.Single(d => d.Title == "Home_Dest_Chart");

        Assert.Equal("The Offspring - Self Esteem", chart.Subtitle);
        chart.OpenCommand.Execute(null);
        Assert.Equal(ShellDestination.Chart, rig.Shell.Went[^1]);
        Assert.Empty(rig.Session.Loaded);                    // it opens what is loaded
    }

    /// <summary>"Open the chart" sits under the hero's song, so it is there only
    /// when that song is the chart the editor has open.</summary>
    [Theory]
    [InlineData("take-my-bones", true)]
    [InlineData("Sakasma", false)]
    [InlineData("", false)]
    public void TheHerosOpenTheChart_IsOnlyForTheSongItSitsUnder(string loaded, bool offered)
    {
        var rig = new Rig { Stats = new ProfileStatistics { RecentlyPlayed = [Song("take-my-bones", "Lead", 0.5, 10)] } };
        rig.Session.Recent = [new RecentProjectEntry { Path = @"C:\songs\sakasma.rscp", DisplayName = "Sakasma" }];
        rig.Session.LoadedChartName = loaded;

        var home = rig.Screen();

        Assert.Equal(offered, home.CanOpenRecentChart);
        home.OpenRecentChartCommand.Execute(null);
        Assert.Empty(rig.Session.Loaded);                    // never loads another song
        if (offered) Assert.Equal(ShellDestination.Chart, rig.Shell.Went[^1]);
    }

    private static SongPlayStats Song(string key, string arrangement, double best, double mastery) =>
        new(key, arrangement, 3, 1000, best, mastery, GradeCalculator.FromAccuracy(best),
            new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), key);

    [Fact]
    public async Task PlayHero_RequestsTheDisplayedSongAndItsRecordedPath()
    {
        var rig = new Rig
        {
            Stats = new ProfileStatistics
            {
                RecentlyPlayed = [Song("lib:first", "Combo:Combo", 0.5, 10)]
            }
        };
        var command = rig.Screen().PlayRecentCommand;
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCommand)
            await asyncCommand.ExecuteAsync(null);
        else
            command.Execute(null);

        Assert.Equal(("lib:first", "Combo:Combo"), Assert.Single(rig.Shell.Played));
    }

    [Fact]
    public async Task JumpBackIn_RequestsTheClickedSongAndItsRecordedPath()
    {
        var rig = new Rig
        {
            Stats = new ProfileStatistics
            {
                RecentlyPlayed = [Song("lib:first", "Lead", 0.5, 10), Song("lib:second", "Bass", 0.4, 20)]
            }
        };
        var command = rig.Screen().JumpBackIn[1].OpenCommand;
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCommand)
            await asyncCommand.ExecuteAsync(null);
        else
            command.Execute(null);

        Assert.Equal(("lib:second", "Bass"), Assert.Single(rig.Shell.Played));
    }

    // ── the rig ─────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public FakeConfigurationService Config { get; } = new();
        public StubSession Session { get; } = new();
        public StubDevice Device { get; } = new();
        public StubShell Shell { get; } = new();
        public ProfileStatistics Stats { get; init; } = new();
        public LibraryIndexFacts Library { get; set; } = LibraryIndexFacts.Empty;
        public Dictionary<string, string> Covers { get; } = [];
        public List<string> CoverQueries { get; } = [];
        public int FactReads { get; private set; }

        /// <summary>Where a show's read runs. Inline by default, so a show is observable at once.</summary>
        public Func<Func<HomeSnapshot>, Task<HomeSnapshot>> OffThread { get; set; } = read => Task.FromResult(read());

        public HomeViewModel Screen()
        {
            var source = new HomeSnapshotSource(
                Config, Session, new StubStats(Stats), new OnboardingWizardRig.StubProfiles(),
                new SongLibraryLookup(new StubIndex(() =>
                {
                    FactReads++;
                    return Library;
                }, key =>
                {
                    CoverQueries.Add(key);
                    return Covers.GetValueOrDefault(key, "");
                })), Device, new EchoLocalization());
            return new HomeViewModel(source, Shell, Session, new EchoLocalization(),
                () => new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), read => OffThread(read));
        }
    }

    /// <summary>Echoes the key and its arguments, so no assertion here is about English.</summary>
    private sealed class EchoLocalization : ILocalizationService
    {
        public IReadOnlyList<LanguageOption> LanguageOptions => [];
        public string ConfiguredCulture => "en-US";
        public CultureInfo Culture => CultureInfo.InvariantCulture;
        public bool IsRightToLeft => false;
        public string this[string key] => key;

        public string Format(string key, params object[] args) =>
            args.Length == 0 ? key : $"{key}({string.Join(", ", args)})";

        public string Plural(string oneKey, string manyKey, int count, params object[] extra) =>
            Format(count == 1 ? oneKey : manyKey, count);

        public string PluralWith(string oneKey, string manyKey, int count, params object[] args) =>
            Format(count == 1 ? oneKey : manyKey, args);

        public void SetConfiguredCulture(string? configValue) { }

        public void LoadCommunityLanguages(string folder) { }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }

    private sealed class StubStats(ProfileStatistics stats) : IGameplayStatsService
    {
        public ProfileStatistics GetStatistics() => stats;
        public SongPlayStats? GetSongStats(string songKey, string arrangementName) => null;
        public SongPlayStats? GetBestForSong(string songKey) => null;
        public LastSessionFacts? GetMostRecentSession() => null;
    }

    private sealed class StubIndex(Func<LibraryIndexFacts> facts, Func<string, string> cover) : ILibraryIndex
    {
        public ILibraryIndexScan? BeginScan() => null;
        public void TrySetSongRating(string psarcPath, string songKey, double rating) { }
        public LibraryIndexFacts ReadFacts() => facts();
        public string FindCoverImage(string songKey) => cover(songKey);
        public string FindCoverImageByTitle(string artist, string title) => "";
    }

    private sealed class StubShell : IShellDestinations
    {
        public List<(string SongKey, string Arrangement)> Played { get; } = [];
        public Task PlaySongAsync(string songKey, string arrangementName)
        {
            Played.Add((songKey, arrangementName));
            return Task.CompletedTask;
        }
        public List<ShellDestination> Went { get; } = [];
        public bool RigOpened { get; private set; }
        public void Go(ShellDestination destination) => Went.Add(destination);
        public void OpenRig() => RigOpened = true;
        public void OpenCommandPalette() { }
    }

    private sealed class StubDevice : IAudioDeviceControl
    {
        public double AudioDelayMs { get; set; }
        public double EstimatedOutputLatencyMs { get; set; }
        public double MeasuredOutputLatencyMs => 0;
        public int ActiveSampleRate => 48000;
        public int ActualBufferFrames => 256;
        public double MidiOffsetMs { get; set; }
        public bool IsDeviceLost { get; set; }

        public event EventHandler<int>? DeviceBufferFramesChanged { add { } remove { } }
        public event EventHandler? DeviceLost { add { } remove { } }
        public event EventHandler<AudioDeviceReconnectedEventArgs>? DeviceReconnected { add { } remove { } }

        public void ApplyAudioConfiguration(RigConfig config) { }
        public void EnsureAsioDevicesProbed(RigConfig config) { }
        public void EnsureWasapiDevicesProbed(RigConfig config) { }

        public bool TryOpenAsioControlPanel(IntPtr parentWindowHandle, out string? error)
        {
            error = null;
            return false;
        }

        public List<string> GetWasapiOutputDevices() => [];
        public List<string> GetAsioDevices() => [];
        public List<string> GetWasapiInputDevices() => [];
        public List<string> GetAsioInputDevices() => [];
        public int GetInputDeviceChannelCount(string driver, string deviceName) => 0;
        public int GetOutputDeviceChannelCount(string driver, string deviceName) => 0;
        public IReadOnlyList<string> GetOutputChannelNames(string driver, string deviceName) => [];
        public IReadOnlyList<string> GetInputChannelNames(string driver, string deviceName) => [];
        public IReadOnlyList<DotCharter.Tone.Audio.Midi.MidiOutputDeviceInfo> GetMidiOutputDevices() => [];
    }

    private sealed class StubSession : IProjectSessionService
    {
        public List<RecentProjectEntry> Recent { get; set; } = [];
        public List<string> Loaded { get; } = [];
        public string LoadedChartName { get; set; } = "";

        public AppConfig AppConfig { get; } = new();
        public bool IsProjectDirty => false;
        public Func<Task<bool>>? ConfirmDiscardAsync { get; set; }
        public event Action? ProjectDirtyChanged { add { } remove { } }
        public event Action? RecentProjectsChanged { add { } remove { } }

        public Task<bool> MayReplaceOpenProjectAsync() => Task.FromResult(true);
        public void MarkProjectClean() { }
        public void MarkProjectDirty() { }
        public void Initialize() { }
        public void LoadAppConfig() { }
        public void SaveAppConfig() { }
        public void FreeAudio() { }

        public Task LoadFile(string filePath)
        {
            Loaded.Add(filePath);
            return Task.CompletedTask;
        }

        public Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false) =>
            Task.CompletedTask;

        public Task<bool> AttachAudioAsync(string sourcePath) => Task.FromResult(true);
        public Task LoadSilentTransportAsync(double projectLengthMs) => Task.CompletedTask;
        public Task OpenProject() => Task.CompletedTask;
        public Task<bool> SaveProjectAsync() => Task.FromResult(true);
        public Task<bool> SaveProjectAsAsync() => Task.FromResult(true);
        public IReadOnlyList<RecentProjectEntry> GetRecentProjects() => Recent;
        public void Dispose() { }
    }
}
