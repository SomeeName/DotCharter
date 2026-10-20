using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     A riff-repeater pass must not be charged for the notes just BEFORE its
///     loop.
///     <para>
///         Arming a pass called <c>LiveNoteMatcher.Rewind(loop.StartMs)</c>,
///         whose horizon is <c>startMs − HitWindowLateMs</c> — the right rule for
///         a SEEK, where the player lands mid-window and the sounds around the
///         landing have to be earnable again. For a loop it re-armed up to
///         250 ms of chart sitting OUTSIDE the region, inside the count-in
///         pre-roll where <c>LivePlaySessionService.Offer</c> refuses to judge at
///         all. Every pass therefore expired them as misses the player could not
///         have avoided: into the run's accuracy, into the "you missed these"
///         list, and into the rock meter's drain, which no rewind undoes.
///     </para>
/// </summary>
public sealed class LoopPassPreRollMissTests
{
    // 120 BPM ⇒ 500 ms a beat, so a 4-beat count-in starts at 2000 ms.
    private static List<BeatMarker> Beats(int count = 40, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm })];
    }

    private static GuitarNote Note(int beat, Fraction within, int fret)
        => new() { Position = new FractionalPosition(beat, within), Fret = fret, StringIndex = 0 };

    /// <summary>
    ///     One note 125 ms BEFORE the loop start — inside the 250 ms late window,
    ///     which is what the old horizon re-armed — and one 250 ms after it.
    /// </summary>
    private static Arrangement Chart()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        arr.Levels[0].Notes.Add(Note(7, new Fraction(3, 4), fret: 5)); // 3875 ms — before the loop
        arr.Levels[0].Notes.Add(Note(8, new Fraction(1, 2), fret: 5)); // 4250 ms — inside it
        return arr;
    }

    private static LivePlaySessionService Started()
    {
        var arr = Chart();
        var session = new LivePlaySessionService();
        session.Start(arr, arr.Levels[0], Beats(), new LivePlaySessionOptions { CountInBeats = 4 });
        return session;
    }

    private static void Ticks(LivePlaySessionService session, double fromMs, double toMs)
    {
        for (var t = fromMs; t <= toMs + 1e-9; t += 50) session.HandleTick(t, isPlaying: true);
    }

    [Fact]
    public void APassDoesNotExpireTheNotesInItsCountInPreRoll()
    {
        var session = Started();
        var seek = session.SetLoop(4000, 6000);
        Assert.Equal(2000, seek);

        Ticks(session, seek!.Value, 5950); // the whole pass, stopping short of the wrap

        // Only the 4250 ms note, which really is inside [4000, 6000).
        Assert.Equal(0, session.Stats!.Hits);
        Assert.Equal(1, session.Stats.Misses);
        Assert.All(session.RecentMisses, m => Assert.True(m.TimeMs >= 4000,
            $"a miss at {m.TimeMs} ms is outside the loop [4000, 6000)"));
    }

    /// <summary>Two full passes, wrap included: still no phantom.</summary>
    [Fact]
    public void RepeatedPassesNeverAccumulateAPhantomMiss()
    {
        var session = Started();
        var seek = session.SetLoop(4000, 6000)!.Value;

        Ticks(session, seek, 6000); // pass 1, ending in the wrap
        Ticks(session, seek, 6000); // pass 2

        Assert.Equal(2, session.PassCount);
        Assert.Equal(2, session.RecentMisses.Count);
        Assert.All(session.RecentMisses, m => Assert.Equal(4250, m.TimeMs));
    }

    /// <summary>
    ///     The loop's OWN first sound is still re-armed by a wrap — that is what
    ///     makes a pass a fresh attempt, and it is the half the old horizon got
    ///     right.
    /// </summary>
    [Fact]
    public void TheLoopsFirstSoundIsStillEarnableAgainOnTheNextPass()
    {
        var session = Started();
        var seek = session.SetLoop(4000, 6000)!.Value;

        Ticks(session, seek, 4250);
        Assert.NotNull(session.Offer([new LivePitch(45, 0f, 1f)], [],
            4250, LiveTechniqueFlags.None, 0f));
        Assert.Equal(1, session.Stats!.Hits);

        session.HandleTick(6000, isPlaying: true); // wrap
        Assert.Equal(0, session.Stats.Hits);
        Assert.Equal(0, session.Stats.Misses);
    }

    /// <summary>
    ///     The matcher's own contract, in isolation: a plain SEEK keeps the
    ///     hit-window horizon (a landing is mid-window), a LOOP arm does not.
    /// </summary>
    [Fact]
    public void RewindToLoop_ClearsFromTheLoopStart_WhileRewindClearsAWindowEarlier()
    {
        var arr = Chart();
        var beats = Beats();

        var seeked = new LiveNoteMatcher();
        seeked.Load(arr, arr.Levels[0], beats);
        seeked.AdvanceTo(9000); // both windows expire
        Assert.Equal(2, seeked.Stats.Misses);
        seeked.Rewind(4000);
        Assert.Equal(0, seeked.Stats.Misses); // the 3875 ms note came back too

        var looped = new LiveNoteMatcher();
        looped.Load(arr, arr.Levels[0], beats);
        looped.AdvanceTo(9000);
        looped.RewindToLoop(4000);
        Assert.Equal(1, looped.Stats.Misses); // the 3875 ms note keeps its verdict
        Assert.Equal(3875, looped.ResolvedThroughMs);
    }
}
