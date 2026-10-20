using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Tone.Diagnostics;
using DotCharter.UI.ViewModels.Application;

namespace DotCharter.Tests.UI;

/// <summary>
///     Four surfaces print a bar number for the same instant of the same chart:
///     the TIMELINE's measure pill (<c>FrameSnapshot</c>'s <c>measureCounter</c>,
///     the ordinal of the <c>IsMeasure</c> beat), the STATUS RAIL's
///     <c>M{bar}.{beat}</c>, the Issues tab's location cell
///     (<see cref="ChartPositionLabel" />) and a recorded run's "BAR 96-112"
///     (<see cref="LiveBarNumbers" />).
///     <para>
///         They used to give TWO answers. The timeline and the practice store
///         counted markers; the rail and the location cell started at bar 1 and
///         incremented on every marker <i>after beat 0</i> — the same number only
///         when beat 0 is itself a marker. On a PICKUP grid it is not, and there
///         the rail printed <c>M2.1</c> at the instant the timeline drew the pill
///         <c>1</c>.
///     </para>
///     <para>
///         <see cref="BarCount" /> is the one rule now and all of them fold it.
///         These pin the agreement on both grids — and pin that the ordinary grid,
///         very nearly every chart in the wild, did not move.
///     </para>
/// </summary>
public sealed class BarNumberAgreementTests
{
    /// <summary>120 BPM 4/4: a beat every 500 ms, a measure marker every four.
    /// <paramref name="pickupBeats" /> beats sit BEFORE the first marker.</summary>
    private static List<BeatMarker> Grid(int pickupBeats, int beats = 40) =>
        Enumerable.Range(0, beats)
            .Select(i => new BeatMarker
            {
                Time = i * 500.0,
                Bpm = 120,
                BeatsInMeasure = 4,
                IsMeasure = i >= pickupBeats && (i - pickupBeats) % 4 == 0
            })
            .ToList();

    /// <summary>
    ///     What the timeline draws. Written out rather than called, because
    ///     <c>FrameSnapshot</c> needs a whole render context — this IS its rule
    ///     (<c>if (b.IsMeasure) measureCounter++</c> over the beats up to the
    ///     time, from a prefix count of the markers behind the viewport).
    /// </summary>
    private static int TimelinePill(IReadOnlyList<BeatMarker> beats, double timeMs)
    {
        var counter = 0;
        foreach (var b in beats)
        {
            if (b.Time > timeMs) break;
            if (b.IsMeasure) counter++;
        }

        return counter;
    }

    [Theory]
    [InlineData(0)]   // ordinary grid: beat 0 is the first downbeat
    [InlineData(1)]   // a one-beat pickup
    [InlineData(3)]   // a three-beat pickup — the RS/EOF anacrusis shape
    public void EverySurfaceAgreesOnTheBarNumber(int pickupBeats)
    {
        var beats = Grid(pickupBeats);

        foreach (var beat in beats)
        {
            var t = beat.Time;
            var expected = Math.Max(1, TimelinePill(beats, t));

            Assert.Equal(expected, LiveBarNumbers.BarAt(beats, t));

            var label = ChartPositionLabel.Measure(beats, t);
            Assert.NotNull(label);
            Assert.Equal(expected, BarOf(label!));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void TheStatusRailAgreesWithTheLocationCell_AsThePlayheadRunsForward(int pickupBeats)
    {
        var beats = Grid(pickupBeats);
        var ui = new TickableUi();
        var state = new ProjectState { ActiveProject = new ChartProject() };
        foreach (var b in beats) state.ActiveProject!.Beats.Add(b);

        var audio = new FakeAudioPlaybackService();
        using var rail = new StatusBarViewModel(
            new StatusBarService(ui), state, audio, ui, new StubProfiles(), new FakeFileStorageService(), FakeLocalization.English());

        // The rail carries cached indices across ticks; the location cell walks
        // from scratch every time. Drive the rail forward one beat at a time
        // through its own 1 Hz refresh and compare at every step.
        foreach (var beat in beats)
        {
            audio.SeekTo(beat.Time);
            ui.Tick?.Invoke();
            Assert.Equal(ChartPositionLabel.Measure(beats, beat.Time), rail.MeasureLabel);
        }

        // ...and backwards, which is the path that throws the incremental scan
        // away and recounts from the top.
        for (var i = beats.Count - 1; i >= 0; i--)
        {
            audio.SeekTo(beats[i].Time);
            ui.Tick?.Invoke();
            Assert.Equal(ChartPositionLabel.Measure(beats, beats[i].Time), rail.MeasureLabel);
        }
    }

    /// <summary>
    ///     The ordinary grid — beat 0 IS a measure marker, which is very nearly
    ///     every chart in the wild — reads exactly as it always did. This is the
    ///     guard that unifying the rule did not quietly renumber every song.
    /// </summary>
    [Fact]
    public void AnOrdinaryGridIsUnchanged()
    {
        var beats = Grid(pickupBeats: 0, beats: 600);

        Assert.Equal("M1.1", ChartPositionLabel.Measure(beats, 0));
        Assert.Equal("M1.4", ChartPositionLabel.Measure(beats, 1_500));
        Assert.Equal("M2.1", ChartPositionLabel.Measure(beats, 2_000));
        Assert.Equal("M67.3", ChartPositionLabel.Measure(beats, 133_160));
        Assert.Equal(67, LiveBarNumbers.BarAt(beats, 133_160));
    }

    /// <summary>
    ///     A pickup belongs to the bar it leads into — bar 1 everywhere, not bar 0
    ///     (the "cannot be barred" sentinel) and not bar 2, which is what the rail
    ///     used to call the first real downbeat.
    /// </summary>
    [Fact]
    public void ThePickupBelongsToTheBarItLeadsInto()
    {
        var beats = Grid(pickupBeats: 3);

        Assert.Equal(1, LiveBarNumbers.BarAt(beats, 0));
        Assert.Equal(1, LiveBarNumbers.BarAt(beats, 1_400));
        Assert.StartsWith("M1.", ChartPositionLabel.Measure(beats, 0));
        // 1_500 ms is the first measure marker: bar 1, beat 1 — the same bar the
        // timeline pill draws there.
        Assert.Equal("M1.1", ChartPositionLabel.Measure(beats, 1_500));
        Assert.Equal(1, TimelinePill(beats, 1_500));
    }

    [Fact]
    public void AChartWithNoMeasureMarkerIsStillNeverBarred()
    {
        var flat = Enumerable.Range(0, 40)
            .Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })
            .ToList();

        Assert.Null(LiveBarNumbers.BarAt(flat, 5_000));
        Assert.Null(ChartPositionLabel.Measure(flat, 5_000));
    }

    private static int BarOf(string label)
    {
        var dot = label.IndexOf('.');
        return int.Parse(label.AsSpan(1, dot - 1));
    }

    /// <summary>Runs everything inline and hands the test the 1 Hz refresh so the
    /// rail can be driven without a wall clock.</summary>
    private sealed class TickableUi : IUiDispatcher
    {
        public Action? Tick;

        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();

        public IDisposable SchedulePeriodic(TimeSpan period, Action tick)
        {
            _ = period;
            Tick = tick;
            return new Noop();
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class StubProfiles : IPlayerProfileService
    {
        private readonly PlayerProfile _active = new() { Id = Guid.NewGuid(), Name = "Test" };
        public IReadOnlyList<PlayerProfile> Profiles => new[] { _active };
        public PlayerProfile ActiveProfile => _active;
        public event Action<PlayerProfile>? ActiveProfileChanged { add { } remove { } }
        public string GetDataDirectory(Guid id) => Path.GetTempPath();
        public void InvalidateCache() { }
        public PlayerProfile Create(string name, string? accentColor = null) => _active;
        public void Rename(Guid id, string newName) { }
        public void SetLeftyFlip(Guid id, bool lefty) { }
        public void SetTuningMatch(Guid id, string myTuningId, bool playInMyTuning) { }
        public bool SwitchTo(Guid id) => false;
        public bool Delete(Guid id) => false;
        public bool SetAvatar(Guid id, string sourceImagePath) => false;
        public string? GetAvatarPath(Guid id) => null;
        public bool ExportProfile(Guid id, string zipPath) => false;
        public PlayerProfile? ImportProfile(string zipPath) => null;
    }
}
