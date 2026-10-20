using DotCharter.Services.Audio.Tuning;
using DotCharter.Tone.Audio.Tuning;
using DotCharter.Tone.Diagnostics;

namespace DotCharter.Tests.Services.Audio.Tuning;

/// <summary>
///     The live tuner the Tuner dialog and the Tools popover share: leases are
///     counted, only the first starts the poll and only the last stops it, a
///     fresh lease forgets what each string last read while a joining one keeps
///     it, and a pitch held in tune long enough confirms its string.
/// </summary>
public sealed class LiveTunerTests
{
    private static readonly double E2 = ArrangementTunerLogic.FrequencyOf(40);
    private static readonly double A2 = ArrangementTunerLogic.FrequencyOf(45);

    [Fact]
    public void Leases_are_counted_and_only_the_last_one_stops_the_poll()
    {
        var rig = new Rig();

        var popover = rig.Tuner.Acquire(fresh: false);
        var dialog = rig.Tuner.Acquire(fresh: true);

        Assert.NotNull(popover);
        Assert.NotNull(dialog);
        Assert.Equal(1, rig.Ui.Started);
        Assert.True(rig.Tuner.IsRunning);

        dialog!.Dispose();
        dialog.Dispose(); // a second dispose of the same lease releases nothing
        Assert.True(rig.Tuner.IsRunning);
        Assert.Equal(0, rig.Ui.Stopped);

        popover!.Dispose();
        Assert.False(rig.Tuner.IsRunning);
        Assert.Equal(1, rig.Ui.Stopped);
    }

    [Fact]
    public void A_vocal_arrangement_has_nothing_to_tune_and_starts_nothing()
    {
        var rig = new Rig(ArrangementType.Vocal);

        Assert.False(rig.Tuner.CanTune);
        Assert.Null(rig.Tuner.Acquire(fresh: true));
        Assert.False(rig.Tuner.IsRunning);
        Assert.Equal(0, rig.Ui.Started);
    }

    [Fact]
    public void A_heard_pitch_moves_the_nearest_string_and_the_others_keep_their_last_reading()
    {
        var rig = new Rig();
        using var lease = rig.Tuner.Acquire(fresh: true);

        rig.Tuner.Hear(E2 * Cents(-20), 66);
        rig.Tuner.Hear(A2 * Cents(7), 66);

        Assert.Equal("A2", rig.Tuner.HeardNote);
        Assert.Equal(7, rig.Tuner.CentsOff, 3);
        Assert.False(rig.Tuner.IsInTune);
        var strings = rig.Tuner.Strings;
        Assert.True(strings[1].IsNearest);
        Assert.False(strings[0].IsNearest);
        Assert.Equal(-20, strings[0].CentsOff, 3);
        Assert.True(strings[0].HasReading);
        Assert.False(strings[2].HasReading);
    }

    [Fact]
    public void Readings_clamp_to_fifty_cents_either_side()
    {
        var rig = new Rig();
        using var lease = rig.Tuner.Acquire(fresh: true);

        rig.Tuner.Hear(E2 * Cents(-240), 66); // still nearest the low E: A2 is 500 ¢ up

        Assert.Equal(-LiveTuner.MaxDisplayCents, rig.Tuner.CentsOff);
        Assert.Equal(-LiveTuner.MaxDisplayCents, rig.Tuner.Strings[0].CentsOff);
    }

    [Fact]
    public void Half_a_second_in_tune_confirms_a_string_and_every_string_makes_all_tuned()
    {
        var rig = new Rig();
        using var lease = rig.Tuner.Acquire(fresh: true);

        for (var i = 0; i < 8; i++) rig.Tuner.Hear(E2, 66);
        Assert.True(rig.Tuner.Strings[0].IsTuned);
        Assert.Equal(1, rig.Tuner.TunedCount);
        Assert.False(rig.Tuner.AllTuned);

        foreach (var midi in new[] { 45, 50, 55, 59, 64 })
        {
            for (var i = 0; i < 8; i++)
                rig.Tuner.Hear(ArrangementTunerLogic.FrequencyOf(midi), 66);
        }

        Assert.True(rig.Tuner.AllTuned);
        Assert.Equal(6, rig.Tuner.TunedCount);
    }

    /// <summary>Tools ▸ Tuner over a chart that only NAMES its tuning tunes to that
    /// tuning (it used to read E standard while Get Ready decoded the name).</summary>
    [Fact]
    public void A_chart_that_only_names_its_tuning_is_tuned_to_that_tuning()
    {
        var rig = new Rig(tuningType: "E_DROP_D");
        using var lease = rig.Tuner.Acquire(fresh: true);

        Assert.Equal(["D2", "A2", "D3", "G3", "B3", "E4"], rig.Tuner.Strings.Select(s => s.NoteName));
    }

    /// <summary>A stray note — or the low E an octave down — still moves the needle,
    /// but never un-confirms a string the player already tuned.</summary>
    [Fact]
    public void A_stray_or_octave_off_note_never_unconfirms_a_tuned_string()
    {
        var rig = new Rig();
        using var lease = rig.Tuner.Acquire(fresh: true);
        for (var i = 0; i < 8; i++) rig.Tuner.Hear(E2, 66);
        Assert.True(rig.Tuner.Strings[0].IsTuned);

        rig.Tuner.Hear(E2 / 2, 66);
        rig.Tuner.Hear(E2 * Cents(-240), 66);

        Assert.True(rig.Tuner.HasReading);
        Assert.True(rig.Tuner.Strings[0].IsTuned);
        Assert.Equal(1, rig.Tuner.TunedCount);
    }

    [Fact]
    public void A_fresh_lease_forgets_the_strings_while_a_joining_one_keeps_them()
    {
        var rig = new Rig();
        using var first = rig.Tuner.Acquire(fresh: false);
        rig.Tuner.Hear(E2 * Cents(9), 66);

        using (rig.Tuner.Acquire(fresh: false))
            Assert.True(rig.Tuner.Strings[0].HasReading);

        using var fresh = rig.Tuner.Acquire(fresh: true);
        Assert.False(rig.Tuner.Strings[0].HasReading);
        Assert.False(rig.Tuner.HasReading);
    }

    [Fact]
    public void The_input_and_the_drop_pedal_are_read_when_a_lease_starts()
    {
        var rig = new Rig();
        rig.Audio.InputSampleRate = 0;
        rig.Audio.InputPitchShiftSemitones = -2.2;

        using (rig.Tuner.Acquire(fresh: true))
        {
            Assert.False(rig.Tuner.HasInput);
            Assert.Equal(-2, rig.Tuner.PitchShiftSemitones);
        }

        rig.Audio.InputSampleRate = 48_000;
        using (rig.Tuner.Acquire(fresh: true))
            Assert.True(rig.Tuner.HasInput);
    }

    private static double Cents(double cents) => Math.Pow(2, cents / 1200.0);

    private sealed class Rig
    {
        public Rig(ArrangementType type = ArrangementType.Lead, string tuningType = "E_STANDARD")
        {
            var state = new ProjectState();
            var project = new ChartProject();
            var arrangement = new Arrangement { Name = "Lead", ArrangementType = type, TuningType = tuningType };
            arrangement.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arrangement);
            state.ActiveProject = project;
            state.ActiveArrangement = arrangement;
            Audio.InputSampleRate = 48_000;
            Tuner = new LiveTuner(Audio, state, Ui, new FakeAppLogger());
        }

        public FakeAudioPlaybackService Audio { get; } = new();

        public CountingDispatcher Ui { get; } = new();

        public LiveTuner Tuner { get; }
    }

    /// <summary>Counts periodic schedules and their disposals; the tick itself never fires.</summary>
    private sealed class CountingDispatcher : IUiDispatcher
    {
        public int Started { get; private set; }

        public int Stopped { get; private set; }

        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public IDisposable SchedulePeriodic(TimeSpan period, Action tick)
        {
            Started++;
            return new Stop(this);
        }

        private sealed class Stop(CountingDispatcher owner) : IDisposable
        {
            public void Dispose() => owner.Stopped++;
        }
    }
}
