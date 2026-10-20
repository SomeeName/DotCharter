using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Playback;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The per-selection audition arms a ONE-SHOT loop and starts playback; the
///     pass disarms itself when it REACHES its end
///     (<c>EditorLoopAction.StopAtStart</c> → <c>EndAudition</c>). Two ways it
///     could never reach one, and both left the flag set — so the next ordinary
///     Play ran to the audition's end and paused, with nothing on screen saying
///     why.
/// </summary>
public sealed class TransportAuditionArmingTests
{
    private sealed class Harness
    {
        public Harness(double totalAudioLengthMs)
        {
            var logger = new FakeAppLogger();
            var config = new FakeConfigurationService();

            var project = new ChartProject();
            for (var i = 0; i < 32; i++)
                project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });
            var arr = new Arrangement { Name = "Lead" };
            arr.Levels.Add(new ChartLevel());
            project.Arrangements.Add(arr);

            State.ActiveProject = project;
            State.ActiveArrangement = arr;
            State.ActiveLevel = arr.Levels[0];
            State.TotalAudioLengthMs = totalAudioLengthMs;

            var scope = new EditingScope(State);
            Loop = new EditorLoopService(State, new EditTargetService(Selection, scope, config), scope, logger);
            Transport = new TransportViewModel(Audio, State, config, FakeLocalization.KeysOnly(), null, logger, Loop);

            // Something to audition: one note with a sustain, selected.
            var note = new GuitarNote
            {
                Position = new FractionalPosition(2), StringIndex = 0, Fret = 5,
                Sustain = new FractionalPosition(2)
            };
            State.PlacedNotes.Add(note);
            Selection.SelectedNotes.Add(note);
        }

        public ProjectState State { get; } = new();
        public SelectionService Selection { get; } = new();
        public FakeAudioPlaybackService Audio { get; } = new();
        public EditorLoopService Loop { get; }
        public TransportViewModel Transport { get; }
    }

    [Fact]
    public void AuditioningWithATransport_ArmsTheOneShotAndPlays()
    {
        var h = new Harness(totalAudioLengthMs: 16_000);

        h.Transport.AuditionSelectionCommand.Execute(null);

        Assert.True(h.Audio.IsPlaying);
        Assert.True(h.State.LoopIsAudition);
        Assert.True(h.State.LoopEnabled);
    }

    /// <summary>
    ///     <c>Play</c> refuses when there is no transport to run at all — a
    ///     chart-only project whose silent-transport fallback did not load. The
    ///     audition is armed by then, and nothing will ever complete its pass.
    /// </summary>
    [Fact]
    public void AuditioningWithNoTransport_LeavesNothingArmed()
    {
        var h = new Harness(totalAudioLengthMs: 0);

        h.Transport.AuditionSelectionCommand.Execute(null);

        Assert.False(h.Audio.IsPlaying);
        Assert.False(h.State.LoopIsAudition);
        Assert.False(h.State.LoopEnabled);
    }

    [Fact]
    public void StoppingMidAudition_EndsThePass()
    {
        var h = new Harness(totalAudioLengthMs: 16_000);
        h.Transport.AuditionSelectionCommand.Execute(null);
        Assert.True(h.State.LoopIsAudition);

        h.Transport.StopCommand.Execute(null);

        Assert.False(h.State.LoopIsAudition);
        Assert.False(h.State.LoopEnabled);
    }

    /// <summary>Stop must not disturb an ordinary A/B loop — only an audition
    /// disarms itself, and <c>EndAudition</c> is a no-op otherwise.</summary>
    [Fact]
    public void StoppingWithAnOrdinaryLoopArmed_KeepsIt()
    {
        var h = new Harness(totalAudioLengthMs: 16_000);
        h.Transport.SetLoopStartCommand.Execute(null);
        h.State.CurrentTimeMs = 4_000;
        h.Transport.SetLoopEndCommand.Execute(null);
        Assert.True(h.State.LoopEnabled);

        h.Transport.StopCommand.Execute(null);

        Assert.True(h.State.LoopEnabled);
        Assert.NotNull(h.State.LoopRegion);
    }
}
