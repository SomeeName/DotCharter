using System.Collections;
using System.Reflection;
using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     <see cref="ChartProjectPositions" /> is the ONE exhaustive walk over a
///     project's absolute positions, and these tests are the reason it exists:
///     the two traversals it replaced were hand-written twice in
///     <c>ChartMaintenanceService.TempoMap</c> and BOTH omitted
///     <see cref="ChartLevel.FretHandMutes" /> and
///     <see cref="Arrangement.NotationVoices" />, so Insert Beat / Remove Beat /
///     Add Beats At Start desynchronised them from the rest of the chart with no
///     error anywhere.
/// </summary>
public sealed class ChartProjectPositionsTests
{
    private static readonly FractionalPosition Ten = new(10);

    [Fact]
    public void ShiftAll_MovesEveryAbsolutePosition()
    {
        var project = BuildProjectWithOneOfEverything(atBeat: 4);

        ChartProjectPositions.ShiftAll(project, Ten);

        AssertEverythingSitsAt(project, 14);
    }

    [Fact]
    public void ShiftFromBeat_MovesEveryAbsolutePosition_AtOrAfterThePivot()
    {
        var project = BuildProjectWithOneOfEverything(atBeat: 4);

        ChartProjectPositions.ShiftFromBeat(project, 4, +10);

        AssertEverythingSitsAt(project, 14);
    }

    [Fact]
    public void ShiftFromBeat_LeavesEverythingBeforeThePivotAlone()
    {
        var project = BuildProjectWithOneOfEverything(atBeat: 4);

        // Pivot 6: past the fixture's ENDS at beat 5 as well, which are absolute
        // and are shifted in their own right.
        ChartProjectPositions.ShiftFromBeat(project, 6, +10);

        AssertEverythingSitsAt(project, 4);
    }

    /// <summary>
    ///     The one thing a shift must NOT touch. <c>Sustain</c> is a DURATION
    ///     measured from its own sound's onset — moving the onset already moves
    ///     it, so adding the delta a second time would stretch every sustain in
    ///     the chart by the size of the shift.
    /// </summary>
    [Fact]
    public void ShiftAll_LeavesRelativeSustainsAlone()
    {
        var project = BuildProjectWithOneOfEverything(atBeat: 4);
        var level = project.Arrangements[0].Levels[0];
        level.Notes[0].Sustain = new FractionalPosition(2);
        level.Chords[0].ChordNotes[0].Sustain = new FractionalPosition(3);

        ChartProjectPositions.ShiftAll(project, Ten);

        Assert.Equal(new FractionalPosition(2), level.Notes[0].Sustain);
        Assert.Equal(new FractionalPosition(3), level.Chords[0].ChordNotes[0].Sustain);
        // …and the derived end therefore follows the onset by construction.
        Assert.Equal(new FractionalPosition(16), level.Notes[0].EndPosition);
    }

    /// <summary>
    ///     A fret-hand mute and a notation sidecar voice are what the two
    ///     hand-written traversals forgot. Called out separately from the
    ///     everything-moved test so a regression names itself.
    /// </summary>
    [Fact]
    public void ShiftAll_MovesFretHandMutesAndNotationVoices()
    {
        var project = BuildProjectWithOneOfEverything(atBeat: 4);
        var level = project.Arrangements[0].Levels[0];
        var voice = project.Arrangements[0].NotationVoices[0];

        ChartProjectPositions.ShiftAll(project, Ten);

        Assert.Equal(new FractionalPosition(14), level.FretHandMutes[0].Position);
        Assert.Equal(new FractionalPosition(14), voice.Notes[0].Position);
        Assert.Equal(new FractionalPosition(14), voice.Chords[0].Position);
    }

    /// <summary>
    ///     THE INVENTORY RULE, made executable: a new position-bearing collection
    ///     on any of the model roots below has to be added to
    ///     <see cref="ChartProjectPositions" /> (and to this list, and to
    ///     <c>RemoveContentAtBeat</c>). Without this the omission is invisible —
    ///     that is exactly how fret-hand mutes and notation voices shipped out of
    ///     step with every other element for three releases.
    /// </summary>
    [Fact]
    public void EveryPositionBearingCollection_IsAccountedFor()
    {
        // Walked by ChartProjectPositions.Remap.
        string[] walked =
        [
            "ChartProject.Showlights",
            "Arrangement.EventPoints", "Arrangement.ToneChanges", "Arrangement.PhraseIterations",
            "ChartLevel.Notes", "ChartLevel.Chords", "ChartLevel.Anchors",
            "ChartLevel.HandShapes", "ChartLevel.FretHandMutes",
            "NotationVoice.Notes", "NotationVoice.Chords",
            "VocalTrack.Phrases", "LyricPhrase.Lyrics",
            "GuitarNote.BendValues", "ChordNote.BendValues"
        ];

        Assert.Equal(
            new SortedSet<string>(walked.Concat(LegacyFlatStore), StringComparer.Ordinal),
            PositionBearingCollections());
    }

    /// <summary>
    ///     The SECOND consumer of the inventory, and the reason it is worth
    ///     deriving rather than listing. <c>BeatScaleService</c> cannot route
    ///     through <see cref="ChartProjectPositions.Remap" /> — that helper takes a
    ///     pure <c>Func&lt;&gt;</c> with nowhere to record the per-element undo
    ///     snapshots a scale action needs — so it hand-writes the walk, which is
    ///     exactly how the walk went wrong everywhere else.
    ///     <para>
    ///         It was the SIXTEENTH hand-written copy and nothing compared it to
    ///         anything: it covered levels, event points, tone changes, showlights
    ///         and vocals, and silently skipped <c>Arrangement.NotationVoices</c>
    ///         and <c>Arrangement.PhraseIterations</c>. Double Time INSERTS beats,
    ///         so every later beat index means a different instant — the two
    ///         skipped collections kept their old <c>BeatId</c> and slid, savably
    ///         and with no error.
    ///     </para>
    ///     <para>
    ///         Textual, and honestly so: it proves the source MENTIONS every
    ///         collection, not that it walks each one correctly (the behavioural
    ///         cases in <c>BeatScaleServiceTests</c> do that). What it makes
    ///         impossible is the failure that actually happened — a whole
    ///         collection nobody remembered.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheBeatScaleTraversal_MentionsEveryPositionBearingCollection()
    {
        var required = PositionBearingCollections()
            .Where(entry => !LegacyFlatStore.Contains(entry))
            .Select(entry => entry.Split('.')[1])
            // NotationVoice carries no Position of its own, so the reflection walk
            // never names the CONTAINER that reaches it. Without this the lint
            // would be satisfied by the level's own `Notes`/`Chords`.
            .Append("NotationVoices")
            .ToHashSet(StringComparer.Ordinal);

        var source = ReadCodeLines(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Services", "Editing", "Tempo", "BeatScaleService.cs"));

        var missing = required
            .Where(name => !source.Contains($".{name}", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "BeatScaleService's hand-written traversal never touches these position-bearing " +
            "collections, so a Double Time / Half Time slides them off the grid: " +
            string.Join(", ", missing));
    }

    /// <summary>
    ///     Deliberately NOT walked: the legacy flat store nothing writes any more
    ///     (<c>ChartProjectRestorer</c> is its only remaining consumer). Documented
    ///     in <see cref="ChartProjectPositions" />' summary.
    /// </summary>
    private static readonly string[] LegacyFlatStore = ["ChartData.SingleNotes", "ChartData.Chords"];

    /// <summary>
    ///     Every <c>Root.Collection</c> on the model whose element carries a
    ///     settable absolute <see cref="FractionalPosition" />. Derived, so a new
    ///     collection joins every rule built on it at once.
    /// </summary>
    private static SortedSet<string> PositionBearingCollections()
    {
        Type[] roots =
        [
            typeof(ChartProject), typeof(ChartData), typeof(Arrangement), typeof(ChartLevel),
            typeof(NotationVoice), typeof(VocalTrack), typeof(LyricPhrase),
            typeof(GuitarNote), typeof(Chord), typeof(ChordNote)
        ];

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            foreach (var property in root.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!typeof(IEnumerable).IsAssignableFrom(property.PropertyType)) continue;
                if (!property.PropertyType.IsGenericType) continue;

                var element = property.PropertyType.GetGenericArguments()[0];
                var position = element.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                if (position?.PropertyType != typeof(FractionalPosition)) continue;
                if (!position.CanWrite) continue;

                found.Add($"{root.Name}.{property.Name}");
            }
        }

        return found;
    }

    /// <summary>The file with its comment lines blanked — a comment naming a
    /// collection must not satisfy the lint.</summary>
    private static string ReadCodeLines(string path)
    {
        var lines = File.ReadAllLines(path).Select(line =>
        {
            var trimmed = line.TrimStart();
            return trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("*", StringComparison.Ordinal)
                ? string.Empty
                : line;
        });
        return string.Join('\n', lines);
    }

    private static ChartProject BuildProjectWithOneOfEverything(int atBeat)
    {
        var at = new FractionalPosition(atBeat);
        var end = new FractionalPosition(atBeat + 1);

        var project = new ChartProject();
        project.Showlights.Add(new Showlight { Position = at });

        var level = new ChartLevel();
        level.Notes.Add(NewNote(at));
        level.Chords.Add(NewChord(at));
        level.Anchors.Add(new FretAnchor { Position = at, Fret = 5, Width = 4 });
        level.HandShapes.Add(new HandShape { Position = at, EndPosition = end });
        level.FretHandMutes.Add(new FretHandMute(at));

        var voice = new NotationVoice { VoiceIndex = 1 };
        voice.Notes.Add(NewNote(at));
        voice.Chords.Add(NewChord(at));

        var arrangement = new Arrangement();
        arrangement.Levels.Add(level);
        arrangement.NotationVoices.Add(voice);
        arrangement.EventPoints.Add(new EventPoint { Position = at, Section = "verse" });
        arrangement.ToneChanges.Add(new ToneChange { Position = at, ToneName = "lead" });
        arrangement.PhraseIterations.Add(new PhraseIteration { Position = at, PhraseId = 0 });
        project.Arrangements.Add(arrangement);

        var track = new VocalTrack();
        var phrase = new LyricPhrase { Position = at, EndPosition = end };
        phrase.Lyrics.Add(new LyricEvent { Position = at, EndPosition = end, Text = "la" });
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        return project;
    }

    private static void AssertEverythingSitsAt(ChartProject project, int beat)
    {
        var at = new FractionalPosition(beat);
        var end = new FractionalPosition(beat + 1);
        var arrangement = project.Arrangements[0];
        var level = arrangement.Levels[0];
        var voice = arrangement.NotationVoices[0];
        var phrase = project.VocalTracks[0].Phrases[0];

        Assert.Equal(at, project.Showlights[0].Position);

        Assert.Equal(at, level.Notes[0].Position);
        Assert.Equal(at, level.Notes[0].BendValues[0].Position);
        Assert.Equal(at, level.Chords[0].Position);
        Assert.Equal(at, level.Chords[0].ChordNotes[0].BendValues[0].Position);
        Assert.Equal(at, level.Anchors[0].Position);
        Assert.Equal(at, level.HandShapes[0].Position);
        Assert.Equal(end, level.HandShapes[0].EndPosition);
        Assert.Equal(at, level.FretHandMutes[0].Position);

        Assert.Equal(at, voice.Notes[0].Position);
        Assert.Equal(at, voice.Notes[0].BendValues[0].Position);
        Assert.Equal(at, voice.Chords[0].Position);
        Assert.Equal(at, voice.Chords[0].ChordNotes[0].BendValues[0].Position);

        Assert.Equal(at, arrangement.EventPoints[0].Position);
        Assert.Equal(at, arrangement.ToneChanges[0].Position);
        Assert.Equal(at, arrangement.PhraseIterations[0].Position);

        Assert.Equal(at, phrase.Position);
        Assert.Equal(end, phrase.EndPosition);
        Assert.Equal(at, phrase.Lyrics[0].Position);
        Assert.Equal(end, phrase.Lyrics[0].EndPosition);
    }

    private static GuitarNote NewNote(FractionalPosition at)
    {
        var note = new GuitarNote { Position = at, StringIndex = 0, Fret = 3 };
        note.BendValues.Add(new BendValue { Position = at, Step = 1.0 });
        return note;
    }

    private static Chord NewChord(FractionalPosition at)
    {
        var chord = new Chord { Position = at };
        var chordNote = new ChordNote { StringIndex = 1, Fret = 5 };
        chordNote.BendValues.Add(new BendValue { Position = at, Step = 1.0 });
        chord.ChordNotes.Add(chordNote);
        return chord;
    }
}
