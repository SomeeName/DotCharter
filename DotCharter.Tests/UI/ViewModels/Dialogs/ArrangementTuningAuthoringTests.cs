using DotCharter.Services.Editing.Arrangements;
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

    /// <summary>The legacy spellings a project written before the fix carries still land on
    ///     their own row: the offsets decide, and theirs are the preset's.</summary>
    [Theory]
    [InlineData("DROP_D", "Drop D", new[] { -2, 0, 0, 0, 0, 0 })]
    [InlineData("DROP_C", "Drop C", new[] { -4, -2, -2, -2, -2, -2 })]
    [InlineData("E_FLAT_STANDARD", "Eb Standard", new[] { -1, -1, -1, -1, -1, -1 })]
    [InlineData("E_STANDARD", "E Standard", new[] { 0, 0, 0, 0, 0, 0 })]
    public void ALegacyOrKnownType_LandsOnItsOwnPresetRow(string tuningType, string expectedPreset, int[] steps)
    {
        var h = new Harness(ArrangementWith(tuningType, steps));
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

    // ── presets on every instrument ─────────────────────────────────────────

    private static Arrangement BassWith(params int[] steps)
    {
        var arrangement = ArrangementWith("CUSTOM", steps);
        arrangement.ArrangementType = ArrangementType.Bass;
        return arrangement;
    }

    private static Arrangement Instrument(int strings, bool bass) =>
        bass ? BassWith(new int[strings]) : ArrangementWith("E_STANDARD", new int[strings]);

    /// <summary>A preset is one shift on the instrument's standard plus a drop on the LOWEST
    ///     string. Five strings and a 6-string bass measure their B E A D G (C) standard against
    ///     the E A D G B (E) reference row.</summary>
    [Theory]
    [InlineData(4, true, "E Standard", new[] { 0, 0, 0, 0 })]
    [InlineData(4, true, "Drop D", new[] { -2, 0, 0, 0 })]
    [InlineData(4, true, "Drop C", new[] { -4, -2, -2, -2 })]
    [InlineData(5, true, "E Standard", new[] { -5, -5, -5, -5, -4 })]
    [InlineData(5, true, "Drop D", new[] { -7, -5, -5, -5, -4 })]
    [InlineData(5, true, "Eb Standard", new[] { -6, -6, -6, -6, -5 })]
    [InlineData(5, true, "D Standard", new[] { -7, -7, -7, -7, -6 })]
    [InlineData(5, true, "Drop C", new[] { -9, -7, -7, -7, -6 })]
    [InlineData(6, false, "E Standard", new[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData(6, false, "Drop D", new[] { -2, 0, 0, 0, 0, 0 })]
    [InlineData(6, false, "Eb Standard", new[] { -1, -1, -1, -1, -1, -1 })]
    [InlineData(6, false, "D Standard", new[] { -2, -2, -2, -2, -2, -2 })]
    [InlineData(6, false, "Drop C", new[] { -4, -2, -2, -2, -2, -2 })]
    [InlineData(6, true, "E Standard", new[] { -5, -5, -5, -5, -4, -4 })]
    [InlineData(6, true, "Drop D", new[] { -7, -5, -5, -5, -4, -4 })]
    [InlineData(7, false, "E Standard", new[] { 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(7, false, "Drop D", new[] { -2, 0, 0, 0, 0, 0, 0 })]
    [InlineData(7, false, "Eb Standard", new[] { -1, -1, -1, -1, -1, -1, -1 })]
    [InlineData(7, false, "D Standard", new[] { -2, -2, -2, -2, -2, -2, -2 })]
    [InlineData(7, false, "Drop C", new[] { -4, -2, -2, -2, -2, -2, -2 })]
    [InlineData(8, false, "Drop D", new[] { -2, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(8, false, "Eb Standard", new[] { -1, -1, -1, -1, -1, -1, -1, -1 })]
    [InlineData(9, false, "E Standard", new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(9, false, "Drop D", new[] { -2, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(9, false, "Eb Standard", new[] { -1, -1, -1, -1, -1, -1, -1, -1, -1 })]
    public void APreset_ResolvesForEveryInstrument_AndReadsBack(int strings, bool bass, string preset, int[] expected)
    {
        Assert.Equal(expected, ArrangementSettingsEdit.PresetOffsets(preset, strings, bass));
        Assert.Equal(preset, ArrangementSettingsEdit.PresetFromOffsets(expected, bass));
    }

    [Fact]
    public void CustomHasNoOffsets_AndAnUnnamedRowReadsAsCustom()
    {
        Assert.Null(ArrangementSettingsEdit.PresetOffsets(ArrangementSettingsEdit.CustomPreset, 7, false));
        Assert.Equal(ArrangementSettingsEdit.CustomPreset,
            ArrangementSettingsEdit.PresetFromOffsets([0, -2, 0, 0, 0, 0, 0], false));
    }

    /// <summary>THE report: Eb Standard on a 7-string left the seventh string in E.</summary>
    [Fact]
    public void PickingAPresetOnASevenString_SetsAllSevenSteps()
    {
        var h = new Harness(ArrangementWith("E_STANDARD", 0, 0, 0, 0, 0, 0, 0));
        h.Open();

        h.ViewModel.TuningPreset = "Eb Standard";

        Assert.Equal([-1, -1, -1, -1, -1, -1, -1], h.ViewModel.TuningOffsets.Select(t => t.Step).ToArray());
    }

    /// <summary>Every preset on every instrument saves under a NAME the domain expands back to its
    ///     offsets (rscharter's where its six-string table means the same thing, else the
    ///     <see cref="ExtendedRangeTunings" /> one), never CUSTOM, and reopens as its preset.</summary>
    [Theory]
    [InlineData(4, true, "Drop D", "E_DROP_D")]
    [InlineData(4, true, "Drop C", "D_DROP_C")]
    [InlineData(5, true, "E Standard", "5_STRING_BASS_STANDARD")]
    [InlineData(5, true, "Drop D", "5_STRING_BASS_DROP_A")]
    [InlineData(5, true, "Eb Standard", "5_STRING_BASS_E_FLAT_STANDARD")]
    [InlineData(5, true, "Drop C", "5_STRING_BASS_DROP_G")]
    [InlineData(6, false, "Drop D", "E_DROP_D")]
    [InlineData(6, true, "E Standard", "6_STRING_BASS_STANDARD")]
    [InlineData(6, true, "Drop D", "6_STRING_BASS_DROP_A")]
    [InlineData(6, true, "D Standard", "6_STRING_BASS_D_STANDARD")]
    [InlineData(7, false, "E Standard", "E_STANDARD")]
    [InlineData(7, false, "Eb Standard", "E_FLAT_STANDARD")]
    [InlineData(7, false, "Drop D", "7_STRING_DROP_A")]
    [InlineData(7, false, "Drop C", "7_STRING_DROP_G")]
    [InlineData(8, false, "Drop D", "8_STRING_DROP_E")]
    [InlineData(8, false, "Drop C", "8_STRING_DROP_D")]
    [InlineData(9, false, "Drop D", "9_STRING_DROP_B")]
    [InlineData(9, false, "Drop C", "9_STRING_DROP_A")]
    [InlineData(9, false, "Eb Standard", "E_FLAT_STANDARD")]
    public void APresetSaves_UnderANameTheDomainExpands(int strings, bool bass, string preset, string expectedType)
    {
        var h = new Harness(Instrument(strings, bass));
        h.Open();

        h.ViewModel.TuningPreset = preset;
        h.ViewModel.SaveCommand.Execute(null);

        var saved = h.State.ActiveArrangement!;
        Assert.Equal(expectedType, saved.TuningType);
        Assert.True(TuningUtilities.TryGetOffsetsForTuningType(saved.TuningType, strings, out var canonical));
        Assert.Equal(canonical, saved.TuningPitches.Select(t => t.Step).ToArray());
        h.Open();
        Assert.Equal(preset, h.ViewModel.TuningPreset);
    }

    [Fact]
    public void AddingAStringToAPresetRow_KeepsThePreset()
    {
        var h = new Harness(ArrangementWith("E_FLAT_STANDARD", -1, -1, -1, -1, -1, -1));
        h.Open();

        h.ViewModel.Strings = 7;

        Assert.Equal("Eb Standard", h.ViewModel.TuningPreset);
        Assert.Equal([-1, -1, -1, -1, -1, -1, -1], h.ViewModel.TuningOffsets.Select(t => t.Step).ToArray());
    }

    /// <summary>…but a row the user typed by hand is not a preset, and a new string stays at 0.</summary>
    [Fact]
    public void AddingAStringToACustomRow_LeavesTheNewStringAtZero()
    {
        var h = new Harness(ArrangementWith("CUSTOM", -1, 0, 0, 0, 0, 0));
        h.Open();

        h.ViewModel.Strings = 7;

        Assert.Equal([-1, 0, 0, 0, 0, 0, 0], h.ViewModel.TuningOffsets.Select(t => t.Step).ToArray());
    }

    /// <summary>A 6-string bass is B E A D G C: making a standard 6-string row a bass moves it
    ///     onto the bass standard and renames every preset.</summary>
    [Fact]
    public void MakingASixStringRowABass_RetunesItToTheBassStandard()
    {
        var h = new Harness(ArrangementWith("E_STANDARD", 0, 0, 0, 0, 0, 0));
        h.Open();

        h.ViewModel.Type = ArrangementType.Bass;

        Assert.Equal("E Standard", h.ViewModel.TuningPreset);
        Assert.Equal([-5, -5, -5, -5, -4, -4], h.ViewModel.TuningOffsets.Select(t => t.Step).ToArray());
        Assert.Equal("6-String Bass Standard (B E A D G C)",
            h.ViewModel.TuningPresets.Single(o => o.Key == "E Standard").Label);
    }

    /// <summary>Each preset carries the instrument's name for the tuning, with the row beside it.</summary>
    [Theory]
    [InlineData(4, true, "Drop D", "Drop D (D A D G)")]
    [InlineData(5, true, "E Standard", "5-String Bass Standard (B E A D G)")]
    [InlineData(5, true, "Drop D", "5-String Bass Drop A (A E A D G)")]
    [InlineData(6, false, "E Standard", "E Standard (E A D G B E)")]
    [InlineData(6, false, "Drop D", "Drop D (D A D G B E)")]
    [InlineData(6, false, "Drop C", "Drop C (C G C F A D)")]
    [InlineData(6, true, "Drop D", "6-String Bass Drop A (A E A D G C)")]
    [InlineData(7, false, "E Standard", "7-String Standard (B E A D G B E)")]
    [InlineData(7, false, "Drop D", "7-String Drop A (A E A D G B E)")]
    [InlineData(7, false, "D Standard", "7-String D Standard (A D G C F A D)")]
    [InlineData(7, false, "Drop C", "7-String Drop G (G D G C F A D)")]
    [InlineData(8, false, "Drop D", "8-String Drop E (E B E A D G B E)")]
    [InlineData(9, false, "E Standard", "9-String Standard (C# F# B E A D G B E)")]
    [InlineData(9, false, "Drop D", "9-String Drop B (B F# B E A D G B E)")]
    [InlineData(7, false, "Custom", "Custom")]
    public void APresetIsLabelledForTheInstrument(int strings, bool bass, string preset, string expectedLabel)
    {
        var h = new Harness(Instrument(strings, bass));
        h.Open();

        Assert.Equal(expectedLabel, h.ViewModel.TuningPresets.Single(o => o.Key == preset).Label);
    }

    [Fact]
    public void ChangingTheStringCount_RelabelsThePresets()
    {
        var h = new Harness(ArrangementWith("E_STANDARD", 0, 0, 0, 0, 0, 0));
        h.Open();
        var dropD = h.ViewModel.TuningPresets.Single(o => o.Key == "Drop D");
        var raised = new List<string?>();
        dropD.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.ViewModel.Strings = 7;

        Assert.Equal("7-String Drop A (A E A D G B E)", dropD.Label);
        Assert.Contains(nameof(dropD.Label), raised);
    }

    /// <summary>A five-string bass in B E A D G reads as its standard, and keeps its offsets.</summary>
    [Fact]
    public void AFiveStringBassInStandard_ReadsAsItsStandard()
    {
        var h = new Harness(BassWith(-5, -5, -5, -5, -4));
        h.Open();

        Assert.Equal("E Standard", h.ViewModel.TuningPreset);
        Assert.Equal([-5, -5, -5, -5, -4], h.ViewModel.TuningOffsets.Select(t => t.Step).ToArray());
    }
}
