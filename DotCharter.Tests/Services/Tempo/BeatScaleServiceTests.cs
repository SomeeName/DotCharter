namespace DotCharter.Tests.Services.Tempo;

public class BeatScaleServiceTests
{
    private static ChartProject BuildProject(int beatCount, double bpm, double startMs = 0.0)
    {
        var project = new ChartProject { Bpm = bpm };
        var msPerBeat = 60000.0 / bpm;
        for (var i = 0; i < beatCount; i++)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = startMs + i * msPerBeat,
                Bpm = bpm,
                BeatsInMeasure = 4,
                NoteDenominator = 4,
                IsMeasure = i % 4 == 0
            });
        }

        return project;
    }

    private static Arrangement AddArrangement(ChartProject project)
    {
        var arr = new Arrangement { Name = "Lead" };
        arr.Levels.Add(new ChartLevel());
        project.Arrangements.Add(arr);
        return arr;
    }

    [Fact]
    public void BuildScaleAction_DoubleTime_DoublesBeatCountInRange()
    {
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0); // beats 0..8 → 0,500,...,4000

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 2.0);
        Assert.NotNull(action);
        action.Execute();

        // beats inside [1000,3000) were 2,3,4,5 (4 beats) → become 8 beats with mids inserted.
        Assert.Equal(9 + 4, project.Beats.Count);
        // Last beat absolute time unchanged
        Assert.Equal(4000.0, project.Beats[^1].Time, 3);
        // BPM of beats in range doubled to 240
        var inRange = project.Beats.Where(b => b.Time is >= 1000.0 and < 3000.0).ToList();
        Assert.All(inRange, b => Assert.Equal(240.0, b.Bpm, 3));
    }

    [Fact]
    public void BuildScaleAction_HalfTime_HalvesBeatCountInRange()
    {
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0);

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 0.5);
        Assert.NotNull(action);
        action.Execute();

        // beats inside [1000,3000): 4 beats → become 2 (every other kept).
        Assert.Equal(9 - 2, project.Beats.Count);
        // BPM of remaining range beats halved to 60
        var inRange = project.Beats.Where(b => b.Time is >= 1000.0 and < 3000.0).ToList();
        Assert.All(inRange, b => Assert.Equal(60.0, b.Bpm, 3));
    }

    /// <summary>
    ///     Half-time keeps every OTHER beat, and which beats those are depends
    ///     on where the selected range starts — not on where the bar lines are.
    ///     A range starting on an off-beat therefore lands on the odd beats and
    ///     deletes every downbeat inside it.
    ///     <para>
    ///         A bar line is not decoration: <see cref="ChartPositionLabel" />
    ///         and the status bar count bars by walking
    ///         <see cref="BeatMarker.IsMeasure" />, so each one silently dropped
    ///         renumbers every bar for the REST OF THE SONG, past the scaled
    ///         range. The existing coverage started its range on a downbeat,
    ///         where the alternation happens to keep them.
    ///     </para>
    ///     <para>
    ///         The same drop takes <see cref="BeatMarker.IsAnchor" /> with it —
    ///         a beat the charter explicitly pinned.
    ///     </para>
    /// </summary>
    [Fact]
    public void BuildScaleAction_HalfTime_FromAnOffBeat_KeepsTheBarLinesAndAnchors()
    {
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0); // 0,500,…,4000; bar lines at beats 0, 4, 8
        project.Beats[2].IsAnchor = true;     // 1000 ms — dropped by the alternation below
        var barLinesBefore = project.Beats.Count(b => b.IsMeasure);

        // From beat 1 (500 ms) to the end: in-range beats 1..7, so the run keeps
        // 1, 3, 5, 7 and drops 2, 4, 6 — beat 4 is a bar line, beat 2 an anchor.
        var action = svc.BuildScaleAction(project, 500.0, 4000.0, 0.5);
        Assert.NotNull(action);
        action.Execute();

        Assert.Equal(barLinesBefore, project.Beats.Count(b => b.IsMeasure));
        Assert.Contains(project.Beats, b => b.IsAnchor);
    }

    [Fact]
    public void BuildScaleAction_PreservesNoteAbsoluteTimes()
    {
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0);
        var arr = AddArrangement(project);
        var lvl = arr.Levels[0];

        // Note at 1500ms (beat 3 in 120bpm 500ms grid).
        var note = new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(1500.0, project.Beats),
            Sustain = TimelineMath.PositionFromMs(2500.0, project.Beats) - TimelineMath.PositionFromMs(1500.0, project.Beats)
        };
        lvl.Notes.Add(note);

        var beatsBefore = project.Beats.ToList();
        var noteStartMsBefore = note.Position.GetPositionMs(beatsBefore);
        var noteEndMsBefore = note.EndPosition.GetPositionMs(beatsBefore);

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 2.0);
        Assert.NotNull(action);
        action.Execute();

        var beatsAfter = project.Beats.ToList();
        var noteStartMsAfter = note.Position.GetPositionMs(beatsAfter);
        var noteEndMsAfter = note.EndPosition.GetPositionMs(beatsAfter);

        Assert.Equal(noteStartMsBefore, noteStartMsAfter, 1);
        Assert.Equal(noteEndMsBefore, noteEndMsAfter, 1);
    }

    [Fact]
    public void BuildScaleAction_Undo_RestoresOriginalBeatsAndPositions()
    {
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0);
        var arr = AddArrangement(project);
        var lvl = arr.Levels[0];
        var note = new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(1500.0, project.Beats),
            Sustain = TimelineMath.PositionFromMs(2000.0, project.Beats) - TimelineMath.PositionFromMs(1500.0, project.Beats)
        };
        lvl.Notes.Add(note);

        var origBeatCount = project.Beats.Count;
        var origPos = note.Position;
        var origSus = note.Sustain;

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 2.0);
        Assert.NotNull(action);
        action.Execute();
        action.Undo();

        Assert.Equal(origBeatCount, project.Beats.Count);
        Assert.Equal(origPos, note.Position);
        Assert.Equal(origSus, note.Sustain);
    }

    [Fact]
    public void BuildScaleAction_MovesBendPointsWithTheirNote_AndUndoRestoresTheCurve()
    {
        // BendValue.Position is ABSOLUTE, so a scale that did not remap it left
        // every bend point at its pre-scale position — i.e. OUTSIDE its own note.
        // The EnforceProject the calling dialogs run then collapsed them onto the
        // note start/end, and Undo could not bring the curve back.
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0);
        var arr = AddArrangement(project);
        var lvl = arr.Levels[0];

        var note = new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(1500.0, project.Beats),
            Sustain = TimelineMath.PositionFromMs(2500.0, project.Beats)
                      - TimelineMath.PositionFromMs(1500.0, project.Beats)
        };
        note.BendValues.Add(new BendValue(TimelineMath.PositionFromMs(1750.0, project.Beats), 1.0));
        note.BendValues.Add(new BendValue(TimelineMath.PositionFromMs(2250.0, project.Beats), 2.0));
        lvl.Notes.Add(note);

        var beatsBefore = project.Beats.ToList();
        var bendMsBefore = note.BendValues.Select(b => b.Position.GetPositionMs(beatsBefore)).ToList();
        var bendPositionsBefore = note.BendValues.Select(b => b.Position).ToList();

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 2.0);
        Assert.NotNull(action);
        action.Execute();

        // The bend points must still sound at the same moment of the audio…
        var beatsAfter = project.Beats.ToList();
        var bendMsAfter = note.BendValues.Select(b => b.Position.GetPositionMs(beatsAfter)).ToList();
        for (var i = 0; i < bendMsBefore.Count; i++)
            Assert.Equal(bendMsBefore[i], bendMsAfter[i], 1);

        // …and therefore still inside the note.
        foreach (var bend in note.BendValues)
        {
            Assert.True(bend.Position >= note.Position);
            Assert.True(bend.Position <= note.EndPosition);
        }

        action.Undo();
        Assert.Equal(bendPositionsBefore, note.BendValues.Select(b => b.Position).ToList());
    }

    [Fact]
    public void BuildScaleAction_MovesFretHandMutesShowlightsAndVocals()
    {
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0);
        var arr = AddArrangement(project);

        var mute = new FretHandMute(TimelineMath.PositionFromMs(1500.0, project.Beats));
        arr.Levels[0].FretHandMutes.Add(mute);

        var showlight = new Showlight { Position = TimelineMath.PositionFromMs(2000.0, project.Beats) };
        project.Showlights.Add(showlight);

        var lyric = new DotCharter.Core.Domain.Vocals.LyricEvent
        {
            Text = "la",
            Position = TimelineMath.PositionFromMs(1500.0, project.Beats),
            EndPosition = TimelineMath.PositionFromMs(1750.0, project.Beats)
        };
        var phrase = new DotCharter.Core.Domain.Vocals.LyricPhrase
        {
            Position = lyric.Position,
            EndPosition = lyric.EndPosition
        };
        phrase.Lyrics.Add(lyric);
        var track = new DotCharter.Core.Domain.Vocals.VocalTrack();
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        var beatsBefore = project.Beats.ToList();
        var muteMs = mute.Position.GetPositionMs(beatsBefore);
        var showlightMs = showlight.Position.GetPositionMs(beatsBefore);
        var lyricMs = lyric.Position.GetPositionMs(beatsBefore);
        var originalMutePosition = mute.Position;

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 2.0);
        Assert.NotNull(action);
        action.Execute();

        var beatsAfter = project.Beats.ToList();
        Assert.Equal(muteMs, mute.Position.GetPositionMs(beatsAfter), 1);
        Assert.Equal(showlightMs, showlight.Position.GetPositionMs(beatsAfter), 1);
        Assert.Equal(lyricMs, lyric.Position.GetPositionMs(beatsAfter), 1);

        action.Undo();
        Assert.Equal(originalMutePosition, mute.Position);
    }

    /// <summary>
    ///     The two collections the traversal never had. Double-time INSERTS beats,
    ///     so every later beat index means a different instant — an element the
    ///     walk skips keeps its old <c>BeatId</c> and silently slides.
    ///     <para>
    ///         Notation voices are GP voices 2-4, drawn by the sheet strip on the
    ///         same grid as the level beside them. Phrase iterations are worse:
    ///         measured over the 89 RS2 exports in <c>TestData/EOF</c>,
    ///         <c>EventPoint.Phrase</c> yields 0 markers and
    ///         <c>PhraseIterations</c> yields 2,596 — so on every psarc/RS-XML
    ///         chart, ALL the phrase markers were in the collection being skipped.
    ///     </para>
    /// </summary>
    [Fact]
    public void BuildScaleAction_MovesNotationVoicesAndPhraseIterations()
    {
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0);
        var arr = AddArrangement(project);

        var voice = new NotationVoice { VoiceIndex = 1 };
        var voiceNote = new GuitarNote
        {
            Position = TimelineMath.PositionFromMs(1500.0, project.Beats),
            Sustain = TimelineMath.PositionFromMs(2500.0, project.Beats)
                      - TimelineMath.PositionFromMs(1500.0, project.Beats)
        };
        voice.Notes.Add(voiceNote);
        var voiceChord = new Chord { Position = TimelineMath.PositionFromMs(2000.0, project.Beats) };
        voiceChord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 1,
            Fret = 5,
            Sustain = TimelineMath.PositionFromMs(2500.0, project.Beats)
                      - TimelineMath.PositionFromMs(2000.0, project.Beats)
        });
        voice.Chords.Add(voiceChord);
        arr.NotationVoices.Add(voice);

        var iteration = new PhraseIteration(TimelineMath.PositionFromMs(1500.0, project.Beats), 0);
        arr.PhraseIterations.Add(iteration);

        var before = project.Beats.ToList();
        var noteMs = voiceNote.Position.GetPositionMs(before);
        var noteEndMs = voiceNote.EndPosition.GetPositionMs(before);
        var chordMs = voiceChord.Position.GetPositionMs(before);
        var chordEndMs = (voiceChord.Position + voiceChord.ChordNotes[0].Sustain).GetPositionMs(before);
        var iterationMs = iteration.Position.GetPositionMs(before);
        var originalIterationPosition = iteration.Position;
        var originalNotePosition = voiceNote.Position;

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 2.0);
        Assert.NotNull(action);
        action.Execute();

        var after = project.Beats.ToList();
        Assert.Equal(noteMs, voiceNote.Position.GetPositionMs(after), 1);
        // The sustain is a span in BEATS and a beat is now worth half as long,
        // so an unscaled sustain would end 500 ms early even with a correct onset.
        Assert.Equal(noteEndMs, voiceNote.EndPosition.GetPositionMs(after), 1);
        Assert.Equal(chordMs, voiceChord.Position.GetPositionMs(after), 1);
        Assert.Equal(chordEndMs,
            (voiceChord.Position + voiceChord.ChordNotes[0].Sustain).GetPositionMs(after), 1);
        Assert.Equal(iterationMs, iteration.Position.GetPositionMs(after), 1);

        action.Undo();
        Assert.Equal(originalIterationPosition, iteration.Position);
        Assert.Equal(originalNotePosition, voiceNote.Position);
    }

    [Fact]
    public void BuildScaleAction_LeavesMillisecondKeyedMarkersAlone()
    {
        // Bookmarks and annotations store absolute MILLISECONDS, and a scale
        // never moves a surviving beat's Time — so they are already correct and
        // must not be "fixed".
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0);
        AddArrangement(project);
        project.Bookmarks.Add(new Bookmark { Index = 0, Time = 1500.0 });
        project.Annotations.Add(new ChartAnnotation { TimeMs = 1500.0, Text = "here" });

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 2.0);
        Assert.NotNull(action);
        action.Execute();

        Assert.Equal(1500.0, project.Bookmarks[0].Time, 3);
        Assert.Equal(1500.0, project.Annotations[0].TimeMs, 3);
    }

    [Fact]
    public void BuildScaleAction_RejectsLockedBeatMap()
    {
        var svc = new BeatScaleService(new FakeAppLogger());
        var project = BuildProject(9, 120.0);
        project.IsBeatMapLocked = true;

        var action = svc.BuildScaleAction(project, 1000.0, 3000.0, 2.0);
        Assert.Null(action);
    }

    /// <summary>
    ///     <see cref="ChartProjectPositions" /> is the ONE exhaustive walk over
    ///     every position-bearing collection a project stores. A tempo scale
    ///     cannot route through it (a scale re-derives every sustain by
    ///     milliseconds and snapshots each slot for an exact undo, which a pure
    ///     position map cannot express), so the scale carries its own walk — and
    ///     that walk forgot <c>NotationVoices</c> for several releases while the
    ///     shared one already had them. This pins the two walks to the SAME set
    ///     of collections: add a collection to <c>ChartProjectPositions</c> and the
    ///     scale must name it too, or this fails.
    /// </summary>
    [Fact]
    public void BuildScaleAction_WalksEveryCollectionChartProjectPositionsWalks()
    {
        var root = TestPaths.RepoRoot();
        var shared = File.ReadAllText(Path.Combine(root, "DotCharter.Core", "Utilities", "ChartProjectPositions.cs"));
        var scale = File.ReadAllText(Path.Combine(root, "DotCharter.Services", "Editing", "Tempo", "BeatScaleService.cs"));

        // Every `foreach (var x in <expr>.Member)` in the shared walk — the
        // trailing PascalCase member is the collection. Lower-case tails are
        // parameters of the private helper (notes/chords), which the scale
        // reaches through the same PascalCase members one level up.
        var collections = System.Text.RegularExpressions.Regex
            .Matches(shared, @"foreach\s*\(\s*var\s+\w+\s+in\s+[\w\.]*?\.?(\w+)\s*\)")
            .Select(m => m.Groups[1].Value)
            .Where(name => char.IsUpper(name[0]))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // The lint must actually be reading the walk it claims to read.
        Assert.Contains("NotationVoices", collections);
        Assert.Contains("FretHandMutes", collections);
        Assert.True(collections.Count >= 10, $"only {collections.Count} collections found: {string.Join(", ", collections)}");

        var missing = collections.Where(name => !scale.Contains("." + name, StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "BeatScaleService does not walk these collections that ChartProjectPositions walks: " +
            string.Join(", ", missing));
    }
}
