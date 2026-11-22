using System.Reflection;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Audio.LivePlay.Takes;
using JuceSharp.Dsp;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The dead-input warning is PER RUN, and it used to outlive one.
///     <para>
///         <c>IsInputSilent</c> ("another app holds your input") and the counters
///         behind it — <c>_silentSamples</c>, <c>_sawAnySignal</c> and the
///         worker's own <c>_workerInputSilent</c> shadow — were reset by nothing.
///         <c>TearDownAnalysis</c> cleared its sibling <c>IsInputStarved</c> and
///         left this one up, so the banner could stay on screen after the run
///         that raised it had ended and with nothing reading the input at all;
///         and because the counter survived, the NEXT run tripped the ~4 s
///         warning a fraction of a second in, on the previous run's total.
///     </para>
///     <para>
///         The shadow matters as much as the banner: a false shadow under a true
///         banner can never flip the banner back down, because the worker only
///         publishes CHANGES.
///     </para>
/// </summary>
public sealed class LivePlayInputSilenceResetTests
{
    /// <summary>~12 s of exact zeros — three times the warning's ~4 s
    /// threshold, so a surviving counter is unmistakable.</summary>
    private const int LastRunsSilence = 12 * 48_000;

    [Fact]
    public async Task A_capture_rate_change_stops_the_take_without_relabeling_its_samples()
    {
        var audio = new RecordingAudio { InputSampleRate = 44100 };
        var vm = LivePlayTestFactory.Create(audio, new ProjectState(), new FakeAppLogger(),
            new FakeConfigurationService(), new InMemoryPracticeStore());
        using var stream = new MemoryStream();
        using var recorder = new LiveTakeRecorder(stream, "take.wav", 44100, TimeSpan.FromMinutes(1), autoStartWriter: false);
        SetField(vm, "_analyzer", new GuitarInputAnalyzer(44100));
        SetField(vm, "_takeRecorder", recorder);
        Invoke(vm, "TickCore");
        audio.InputSampleRate = 48000;
        Invoke(vm, "TickCore");
        audio.InputSampleRate = 44100;
        Invoke(vm, "TickCore"); // changing back must not resume the take

        var result = await recorder.StopAsync();

        Assert.Equal(100, result.SamplesWritten);
        var bytes = stream.ToArray();
        Assert.Equal(44100, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(100 * 2, BitConverter.ToInt32(bytes, 40));
    }

    private sealed class RecordingAudio : FakeAudioPlaybackService
    {
        private ulong _position;

        public override int ReadInputCapture(float[] destination, out ulong streamPosition)
        {
            streamPosition = _position;
            _position += 100;
            Array.Clear(destination);
            return 100;
        }
    }

    [Fact]
    public void StartingARun_ResetsTheInputSilenceWatchAndItsBanner()
    {
        var vm = NewViewModel();
        LoadChart(vm);
        // An analyzer already exists, so EnsureAnalyzer is a no-op and no
        // detection worker is started: this test is about StartCore's resets,
        // not about pumping audio on a thread.
        SetField(vm, "_analyzer", new GuitarInputAnalyzer(48_000));
        MarkPreviousRunSilent(vm);

        Assert.True((bool)Invoke(vm, "StartCore")!,
            "the session refused to start — the resets under test never ran");

        AssertWatchIsClean(vm);
    }

    [Fact]
    public void TearingDownAnalysis_ClearsBothInputBanners()
    {
        var vm = NewViewModel();
        MarkPreviousRunSilent(vm);
        vm.IsInputStarved = true;

        Invoke(vm, "TearDownAnalysis");

        AssertWatchIsClean(vm);
        Assert.False(vm.IsInputStarved); // the half that already worked
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(-80)]
    [InlineData(-1120)]
    public void StartingARun_WiresStrictChordsAndTheArrangementsReferencePitch(double centOffset)
    {
        WithVerifier(centOffset, "Strict", verifier =>
        {
            verifier.Push(PitchedAudio([64, 71, 76], ArrangementInstrument.ResidualCents(centOffset)), 0);
            var sound = new ExpectedSound { TimeMs = 200, MidiNotes = [64, 71, 76], IsChord = true };
            Assert.True(verifier.Verify(sound).Confirmed);
            Assert.True(verifier.VerifyEarly(sound).Confirmed);
            var missing = new ExpectedSound { TimeMs = 200, MidiNotes = [64, 71, 76, 81], IsChord = true };
            Assert.False(verifier.Verify(missing).Confirmed);
            Assert.False(verifier.VerifyEarly(missing).Confirmed);
        });
    }

    [Fact]
    public void StandardChords_StillAcceptAMissingToneInADenseVoicing()
    {
        WithVerifier(0, "Standard", verifier =>
        {
            verifier.Push(PitchedAudio([64, 71, 76], 0), 0);
            Assert.True(verifier.Verify(new ExpectedSound
            {
                TimeMs = 200, MidiNotes = [64, 71, 76, 81], IsChord = true
            }).Confirmed);
        });
    }

    [Fact]
    public void StrictChords_AcceptACompleteDenseVoicing()
    {
        WithVerifier(0, "Strict", verifier =>
        {
            verifier.Push(PitchedAudio([64, 71, 76, 81], 0), 0);
            Assert.True(verifier.Verify(new ExpectedSound
            {
                TimeMs = 200, MidiNotes = [64, 71, 76, 81], IsChord = true
            }).Confirmed);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredDetuning_RejectsTheOppositeDetuning(bool chord)
    {
        WithVerifier(45, "Strict", verifier =>
        {
            var notes = chord ? (int[])[64, 71, 76] : [76];
            verifier.Push(PitchedAudio(notes, -45), 0);
            var sound = new ExpectedSound { TimeMs = 200, MidiNotes = notes, IsChord = chord };
            Assert.False(verifier.Verify(sound).Confirmed);
            Assert.False(verifier.VerifyEarly(sound).Confirmed);
        });
    }

    [Fact]
    public void AuthoredReferenceOffset_IsNotReadAsABendInTheContour()
    {
        WithVerifier(80, "Strict", verifier =>
        {
            verifier.Push(PitchedAudio([64], 80), 0);
            var sound = new ExpectedSound
            {
                TimeMs = 200, MidiNotes = [64],
                TechniqueContour = new TechniqueContourExpectation(ContourTechniques.Bend, 2, 250, 0, 700)
            };
            Assert.True(verifier.Verify(sound).Confirmed);
            var cents = new float[100];
            var first = (long)Math.Round(0.2 * 48_000 / LiveCqtConfig.Hop);
            verifier.ReadContour(sound, first, cents);
            var held = cents.Skip(10).Where(c => !float.IsNaN(c)).ToArray();
            Assert.NotEmpty(held);
            Assert.InRange(held.Average(), -20, 20);
        });
    }

    private static float[] PitchedAudio(int[] notes, double cents)
    {
        var audio = new float[48_000];
        foreach (var midi in notes)
        {
            var frequency = 440.0 * Math.Pow(2, (midi - 69 + cents / 100) / 12);
            for (var i = 9_600; i < audio.Length; i++)
            {
                var t = (i - 9_600) / 48_000.0;
                audio[i] += (float)(Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-1.2 * t) / notes.Length);
            }
        }

        return audio;
    }

    // Use the existing startup seam so these tests also compile against the pre-fix API.
    private static void WithVerifier(double centOffset, string strictness, Action<LiveCqtVerifier> check)
    {
        var vm = NewViewModel();
        LoadChart(vm);
        GetField<ProjectState>(vm, "_projectState").ActiveArrangement!.CentOffset = centOffset;
        var config = GetField<IConfigurationService>(vm, "_configService").LoadAppConfig();
        config.LivePlayUseCqtVerifier = true;
        config.LivePlayChordStrictness = strictness;
        SetField(vm, "_analyzer", new GuitarInputAnalyzer(48_000));
        try
        {
            Assert.True((bool)Invoke(vm, "StartCore")!);
            check(GetField<LiveCqtVerifier>(vm, "_cqtVerifier"));
        }
        finally
        {
            Invoke(vm, "TearDownAnalysis");
        }
    }

    // ---- fixture -------------------------------------------------------------

    private static void MarkPreviousRunSilent(LivePlayViewModel vm)
    {
        SetField(vm, "_silentSamples", LastRunsSilence);
        SetField(vm, "_sawAnySignal", true);
        SetField(vm, "_workerInputSilent", true);
        vm.IsInputSilent = true;
    }

    private static void AssertWatchIsClean(LivePlayViewModel vm)
    {
        Assert.False(vm.IsInputSilent,
            "the dead-input banner outlived the run that raised it");
        Assert.Equal(0, GetField<int>(vm, "_silentSamples"));
        Assert.False(GetField<bool>(vm, "_sawAnySignal"));
        Assert.False(GetField<bool>(vm, "_workerInputSilent"),
            "the worker's shadow must be reset with the banner — a false shadow under a true banner can never " +
            "flip it back down, because only CHANGES are published");
    }

    private static LivePlayViewModel NewViewModel()
        => LivePlayTestFactory.Create(new FakeAudioPlaybackService { InputSampleRate = 48_000 },
            new ProjectState(),
            new FakeAppLogger(),
            new FakeConfigurationService(),
            new InMemoryPracticeStore());

    /// <summary>A one-beat chart with one empty level — enough for
    /// <c>StartCore</c> to reach its per-run resets.</summary>
    private static void LoadChart(LivePlayViewModel vm)
    {
        var state = GetField<ProjectState>(vm, "_projectState");
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        state.Beats.Add(new BeatMarker { Time = 0, IsMeasure = true });
        state.ActiveArrangement = arrangement; // also sets ActiveLevel
    }

    // The silence watch is worker-confined by design, so the test reaches it the
    // way the worker does rather than widening the type's surface for a test.

    private static FieldInfo Field(string name)
    {
        var field = typeof(LivePlayViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field != null, $"LivePlayViewModel has no field '{name}' — this test was renamed out of its job");
        return field!;
    }

    private static T GetField<T>(LivePlayViewModel vm, string name) => (T)Field(name).GetValue(vm)!;

    private static void SetField(LivePlayViewModel vm, string name, object? value) => Field(name).SetValue(vm, value);

    private static object? Invoke(LivePlayViewModel vm, string name, params object?[] args)
    {
        var method = typeof(LivePlayViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(method != null, $"LivePlayViewModel has no method '{name}' — this test was renamed out of its job");
        return method!.Invoke(vm, args);
    }

    /// <summary>Never reached by these tests (no run is stopped); it exists
    /// because the view model takes one.</summary>
    private sealed class InMemoryPracticeStore : IPracticeProfileService
    {
        private readonly Dictionary<string, PracticeArrangementProfile> _profiles = new(StringComparer.Ordinal);

        public PracticeArrangementProfile GetProfile(string songKey, string arrangementName)
            => _profiles.TryGetValue(songKey + "|" + arrangementName, out var p) ? p : new PracticeArrangementProfile();

        public PracticeArrangementProfile Record(string songKey, string arrangementName,
            LivePlaySessionResult result, string? displayName = null,
            IReadOnlyList<PhraseMasteryOutcome>? phraseOutcomes = null,
            IReadOnlyList<SectionResult>? sections = null,
            IReadOnlyList<TechniqueResult>? techniques = null,
            double? chartDifficulty = null, bool failed = false, PracticeRunIdentity? run = null)
        {
            var key = songKey + "|" + arrangementName;
            if (!_profiles.TryGetValue(key, out var profile)) _profiles[key] = profile = new PracticeArrangementProfile();
            return profile;
        }

        public IReadOnlyDictionary<string, PracticeArrangementProfile> GetAll() => _profiles;
        public bool AttachTakeFile(string songKey, string arrangementName, string takeFileName) => false;
        public bool AttachReplayFile(string songKey, string arrangementName, string replayFileName) => false;
        public void InvalidateCache() { }

        public bool IsStoreAuthoritative => true;

        public int RekeySong(string fromSongKey, string toSongKey) => 0;
    }
}
