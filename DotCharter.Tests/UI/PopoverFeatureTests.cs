using System.Globalization;
using System.Xml.Linq;
using DotCharter.Localization;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.Tuning;
using DotCharter.Tone.Audio.Tuning;
using DotCharter.Tone.Audio.Vst;
using DotCharter.Services.Editing.MultiTrack;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.Converters;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The six things `Play Menus.dc.html` draws that the first pass at these
///     popovers declared "not built" — each one wired to a real verb, and each
///     one with the test that fails if it goes back to being drawn.
/// </summary>
/// <remarks>
///     <para>
///         This is the file that answers "would anything fail if this
///         regressed?" for the popover feature work. It is in the DEFAULT gate:
///         every case here is a view model or a pure function, so none of it
///         needs a platform.
///     </para>
///     <para>
///         The drawn-but-dead shape is the one this repo has shipped three
///         times (<c>CoopRigApplier</c>, the 0.22 power tools, couch co-op's own
///         entry point), and a control is exactly as dead when its command is
///         missing as when its service is unresolved. So the assertions are on
///         the VERB and its effect, never on "the control exists".
///     </para>
/// </remarks>
// Mutates the AMBIENT culture, which is process-wide, so it shares the
// serialized collection with the other culture tests. See
// Infrastructure/CultureCollectionLintTests.
[Collection("LocalizationSingleton")]
public sealed class PopoverFeatureTests
{
    // ── 1. Mix: the INPUT LEVEL meter ───────────────────────────────────────

    [Fact]
    public void TheMixMeter_readsTheEnginesCaptureWindow_andIsSilentUntilItHears()
    {
        var h = new Harness();

        Assert.True(double.IsNegativeInfinity(h.Transport.InputLevelDb));
        h.Transport.PollInputMeter();
        Assert.True(double.IsNegativeInfinity(h.Transport.InputLevelDb));

        // −6 dBFS is 0.5 linear.
        h.Audio.InputCaptureWindow = [0.1f, -0.5f, 0.25f];
        h.Transport.PollInputMeter();

        Assert.Equal(-6.02, h.Transport.InputLevelDb, 1);
        Assert.False(h.Transport.IsInputClipping);
        Assert.Contains("dB PEAK", h.Transport.InputPeakLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMixMeter_reportsClippingAtFullScale()
    {
        var h = new Harness();
        h.Audio.InputCaptureWindow = [1.0f];
        h.Transport.PollInputMeter();

        Assert.Equal(0.0, h.Transport.InputLevelDb, 3);
        Assert.True(h.Transport.IsInputClipping);
    }

    // ── 2. Tempo: "Keep pitch when slowing down" ────────────────────────────

    /// <summary>
    ///     Tempo reads in the unit its presets use: "100 %", beside presets of 50 / 65 / 80 / 100 %.
    ///     It read "1,00x" next to them, and its reset said "1.00x" with the other decimal separator
    ///     (2026-09-23). The always-on "Keep pitch when slowing down" chip went at the same time: a
    ///     switch that can never change is noise, and its useful sentence moved to the Pitch tooltip.
    /// </summary>
    [Fact]
    public void Tempo_ReadsInPercent_AndOffersNoSwitchItCannotHonour()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Shell", "Popovers",
            "TempoPopoverView.axaml")));
        Assert.Contains("Transport.PlaybackSpeed, StringFormat=", markup, StringComparison.Ordinal);
        Assert.Contains("0:P0", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("0:0.00", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("KeepPitch", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("IsEnabled=\"False\"", markup, StringComparison.Ordinal);
    }

    // ── 3. Tools: the live IN TUNE readout ──────────────────────────────────

    /// <summary>
    ///     The readout runs without the dialog: it holds its own lease on the
    ///     shared <c>LiveTuner</c>, and leases are REFERENCE COUNTED — the popover
    ///     and the dialog can both be up, and the first to close must not stop the
    ///     other's reading.
    /// </summary>
    [Fact]
    public void TheTunerReadout_runsWithoutTheDialog_andStopsOnlyWhenNothingWantsIt()
    {
        var h = new Harness();
        var tuner = h.NewTuner();
        var readout = new TunerReadout(tuner, LocalizationService.Instance);

        Assert.False(tuner.IsRunning);

        var popover = readout.Begin();
        Assert.NotNull(popover);
        Assert.True(tuner.IsRunning);

        var dialog = tuner.Acquire(fresh: true);
        popover!.Dispose();
        Assert.True(tuner.IsRunning);

        dialog!.Dispose();
        Assert.False(tuner.IsRunning);
    }

    [Fact]
    public void TheTunerReadout_isAnEmDashUntilSomethingIsHeard()
    {
        var h = new Harness();
        var tuner = h.NewTuner();
        var readout = new TunerReadout(tuner, LocalizationService.Instance);
        using var lease = readout.Begin();
        var a2 = ArrangementTunerLogic.FrequencyOf(45);

        Assert.True(readout.IsSilent);
        Assert.Equal("—", readout.Text);

        tuner.Hear(a2 * Math.Pow(2, 13 / 1200.0), 66);
        Assert.False(readout.IsSilent);
        Assert.False(readout.IsInTune);
        Assert.Equal("+13 ¢", readout.Text);

        tuner.Hear(a2, 66);
        Assert.True(readout.IsInTune);
        Assert.Equal(LocalizationService.Instance["PlayMode_GetReady_Tuner_InTune"], readout.Text);
    }

    /// <summary>A VOCAL arrangement has no strings to tune, and a popover
    /// opening is not the user asking for a tuner — so monitoring declines
    /// QUIETLY rather than pushing a status-bar message.</summary>
    [Fact]
    public void TheTunerReadout_declinesQuietlyWhenThereIsNothingToTuneAgainst()
    {
        var h = new Harness();
        h.State.ActiveArrangement =
            new Arrangement { Name = "Vocals", ArrangementType = ArrangementType.Vocal };

        var tuner = h.NewTuner();
        var message = h.State.MainDisplayMessage;

        Assert.Null(new TunerReadout(tuner, LocalizationService.Instance).Begin());
        Assert.False(tuner.IsRunning);
        Assert.Equal(message, h.State.MainDisplayMessage);
    }

    // ── 4. Tools: "Rescan plugin folders" ───────────────────────────────────

    [Fact]
    public async Task RescanPluginFolders_reallyReadsTheFoldersAgain()
    {
        var h = new Harness();
        await h.Transport.VstStartupTask;

        var scansAfterStartup = h.Audio.VstScans;
        Assert.Equal(1, h.Transport.VstPluginScans);

        // A plugin installed while the app is running.
        h.Audio.VstPluginsOnDisk =
        [
            new VstPluginInfo { Name = "TS-9", PluginPath = @"C:\VST3\TS-9.vst3" },
            new VstPluginInfo { Name = "Room", PluginPath = @"C:\VST3\Room.vst3" }
        ];

        await h.Transport.RescanVstPluginsCommand.ExecuteAsync(null);

        Assert.Equal(scansAfterStartup + 1, h.Audio.VstScans);
        Assert.Equal(2, h.Transport.VstPluginScans);
        Assert.Contains(h.Transport.AvailableVstPlugins, p => p.Name == "TS-9");
        Assert.Contains(h.Transport.AvailableVstPlugins, p => p.Name == "Room");

        // The "None" sentinel is re-seeded exactly once — the picker's own
        // "no plugin" row, which a naive rebuild drops or duplicates.
        Assert.Single(h.Transport.AvailableVstPlugins, p => p.PluginPath.Length == 0);
        Assert.False(h.Transport.IsRescanningVstPlugins);
    }

    /// <summary>A stale VST3 folder throws in the real scan. A rescan that
    /// throws must leave the list it already had, not empty it.</summary>
    [Fact]
    public async Task RescanPluginFolders_keepsTheOldListWhenTheFoldersCannotBeRead()
    {
        var h = new Harness();
        await h.Transport.VstStartupTask;

        h.Audio.VstPluginsOnDisk = [new VstPluginInfo { Name = "TS-9", PluginPath = @"C:\VST3\TS-9.vst3" }];
        await h.Transport.RescanVstPluginsCommand.ExecuteAsync(null);
        Assert.Contains(h.Transport.AvailableVstPlugins, p => p.Name == "TS-9");

        h.Audio.ThrowOnScanVstPlugins = true;
        await h.Transport.RescanVstPluginsCommand.ExecuteAsync(null);

        Assert.Contains(h.Transport.AvailableVstPlugins, p => p.Name == "TS-9");
        Assert.False(h.Transport.IsRescanningVstPlugins);
    }

    // ── 5. Tools: drag-to-reorder the VST3 chain ────────────────────────────

    /// <summary>
    ///     A gap index is <c>[0, count]</c> and a position is
    ///     <c>[0, count-1]</c>: dropping BELOW your own row means the row above
    ///     the gap has already shifted up. This is the one place a reorder
    ///     silently lands one row off.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 3, 0)]
    [InlineData(0, 1, 3, 0)] // the gap directly below itself is a no-op
    [InlineData(0, 2, 3, 1)]
    [InlineData(0, 3, 3, 2)] // dropped at the very bottom
    [InlineData(2, 0, 3, 0)] // dragged to the very top
    [InlineData(2, 1, 3, 1)]
    [InlineData(1, 3, 3, 2)]
    [InlineData(0, 5, 3, 2)] // clamped
    [InlineData(0, 0, 0, 0)] // empty chain
    public void TheDragIndex_convertsAGapToAPosition(int from, int insertion, int count, int expected)
        => Assert.Equal(expected, VstChainDragController.TargetIndex(from, insertion, count));

    [Fact]
    public async Task MoveGuitarSlot_reordersTheChain_renumbersIt_andPushesTheWholeThingAtTheEngine()
    {
        var h = new Harness();
        await h.Transport.VstStartupTask;
        await AddThreeSlots(h);

        var pushesBefore = h.Audio.PushedGuitarChains.Count;
        var moved = h.Transport.GuitarVstChain[0];

        Assert.True(h.Transport.MoveGuitarSlot(0, 2));

        Assert.Equal(2, h.Transport.GuitarVstChain.IndexOf(moved));
        Assert.Equal([1, 2, 3], h.Transport.GuitarVstChain.Select(s => s.DisplayIndex).ToArray());

        // The reorder is only REAL once the engine has been told, and it is told
        // the whole list — `SetGuitarVstChain` diffs host-side, so an arbitrary
        // permutation costs what a neighbour swap costs.
        Assert.Equal(pushesBefore + 1, h.Audio.PushedGuitarChains.Count);
        Assert.Equal(moved.PluginPath, h.Audio.PushedGuitarChains[^1][2].PluginPath);
    }

    /// <summary>A drag that ends where it started must not write the config file
    /// or re-push the chain.</summary>
    [Fact]
    public async Task MoveGuitarSlot_doesNothingForANoOpOrAnOutOfRangeMove()
    {
        var h = new Harness();
        await h.Transport.VstStartupTask;
        await AddThreeSlots(h);

        var pushes = h.Audio.PushedGuitarChains.Count;

        Assert.False(h.Transport.MoveGuitarSlot(1, 1));
        Assert.False(h.Transport.MoveGuitarSlot(-1, 0));
        Assert.False(h.Transport.MoveGuitarSlot(0, 9));

        Assert.Equal(pushes, h.Audio.PushedGuitarChains.Count);
    }

    /// <summary>The seat travels with every copy of a slot — a reorder that
    /// dropped it would re-seat a co-op player's chain onto player 1.</summary>
    [Fact]
    public async Task MoveGuitarSlot_carriesEachSlotsSeat()
    {
        var h = new Harness();
        await h.Transport.VstStartupTask;
        await AddThreeSlots(h);

        h.Transport.MoveGuitarSlot(2, 0);

        Assert.All(h.Audio.PushedGuitarChains[^1], e => Assert.Equal(0, e.Player));
    }

    // ── 6. Tracks: Solo (and what Mute honestly means) ──────────────────────

    /// <summary>
    ///     SOLO here is a TIMELINE solo — on, nothing but the arrangement you are
    ///     editing is drawn — and it is REVERSIBLE: turning it off must put back
    ///     exactly the overlays it hid, not "restore" to nothing.
    /// </summary>
    [Fact]
    public void TracksSolo_hidesEveryOtherArrangement_andPutsBackExactlyWhatItHid()
    {
        var ghosts = new GhostArrangementsService();
        var stacked = new StackedArrangementsService();
        var rhythm = new Arrangement { Name = "Rhythm" };
        var bass = new Arrangement { Name = "Bass" };

        ghosts.SetVisible(rhythm, true);
        stacked.SetStacked(bass, true);

        var (hiddenGhosts, hiddenStacks) = TimelineSolo.Engage(ghosts, stacked);

        Assert.Empty(ghosts.VisibleGhosts);
        Assert.Empty(stacked.StackedArrangements);
        Assert.Equal([rhythm], hiddenGhosts);
        Assert.Equal([bass], hiddenStacks);

        TimelineSolo.Release(ghosts, stacked, hiddenGhosts, hiddenStacks);

        Assert.Equal([rhythm], ghosts.VisibleGhosts.ToArray());
        Assert.Equal([bass], stacked.StackedArrangements.ToArray());
    }

    /// <summary>
    ///     The snapshot is taken BEFORE the clear. Both services expose LIVE
    ///     collections, so a snapshot that kept the reference would be emptied
    ///     along with them and un-soloing would restore nothing — which is the
    ///     failure that makes a solo button a one-way trip.
    /// </summary>
    [Fact]
    public void TracksSolo_snapshotsBeforeItClears()
    {
        var ghosts = new GhostArrangementsService();
        var stacked = new StackedArrangementsService();
        foreach (var name in new[] { "A", "B", "C" })
            ghosts.SetVisible(new Arrangement { Name = name }, true);

        var (hiddenGhosts, _) = TimelineSolo.Engage(ghosts, stacked);

        Assert.Equal(3, hiddenGhosts.Count);
        Assert.Empty(ghosts.VisibleGhosts);
    }

    // ── the chips that light from live state ────────────────────────────────

    [Fact]
    public void QuickBalance_lightsWhicheverMixIsActuallySet()
    {
        var h = new Harness();

        h.Transport.SetMixBalanceCommand.Execute("even");
        Assert.True(h.Transport.IsMixEven);
        Assert.False(h.Transport.IsMixSongOnly);
        Assert.False(h.Transport.IsMixGuitarOnly);

        h.Transport.SetMixBalanceCommand.Execute("guitar");
        Assert.True(h.Transport.IsMixGuitarOnly);
        Assert.False(h.Transport.IsMixEven);

        // …and dragging a slider off the preset un-lights it, which a
        // remembered "which chip did I press" flag would not.
        h.Transport.SongVolume = 0.4;
        Assert.False(h.Transport.IsMixGuitarOnly);

        // MASTER is deliberately not part of the balance: it scales the whole
        // output bus, so it changes how loud the mix is, never which mix it is.
        // Driving these from master is what made "guitar only" set the bus to
        // zero and silence the guitar it was isolating.
        h.Transport.SetMixBalanceCommand.Execute("guitar");
        h.Transport.Volume = 0.4;
        Assert.True(h.Transport.IsMixGuitarOnly);
    }

    /// <summary>The speed presets light from the LIVE speed, through a converter
    /// that parses its parameter INVARIANTLY — "0.65" is 0.65 on a German
    /// locale, where the current culture would read it as 65.</summary>
    [Fact]
    public void SpeedPresetChips_lightFromTheLiveSpeed_underAnyCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var c = DoubleEqualsConverter.Instance;
            Assert.True((bool)c.Convert(0.65, typeof(bool), "0.65", CultureInfo.CurrentCulture));
            Assert.False((bool)c.Convert(0.8, typeof(bool), "0.65", CultureInfo.CurrentCulture));
            Assert.False((bool)c.Convert(65.0, typeof(bool), "0.65", CultureInfo.CurrentCulture));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ── the strings all of it needs ─────────────────────────────────────────

    [Theory]
    [InlineData("Toolbar_Popover_Live")]
    [InlineData("Toolbar_Mix_NoInput")]
    [InlineData("Toolbar_FxChain_On")]
    [InlineData("Toolbar_FxChain_Add")]
    [InlineData("Toolbar_FxChain_DragTip")]
    [InlineData("Toolbar_FxChain_Rescan")]
    [InlineData("Toolbar_FxChain_Rescan_Tip")]
    [InlineData("Toolbar_FxChain_Rescanning")]
    [InlineData("Toolbar_Tracks_Solo")]
    [InlineData("Toolbar_Tracks_Solo_Tip")]
    [InlineData("Toolbar_Tracks_Mute")]
    [InlineData("Toolbar_Tracks_Mute_Tip")]
    [InlineData("Toolbar_Tracks_Settings")]
    [InlineData("Toolbar_Vocals_Track")]
    public void EveryNewPopoverString_isInBothResxFiles(string key) => AssertKeyInBothResx(key);

    /// <summary>
    ///     The chain's help line used to say "Use ↑/↓ to reorder". The arrows are
    ///     gone, so that sentence became a lie about a control that no longer
    ///     exists — the exact class of thing this pass was commissioned to
    ///     remove. It must name the DRAG, in both languages.
    /// </summary>
    [Fact]
    public void TheChainHelpLine_namesTheDrag_andNoLongerNamesTheDeletedArrows()
    {
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            var help = ValueOf(file, "Toolbar_FxChain_Help");
            Assert.DoesNotContain("↑", help, StringComparison.Ordinal);
            Assert.DoesNotContain("↓", help, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The two arrow commands are DELETED, not merely unbound. A leftover
    ///     `MoveUpCommand` binding builds clean under compiled bindings only
    ///     because the property still exists; removing the property is what makes
    ///     the compiler the guard. This lint keeps them from coming back.
    /// </summary>
    [Fact]
    public void NoSurface_StillReachesForTheDeletedChainArrows()
    {
        var slot = Read("DotCharter.Tone.UI", "ViewModels", "Editor", "VstChainSlotViewModel.cs");
        Assert.DoesNotContain("MoveUpRequested", SourceText.StripComments(slot), StringComparison.Ordinal);
        Assert.DoesNotContain("MoveDownRequested", SourceText.StripComments(slot), StringComparison.Ordinal);

        foreach (var view in UiSourceTree.EnumerateFiles(
                     UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "*.axaml", SearchOption.AllDirectories))
        {
            var text = UiSourceTree.ReadAllText(view);
            Assert.DoesNotContain("MoveUpCommand", text, StringComparison.Ordinal);
            Assert.DoesNotContain("MoveDownCommand", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     Every surface that shows the guitar chain must offer the SAME rack —
    ///     and so the same reorder gesture. Two gestures over one collection is
    ///     how the Tools popover and the Play Mode amp panel end up disagreeing
    ///     about the signal path, so the drag controller lives in ONE place (the
    ///     shared <c>VstRackView</c>), and each host must draw that view over the
    ///     transport's rack rather than a chain template of its own.
    /// </summary>
    [Fact]
    public void EveryChainSurface_HostsTheOneSharedRack()
    {
        var rack = SourceText.StripComments(Read("DotCharter.Tone.UI", "Views", "PlayMode", "VstRackView.axaml.cs"));
        Assert.Contains("new VstChainDragController(", rack, StringComparison.Ordinal);
        Assert.Contains("Dispose()", rack, StringComparison.Ordinal);

        foreach (var host in new[]
                 {
                     Path.Combine("DotCharter.UI", "Views", "Shell", "Popovers", "ToolsPopoverView.axaml"),
                     Path.Combine("DotCharter.UI", "Views", "Shell", "AmpFxPanel.axaml"),
                     Path.Combine("DotCharter.RigLab", "PluginsView.axaml")
                 })
        {
            var markup = Read(host);
            // An ELEMENT, not a mention: every host's comments name the rack too.
            Assert.Matches(@"<\w+:VstRackView\b", markup);
            // A host drawing its OWN chain rows would carry the slot template.
            Assert.DoesNotContain("x:DataType=\"editorVms:VstChainSlotViewModel\"", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("FirePopoverSlot", markup, StringComparison.Ordinal);
        }

        // …and no view anywhere else builds a second drag controller.
        foreach (var file in UiSourceTree.EnumerateFiles(
                     UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views")), "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith("VstRackView.axaml.cs", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain("new VstChainDragController(", SourceText.StripComments(UiSourceTree.ReadAllText(file)),
                StringComparison.Ordinal);
        }
    }

    /// <summary>The DotCharter hosts bind the rack through the transport's ONE
    /// adapter, never the transport itself (whose property names are not the
    /// rack's, so every binding in the shared view would silently miss).</summary>
    [Fact]
    public void TheDotCharterHosts_BindTheTransportsRackAdapter()
    {
        Assert.Contains("DataContext=\"{Binding Transport.VstRack}\"",
            Read("DotCharter.UI", "Views", "Shell", "Popovers", "ToolsPopoverView.axaml"), StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{Binding VstRack}\"",
            Read("DotCharter.UI", "Views", "Shell", "AmpFxPanel.axaml"), StringComparison.Ordinal);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task AddThreeSlots(Harness h)
    {
        h.Audio.VstPluginsOnDisk =
        [
            new VstPluginInfo { Name = "TS-9", PluginPath = @"C:\VST3\TS-9.vst3" },
            new VstPluginInfo { Name = "Archetype", PluginPath = @"C:\VST3\Archetype.vst3" },
            new VstPluginInfo { Name = "Room", PluginPath = @"C:\VST3\Room.vst3" }
        ];
        await h.Transport.RescanVstPluginsCommand.ExecuteAsync(null);

        // Writing the selection IS the gesture — the two surfaces that show the
        // chain bind `SelectedItem`, and the property-changed handler does the
        // add and clears the picker. The extra `AddGuitarVstFromSelectionCommand
        // .Execute(null)` this used to carry was already a no-op by the time it
        // ran (the selection had been nulled), and the command is gone in 0.58.
        foreach (var plugin in h.Transport.AvailableVstPlugins.Where(p => p.PluginPath.Length > 0).ToList())
            h.Transport.AddGuitarVstSelection = plugin;

        Assert.Equal(3, h.Transport.GuitarVstChain.Count);
    }

    private static string ValueOf(string resxFile, string key)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", resxFile);
        var element = XDocument.Load(path).Root!.Elements("data")
            .FirstOrDefault(d => d.Attribute("name")?.Value == key);
        Assert.True(element != null, $"{resxFile} is missing '{key}'.");
        return element!.Element("value")?.Value ?? "";
    }

    private static void AssertKeyInBothResx(string key)
    {
        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx" })
        {
            Assert.False(string.IsNullOrWhiteSpace(ValueOf(file, key)),
                $"{file} carries '{key}' with no text.");
        }
    }

    private static string Read(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { TestPaths.RepoRoot() }.Concat(relativeParts).ToArray());
        Assert.True(File.Exists(path), $"source file not found at {path}");
        return UiSourceTree.ReadAllText(path);
    }

    private sealed class Harness
    {
        public Harness()
        {
            var logger = new FakeAppLogger();
            Config = new FakeConfigurationService();
            Transport = new TransportViewModel(Audio, State, Config, FakeLocalization.KeysOnly(), null, logger);
        }

        public FakeAudioPlaybackService Audio { get; } = new();
        public FakeConfigurationService Config { get; }
        public TransportViewModel Transport { get; }

        public LiveTuner NewTuner() => new(Audio, State, new TestUiDispatcher(), new FakeAppLogger());

        /// <summary>One ordinary six-string arrangement, so the tuner has
        /// something to tune against.</summary>
        public ProjectState State { get; } = NewState();

        private static ProjectState NewState()
        {
            var state = new ProjectState();
            var project = new ChartProject();
            var lead = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            lead.Levels.Add(new ChartLevel());
            project.Arrangements.Add(lead);
            state.ActiveProject = project;
            state.ActiveArrangement = lead;
            return state;
        }
    }
}
