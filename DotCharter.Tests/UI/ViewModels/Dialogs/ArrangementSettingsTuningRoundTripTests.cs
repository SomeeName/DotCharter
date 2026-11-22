using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     #905: a 4-string bass in B standard (-5 -5 -5 -5, type CUSTOM) came back E standard after
///     the arrangement dialog was opened and closed with OK. Open + OK with nothing touched must
///     leave every string count's tuning as it was, on bass and guitar, preset or custom.
/// </summary>
public sealed class ArrangementSettingsTuningRoundTripTests
{
    /// <summary>Every instrument the dialog edits: bass 4/5/6, guitar 6/7/8 on each guitar path
    ///     (Combo rows also carry the Bonus subtype).</summary>
    private static readonly (ArrangementType Type, ArrangementSubtype Subtype, int Strings)[] Instruments =
    [
        (ArrangementType.Bass, ArrangementSubtype.Main, 4),
        (ArrangementType.Bass, ArrangementSubtype.Main, 5),
        (ArrangementType.Bass, ArrangementSubtype.Main, 6),
        (ArrangementType.Lead, ArrangementSubtype.Main, 6),
        (ArrangementType.Rhythm, ArrangementSubtype.Main, 6),
        (ArrangementType.Combo, ArrangementSubtype.Bonus, 6),
        (ArrangementType.Lead, ArrangementSubtype.Main, 7),
        (ArrangementType.Rhythm, ArrangementSubtype.Main, 7),
        (ArrangementType.Combo, ArrangementSubtype.Bonus, 7),
        (ArrangementType.Lead, ArrangementSubtype.Main, 8),
        (ArrangementType.Rhythm, ArrangementSubtype.Main, 8),
        (ArrangementType.Combo, ArrangementSubtype.Bonus, 8)
    ];

    /// <summary>Uniform -1..-12 (B standard is -5) and +1/+2, a drop on the lowest string, the
    ///     instrument's own standard, and mixed rows (5-string bass -5 -5 -5 -5 -4, 7-string -2 0 …).</summary>
    private static IEnumerable<int[]> TuningsFor(int strings, bool bass)
    {
        for (var shift = -12; shift <= 2; shift++)
        {
            if (shift != 0) yield return Enumerable.Repeat(shift, strings).ToArray();
        }

        yield return new int[strings];
        var drop = new int[strings];
        drop[0] = -2;
        yield return drop;
        yield return ExtendedRangeTunings.StandardOffsets(strings, bass);
        var mixed = Enumerable.Repeat(-5, strings).ToArray();
        mixed[^1] = -4;
        yield return mixed;
        var odd = new int[strings];
        odd[0] = -3;
        odd[^2] = -1;
        odd[^1] = 1;
        yield return odd;
    }

    public static TheoryData<ArrangementType, ArrangementSubtype, int[]> Tunings
    {
        get
        {
            var data = new TheoryData<ArrangementType, ArrangementSubtype, int[]>();
            foreach (var (type, subtype, strings) in Instruments)
            {
                foreach (var steps in TuningsFor(strings, type == ArrangementType.Bass).DistinctBy(t => string.Join(",", t)))
                    data.Add(type, subtype, steps);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Tunings))]
    public void OpenShows_AndOkKeeps_TheStoredOffsets(ArrangementType type, ArrangementSubtype subtype, int[] steps)
    {
        var h = new Harness(ArrangementWith(type, "CUSTOM", steps, subtype));

        var vm = h.Open();
        Assert.Equal(steps, vm.TuningOffsets.Select(t => t.Step).ToArray());
        vm.SaveCommand.Execute(null);

        Assert.Equal(steps.Length, h.Arrangement.StringCount);
        Assert.Equal(steps, h.Arrangement.TuningPitches.Select(t => t.Step).ToArray());
        Assert.Equal(steps, h.Open().TuningOffsets.Select(t => t.Step).ToArray());
    }

    /// <summary>A null from a cleared picker (what a re-bound ComboBox writes) is not a pick.</summary>
    [Theory]
    [MemberData(nameof(Tunings))]
    public void AClearedPicker_LeavesTheOffsets(ArrangementType type, ArrangementSubtype subtype, int[] steps)
    {
        var h = new Harness(ArrangementWith(type, "CUSTOM", steps, subtype));

        var vm = h.Open();
        var preset = vm.TuningPreset;
        vm.TuningPreset = null!;
        vm.TuningPreset = preset;
        Assert.Equal(steps, vm.TuningOffsets.Select(t => t.Step).ToArray());
        vm.SaveCommand.Execute(null);

        Assert.Equal(steps, h.Arrangement.TuningPitches.Select(t => t.Step).ToArray());
    }

    [Theory]
    [MemberData(nameof(Tunings))]
    public void TypingANegativeValue_Applies(ArrangementType type, ArrangementSubtype subtype, int[] steps)
    {
        var h = new Harness(ArrangementWith(type, "CUSTOM", steps, subtype));
        var typed = steps[0] == -9 ? -10 : -9;
        var expected = steps.ToArray();
        expected[0] = typed;
        expected[^1] = -5;

        var vm = h.Open();
        vm.TuningOffsets[0].Step = typed;
        vm.TuningOffsets[^1].Step = -5;
        vm.SaveCommand.Execute(null);

        Assert.Equal(expected, h.Arrangement.TuningPitches.Select(t => t.Step).ToArray());
        Assert.Equal(expected, h.Open().TuningOffsets.Select(t => t.Step).ToArray());
    }

    [Theory]
    [MemberData(nameof(Tunings))]
    public void OkThenSaveAndReload_KeepsTheOffsets(ArrangementType type, ArrangementSubtype subtype, int[] steps)
    {
        var h = new Harness(ArrangementWith(type, "CUSTOM", steps, subtype));
        h.Open().SaveCommand.Execute(null);

        var path = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"), "tuning.rscp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var serializer = new RscpChartSerializer();
            serializer.Save(path, h.State.ActiveProject!);
            var reloaded = Assert.Single(serializer.Load(path).Arrangements);

            Assert.Equal(steps.Length, reloaded.StringCount);
            Assert.Equal(steps, reloaded.TuningPitches.Select(t => t.Step).ToArray());
            var again = new Harness(reloaded);
            Assert.Equal(steps, again.Open().TuningOffsets.Select(t => t.Step).ToArray());
        }
        finally
        {
            BestEffortDelete.Directory(Path.GetDirectoryName(path)!);
        }
    }

    /// <summary>The fix must not freeze the picker: a real preset choice still retunes.</summary>
    [Theory]
    [InlineData(ArrangementType.Bass, new[] { -5, -5, -5, -5 }, "Drop D", new[] { -2, 0, 0, 0 })]
    [InlineData(ArrangementType.Bass, new[] { -5, -5, -5, -5, -4 }, "Eb Standard", new[] { -6, -6, -6, -6, -5 })]
    [InlineData(ArrangementType.Lead, new[] { -2, 0, 0, 0, 0, 0, 0 }, "E Standard", new[] { 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(ArrangementType.Lead, new[] { -5, -5, -5, -5, -5, -5, -5, -5 }, "D Standard", new[] { -2, -2, -2, -2, -2, -2, -2, -2 })]
    public void PickingAPreset_StillRetunes(ArrangementType type, int[] steps, string preset, int[] expected)
    {
        var h = new Harness(ArrangementWith(type, "CUSTOM", steps));

        var vm = h.Open();
        vm.TuningPreset = preset;
        vm.SaveCommand.Execute(null);

        Assert.Equal(expected, h.Arrangement.TuningPitches.Select(t => t.Step).ToArray());
    }

    internal static Arrangement ArrangementWith(ArrangementType type, string tuningType, int[] steps,
        ArrangementSubtype subtype = ArrangementSubtype.Main)
    {
        var arrangement = new Arrangement
        {
            Name = type.ToString(), ArrangementType = type, Subtype = subtype, StringCount = steps.Length,
            TuningType = tuningType
        };
        foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        return arrangement;
    }

    internal sealed class Harness
    {
        private readonly ChartEditDialogServices _services;

        public Harness(Arrangement arrangement)
        {
            Arrangement = arrangement;
            var logger = new FakeAppLogger();
            var history = new HistoryService(logger);
            var config = new FakeConfigurationService();
            var state = State;

            var project = new ChartProject();
            for (var i = 0; i < 8; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, IsMeasure = i % 4 == 0 });
            project.Arrangements.Add(arrangement);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            state.ActiveLevel = arrangement.Levels[0];

            var selection = new SelectionService();
            var transport = new TransportViewModel(new FakeAudioPlaybackService(), state, config, FakeLocalization.KeysOnly());
            var editor = new ChartEditorViewModel(state, transport, history, selection,
                new TempoService(logger), new TrackContextRegistry(state, selection), FakeLocalization.KeysOnly())
            {
                CurrentChart = project.Chart,
                ActiveArrangement = arrangement
            };
            _services = new ChartEditDialogServices(editor, history, state, config, FakeLocalization.English(), logger);
        }

        public Arrangement Arrangement { get; }

        public ProjectState State { get; } = new();

        public ArrangementSettingsViewModel Create() =>
            new(_services, new DotCharter.Tests.Infrastructure.FakeReferencePitchService());

        public ArrangementSettingsViewModel Open()
        {
            var vm = Create();
            vm.Configure(new ArrangementSettingsRequest());
            return vm;
        }
    }
}
