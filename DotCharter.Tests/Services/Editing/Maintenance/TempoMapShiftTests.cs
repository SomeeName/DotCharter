namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     The tempo-map verbs (Insert Beat / Remove Beat / Add Measures At Start) move
///     the whole chart along the beat grid. Their two position traversals were
///     hand-written twice and both omitted <see cref="ChartLevel.FretHandMutes" />
///     and <see cref="Arrangement.NotationVoices" />, so every fret-hand mute and
///     every imported Guitar Pro voice 2-4 silently drifted one beat away from
///     the notes it belongs to. That file had zero tests.
///     <para>
///         The traversal itself is pinned in <c>ChartProjectPositionsTests</c>;
///         what these add is that the SHIPPED verbs go through it — i.e. that
///         nobody can re-inline a private walk here.
///     </para>
/// </summary>
public sealed class TempoMapShiftTests
{
    [Fact]
    public void InsertBeatAtPlayhead_MovesFretHandMutesAndNotationVoicesWithTheNotes()
    {
        var h = Build(playheadBeat: 0);

        h.Maintenance.InsertBeatAtPlayhead();

        var level = h.State.ActiveArrangement!.Levels[0];
        var voice = h.State.ActiveArrangement!.NotationVoices[0];
        Assert.Equal(new FractionalPosition(5), level.Notes[0].Position);
        Assert.Equal(new FractionalPosition(5), level.FretHandMutes[0].Position);
        Assert.Equal(new FractionalPosition(5), voice.Notes[0].Position);
        Assert.Equal(new FractionalPosition(5), voice.Chords[0].Position);
    }

    [Fact]
    public void RemoveBeatAtPlayhead_MovesFretHandMutesAndNotationVoicesWithTheNotes()
    {
        var h = Build(playheadBeat: 0);

        h.Maintenance.RemoveBeatAtPlayhead();

        var level = h.State.ActiveArrangement!.Levels[0];
        var voice = h.State.ActiveArrangement!.NotationVoices[0];
        Assert.Equal(new FractionalPosition(3), level.Notes[0].Position);
        Assert.Equal(new FractionalPosition(3), level.FretHandMutes[0].Position);
        Assert.Equal(new FractionalPosition(3), voice.Notes[0].Position);
        Assert.Equal(new FractionalPosition(3), voice.Chords[0].Position);
    }

    /// <summary>
    ///     Remove Beat deletes whatever sat ON the beat it removes, so that
    ///     content is not left anchored to a beat that no longer exists — and
    ///     that removal traversal skipped the same two collections.
    /// </summary>
    [Fact]
    public void RemoveBeatAtPlayhead_DeletesTheFretHandMuteAndVoiceSoundsOnThatBeat()
    {
        var h = Build(playheadBeat: 0);
        var level = h.State.ActiveArrangement!.Levels[0];
        var voice = h.State.ActiveArrangement!.NotationVoices[0];
        level.FretHandMutes.Add(new FretHandMute(new FractionalPosition(0)));
        voice.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), StringIndex = 2, Fret = 1 });

        h.Maintenance.RemoveBeatAtPlayhead();

        Assert.Single(level.FretHandMutes);
        Assert.Equal(new FractionalPosition(3), level.FretHandMutes[0].Position);
        Assert.Single(voice.Notes);
        Assert.Equal(new FractionalPosition(3), voice.Notes[0].Position);
    }

    [Fact]
    public void AddMeasuresAtStart_MovesFretHandMutesAndNotationVoicesWithTheNotes()
    {
        var h = Build(playheadBeat: 0);

        // One 4/4 measure = four beats, so the note at beat 4 lands on beat 8.
        h.Maintenance.AddMeasuresAtStart(1);

        var level = h.State.ActiveArrangement!.Levels[0];
        var voice = h.State.ActiveArrangement!.NotationVoices[0];
        Assert.Equal(new FractionalPosition(8), level.Notes[0].Position);
        Assert.Equal(new FractionalPosition(8), level.FretHandMutes[0].Position);
        Assert.Equal(new FractionalPosition(8), voice.Notes[0].Position);
        Assert.Equal(new FractionalPosition(8), voice.Chords[0].Position);
    }

    /// <summary>
    ///     A note, its mute marker and its sidecar voice at beat 4 — the whole
    ///     point is that they stay together, so every assertion above compares
    ///     them to the note rather than to a literal.
    /// </summary>
    private static ChartPowerToolsTests.Harness Build(int playheadBeat)
    {
        var h = new ChartPowerToolsTests.Harness();
        // Room for one 2 s measure before 0:00: AddMeasuresAtStart adds only
        // whole measures that fit there (#282).
        foreach (var beat in h.State.ActiveProject!.Beats) beat.Time += 2000;
        var arrangement = h.State.ActiveArrangement!;
        var level = arrangement.Levels[0];

        level.Notes.Clear();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 0, Fret = 3 });
        level.FretHandMutes.Add(new FretHandMute(new FractionalPosition(4)));

        var voice = new NotationVoice { VoiceIndex = 1 };
        voice.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 1, Fret = 5 });
        var chord = new Chord { Position = new FractionalPosition(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
        voice.Chords.Add(chord);
        arrangement.NotationVoices.Add(voice);

        h.State.CurrentTimeMs = h.State.ActiveProject!.Beats[playheadBeat].Time;
        return h;
    }
}
