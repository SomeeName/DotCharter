using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Phrases;

namespace DotCharter.Tests.Services.Editing.Phrases;

public sealed class PhraseIterationPropagationServiceTests
{
    private const int RiffPhraseId = 1;
    private const int IntroPhraseId = 0;

    [Fact]
    public void Propagate_ReplicatesSourceContent_IntoEveryOtherIterationOfThePhrase()
    {
        var h = CreateHarness();

        var count = h.Service.PropagateToAllIterations(h.SourceIteration);

        Assert.Equal(2, count);

        // Target riff@12 (span [12,16)): shifted copies of the source content.
        Assert.Contains(h.Level.Notes, n => n.Position == Pos(12) && n.Fret == 3);
        Assert.Contains(h.Level.Notes, n => n.Position == Pos(14) && n.Fret == 5);
        Assert.Contains(h.Level.Chords, c => c.Position == Pos(13) && c.TemplateId == 2);
        Assert.Contains(h.Level.Anchors, a => a.Position == Pos(12) && a.Fret == 3);

        // Target riff@20 (open-ended): full copy, nothing clipped.
        Assert.Contains(h.Level.Notes, n => n.Position == Pos(20) && n.Fret == 3);
        Assert.Contains(h.Level.Notes, n => n.Position == Pos(22) && n.Fret == 5);
        Assert.Contains(h.Level.Notes, n => n.Position == Pos(25) && n.Fret == 7);
        Assert.Contains(h.Level.Chords, c => c.Position == Pos(21));

        // Pre-existing content inside the target spans was replaced.
        Assert.DoesNotContain(h.Level.Notes, n => n.Fret == 99);

        // Content in OTHER phrases' spans is untouched (intro spans).
        Assert.Contains(h.Level.Notes, n => n.Position == Pos(9) && n.Fret == 42);
        Assert.Contains(h.Level.Notes, n => n.Position == Pos(16) && n.Fret == 42);
    }

    [Fact]
    public void Propagate_ClipsToShorterTargetSpan()
    {
        var h = CreateHarness();

        h.Service.PropagateToAllIterations(h.SourceIteration);

        // Source note @5 would land at 17, past the riff@12 span end (16) — dropped.
        Assert.DoesNotContain(h.Level.Notes, n => n.Position == Pos(17));
        // …but the open-ended riff@20 target still gets it (at 25).
        Assert.Contains(h.Level.Notes, n => n.Position == Pos(25));

        // Source note @2 (sustain 3, would end at 17 in the short span) is clamped to end at 16.
        var clipped = Assert.Single(h.Level.Notes, n => n.Position == Pos(14));
        Assert.Equal(Pos(2), clipped.Sustain);
        // The open-ended copy keeps the full sustain.
        var unclipped = Assert.Single(h.Level.Notes, n => n.Position == Pos(22));
        Assert.Equal(Pos(3), unclipped.Sustain);

        // Hand shape [1,6) lands at [13,18) in the short span — end clamped to 16.
        var shortShape = Assert.Single(h.Level.HandShapes, s => s.Position == Pos(13));
        Assert.Equal(Pos(16), shortShape.EndPosition);
        var openShape = Assert.Single(h.Level.HandShapes, s => s.Position == Pos(21));
        Assert.Equal(Pos(26), openShape.EndPosition);
    }

    [Fact]
    public void Propagate_ShiftsBendValuePositions_WithTheNote()
    {
        var h = CreateHarness();

        h.Service.PropagateToAllIterations(h.SourceIteration);

        var copy = Assert.Single(h.Level.Notes, n => n.Position == Pos(22));
        var bend = Assert.Single(copy.BendValues);
        Assert.Equal(Pos(22), bend.Position);
        Assert.Equal(1.0, bend.Step);
    }

    [Fact]
    public void Propagate_KeepsCollectionsPositionSorted()
    {
        var h = CreateHarness();

        h.Service.PropagateToAllIterations(h.SourceIteration);

        AssertSorted(h.Level.Notes.Select(n => n.Position));
        AssertSorted(h.Level.Chords.Select(c => c.Position));
        AssertSorted(h.Level.Anchors.Select(a => a.Position));
        AssertSorted(h.Level.HandShapes.Select(s => s.Position));
    }

    [Fact]
    public void Propagate_IsOneUndoStep_ThatRestoresEveryTarget()
    {
        var h = CreateHarness();
        var before = Fingerprint(h.Level);

        var count = h.Service.PropagateToAllIterations(h.SourceIteration);
        Assert.Equal(2, count);
        Assert.NotEqual(before, Fingerprint(h.Level));
        Assert.True(h.History.CanUndo);

        h.History.Undo();

        Assert.Equal(before, Fingerprint(h.Level));
        Assert.False(h.History.CanUndo);

        // Redo replays the whole propagation.
        h.History.Redo();
        Assert.NotEqual(before, Fingerprint(h.Level));
    }

    [Fact]
    public void Propagate_ResolvesSourceByValue_WhenNotTheSameInstance()
    {
        var h = CreateHarness();

        var detachedSource = new PhraseIteration(Pos(0), RiffPhraseId);
        var count = h.Service.PropagateToAllIterations(detachedSource);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Propagate_ReturnsZero_WhenPhraseHasNoOtherIterations()
    {
        var h = CreateHarness();
        var introIteration = h.Arrangement.PhraseIterations.First(pi => pi.PhraseId == IntroPhraseId &&
                                                                        pi.Position == Pos(8));
        // Give intro a single iteration only by pointing at the other intro's phrase id.
        h.Arrangement.PhraseIterations.Remove(
            h.Arrangement.PhraseIterations.First(pi => pi.PhraseId == IntroPhraseId && pi.Position == Pos(16)));

        var count = h.Service.PropagateToAllIterations(introIteration);

        Assert.Equal(0, count);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Propagate_ReturnsZero_WhenSourceIterationIsUnknown()
    {
        var h = CreateHarness();

        var count = h.Service.PropagateToAllIterations(new PhraseIteration(Pos(99), RiffPhraseId));

        Assert.Equal(0, count);
        Assert.False(h.History.CanUndo);
    }

    // ---------------------------------------------------------------- harness

    private sealed record Harness(
        PhraseIterationPropagationService Service,
        HistoryService History,
        Arrangement Arrangement,
        ChartLevel Level,
        PhraseIteration SourceIteration);

    /// <summary>
    /// Phrases: intro (id 0), riff (id 1). Iterations: riff@0 (span [0,8), the
    /// source), intro@8 ([8,12)), riff@12 ([12,16) — SHORTER than the source),
    /// intro@16 ([16,20)), riff@20 (open-ended).
    /// Source span content: note@0 f3, chord@1 (template 2), note@2 f5
    /// (sustain 3, bend @2), hand shape [1,6), note@5 f7, anchor@0.
    /// Targets contain sacrificial fret-99 notes; intro spans contain fret-42
    /// notes that must survive untouched.
    /// </summary>
    private static Harness CreateHarness()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Phrases.Add("intro");
        arrangement.Phrases.Add("riff");

        var source = new PhraseIteration(Pos(0), RiffPhraseId);
        arrangement.PhraseIterations.Add(source);
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(8), IntroPhraseId));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(12), RiffPhraseId));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(16), IntroPhraseId));
        arrangement.PhraseIterations.Add(new PhraseIteration(Pos(20), RiffPhraseId));

        var level = new ChartLevel { DifficultyIndex = 0 };

        // Source span [0,8), then intro content (must never be touched), then
        // sacrificial content inside the riff target spans — all collections
        // position-sorted, as the domain requires.
        var bendNote = new GuitarNote { Position = Pos(2), StringIndex = 1, Fret = 5, Sustain = Pos(3) };
        bendNote.BendValues.Add(new BendValue(Pos(2), 1.0));
        level.Notes.Add(new GuitarNote { Position = Pos(0), StringIndex = 0, Fret = 3 });
        level.Notes.Add(bendNote);
        level.Notes.Add(new GuitarNote { Position = Pos(5), StringIndex = 2, Fret = 7 });
        level.Notes.Add(new GuitarNote { Position = Pos(9), StringIndex = 0, Fret = 42 });
        level.Notes.Add(new GuitarNote { Position = Pos(13), StringIndex = 0, Fret = 99 });
        level.Notes.Add(new GuitarNote { Position = Pos(16), StringIndex = 0, Fret = 42 });
        level.Notes.Add(new GuitarNote { Position = Pos(21), StringIndex = 0, Fret = 99 });

        level.Chords.Add(new Chord
        {
            Position = Pos(1), TemplateId = 2,
            ChordNotes = { new ChordNote { StringIndex = 0, Fret = 3 }, new ChordNote { StringIndex = 1, Fret = 5 } }
        });
        level.Chords.Add(new Chord { Position = Pos(14), TemplateId = 7 });

        level.Anchors.Add(new FretAnchor(Pos(0), 3));
        level.Anchors.Add(new FretAnchor(Pos(22), 9));

        level.HandShapes.Add(new HandShape { Position = Pos(1), EndPosition = Pos(6), TemplateId = 2 });
        level.HandShapes.Add(new HandShape { Position = Pos(13), EndPosition = Pos(14), TemplateId = 7 });

        arrangement.Levels.Add(level);

        var state = new ProjectState { ActiveArrangement = arrangement };
        var scope = new EditingScope(state);
        var history = new HistoryService(new FakeAppLogger(), scope);
        var service = new PhraseIterationPropagationService(history, scope, new FakeAppLogger());
        return new Harness(service, history, arrangement, level, source);
    }

    /// <summary>A chart placed in the editor has phrase markers and no explicit iterations; the
    ///     inspector hands over a resolved iteration, matched by position and phrase.</summary>
    [Fact]
    public void Propagate_WorksOnAMarkersOnlyArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        foreach (var (beat, name) in new[] { (0, "riff"), (8, "intro"), (12, "riff") })
            arrangement.EventPoints.Add(new EventPoint { Position = Pos(beat), Phrase = name });
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = Pos(1), StringIndex = 0, Fret = 3 });
        level.Notes.Add(new GuitarNote { Position = Pos(13), StringIndex = 0, Fret = 99 });
        arrangement.Levels.Add(level);
        var scope = new EditingScope(new ProjectState { ActiveArrangement = arrangement });
        var service = new PhraseIterationPropagationService(new HistoryService(new FakeAppLogger(), scope), scope,
            new FakeAppLogger());

        var count = service.PropagateToAllIterations(PhraseMarkers.Iterations(arrangement).Iterations[0]);

        Assert.Equal(1, count);
        Assert.Contains(level.Notes, n => n.Position == Pos(13) && n.Fret == 3);
        Assert.DoesNotContain(level.Notes, n => n.Fret == 99);
    }

    private static FractionalPosition Pos(int beat) => new(beat);

    private static string Fingerprint(ChartLevel level)
    {
        var parts = new List<string>();
        foreach (var n in level.Notes.OrderBy(n => n.Position).ThenBy(n => n.StringIndex))
            parts.Add($"N {n.Position} s{n.StringIndex} f{n.Fret} su{n.Sustain} b{n.BendValues.Count}");
        foreach (var c in level.Chords.OrderBy(c => c.Position))
            parts.Add($"C {c.Position} t{c.TemplateId} n{c.ChordNotes.Count}");
        foreach (var a in level.Anchors.OrderBy(a => a.Position))
            parts.Add($"A {a.Position} f{a.Fret}");
        foreach (var s in level.HandShapes.OrderBy(s => s.Position))
            parts.Add($"H {s.Position}..{s.EndPosition} t{s.TemplateId}");
        return string.Join("|", parts);
    }

    private static void AssertSorted(IEnumerable<FractionalPosition> positions)
    {
        var list = positions.ToList();
        for (var i = 1; i < list.Count; i++)
            Assert.True(list[i - 1] <= list[i], $"Out of order at {i}: {list[i - 1]} > {list[i]}.");
    }
}
