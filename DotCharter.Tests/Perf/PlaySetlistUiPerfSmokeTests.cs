using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Setlists;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tests.UI.ViewModels.PlayMode;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Play Mode setlists (#646) in the real <see cref="PlayModeView" />, attached to
///     a window and laid out: Song Select's setlist panel in the detail rail (empty,
///     and with a list of long titles that must stay inside the rail), Results with
///     "Next: &lt;song&gt;" as the primary action and then the setlist-complete
///     line, Pause's Skip and End, and Get Ready naming a skipped song. The song
///     flow is the real one, over <see cref="TwoPathChartSession" />.
///     <para>
///         It compares widths (the rail's rows), so it is in <c>$HeadlessLayoutFilter</c>
///         and re-runs in de-DE and ja-JP before a release. With
///         <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> the capture case writes
///         <c>TestResults/setlist-capture/*-&lt;culture&gt;.png</c> for review.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class PlaySetlistUiPerfSmokeTests : IDisposable
{
    private const double WindowWidth = 1600;
    private const double WindowHeight = 1000;

    /// <summary>A 1280×720 screen less the window chrome and the taskbar.</summary>
    private const double SmallWidth = 1280;
    private const double SmallHeight = 660;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", "setlist-ui-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SongSelect_SetlistPanel_LaysOutItsEmptyState()
    {
        await WithView(async (view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            view.UpdateLayout();

            AssertLaidOut(view, "SongSelectSetlistPanel");
            AssertLaidOut(view, "SetlistNoneText");
            AssertLaidOut(view, "SetlistAddButton");
            AssertLaidOut(view, "SetlistPlayButton");
            Assert.False(Find(view, "SetlistPicker").IsVisible);
            Assert.False(Find(view, "SetlistPlayButton").IsEnabled, "an empty setlist cannot be played");
            Assert.False(Find(view, "SetlistAddButton").IsEnabled, "no song is picked yet");
            AssertInside(Find(view, "SongSelectDetailRail"), Find(view, "SongSelectSetlistPanel"), "the setlist panel");
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task SongSelect_SetlistPanel_ListsItsSongsInsideTheRail()
    {
        await WithView(async (view, vm) =>
        {
            UseList(vm, LongList());
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            view.UpdateLayout();

            AssertLaidOut(view, "SetlistPicker");
            AssertLaidOut(view, "SetlistNameBox");
            AssertLaidOut(view, "SetlistEntries");
            Assert.True(Find(view, "SetlistPlayButton").IsEnabled);
            var rail = Find(view, "SongSelectDetailRail");
            var rows = Find(view, "SetlistEntries").GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.DataContext is PlaySetlistEntryRow && t.IsEffectivelyVisible).ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, t => Assert.True(t.Bounds.Width > 1, $"'{t.Text}' laid out as {t.Bounds}"));
            string[] parts =
                ["SetlistPicker", "SetlistNameBox", "SetlistEntries", "SetlistAddButton", "SetlistPlayButton", "SetlistDeleteButton"];
            foreach (var name in parts) AssertInside(rail, Find(view, name), name);

            // A rename reaches the closed picker's own text, not only the field.
            vm.Setlists.SelectedListName = "Saturday gig";
            view.UpdateLayout();
            Assert.Contains(Find(view, "SetlistPicker").GetVisualDescendants().OfType<TextBlock>(),
                t => t.IsEffectivelyVisible && t.Text == "Saturday gig");
            await Task.CompletedTask;
        });
    }

    /// <summary>A picked song's details and a full setlist share the rail: at the
    /// smallest window Play Mode is laid out for, the song's Play button and every
    /// part of the panel still land inside it.</summary>
    [Fact]
    public async Task SongSelect_WithASongPickedAndALongList_KeepsPlayAndThePanelInsideTheRail()
    {
        await WithView(async (view, vm) =>
        {
            UseList(vm, LongList());
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            Pick(vm);
            view.UpdateLayout();

            var rail = Find(view, "SongSelectDetailRail");
            var play = Find(view, "SongSelectPlayButton");
            AssertLaidOut(view, "SongSelectPlayButton");
            AssertInside(rail, play, "the song's Play button");
            // Laid out is not shown: an over-tall panel squeezes the details row
            // to nothing and the Play button draws UNDER it. It must end above
            // the panel's top.
            var playBottom = play.TranslatePoint(new Point(0, play.Bounds.Height), rail)!.Value.Y;
            var panelTop = Find(view, "SongSelectSetlistPanel").TranslatePoint(default, rail)!.Value.Y;
            Assert.True(playBottom <= panelTop + 1,
                $"the song's Play button ends at {playBottom}, under the setlist panel starting at {panelTop}");
            string[] parts = ["SetlistPicker", "SetlistBodyScroll", "SetlistAddButton", "SetlistPlayButton"];
            foreach (var name in parts)
            {
                AssertLaidOut(view, name);
                AssertInside(rail, Find(view, name), name);
            }

            Assert.True(Find(view, "SetlistAddButton").IsEnabled, "a playable song is picked");
            await Task.CompletedTask;
        }, SmallWidth, SmallHeight);
    }

    [Fact]
    public async Task Results_WithASetlistRunning_MakesNextThePrimaryAction_ThenSaysItIsDone()
    {
        await WithView(async (view, vm) =>
        {
            UseList(vm, Entry("First song", "Lead"), Entry("Second song", "Bass"));
            await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
            view.UpdateLayout();
            AssertLaidOut(view, "GetReadySetlistProgress");

            vm.CurrentScreen = PlayModeScreen.Results;
            view.UpdateLayout();
            AssertLaidOut(view, "ResultsSetlistNext");
            AssertLaidOut(view, "ResultsSetlistProgress");
            AssertLaidOut(view, "ResultsSetlistEnd");
            var next = Find<Button>(view, "ResultsSetlistNext");
            Assert.Equal(vm.Setlists.NextLabel,
                next.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Assert.False(Find(view, "ResultsSetlistComplete").IsVisible);
            var actions = Find(view, "ResultsActions");
            Assert.DoesNotContain(actions.GetVisualDescendants().OfType<Button>(),
                b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, vm.BackToSongSelectCommand));

            await vm.Setlists.NextSetlistSongCommand.ExecuteAsync(null);
            vm.CurrentScreen = PlayModeScreen.Results;
            view.UpdateLayout();

            AssertLaidOut(view, "ResultsSetlistComplete");
            Assert.False(Find(view, "ResultsSetlistNext").IsVisible);
            Assert.Contains(actions.GetVisualDescendants().OfType<Button>(),
                b => b.IsEffectivelyVisible && ReferenceEquals(b.Command, vm.BackToSongSelectCommand));
            LeaveResults(view, vm);
        });
    }

    [Fact]
    public async Task Pause_OffersSkipAndEnd_OnlyWhileASetlistRuns()
    {
        await WithView(async (view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.Paused;
            view.UpdateLayout();
            Assert.False(Find(view, "PauseSetlistRow").IsVisible);

            UseList(vm, Entry("First song", "Lead"), Entry("Second song", "Lead"));
            await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
            vm.CurrentScreen = PlayModeScreen.Paused;
            view.UpdateLayout();

            AssertLaidOut(view, "PauseSetlistRow");
            AssertLaidOut(view, "PauseSetlistSkip");
            AssertLaidOut(view, "PauseSetlistEnd");
            AssertLaidOut(view, "PauseSetlistProgress");
            // The pause card is a fixed 620 px: both verbs, German included, fit it.
            var card = Find(view, "PauseScreenRoot");
            foreach (var name in new[] { "PauseSetlistSkip", "PauseSetlistEnd", "PauseSetlistProgress" })
                AssertInside(card, Find(view, name), name);
            foreach (var name in new[] { "PauseSetlistSkip", "PauseSetlistEnd" })
            {
                var label = Find(view, name).GetVisualDescendants().OfType<TextBlock>().First();
                Assert.True(label.DesiredSize.Width <= label.Bounds.Width + 1,
                    $"{name}'s label wants {label.DesiredSize.Width} px and has {label.Bounds.Width} ({HeadlessSessionFixture.SweptCulture})");
            }
        });
    }

    [Fact]
    public async Task GetReady_NamesASongTheSetlistSkipped()
    {
        await WithView(async (view, vm) =>
        {
            UseList(vm, Entry("Moved away", "Lead", exists: false), Entry("Still here", "Lead"));
            await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
            view.UpdateLayout();

            Assert.Equal(PlayModeScreen.TuningCheck, vm.CurrentScreen);
            AssertLaidOut(view, "GetReadySetlistSkipped");
            Assert.Contains("Moved away", Find<TextBlock>(view, "GetReadySetlistSkipped").Text);
        });
    }

    /// <summary>Song Select with the picker open on a list (with and without a
    /// picked song, and at the smallest window), and Results with "Next:",
    /// written as PNGs for review. Pause is not captured: an offscreen bitmap
    /// draws its Viewbox-hosted card as a mis-placed slice, with or without a
    /// setlist; its fit is asserted by <c>Pause_OffersSkipAndEnd_OnlyWhileASetlistRuns</c>. Only with
    /// <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (real Skia drawing);
    /// <c>DOTCHARTER_HEADLESS_CULTURE</c> picks the language. A review aid, never
    /// the assertion.</summary>
    [Fact]
    public async Task Capture_SongSelectPickerAndResultsNext()
    {
        Assert.SkipWhen(!UiSweepRunner.CaptureEnabled,
            $"set {UiSweepRunner.CaptureEnvVar}=1 — captures need real Skia drawing");
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults", "setlist-capture");
        Directory.CreateDirectory(dir);
        var culture = HeadlessSessionFixture.SweptCulture;

        await WithView(async (view, vm) =>
        {
            UseList(vm, LongList());
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            view.UpdateLayout();
            Save(view, Path.Combine(dir, $"songselect-setlist-{culture}.png"));
            Pick(vm);
            view.UpdateLayout();
            Save(view, Path.Combine(dir, $"songselect-picked-{culture}.png"));

            vm.Setlists.SelectedList = vm.Setlists.Lists[^1];
            await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
            vm.CurrentScreen = PlayModeScreen.Results;
            view.UpdateLayout();
            Save(view, Path.Combine(dir, $"results-next-{culture}.png"));
            LeaveResults(view, vm);
        });

        // The smallest window, a song picked: the rail is shared.
        await WithView(async (view, vm) =>
        {
            UseList(vm, LongList());
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            Pick(vm);
            view.UpdateLayout();
            Save(view, Path.Combine(dir, $"songselect-picked-small-{culture}.png"));

            // Get Ready after the list's first song was skipped as missing.
            vm.Setlists.SelectedList = vm.Setlists.Lists[0];
            vm.Setlists.Lists[0].Setlist.Entries.Insert(0, Entry("Moved Away: A Song That Left The Library", "Lead", exists: false));
            await vm.Setlists.StartSetlistCommand.ExecuteAsync(null);
            view.UpdateLayout();
            Save(view, Path.Combine(dir, $"getready-skipped-{culture}.png"));
        }, SmallWidth, SmallHeight);
    }

    // ── harness ────────────────────────────────────────────────────────────

    private static void Save(PlayModeView view, string path)
    {
        // The Pause card sits in a Viewbox sized from the live surface: let its
        // bindings and a few layout passes settle, or it draws mid-measure.
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        for (var pass = 0; pass < 3; pass++) view.UpdateLayout();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        view.UpdateLayout();
        // 2x so a 3x crop of the result still reads.
        const double scale = 2;
        var size = new PixelSize((int)(view.Bounds.Width * scale), (int)(view.Bounds.Height * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(view);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    private PlaySetlistEntry[] LongList() =>
    [
        Entry("Through the Fire and Flames (Extended Live Version)", "Lead", 80, artist: "DragonForce"),
        Entry("Schwanensee, Op. 20 — Szene am See (Bearbeitung für Gitarre)", "Rhythm", artist: "Tschaikowski"),
        Entry("Gone", "Bass", 60, exists: false, artist: "Somebody"),
        Entry("Short", "Lead")
    ];

    private PlaySetlistEntry Entry(string name, string arrangement, int speed = 100, bool exists = true,
        string artist = "")
    {
        Directory.CreateDirectory(_dir);
        // The chart's file name is the title Get Ready and Results print.
        var folder = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".rscp");
        if (exists) File.WriteAllText(path, "");
        return new PlaySetlistEntry
        {
            SongKey = path, Arrangement = arrangement, SpeedPercent = speed, Title = name, Artist = artist
        };
    }

    /// <summary>Song Select's pick: a playable own chart with a long title.</summary>
    private void Pick(PlayModeViewModel vm)
    {
        var entry = Entry("A Song With A Rather Long Title, Picked On Song Select", "Lead");
        vm.SelectedSong = PlaySongItem.FromProject(entry.SongKey, true, LocalizationService.Instance);
    }

    private static void UseList(PlayModeViewModel vm, params PlaySetlistEntry[] entries)
    {
        var name = LocalizationService.Instance.Format("PlayMode_Setlist_DefaultName", 1);
        vm.Setlists.Lists.Add(new PlaySetlistOption(new PlaySetlist { Name = "Friday rehearsal", Entries = [.. entries] }));
        vm.Setlists.Lists.Add(new PlaySetlistOption(
            new PlaySetlist { Name = name, Entries = [.. entries.Where(e => File.Exists(e.SongKey)).Take(2)] }));
        vm.Setlists.SelectedList = vm.Setlists.Lists[0];
    }

    private static void LeaveResults(PlayModeView view, PlayModeViewModel vm)
    {
        vm.CurrentScreen = PlayModeScreen.SongSelect;
        view.UpdateLayout();
    }

    private async Task WithView(Func<PlayModeView, PlayModeViewModel, Task> body,
        double width = WindowWidth, double height = WindowHeight)
    {
        await HeadlessSessionFixture.Dispatch(async () =>
        {
            Directory.CreateDirectory(_dir);
            var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
            var store = new PracticeProfileService(profiles, logger: null,
                legacyFilePath: Path.Combine(_dir, "practice-profiles.json"));
            var state = new ProjectState();
            var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
                new LeaderboardService(profiles), state, LocalizationService.Instance, projectSession: new TwoPathChartSession(state), toneRig: HeadlessToneRig.FromSession(state));

            var view = new PlayModeView { DataContext = vm };
            var window = new Window
            {
                Width = width, Height = height, Content = view, RequestedThemeVariant = ThemeVariant.Dark
            };
            window.Show();
            try
            {
                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                await body(view, vm);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static T Find<T>(PlayModeView view, string name) where T : Control =>
        Assert.IsAssignableFrom<T>(Find(view, name));

    private static Control Find(PlayModeView view, string name)
    {
        var control = view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        Assert.True(control != null, $"'{name}' is not in the visual tree — the screen never realized.");
        return control!;
    }

    private static void AssertLaidOut(PlayModeView view, string name)
    {
        var control = Find(view, name);
        Assert.True(control.IsEffectivelyVisible, $"'{name}' realized but is not visible");
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"'{name}' laid out as {control.Bounds} — it rendered nothing");
    }

    /// <summary>A descendant inside its region, 1 px of tolerance for hairlines.</summary>
    private static void AssertInside(Control region, Control child, string what)
    {
        var topLeft = child.TranslatePoint(default, region);
        var bottomRight = child.TranslatePoint(new Point(child.Bounds.Width, child.Bounds.Height), region);
        Assert.True(topLeft.HasValue && bottomRight.HasValue, $"{what} is not under its region");
        Assert.True(topLeft!.Value.X >= -1 && topLeft.Value.Y >= -1,
            $"{what} starts at {topLeft.Value} ({HeadlessSessionFixture.SweptCulture})");
        Assert.True(bottomRight!.Value.X <= region.Bounds.Width + 1 && bottomRight.Value.Y <= region.Bounds.Height + 1,
            $"{what} reaches {bottomRight.Value} in a {region.Bounds.Size} region ({HeadlessSessionFixture.SweptCulture})");
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
