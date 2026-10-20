using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.Tuning;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Tools ▸ Tuner over the shared <see cref="LiveTuner" />: it opens on a fresh
///     lease (one row per open string, none plucked yet), formats what the tuner
///     hears, says why there is no needle, and lets go when the host disposes it.
/// </summary>
public sealed class TunerViewModelTests
{
    private static readonly double A2 = ArrangementTunerLogic.FrequencyOf(45);

    [Fact]
    public void It_opens_with_one_unplucked_row_per_string_of_the_active_arrangement()
    {
        var rig = new Rig();

        using var vm = rig.Open();

        Assert.Equal(["E2", "A2", "D3", "G3", "B3", "E4"], vm.Strings.Select(s => s.Label));
        Assert.All(vm.Strings, s => Assert.False(s.HasReading));
        Assert.False(vm.HasReading);
        Assert.Equal("–", vm.NoteText);
        Assert.Equal(0, vm.CentsOff);
        Assert.Equal(FakeLocalization.English().Format("Tuner_TunedCountFmt", 0, 6), vm.TunedCountLabel);
        Assert.True(vm.HasInput);
        Assert.Equal("", vm.StatusHint);
        Assert.True(rig.Tuner.IsRunning);
    }

    [Fact]
    public void A_heard_pitch_moves_the_needle_the_note_and_its_row()
    {
        var rig = new Rig();
        using var vm = rig.Open();

        rig.Tuner.Hear(A2 * Math.Pow(2, 25 / 1200.0), 66);

        Assert.True(vm.HasReading);
        Assert.Equal("A2", vm.NoteText);
        Assert.Equal("+25 ¢", vm.CentsText);
        Assert.False(vm.IsInTune);
        Assert.Equal(25, vm.CentsOff, 3);
        Assert.True(vm.Strings[1].IsNearest);
        Assert.True(vm.Strings[1].HasReading);
        Assert.Equal(25, vm.Strings[1].CentsOff, 3);
        Assert.False(vm.Strings[0].HasReading);
    }

    /// <summary>"6 of 6 in tune" sat right above the "All strings in tune!" strip that
    /// replaces it; the count is for the way there.</summary>
    [Fact]
    public void Once_every_string_is_tuned_the_strip_says_so_and_the_count_steps_aside()
    {
        var rig = new Rig();
        using var vm = rig.Open();

        foreach (var midi in new[] { 40, 45, 50, 55, 59, 64 })
        {
            for (var i = 0; i < 8; i++)
                rig.Tuner.Hear(ArrangementTunerLogic.FrequencyOf(midi), 66);
        }

        Assert.True(vm.AllTuned);
        Assert.Equal("", vm.TunedCountLabel);
    }

    [Fact]
    public void Its_rows_raise_changes_so_the_meters_follow()
    {
        var rig = new Rig();
        using var vm = rig.Open();
        var raised = new List<string?>();
        vm.Strings[1].PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        rig.Tuner.Hear(A2, 66);

        Assert.Contains(nameof(ITunerString.IsNearest), raised);
        Assert.Contains(nameof(ITunerString.HasReading), raised);
    }

    [Fact]
    public void Without_an_input_stream_it_says_so_instead_of_drawing_a_needle()
    {
        var rig = new Rig();
        rig.Audio.InputSampleRate = 0;

        using var vm = rig.Open();

        Assert.False(vm.HasInput);
        Assert.Equal(FakeLocalization.English()["Tuner_NoInput"], vm.StatusHint);
    }

    [Fact]
    public void An_active_drop_pedal_explains_the_shifted_reading()
    {
        var rig = new Rig();
        rig.Audio.InputPitchShiftSemitones = -2;

        using var vm = rig.Open();

        Assert.True(vm.IsPitchShiftActive);
        Assert.Equal(FakeLocalization.English().Format("Tuner_DropPedal", "−2"), vm.PitchShiftInfo);
    }

    [Fact]
    public void With_nothing_to_tune_against_it_holds_no_lease_and_says_why()
    {
        var rig = new Rig(ArrangementType.Vocal);

        using var vm = rig.Open();

        Assert.Empty(vm.Strings);
        Assert.Equal(FakeLocalization.English()["Tuner_NoArrangement"], vm.StatusHint);
        Assert.False(rig.Tuner.IsRunning);
    }

    [Fact]
    public async Task Close_answers_true_and_disposing_releases_the_lease()
    {
        var rig = new Rig();
        var vm = rig.Open();

        vm.CloseCommand.Execute(null);
        Assert.True(await vm.Result);

        vm.Dispose();
        Assert.False(rig.Tuner.IsRunning);
    }

    [Fact]
    public void Opening_forgets_what_the_strings_read_while_the_popover_was_listening()
    {
        var rig = new Rig();
        using var popover = rig.Tuner.Acquire(fresh: false);
        rig.Tuner.Hear(A2, 66);

        using var vm = rig.Open();

        Assert.All(vm.Strings, s => Assert.False(s.HasReading));
        vm.Dispose();
        Assert.True(rig.Tuner.IsRunning); // the popover's lease is still held
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        using var vm = new Rig().Open();
        Assert.Empty(ContractConformanceTests.Violations(typeof(ITunerScreen), vm));
    }

    private sealed class Rig
    {
        public Rig(ArrangementType type = ArrangementType.Lead)
        {
            var state = new ProjectState();
            var project = new ChartProject();
            var arrangement = new Arrangement { Name = "Lead", ArrangementType = type };
            arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arrangement);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            Audio.InputSampleRate = 48_000;
            Tuner = new LiveTuner(Audio, state, new TestUiDispatcher(), new FakeAppLogger());
        }

        public FakeAudioPlaybackService Audio { get; } = new();

        public LiveTuner Tuner { get; }

        public TunerViewModel Open() => new(Tuner, FakeLocalization.English(), new FakeAppLogger());
    }
}
