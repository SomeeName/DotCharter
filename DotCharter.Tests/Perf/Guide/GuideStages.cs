using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.RigLab;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Library;
using DotCharter.Tests.Guide;
using DotCharter.Tests.Perf.InspectorCapture;
using DotCharter.Tests.UI.ViewModels.Panels;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Library;
using DotCharter.UI.Views.PlayMode;
using DotCharter.UI.Views.Settings;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf.Guide;

/// <summary>
///     Builds each usage-guide scene (<see cref="GuideScenes" />) over a SEEDED project, never an
///     empty fixture: a small playable song over the Inspector review's project (riff, open chords,
///     solo, sections, lyrics, annotations, <see cref="GuideDemoChart" />), a library of invented songs, a finished run
///     for Results, and a rig with a full chain. The editor, Library and Settings shots share ONE
///     <see cref="MainWindow" /> (a second shell window in one process does not start);
///     Play Mode, the rig, RigLab and the lone Inspector get a window each.
/// </summary>
internal sealed class GuideStages : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "dc-guide-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _owned = [];
    private MainWindow? _shell;
    private ChartLevel? _level;

    public GuideStages() => Directory.CreateDirectory(_scratch);

    /// <summary>Where the editor's timeline and 3D preview look: just before the selected sound.</summary>
    public double FocusMs { get; private set; } = 8_000;

    private static IServiceProvider Services =>
        DotCharter.UI.App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");

    /// <summary>Shows <paramref name="shot" />'s scene and returns the window to photograph and an undo for its state.</summary>
    public async Task<(Window Window, Action Undo)> ShowAsync(GuideShot shot) => shot.Scene switch
    {
        GuideScenes.Editor => Editor(shot),
        GuideScenes.Library => await LibraryAsync(shot),
        GuideScenes.Settings => Settings(shot),
        GuideScenes.Inspector => Inspector(shot),
        GuideScenes.PlayMode => PlayMode(shot),
        GuideScenes.Rig => Rig(shot),
        GuideScenes.RigLab => RigLab(shot),
        _ => throw new InvalidOperationException($"unknown scene '{shot.Scene}'")
    };

    public static void Settle(Window window)
    {
        for (var round = 0; round < 4; round++)
        {
            Dispatcher.UIThread.RunJobs();
            for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    // ── the shell ──────────────────────────────────────────────────────────────

    private MainWindow Shell(GuideShot shot)
    {
        if (_shell is null)
        {
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            foreach (var dialog in vm.Dialogs.AllDialogs)
            {
                if (dialog.IsOpen)
                    dialog.RequestClose();
            }

            vm.CommandPalette.Close();
            vm.IsGameplayPresentation = false;
            var state = Services.GetRequiredService<ProjectState>();
            _level = GuideDemoChart.Seed(state);
            state.SetMessage(string.Empty);
            InspectorCaptureFixture.SeedApp(InspectorCaptureFixture.Inspector, state.ActiveArrangement!, _level);
            InspectorCaptureFixture.Inspector.Issues.RefreshIssues();
            _shell = new MainWindow { DataContext = vm, Width = shot.Width, Height = shot.Height };
            _shell.Show();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        }

        Services.GetRequiredService<ProjectState>().SetMessage(string.Empty);
        _shell.Width = shot.Width;
        _shell.Height = shot.Height;
        return _shell;
    }

    private static MainWindowViewModel ShellVm => Services.GetRequiredService<MainWindowViewModel>();

    private (Window, Action) Editor(GuideShot shot)
    {
        var window = Shell(shot);
        ShellVm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Chart);
        Select(shot.Arg("select", "note"));
        var inspector = InspectorCaptureFixture.Inspector;
        inspector.SelectedTabIndex = int.Parse(shot.Arg("inspectorTab", "0"), System.Globalization.CultureInfo.InvariantCulture);
        Settle(window);
        ClearStatus(window);

        var gameplay = shot.Arg("gameplay") == "true";
        if (gameplay)
        {
            ShellVm.IsGameplayPresentation = true;
            Settle(window);
        }

        var opened = new List<MenuItem>();
        if (shot.Arg("menu") is { Length: > 0 } menuKey)
        {
            var bar = window.GetVisualDescendants().OfType<MainMenuView>().Single();
            var top = bar.TopLevelMenus().Single(m => Equals(m.Header, LocalizationService.Instance[menuKey]));
            MenuFlyoutPrewarmer.Realize(top, new Size(shot.Width, shot.Height));
            top.Open();
            opened.Add(top);
            Settle(window);
            if (shot.Arg("submenu") is { Length: > 0 } subKey)
            {
                var sub = top.Items.OfType<MenuItem>().Single(m => Equals(m.Header, LocalizationService.Instance[subKey]));
                sub.Open();
                opened.Add(sub);
                Settle(window);
            }
        }

        void Undo()
        {
            for (var i = opened.Count - 1; i >= 0; i--)
            {
                opened[i].Close();
                opened[i].IsSelected = false;
            }

            window.GetVisualDescendants().OfType<EditorMenu>().Single().Close();
            if (gameplay)
            {
                // The shell keeps the Playing context after a gameplay presentation, and only a tab
                // CHANGE re-derives it: step off the editor and back to return to Charting.
                ShellVm.IsGameplayPresentation = false;
                ShellVm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Library);
                ShellVm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Chart);
            }

            inspector.SelectedTabIndex = 0;
            Settle(window);
        }

        return (window, Undo);
    }

    private static IList<BeatMarker> Beats => Services.GetRequiredService<ProjectState>().ActiveProject!.Beats;

    /// <summary>The headless session has no GPU interop, so the shell posts "the 3D preview could not
    /// start" once the preview pane shows. True here, not for a reader: the guide's frames paint the
    /// preview offscreen (GuideChartFrames), so the status line is cleared.</summary>
    private static void ClearStatus(Window window)
    {
        Services.GetRequiredService<ProjectState>().SetMessage(string.Empty);
        Settle(window);
    }

    private void Select(string what)
    {
        var selection = Services.GetRequiredService<ISelectionService>();
        object? sound = what switch
        {
            "note" => _level!.Notes.First(n => n.BendValues.Count == 0 && n.Fret > 2 && n.Position.GetPositionMs(Beats) > 6_000),
            "chord" => _level!.Chords.First(c => c.TemplateId >= 0 && c.Position.GetPositionMs(Beats) > 6_000),
            _ => null
        };
        InspectorCaptureFixture.Select(selection, sound);
        if (sound is GuitarNote note) FocusMs = note.Position.GetPositionMs(Beats);
        else if (sound is Chord chord) FocusMs = chord.Position.GetPositionMs(Beats);
    }

    private async Task<(Window, Action)> LibraryAsync(GuideShot shot)
    {
        var window = Shell(shot);
        ShellVm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Library);
        Settle(window);
        var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
        var own = view.DataContext;

        var harness = await SongBrowserFilterChipsTests.Harness.Scanned();
        harness.Vm.PsarcLocation = @"D:\Music\Songs";
        harness.Library.Result = GuideSongs.Library();
        await harness.Vm.ScanCommand.ExecuteAsync(null);
        harness.Drain();
        var screen = new LibraryScreen(harness.Vm);
        screen.SelectedRow = screen.Rows.OfType<LibraryRow>().FirstOrDefault(r => r.Title == GuideSongs.Featured) ?? screen.Rows.FirstOrDefault();
        view.DataContext = screen;
        Settle(window);
        ClearStatus(window);
        void Undo()
        {
            view.DataContext = own;
            ShellVm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Chart);
            Settle(window);
        }

        return (window, Undo);
    }

    private (Window, Action) Settings(GuideShot shot)
    {
        var window = Shell(shot);
        ShellVm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Settings);
        Settle(window);
        var shellView = window.GetVisualDescendants().OfType<SettingsShellView>().First(v => v.IsEffectivelyVisible);
        var settings = (SettingsShellViewModel)shellView.DataContext!;
        settings.Select(shot.Arg("tab", SettingsTabKeys.General));
        Settle(window);
        ClearStatus(window);
        void Undo()
        {
            settings.Select(SettingsTabKeys.General);
            ShellVm.Go(DotCharter.Presentation.Contracts.Shell.ShellDestination.Chart);
            Settle(window);
        }

        return (window, Undo);
    }

    // ── views in their own window ─────────────────────────────────────────────

    private (Window, Action) Inspector(GuideShot shot)
    {
        Shell(shot);
        Select(shot.Arg("select", "note"));
        var vm = InspectorCaptureFixture.Inspector;
        vm.SelectedTabIndex = int.Parse(shot.Arg("tab", "0"), System.Globalization.CultureInfo.InvariantCulture);
        var window = InspectorCaptureFixture.NewWindow(shot.Width, shot.Height, new InspectorView { DataContext = vm });
        window.Show();
        Settle(window);
        void Undo()
        {
            vm.SelectedTabIndex = 0;
            window.Close();
        }

        return (window, Undo);
    }

    private (Window, Action) PlayMode(GuideShot shot)
    {
        var dir = Path.Combine(_scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(dir, "player-profiles.json"), defaultProfileName: "Player 1");
        var store = new PracticeProfileService(profiles, logger: null, legacyFilePath: Path.Combine(dir, "practice-profiles.json"));
        var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store), new LeaderboardService(profiles), new ProjectState(),
            toneRig: HeadlessToneRig.FromSession(new ProjectState()), localization: LocalizationService.Instance);
        vm.SongSelect.SetLibraryForTest([.. GuideSongs.Library().Select(e => PlaySongItem.FromPsarc(new PsarcSongListItem(e, LocalizationService.Instance)))]);

        var view = new PlayModeView { DataContext = vm };
        var window = new Window { Width = shot.Width, Height = shot.Height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        window.UpdateLayout();

        var screen = Enum.Parse<PlayModeScreen>(shot.Arg("screen", nameof(PlayModeScreen.SongSelect)));
        if (screen != PlayModeScreen.Start) vm.CurrentScreen = PlayModeScreen.SongSelect;
        Settle(window);
        if (screen != PlayModeScreen.Start)
            vm.SelectedSong = vm.SongSelect.SongList.FirstOrDefault(s => s.Title == GuideSongs.Featured) ?? vm.SongSelect.SongList[0];
        if (screen == PlayModeScreen.Results) FinishRun(vm, store);
        else if (screen != PlayModeScreen.SongSelect) vm.CurrentScreen = screen;
        if (shot.Arg("overlay") == "settings") vm.Settings.OpenGameplaySettingsCommand.Execute(null);
        Settle(window);
        void Undo()
        {
            if (vm.CurrentScreen == PlayModeScreen.Results) vm.CurrentScreen = PlayModeScreen.SongSelect;
            window.Close();
        }

        return (window, Undo);
    }

    /// <summary>A finished run, through Play Mode's own session-end handler (as the Results smoke test does).</summary>
    private static void FinishRun(PlayModeViewModel vm, PracticeProfileService store)
    {
        var result = new LivePlaySessionResult(184_200, 0.92, 92, 8, 100, 41, 180, 100, 1.0);
        store.Record("lib:guide", "Lead:Lead", result, displayName: $"The Night Shift - {GuideSongs.Featured}");
        vm.CurrentScreen = PlayModeScreen.Playing;
        var bests = new PracticeBests(150_000, 0.85, 30, 0.4);
        SectionResult[] sections =
        [
            new("Intro", 0, 12_000, 18, 18, 0), new("Verse", 12_000, 40_000, 40, 37, 3),
            new("Chorus", 40_000, 62_000, 26, 22, 4), new("Solo", 62_000, 80_000, 16, 15, 1)
        ];
        var report = new LivePlaySessionReport(result, bests, bests, WasRecorded: true, "lib:guide", "Lead:Lead", sections);
        typeof(PlayModeViewModel).GetMethod("OnSessionCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [report]);
    }

    private (Window, Action) Rig(GuideShot shot)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "the native DSP catalogue (juce_sharp.dll) is not loadable here");
        var chain = new FakeDspChainService(catalogue);
        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance);
        _owned.Add(rig);
        foreach (var id in new[] { "gate", "overdrive", "namamp", "cab", "chorus", "delay", "reverb" })
            rig.AddGear(catalogue.Single(m => m.Id == id));
        rig.SelectNodeAt(rig.Nodes.ToList().FindIndex(n => n.ModuleTypeId == shot.Arg("select", "overdrive")));
        var model = new AmpFxRigViewModel(rig);
        if (shot.Arg("state") == "tones") rig.ToggleBrowserCommand.Execute(null);
        var view = new AmpFxRigView { DataContext = model };
        var window = new Window { Width = shot.Width, Height = shot.Height, Content = view };
        window.Show();
        Settle(window);
        view.FindControl<Button>("RigFitButton")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);
        return (window, window.Close);
    }

    private (Window, Action) RigLab(GuideShot shot)
    {
        var dir = Path.Combine(_scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new RigLabConfigService(Path.Combine(dir, RigLabConfigService.FileName), TimeSpan.Zero);
        var host = new RigLabHost(config, new GatedInlineDispatcher(), useNullAudio: true, namLibraryRoot: Path.Combine(dir, "nam"));
        _owned.Add(host);
        _owned.Add(config);
        var lab = new RigLabShellViewModel(host.Screen, host.Plugins, host.Settings, host.Ai)
        {
            Tab = Enum.Parse<RigLabTab>(shot.Arg("tab", nameof(RigLabTab.Rig)))
        };
        var window = new RigLabWindow { DataContext = lab, Width = shot.Width, Height = shot.Height };
        window.Show();
        Settle(window);
        return (window, window.Close);
    }

    public void Dispose()
    {
        // The shell window is left for the session's teardown to close: closing a
        // MainWindow mid-run ends the testhost before the run can report.
        _shell = null;
        foreach (var owned in Enumerable.Reverse(_owned)) owned.Dispose();
        _owned.Clear();
        BestEffortDelete.Directory(_scratch);
    }
}

/// <summary>Invented songs for the Library and Song Select shots: no real artists, nothing private.</summary>
internal static class GuideSongs
{
    public const string Featured = "Northern Lights";

    public static List<LibrarySongEntry> Library() =>
    [
        Song("northern", Featured, "The Night Shift", "Afterglow", 2019, "E A D G B E", 0, 7, "Lead,Rhythm,Bass"),
        Song("harbour", "Harbour Lights", "Paper Satellites", "Low Tide", 2021, "Eb Ab Db Gb Bb Eb", 0, 5, "Lead,Rhythm"),
        Song("copper", "Copper Sky", "Glass Animals Club", "Copper Sky", 2016, "D A D G B E", 0, 6, "Lead,Bass"),
        Song("velvet", "Velvet Machine", "Static Garden", "Night Drive", 2014, "E A D G B E", 2, 4, "Rhythm,Bass"),
        Song("ember", "Ember & Ash", "Hollow Pines", "Kindling", 2022, "C G C F A D", 0, 8, "Lead,Rhythm,Bass"),
        Song("signal", "Signal Fire", "The Wayfarers", "North Road", 2010, "E A D G B E", 0, 3, "Lead,Rhythm"),
        Song("mirror", "Mirror Lake", "Lantern Bay", "Still Water", 2018, "D G C F A D", 0, 6, "Lead"),
        Song("riot", "Quiet Riot Season", "Neon Harbour", "Overdrive", 2012, "E A D G B E", 0, 9, "Lead,Rhythm,Bass"),
        Song("paper", "Paper Crowns", "Kite Theory", "Paper Crowns", 2020, "E A D G B E", 3, 2, "Rhythm"),
        Song("thunder", "Thunder Road Revival", "Iron Orchard", "Revival", 2008, "Eb Ab Db Gb Bb Eb", 0, 7, "Lead,Bass"),
        Song("glasshouse", "Glasshouse", "Mile Markers", "Signals", 2015, "E A D G B E", 0, 5, "Lead,Rhythm,Bass"),
        Song("drift", "Continental Drift", "Polar Atlas", "Meridian", 2023, "B E A D F# B", 0, 8, "Lead,Rhythm")
    ];

    private static LibrarySongEntry Song(string key, string title, string artist, string album, int year, string tuning, int capo,
        int difficulty, string paths)
    {
        var arrangements = paths.Split(',')
            .Select(p => new LibraryArrangementEntry { ArrangementName = p, SngPath = $"s/{key}_{p}.sng", NoteCount = 300 + (p.Length * 41) });
        return new LibrarySongEntry
        {
            SongKey = key,
            Title = title,
            Artist = artist,
            Album = album,
            Year = year,
            SongLength = 180 + (key.Length * 17),
            TuningSummary = tuning,
            CapoSummary = capo,
            Difficulty = difficulty,
            Charter = "Guide",
            PackagePath = $@"D:\Music\Songs\{key}.dcsong",
            Arrangements = [.. arrangements]
        };
    }
}
