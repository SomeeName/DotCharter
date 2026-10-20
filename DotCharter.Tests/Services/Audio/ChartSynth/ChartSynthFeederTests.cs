using DotCharter.Tone.Audio.ChartSynth;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The feeder uploads the chart ONCE per change — never per tick — on an
///     injected clock: off means nothing, on means the next tick, an unchanged
///     chart costs no upload, an edit re-uploads within one check interval, and an
///     engine that lost its notes gets them back.
/// </summary>
public sealed class ChartSynthFeederTests
{
    private sealed class FakeSynth : IChartSynthEngine
    {
        public List<ChartNoteUpload> Uploads { get; } = [];
        public bool Accept { get; set; } = true;
        public bool IsChartSynthAvailable => true;

        public event EventHandler? ChartSynthAvailabilityChanged
        {
            add { }
            remove { }
        }

        public bool IsChartSynthEnabled { get; set; }
        public void SetChartSynthEnabled(bool enabled) => IsChartSynthEnabled = enabled;
        public ChartSynthVoice ChartSynthVoice => ChartSynthVoice.Placeholder;
        public bool IsChartSynthVoiceAvailable(ChartSynthVoice voice) => voice == ChartSynthVoice.Placeholder;
        public bool TrySetChartSynthVoice(ChartSynthVoice voice) => voice == ChartSynthVoice.Placeholder;
        public float ChartSynthGain { get; set; } = 1f;
        public long? UploadedChartFingerprint { get; set; }

        /// <summary>Runs inside an upload, before it completes: a timer tick that
        /// arrives while the engine is still decoding the new chart's zones.</summary>
        public Action? DuringUpload { get; set; }

        public bool UploadChartNotes(ChartNoteUpload upload)
        {
            if (!Accept) return false;
            DuringUpload?.Invoke();
            Uploads.Add(upload);
            UploadedChartFingerprint = upload.Fingerprint;
            return true;
        }

        public bool CanRouteInstrumentToRig => true;
        public bool RouteInstrumentToRig { get; set; }

        public bool TryGetChartSynthStatus(out ChartSynthStatus status)
        {
            status = default;
            return false;
        }

        public ChartSynthInstrument GuitarInstrument => ChartSynthInstrumentSelection.DefaultGuitar;
        public ChartSynthInstrument BassInstrument => ChartSynthInstrumentSelection.DefaultBass;
        public bool TrySetChartSynthInstrument(ChartSynthInstrument instrument) => false;
        public ChartSynthModelInfo? GetChartSynthModelInfo(ChartSynthModel model) => null;
    }

    private readonly Arrangement _arr = new();
    private readonly List<GuitarNote> _notes = [ChartNoteEventBuilderTests.Note(2, 0, 3), ChartNoteEventBuilderTests.Note(4, 1, 5)];
    private readonly List<Chord> _chords = [];
    private readonly List<BeatMarker> _beats = ChartNoteEventBuilderTests.Beats();
    private readonly ChartSynthFeeder _feeder = new();
    private readonly FakeSynth _synth = new();

    private bool Tick(long now) => _feeder.Tick(now, _synth, _arr, _notes, _chords, _beats, null, 120);

    [Fact]
    public void Off_UploadsNothing()
    {
        Assert.False(Tick(0));
        Assert.False(Tick(10_000));
        Assert.Empty(_synth.Uploads);
    }

    [Fact]
    public void On_UploadsOnTheNextTick_ThenOnlyOnAChange_AtMostEveryInterval()
    {
        _synth.IsChartSynthEnabled = true;
        Assert.True(Tick(1000));
        Assert.Equal(2, _synth.Uploads[0].Notes.Length);

        // 250 Hz ticks over one second of an unchanged chart: no second upload.
        for (var t = 1004; t < 2000; t += 4) Assert.False(Tick(t));
        Assert.Single(_synth.Uploads);

        // An edit is seen at the next check, not sooner.
        _notes[0].Fret = 7;
        var uploadedAt = -1L;
        for (var t = 2000L; t < 2000 + 2 * ChartSynthFeeder.CheckIntervalMs && uploadedAt < 0; t += 4)
            if (Tick(t)) uploadedAt = t;
        Assert.True(uploadedAt >= 0, "an edit was never uploaded");
        Assert.True(uploadedAt - 2000 <= ChartSynthFeeder.CheckIntervalMs, $"the edit took {uploadedAt - 2000} ms to upload");
        Assert.Equal(2, _synth.Uploads.Count);
        Assert.Equal(47f, _synth.Uploads[1].Notes[0].Pitch);   // E2 + fret 7
    }

    /// <summary>
    ///     The position timer runs a late callback beside the next one, and since
    ///     #417 an upload decodes voice zones first, so it can outlast the check
    ///     interval. A tick that arrives during an upload must not start a second
    ///     upload of the same chart beside it.
    /// </summary>
    [Fact]
    public void ATickDuringAnUpload_DoesNotUploadAgain()
    {
        _synth.IsChartSynthEnabled = true;
        bool? overlapping = null;
        _synth.DuringUpload = () =>
        {
            _synth.DuringUpload = null;
            overlapping = Tick(1000 + 4 * ChartSynthFeeder.CheckIntervalMs);
        };

        Assert.True(Tick(1000));
        Assert.False(overlapping, "the overlapping tick never ran or uploaded a second time");
        Assert.Single(_synth.Uploads);
        Assert.Equal(1, _feeder.Uploads);
    }

    [Fact]
    public void AnEngineThatLostItsNotes_GetsThemBack()
    {
        _synth.IsChartSynthEnabled = true;
        Assert.True(Tick(0));
        _synth.UploadedChartFingerprint = null;   // e.g. a fresh engine that replayed nothing
        Assert.True(Tick(ChartSynthFeeder.CheckIntervalMs));
        Assert.Equal(2, _synth.Uploads.Count);
    }

    [Fact]
    public void ARefusedUpload_IsRetriedAtTheNextCheck()
    {
        _synth.IsChartSynthEnabled = true;
        _synth.Accept = false;
        Assert.False(Tick(0));
        _synth.Accept = true;
        Assert.False(Tick(10));
        Assert.True(Tick(ChartSynthFeeder.CheckIntervalMs));
    }

    [Fact]
    public void TurningItOffAndOn_UploadsAgainAtOnce_WhenTheEngineDiffers()
    {
        _synth.IsChartSynthEnabled = true;
        Assert.True(Tick(0));
        _synth.IsChartSynthEnabled = false;
        Assert.False(Tick(4));
        _synth.IsChartSynthEnabled = true;
        _notes.Add(ChartNoteEventBuilderTests.Note(6, 2, 2));
        Assert.True(Tick(8));   // no 250 ms wait after a re-enable
        Assert.Equal(3, _synth.Uploads[^1].Notes.Length);
    }

    [Fact]
    public void NoArrangement_UploadsNothing()
    {
        _synth.IsChartSynthEnabled = true;
        Assert.False(_feeder.Tick(0, _synth, null, _notes, _chords, _beats, null, 120));
        Assert.Empty(_synth.Uploads);
    }
}
