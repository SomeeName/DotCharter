using DotCharter.Services.Editing.Selection;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.Services.Editing.Tracks;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     The arrangement dialog is the ONLY place a tuning type is authored, and it
///     was flattening every tuning it could not name.
///     <para>
///         Two independent faults. Its five-entry picker emitted <c>DROP_D</c> and
///         <c>DROP_C</c>, and <see cref="TuningUtilities" /> knows neither —
///         <c>UnknownTuningNameTests.AnUnknownName_IsRefused_NotAnsweredAsStandard</c>
///         asserts <c>DROP_D</c> is REFUSED — so authoring Drop D produced a type
///         nothing in the app could expand and <c>RscpWriter</c> refused to
///         certify, i.e. the choice did not even survive a save. And every type
///         OUTSIDE the five folded to "E Standard" on open and was written back as
///         <c>E_STANDARD</c> on Save, so a Save that meant to change the capo
///         retuned a DADGAD chart for the tuner, the refret planner and the
///         string gutter.
///     </para>
/// </summary>
public sealed class ArrangementTuningAuthoringTests
{
    private sealed class Harness
    {
        public Harness(Arrangement arrangement)
        {
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var config = new FakeConfigurationService();

            var project = new ChartProject();
            for (var i = 0; i < 8; i++)
            {
                project.Beats.Add(new BeatMarker
                { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
            }

            project.Arrangements.Add(arrangement);

            State.ActiveProject = project;
            State.ActiveArrangement = arrangement;
            State.ActiveLevel = arrangement.Levels.Count > 0 ? arrangement.Levels[0] : null;

            var selection = new SelectionService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), State, config, FakeLocalization.KeysOnly());
            var editor = new ChartEditorViewModel(State, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(State, selection), FakeLocalization.KeysOnly())
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
        public ArrangementSettingsViewModel ViewModel { get; private set; }
    }

    private static Arrangement ArrangementWith(string tuningType, params int[] steps)
    {
        var arrangement = new Arrangement
        {
            Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = steps.Length,
            TuningType = tuningType
        };
        foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        return arrangement;
    }

    // ── the picker's own vocabulary ─────────────────────────────────────────

    /// <summary>
    ///     Picking "Drop D" must produce a type the domain can expand. The OFFSETS
    ///     the dialog writes are unchanged — they already were E_DROP_D's — so the
    ///     old name was pure loss.
    /// </summary>
    [Fact]
    public void PickingDropD_AuthorsATypeTheDomainCanResolve()
    {
        var h = new Harness(ArrangementWith("E_STANDARD", 0, 0, 0, 0, 0, 0));
        h.Open();

        h.ViewModel.TuningPreset = "Drop D";
        h.ViewModel.SaveCommand.Execute(null);

        var saved = h.State.ActiveArrangement!;
        Assert.True(TuningUtilities.TryGetOffsetsForTuningType(saved.TuningType, 6, out var canonical));
        Assert.Equal(canonical, saved.TuningPitches.Select(t => t.Step).ToArray());
        Assert.Equal([-2, 0, 0, 0, 0, 0], saved.TuningPitches.Select(t => t.Step).ToArray());
    }

    /// <summary>Same for the other drop entry, which mapped to the equally unknown
    ///     <c>DROP_C</c> while writing D_DROP_C's offsets.</summary>
    [Fact]
    public void PickingDropC_AuthorsATypeTheDomainCanResolve()
    {
        var h = new Harness(ArrangementWith("E_STANDARD", 0, 0, 0, 0, 0, 0));
        h.Open();

        h.ViewModel.TuningPreset = "Drop C";
        h.ViewModel.SaveCommand.Execute(null);

        var saved = h.State.ActiveArrangement!;
        Assert.True(TuningUtilities.TryGetOffsetsForTuningType(saved.TuningType, 6, out var canonical));
        Assert.Equal(canonical, saved.TuningPitches.Select(t => t.Step).ToArray());
    }

    // ── a tuning the picker cannot name ─────────────────────────────────────

    /// <summary>
    ///     THE regression. Open a DADGAD arrangement to change the capo, press
    ///     Save, and both the type and the offsets used to come back E standard.
    /// </summary>
    [Fact]
    public void SavingWithoutTouchingThePicker_KeepsATuningThePickerCannotName()
    {
        var dadgad = TuningUtilities.GetOffsetsForTuningType("DADGAD", 6);
        var h = new Harness(ArrangementWith("DADGAD", dadgad));

        h.Open();
        h.ViewModel.Capo = 3;
        h.ViewModel.SaveCommand.Execute(null);

        var saved = h.State.ActiveArrangement!;
        Assert.Equal(3, saved.Capo);
        Assert.Equal("DADGAD", saved.TuningType);
        Assert.Equal(dadgad, saved.TuningPitches.Select(t => t.Step).ToArray());
    }

    /// <summary>
    ///     …and the picker SHOWS it as Custom rather than mislabelling it "E
    ///     Standard". That label was not cosmetic: the preset's change handler
    ///     rewrites every offset, so the mislabel zeroed the tuning it had just
    ///     seeded the moment the value differed from the previous open's.
    /// </summary>
    [Fact]
    public void ATuningThePickerCannotName_ReadsAsCustom_AndKeepsItsOffsets()
    {
        var dadgad = TuningUtilities.GetOffsetsForTuningType("DADGAD", 6);
        var h = new Harness(ArrangementWith("DADGAD", dadgad));

        // The previous open left the picker on a DIFFERENT entry — this is what
        // makes the seeding assignment a CHANGE, and it is the whole reproduction.
        h.ViewModel.TuningPreset = "Drop D";
        h.Open();

        Assert.Equal("Custom", h.ViewModel.TuningPreset);
        Assert.Equal(dadgad, h.ViewModel.TuningOffsets.Select(t => t.Step).ToArray());
    }

    /// <summary>The legacy spellings a project written before the fix carries are
    ///     still READ onto their own row.</summary>
    [Theory]
    [InlineData("DROP_D", "Drop D")]
    [InlineData("DROP_C", "Drop C")]
    [InlineData("E_FLAT_STANDARD", "Eb Standard")]
    [InlineData("E_STANDARD", "E Standard")]
    public void ALegacyOrKnownType_LandsOnItsOwnPresetRow(string tuningType, string expectedPreset)
    {
        var h = new Harness(ArrangementWith(tuningType, 0, 0, 0, 0, 0, 0));
        h.Open();
        Assert.Equal(expectedPreset, h.ViewModel.TuningPreset);
    }

    // ── the honesty rule ────────────────────────────────────────────────────

    /// <summary>
    ///     Hand-editing the offsets without touching the picker leaves a type that
    ///     no longer describes them. That is CUSTOM — the same verdict
    ///     <c>PhraseCatalogFragment.Build</c> and <c>RscpWriter</c> reach, and for
    ///     the same reason: the offsets are the truth, the name is a shorthand.
    /// </summary>
    [Fact]
    public void EditingTheOffsetsAlone_DemotesTheTypeToCustom()
    {
        var h = new Harness(ArrangementWith("E_STANDARD", 0, 0, 0, 0, 0, 0));
        h.Open();

        h.ViewModel.TuningOffsets[0].Step = -5;
        h.ViewModel.SaveCommand.Execute(null);

        Assert.Equal("CUSTOM", h.State.ActiveArrangement!.TuningType);
        Assert.Equal(-5, h.State.ActiveArrangement!.TuningPitches[0].Step);
    }

    /// <summary>The control: an untouched standard arrangement is still standard,
    ///     so the demotion above cannot be firing for everything.</summary>
    [Fact]
    public void AnUntouchedStandardArrangement_StaysStandard()
    {
        var h = new Harness(ArrangementWith("E_STANDARD", 0, 0, 0, 0, 0, 0));
        h.Open();
        h.ViewModel.Capo = 2;
        h.ViewModel.SaveCommand.Execute(null);

        Assert.Equal("E_STANDARD", h.State.ActiveArrangement!.TuningType);
    }
}
