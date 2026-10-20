using System.Collections;
using System.Reflection;
using DotCharter.Tests.Services.State;

namespace DotCharter.Tests.Services.Tempo;

/// <summary>
///     Tempo map ▸ Align beat grid to notes (#403): the grid moves, nothing timed does.
///     Positions are relative to the beats, so the failure this guards is silent — a
///     forgotten field rides along with the grid and lands the shift's size away from
///     where it was, and still saves.
/// </summary>
public sealed class BeatGridShiftTests
{
    private const double OneMs = 1.0;

    /// <summary>
    ///     The coverage half, by REFLECTION: every <see cref="FractionalPosition" />
    ///     anywhere in a fully populated project keeps its absolute time. A field the
    ///     domain gains tomorrow is checked the day it compiles; one the shift forgets
    ///     moves by the whole offset. A <c>Sustain</c> is a span from the nearest
    ///     enclosing <c>Position</c>, so its END is what must stay put.
    /// </summary>
    [Theory]
    [InlineData(121.0)]
    [InlineData(-173.4)]
    public void EveryPositionInTheModel_KeepsItsAbsoluteTime(double offsetMs)
    {
        var project = ModelGraphWalker.BuildFullyPopulated();
        project.Beats = new(BeatGridFixtures.Grid(3000, (24, 120), (24, 150), (16, 96)));
        var before = TimedSlots(project);

        var result = BeatGridShift.Apply(project, offsetMs);

        Assert.True(result.Applied);
        var after = TimedSlots(project);
        foreach (var reached in new[] { ".Sustain", ".EndPosition", ".BendValues[", ".FretHandMutes[", ".NotationVoices[", ".Lyrics[", ".Showlights[" })
            Assert.True(before.Keys.Any(k => k.Contains(reached, StringComparison.Ordinal)), $"the walk never reached a {reached} slot — the fixture went empty");
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        var moved = before
            .Where(slot => Math.Abs(slot.Value - after[slot.Key]) > OneMs)
            .Select(slot => $"{slot.Key}: {slot.Value:0.###} → {after[slot.Key]:0.###} ms")
            .ToList();
        Assert.True(moved.Count == 0, "these moved with the grid instead of keeping their time:\n" + string.Join("\n", moved));
    }

    /// <summary>
    ///     The #403 case end to end: a grid shipped 121 ms early, moved 121 ms later, IS the
    ///     grid the notes were written on — every beat where the true one is, and every
    ///     note back on the very subdivision it was written at, across three tempos and
    ///     triplets.
    /// </summary>
    [Fact]
    public void AGridShippedEarly_MovedByItsError_PutsEveryNoteBackOnItsSubdivision()
    {
        var project = BeatGridFixtures.Chart(gridErrorMs: -121);
        var truth = BeatGridFixtures.TrueGrid();
        var timesBefore = project.Arrangements[0].Levels[0].Notes.Select(n => n.Position.GetPositionMs(project.Beats)).ToList();

        var result = BeatGridShift.Apply(project, 121);

        Assert.Equal(new BeatGridShiftResult(true, 0, 0, 0), result);
        Assert.Equal(truth.Select(b => b.Time), project.Beats.Select(b => b.Time));
        var notes = project.Arrangements[0].Levels[0].Notes;
        var expected = BeatGridFixtures.NotePositions();
        for (var i = 0; i < notes.Count; i++)
        {
            // Within half a 1/192 tick: the subdivision it was written on, not its neighbour.
            Assert.True(Math.Abs(expected[i].DoubleValue - notes[i].Position.DoubleValue) < 1.0 / 384,
                $"note {i} landed at {notes[i].Position}, written at {expected[i]}");
            Assert.Equal(timesBefore[i], notes[i].Position.GetPositionMs(project.Beats), OneMs);
        }
    }

    /// <summary>
    ///     A sustain is a span in BEATS, and the beats it spans are worth different
    ///     amounts on either side of a tempo change — so moving the grid changes how
    ///     many beats the same sound lasts. Its END keeps its time.
    /// </summary>
    [Fact]
    public void ASustainAcrossATempoChange_KeepsItsEndTime()
    {
        var project = BeatGridFixtures.Chart(gridErrorMs: 0);
        var note = new GuitarNote { Position = new FractionalPosition(15), Sustain = new FractionalPosition(2), Fret = 5 };
        project.Arrangements[0].Levels[0].Notes.Add(note);
        var chord = new Chord { Position = new FractionalPosition(15, new Fraction(1, 2)) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 7, Sustain = new FractionalPosition(1) });
        project.Arrangements[0].Levels[0].Chords.Add(chord);
        var noteEnd = note.EndPosition.GetPositionMs(project.Beats);
        var chordEnd = (chord.Position + chord.ChordNotes[0].Sustain).GetPositionMs(project.Beats);

        BeatGridShift.Apply(project, 90);

        Assert.Equal(noteEnd, note.EndPosition.GetPositionMs(project.Beats), OneMs);
        Assert.Equal(chordEnd, (chord.Position + chord.ChordNotes[0].Sustain).GetPositionMs(project.Beats), OneMs);
        Assert.NotEqual(new FractionalPosition(2), note.Sustain);
    }

    /// <summary>
    ///     Moving the grid LATER uncovers the start: the phrase on the old first beat
    ///     would sit before the new one. A beat is prepended at the first interval, the
    ///     first beat stays a bar line, and the short leading bar says how long it is.
    /// </summary>
    [Fact]
    public void MovingLater_PrependsABeatSoTheFirstElementIsStillCovered()
    {
        var project = OneBarChart(firstBeatMs: 1000);

        var result = BeatGridShift.Apply(project, 121);

        Assert.Equal(1, result.BeatsAddedAtStart);
        Assert.Equal(621, project.Beats[0].Time, 6);
        Assert.True(project.Beats[0].IsMeasure);
        Assert.Equal(1, project.Beats[0].BeatsInMeasure);
        Assert.True(project.Beats[1].IsMeasure);
        Assert.Equal(1000, project.Arrangements[0].PhraseIterations[0].Position.GetPositionMs(project.Beats), OneMs);
        Assert.True(project.Arrangements[0].PhraseIterations[0].Position >= FractionalPosition.Zero);
    }

    /// <summary>No beat before 0:00 (#282): a prepended beat that would land there is clamped to 0:00.</summary>
    [Fact]
    public void MovingLater_NearTheStart_ClampsThePrependedBeatToZero()
    {
        var project = OneBarChart(firstBeatMs: 100);

        BeatGridShift.Apply(project, 121);

        Assert.Equal(0, project.Beats[0].Time);
        Assert.All(project.Beats, b => Assert.True(b.Time >= 0));
        Assert.Equal(100, project.Arrangements[0].PhraseIterations[0].Position.GetPositionMs(project.Beats), OneMs);
    }

    /// <summary>
    ///     Moving EARLIER can push the first beat before 0:00, which a Rocksmith beat
    ///     list cannot hold: it is dropped, the start is covered from 0:00, the new
    ///     first beat is a bar line — and the END, uncovered at the other side, gets
    ///     a beat of its own.
    /// </summary>
    [Fact]
    public void MovingEarlier_DropsBeatsBeforeZero_AndCoversBothEnds()
    {
        var project = OneBarChart(firstBeatMs: 50);
        var lastBeat = project.Beats.Count - 1;
        project.Arrangements[0].PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(lastBeat), PhraseId = 1 });
        var endMs = project.Beats[^1].Time;

        var result = BeatGridShift.Apply(project, -121);

        Assert.Equal(1, result.BeatsDropped);
        Assert.True(result.BeatsAddedAtEnd >= 1);
        Assert.All(project.Beats, b => Assert.True(b.Time >= 0));
        Assert.True(project.Beats[0].IsMeasure);
        var iterations = project.Arrangements[0].PhraseIterations;
        Assert.Equal(50, iterations[0].Position.GetPositionMs(project.Beats), OneMs);
        Assert.Equal(endMs, iterations[1].Position.GetPositionMs(project.Beats), OneMs);
        Assert.True(iterations[1].Position.DoubleValue <= project.Beats.Count - 1, "the END phrase fell off the grid");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void NothingToMove_ChangesNothing(double offsetMs)
    {
        var project = BeatGridFixtures.Chart(gridErrorMs: 0);
        var times = project.Beats.Select(b => b.Time).ToList();

        Assert.False(BeatGridShift.Apply(project, offsetMs).Applied);
        Assert.Equal(times, project.Beats.Select(b => b.Time));
    }

    /// <summary>Two bars at 120 BPM from <paramref name="firstBeatMs" />, a phrase on the first beat and a note in bar two.</summary>
    private static ChartProject OneBarChart(double firstBeatMs)
    {
        var project = new ChartProject();
        foreach (var beat in BeatGridFixtures.Grid(firstBeatMs, (8, 120))) project.Beats.Add(beat);
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(5), Fret = 2 });
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(level);
        arrangement.PhraseIterations.Add(new PhraseIteration { Position = FractionalPosition.Zero, PhraseId = 0 });
        project.Arrangements.Add(arrangement);
        return project;
    }

    /// <summary>
    ///     Every <see cref="FractionalPosition" /> reachable from the project, as an
    ///     absolute time keyed by its property path. The legacy flat store is left out
    ///     on purpose (<c>ChartProjectPositions</c>' summary: nothing writes it).
    /// </summary>
    private static Dictionary<string, double> TimedSlots(ChartProject project)
    {
        var slots = new Dictionary<string, double>(StringComparer.Ordinal);
        Walk(project, "project", null, project.Beats, slots, 0);
        return slots;
    }

    private static void Walk(object node, string path, FractionalPosition? anchor, IList<BeatMarker> beats,
        Dictionary<string, double> slots, int depth)
    {
        if (depth > 8 || path.StartsWith("project.Chart.SingleNotes", StringComparison.Ordinal) ||
            path.StartsWith("project.Chart.Chords", StringComparison.Ordinal))
        {
            return;
        }

        var properties = node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .ToList();
        var own = properties.FirstOrDefault(p => p.Name == "Position" && p.PropertyType == typeof(FractionalPosition));
        if (own != null) anchor = (FractionalPosition)own.GetValue(node)!;

        foreach (var property in properties)
        {
            var childPath = $"{path}.{property.Name}";
            if (property.PropertyType == typeof(FractionalPosition))
            {
                var value = (FractionalPosition)property.GetValue(node)!;
                var absolute = property.Name == "Sustain" && anchor is { } start ? start + value : value;
                slots[childPath] = absolute.GetPositionMs(beats);
                continue;
            }

            if (property.PropertyType == typeof(string) || property.PropertyType.IsValueType) continue;
            var child = property.GetValue(node);
            switch (child)
            {
                case null:
                case string:
                    continue;
                case IDictionary dictionary:
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Value is { } value && IsDomain(value)) Walk(value, $"{childPath}[{entry.Key}]", anchor, beats, slots, depth + 1);
                    }

                    continue;
                case IEnumerable items:
                    var index = 0;
                    foreach (var item in items)
                    {
                        if (item is not null && IsDomain(item)) Walk(item, $"{childPath}[{index}]", anchor, beats, slots, depth + 1);
                        index++;
                    }

                    continue;
            }

            if (IsDomain(child)) Walk(child, childPath, anchor, beats, slots, depth + 1);
        }
    }

    private static bool IsDomain(object value) =>
        value.GetType() is { IsClass: true } type &&
        (type.Namespace?.StartsWith("DotCharter.Core.Domain", StringComparison.Ordinal) ?? false) &&
        value is not BeatMarker;
}
