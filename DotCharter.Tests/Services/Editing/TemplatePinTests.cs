using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.FretAnchors;
using DotCharter.Services.Editing.Refret;

namespace DotCharter.Tests.Services.Editing;

/// <summary>
///     #785, PINNED templates: a grip the charter authored (<see cref="ChordTemplate.Pinned" />, or
///     one holding ghost gems) is never replaced or re-fingered by an automatic flow. One test per
///     flow, and each states its CONTROL in the same test — the identical setup unpinned, where the
///     flow does change the grip — so none passes because the flow had nothing to do.
///     <see cref="TemplatePinReachabilityTests" /> holds the registry of flows and the source lint.
/// </summary>
public sealed class TemplatePinTests
{
    private static readonly FingeringGenerationService Fingering = new();

    private static Arrangement Guitar()
    {
        var arrangement = new Arrangement { StringCount = 6, Tuning = ["E", "A", "D", "G", "B", "E"] };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private static Chord ChordOn(ChordTemplate template, int beat)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = template.Id };
        foreach (var (s, f) in template.Frets.OrderBy(kv => kv.Key)) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return chord;
    }

    // ── ChordTemplateResolver ───────────────────────────────────────────

    [Fact]
    public void Resolver_KeepsAPinnedTemplateItsChordStillPlays()
    {
        var arrangement = Guitar();
        var earlier = new ChordTemplate { Id = 0, Name = "G5", Frets = { [0] = 3, [1] = 5 }, Fingers = { [0] = 1, [1] = 3 } };
        var authored = new ChordTemplate { Id = 1, Name = "G5", Frets = { [0] = 3, [1] = 5 }, Fingers = { [0] = 1, [1] = 4 }, Pinned = true };
        arrangement.ChordTemplates.Add(earlier);
        arrangement.ChordTemplates.Add(authored);
        var chord = ChordOn(authored, 2);

        Assert.Same(authored, ChordTemplateResolver.ResolveForChord(arrangement, chord, []));

        // Control: unpinned, the frets-only key hands the chord the FIRST equal grip.
        authored.Pinned = false;
        Assert.Same(earlier, ChordTemplateResolver.ResolveForChord(arrangement, chord, []));
    }

    // ── ArrangementFingeringStyle ───────────────────────────────────────

    [Fact]
    public void FingeringStyle_FollowAuthored_LeavesAPinnedPairAlone()
    {
        // Two authored 1·4 power chords set the style; the third carries the suggester's 1·3.
        var arrangement = Guitar();
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Frets = { [0] = 3, [1] = 5 }, Fingers = { [0] = 1, [1] = 4 } });
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 1, Frets = { [0] = 5, [1] = 7 }, Fingers = { [0] = 1, [1] = 4 } });
        var third = new ChordTemplate { Id = 2, Frets = { [0] = 7, [1] = 9 }, Fingers = { [0] = 1, [1] = 3 }, Pinned = true };
        arrangement.ChordTemplates.Add(third);

        Assert.Equal(0, ArrangementFingeringStyle.FollowAuthored(arrangement, t => t.Id < 2));
        Assert.Equal(3, third.Fingers[1]);

        third.Pinned = false;
        Assert.Equal(1, ArrangementFingeringStyle.FollowAuthored(arrangement, t => t.Id < 2));
        Assert.Equal(4, third.Fingers[1]);
    }

    [Fact]
    public void FingeringStyle_SetSuggestedFingers_LeavesAPinnedGripAlone()
    {
        var arrangement = Guitar();
        var grip = new ChordTemplate { Frets = { [0] = 3, [1] = 5, [2] = 5 }, Pinned = true };

        ArrangementFingeringStyle.SetSuggestedFingers(arrangement, grip, 6);
        Assert.Empty(grip.Fingers);

        grip.Pinned = false;
        ArrangementFingeringStyle.SetSuggestedFingers(arrangement, grip, 6);
        Assert.Equal(3, grip.Fingers.Count);
    }

    // ── Refret ──────────────────────────────────────────────────────────

    private static (Arrangement Arrangement, List<BeatMarker> Beats, ChordTemplate A5) AwkwardA5Run()
    {
        // A5 as 5-7-7 on the low E: the open A5 is strictly cheaper, so Refret moves the run.
        var arrangement = Guitar();
        arrangement.TuningType = "E_STANDARD";
        var beats = Enumerable.Range(0, 40).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 }).ToList();
        var template = new ChordTemplate { Id = 0, Name = "A5", Frets = { [0] = 5, [1] = 7, [2] = 7 } };
        arrangement.ChordTemplates.Add(template);
        for (var b = 1; b <= 3; b++) arrangement.Levels[0].Chords.Add(ChordOn(template, b));
        return (arrangement, beats, template);
    }

    private static RefretService Refret() => new(new FretAnchorGenerationService(), Fingering);

    [Fact]
    public void Refret_NeverMovesAChordOnAPinnedTemplate()
    {
        var (pinned, beats, template) = AwkwardA5Run();
        template.Pinned = true;
        var result = Refret().RefretArrangement(pinned, beats);
        Assert.Equal(0, result.ChordsChanged);
        Assert.All(pinned.Levels[0].Chords, c => Assert.Equal(0, c.TemplateId));

        var (control, controlBeats, _) = AwkwardA5Run();
        Assert.True(Refret().RefretArrangement(control, controlBeats).ChordsChanged > 0);
    }

    [Fact]
    public void Refret_NeverMovesASoundUnderAPinnedHandShape()
    {
        // The chords' own template is free; the BAR over them holds a pinned grip. Its sounds stay
        // because a bar on another template than its chords is "mixed" (RefretPlanner's own rule) —
        // this pins the behaviour, the pinned-chord test above pins the IsPinned check.
        var (pinned, beats, _) = AwkwardA5Run();
        var grip = new ChordTemplate { Id = 1, Name = "A5", Frets = { [0] = 5, [1] = 7, [2] = 7 }, Pinned = true };
        pinned.ChordTemplates.Add(grip);
        pinned.Levels[0].HandShapes.Add(new HandShape { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(4), TemplateId = 1 });

        Assert.Equal(0, Refret().RefretArrangement(pinned, beats).ChordsChanged);
        Assert.Equal(1, pinned.Levels[0].HandShapes[0].TemplateId);

        // Control: the same bar on the chords' own (free) template moves with them.
        var (control, controlBeats, _) = AwkwardA5Run();
        control.Levels[0].HandShapes.Add(new HandShape { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(4), TemplateId = 0 });
        Assert.True(Refret().RefretArrangement(control, controlBeats).ChordsChanged > 0);
        Assert.NotEqual(0, control.Levels[0].HandShapes[0].TemplateId);
    }

    // ── ChordFingeringFollower (Generate FHP, the AI pass) ──────────────

    private static (Arrangement Arrangement, ChartLevel Level, ChordTemplate Shared) DoubleStop()
    {
        // G9+B9 fingered 1-1 (the suggestion), the hand anchored at 8: a free grip shifts to 2-2.
        var arrangement = Guitar();
        var level = arrangement.Levels[0];
        var shared = new ChordTemplate { Id = 0, Name = "x", Frets = { [3] = 9, [4] = 9 }, Fingers = { [3] = 1, [4] = 1 } };
        arrangement.ChordTemplates.Add(shared);
        level.Chords.Add(ChordOn(shared, 2));
        level.HandShapes.Add(new HandShape { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(4), TemplateId = 0 });
        return (arrangement, level, shared);
    }

    [Fact]
    public void Follower_NeverReFingersAPinnedGrip()
    {
        FretAnchor[] anchors = [new(new FractionalPosition(0), 8, 4)];
        var (pinned, level, shared) = DoubleStop();
        shared.Pinned = true;

        Assert.Null(ChordFingeringFollower.FreeGripResolver(pinned)(shared));
        Assert.True(ChordFingeringFollower.Plan(pinned, level, anchors, ChordFingeringFollower.FreeGripResolver(pinned), Fingering).IsEmpty);

        var (control, controlLevel, _) = DoubleStop();
        Assert.False(ChordFingeringFollower.Plan(control, controlLevel, anchors, ChordFingeringFollower.FreeGripResolver(control), Fingering).IsEmpty);
    }

    // ── HandShapeAutoTemplate (a wheel-resized bar) ─────────────────────

    [Fact]
    public void HandShapeAutoTemplate_NeverRebuildsAPinnedGrip()
    {
        var current = new ChordTemplate { Id = 0, Frets = { [0] = 7 }, Fingers = { [0] = 1 }, Pinned = true };
        var seed = new ChordTemplate { Frets = { [0] = 7, [1] = 9, [2] = 9 } };

        Assert.Null(HandShapeAutoTemplate.Rebuild(current, seed, 6));
        Assert.Null(ArrangementFingeringStyle.RebuildHandShapeGrip(Guitar(), current, seed, 6));

        current.Pinned = false;
        Assert.NotNull(HandShapeAutoTemplate.Rebuild(current, seed, 6));

        // Ghost gems pin a grip whatever the flag says: the notes under it cannot describe it.
        current.GhostMask = ChordTemplate.GhostBit(0);
        Assert.Null(HandShapeAutoTemplate.Rebuild(current, seed, 6));
    }

    // ── the ghost-gem grips the editor mints are pinned ─────────────────

    [Fact]
    public void AGripTheGhostVerbsMint_IsPinned()
    {
        var arrangement = Guitar();
        var note = new GuitarNote { Position = new FractionalPosition(1), StringIndex = 3, Fret = 15 };
        arrangement.Levels[0].Notes.Add(note);
        GhostGems.TappedHarmonic(arrangement, arrangement.Levels[0], [note], 12).Action!.Execute();

        Assert.All(arrangement.ChordTemplates, t => Assert.True(t.IsPinned));
    }
}
