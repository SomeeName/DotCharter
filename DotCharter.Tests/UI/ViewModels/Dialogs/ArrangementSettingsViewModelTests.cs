using DotCharter.Services.Editing.Selection;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Arrangements;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     The authoring half of the <c>ArrangementProperties</c> gap: before 0.35 the
///     scroll speed, the path, picked bass and every technique flag were reachable
///     only by IMPORTING a file that already carried them, so a chart authored in
///     DotCharter could never originate one.
/// </summary>
public sealed class ArrangementSettingsViewModelTests
{
    private sealed class Harness
    {
        public Harness(Arrangement arrangement)
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var tempo = new TempoService(logger);
            var config = new FakeConfigurationService();

            var project = new ChartProject();
            for (var i = 0; i < 16; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
            project.Arrangements.Add(arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = arrangement.Levels.Count > 0 ? arrangement.Levels[0] : null;

            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            var editor = new ChartEditorViewModel(State, transport, history, Selection, tempo,
                new TrackContextRegistry(State, Selection), FakeLocalization.KeysOnly())
            {
                CurrentChart = project.Chart,
                ActiveArrangement = arrangement
            };

            _services = new ChartEditDialogServices(editor, history, State, config, FakeLocalization.English(),
                logger);
            ViewModel = new ArrangementSettingsViewModel(_services);
        }

        private readonly ChartEditDialogServices _services;

        /// <summary>One instance per open, as on the dialog host.</summary>
        public void Open()
        {
            ViewModel = new ArrangementSettingsViewModel(_services);
            ViewModel.Configure(new ArrangementSettingsRequest());
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public ArrangementSettingsViewModel ViewModel { get; private set; }
    }

    private static IArrangementPathOption Path(Harness h, int routeMask) =>
        h.ViewModel.PathOptions[ArrangementSettingsEdit.Paths.ToList().FindIndex(p => p.RouteMask == routeMask)];

    private static IArrangementFlag Flag(IReadOnlyList<IArrangementFlag> flags,
        IReadOnlyList<ArrangementFlag> table, string key) =>
        flags[table.ToList().FindIndex(f => f.LabelKey == key)];

    private static Arrangement NewArrangement(ArrangementType type = ArrangementType.Lead)
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = type };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 0, Fret = 5 });
        arrangement.Levels.Add(level);
        return arrangement;
    }

    // ── scroll speed ────────────────────────────────────────────────────────

    /// <summary>
    ///     `DynamicVisualDensity` IS how Rocksmith stores scroll speed — there is no
    ///     other attribute — so before this an authored chart could only ever ship the
    ///     1.3 default.
    /// </summary>
    [Fact]
    public void ScrollSpeed_CanBeAuthoredAndCleared()
    {
        var arrangement = NewArrangement();
        var h = new Harness(arrangement);

        h.Open();
        Assert.False(h.ViewModel.HasScrollSpeed);

        h.ViewModel.HasScrollSpeed = true;
        h.ViewModel.ScrollSpeed = 2.4;
        h.ViewModel.SaveCommand.Execute(null);
        Assert.Equal(2.4, arrangement.ScrollSpeed);

        h.Open();
        Assert.True(h.ViewModel.HasScrollSpeed);
        Assert.Equal(2.4, h.ViewModel.ScrollSpeed);

        // Unticking must restore "no preference" — null, not 1.3, or the player's
        // "use the chart's speed" can no longer tell the two apart.
        h.ViewModel.HasScrollSpeed = false;
        h.ViewModel.SaveCommand.Execute(null);
        Assert.Null(arrangement.ScrollSpeed);
    }

    [Fact]
    public void ScrollSpeed_IsClampedToTheRocksmithRange()
    {
        var arrangement = NewArrangement();
        var h = new Harness(arrangement);

        h.Open();
        h.ViewModel.HasScrollSpeed = true;
        h.ViewModel.ScrollSpeed = 99;
        h.ViewModel.SaveCommand.Execute(null);

        Assert.Equal(5.0, arrangement.ScrollSpeed);
    }

    // ── path / picked bass ──────────────────────────────────────────────────

    [Fact]
    public void ThePath_DefaultsToAuto_AndAnAuthoredChoiceIsStored()
    {
        var arrangement = NewArrangement(ArrangementType.Combo);
        var h = new Harness(arrangement);

        h.Open();
        Assert.Same(Path(h, 0), h.ViewModel.SelectedPath);

        h.ViewModel.SelectedPath = Path(h, ArrangementPropertiesDeriver.RouteRhythm);
        h.ViewModel.SaveCommand.Execute(null);

        Assert.Equal(0, arrangement.Properties.PathLead);
        Assert.Equal(1, arrangement.Properties.PathRhythm);
        Assert.Equal(ArrangementPropertiesDeriver.RouteRhythm,
            ArrangementPropertiesDeriver.ResolveRouteMask(arrangement));

        // …and back to auto clears all three, so the type default takes over again.
        h.Open();
        h.ViewModel.SelectedPath = Path(h, 0);
        h.ViewModel.SaveCommand.Execute(null);
        Assert.Equal(0, arrangement.Properties.PathRhythm);
        Assert.Equal(ArrangementPropertiesDeriver.RouteLead | ArrangementPropertiesDeriver.RouteRhythm,
            ArrangementPropertiesDeriver.ResolveRouteMask(arrangement));
    }

    [Fact]
    public void PickedBass_IsAuthored_AndOnlyOfferedOnBass()
    {
        var bass = NewArrangement(ArrangementType.Bass);
        var h = new Harness(bass);

        h.Open();
        Assert.True(h.ViewModel.IsBass);
        h.ViewModel.PickedBass = true;
        h.ViewModel.SaveCommand.Execute(null);
        Assert.True(bass.PickedBass);

        // Retyping the arrangement must not leave a stale claim behind.
        h.Open();
        h.ViewModel.Type = ArrangementType.Rhythm;
        Assert.False(h.ViewModel.IsBass);
        h.ViewModel.SaveCommand.Execute(null);
        Assert.False(bass.PickedBass);
    }

    // ── technique flags ─────────────────────────────────────────────────────

    /// <summary>
    ///     The four flags nothing derives are editable without pinning anything —
    ///     gating them behind the override would make them unreachable unless the
    ///     charter also took over the 20 that ARE derived.
    /// </summary>
    [Fact]
    public void TheUndecidableFlags_AreAuthoredWithoutThePin()
    {
        var arrangement = NewArrangement();
        var h = new Harness(arrangement);

        h.Open();
        Assert.Equal(4, h.ViewModel.AuthoredFlags.Count);
        Flag(h.ViewModel.AuthoredFlags, ArrangementSettingsEdit.AuthoredFlags, "ArrProp_Syncopation").IsSet = true;
        h.ViewModel.SaveCommand.Execute(null);

        Assert.Equal(1, arrangement.Properties.Syncopation);
        Assert.False(arrangement.Properties.ManualOverride);
        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(arrangement).Syncopation);
    }

    /// <summary>
    ///     Opening the dialog shows what the export would ship right now, so turning
    ///     the pin on starts from the derived answer rather than from zero — DLC
    ///     Builder's non-destructive "Edit" seed.
    /// </summary>
    [Fact]
    public void TheDerivedFlags_ShowTheCurrentDerivation()
    {
        var arrangement = NewArrangement();
        var bend = arrangement.Levels[0].Notes[0];
        bend.BendValues.Add(new BendValue(bend.Position, 1.0));

        var h = new Harness(arrangement);
        h.Open();

        Assert.Equal(20, h.ViewModel.DerivedFlags.Count);
        Assert.True(Flag(h.ViewModel.DerivedFlags, ArrangementSettingsEdit.DerivedFlags, "ArrProp_Bends").IsSet);
        Assert.False(Flag(h.ViewModel.DerivedFlags, ArrangementSettingsEdit.DerivedFlags, "ArrProp_Tapping").IsSet);
    }

    /// <summary>
    ///     With the pin OFF nothing may be written into the stored flags — otherwise
    ///     every save would look authored and the rscp would grow attributes the very
    ///     next export recomputes anyway.
    /// </summary>
    [Fact]
    public void WithoutThePin_TheDerivedFlagsAreNotWrittenBack()
    {
        var arrangement = NewArrangement();
        var bend = arrangement.Levels[0].Notes[0];
        bend.BendValues.Add(new BendValue(bend.Position, 1.0));

        var h = new Harness(arrangement);
        h.Open();
        h.ViewModel.SaveCommand.Execute(null);

        Assert.Equal(0, arrangement.Properties.Bends); // the field, not the derivation
        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(arrangement).Bends);
        Assert.False(arrangement.Properties.ManualOverride);
    }

    [Fact]
    public void WithThePin_TheTickedFlagsBecomeTheAnswer()
    {
        var arrangement = NewArrangement();
        var h = new Harness(arrangement);

        h.Open();
        h.ViewModel.OverrideDerivedFlags = true;
        Flag(h.ViewModel.DerivedFlags, ArrangementSettingsEdit.DerivedFlags, "ArrProp_Bends").IsSet = true;
        h.ViewModel.SaveCommand.Execute(null);

        Assert.True(arrangement.Properties.ManualOverride);
        Assert.Equal(1, arrangement.Properties.Bends);
        // The chart has no bend at all; the pin is what makes the claim survive.
        Assert.Equal(1, ArrangementPropertiesDeriver.Derive(arrangement).Bends);
    }

    /// <summary>
    ///     The flag labels are resolved in C#, not by the <c>{localization:Loc}</c>
    ///     markup, so the axaml sweep cannot see them — a missing key would render the
    ///     raw key name in the dialog. (EN/DE parity for whatever exists is
    ///     <c>LocalizationSweepTests</c>'s job.)
    /// </summary>
    [Fact]
    public void EveryFlagLabel_ResolvesToRealText()
    {
        var h = new Harness(NewArrangement());
        h.Open();

        var labelled = h.ViewModel.DerivedFlags.Concat(h.ViewModel.AuthoredFlags).ToList();
        var keys = ArrangementSettingsEdit.DerivedFlags.Concat(ArrangementSettingsEdit.AuthoredFlags)
            .Select(f => f.LabelKey).ToList();
        Assert.Equal(24, labelled.Count);

        for (var i = 0; i < labelled.Count; i++)
        {
            Assert.False(string.IsNullOrWhiteSpace(labelled[i].Label), $"'{keys[i]}' is blank");
            Assert.NotEqual(keys[i], labelled[i].Label);
        }

        foreach (var option in h.ViewModel.PathOptions)
            Assert.False(string.IsNullOrWhiteSpace(option.Label));
    }
}
