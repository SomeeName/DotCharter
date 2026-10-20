using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

public class FretAnchorGenerationServiceTests
{
    private static GuitarNote Note(int beat, int fret, int stringIdx = 0)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            StringIndex = stringIdx,
            Fret = fret
        };
    }

    private static GuitarNote SlideNote(int beat, int fret, int slideTo, int stringIdx = 0)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            StringIndex = stringIdx,
            Fret = fret,
            SlideTo = slideTo
        };
    }

    [Fact]
    public void Generate_EmptyLevel_ReturnsEmpty()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var anchors = svc.GenerateAnchors(new ChartLevel());
        Assert.Empty(anchors);
    }

    [Fact]
    public void Generate_SingleNote_ProducesAnchorCoveringIt()
    {
        var logger = new FakeAppLogger();
        var svc = new FretAnchorGenerationService(logger);
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 5));

        var anchors = svc.GenerateAnchors(level);

        Assert.NotEmpty(anchors);
        var anchor = anchors[0];
        Assert.True(anchor.Fret <= 5 && anchor.Fret + anchor.Width > 5,
            $"Anchor [{anchor.Fret}..{anchor.Fret + anchor.Width}] should cover fret 5.");
        Assert.True(logger.TimedOperationsCompleted >= 1);
    }

    [Fact]
    public void Generate_RespectsRangeFilter()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 3));
        level.Notes.Add(Note(10, 7));

        var anchors = svc.GenerateAnchors(level,
            new FractionalPosition(5),
            new FractionalPosition(15));

        Assert.All(anchors, a =>
        {
            Assert.True(a.Position >= new FractionalPosition(5));
            Assert.True(a.Position <= new FractionalPosition(15));
        });
    }

    [Fact]
    public void Generate_OpenStringsOnly_ProducesNoOrBaseAnchor()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 0));
        level.Notes.Add(Note(1, 0, 1));

        var anchors = svc.GenerateAnchors(level);

        // Either no anchors (nothing fretted) or anchors don't suggest a weird position
        // deep into the fretboard.
        Assert.All(anchors, a => Assert.InRange(a.Fret, 1, 5));
    }

    [Fact]
    public void Generate_LogsElapsedTime()
    {
        var logger = new FakeAppLogger();
        var svc = new FretAnchorGenerationService(logger);
        var level = new ChartLevel();
        for (var i = 0; i < 10; i++) level.Notes.Add(Note(i, 3 + i % 5));

        svc.GenerateAnchors(level);

        Assert.Equal(1, logger.TimedOperationsStarted);
        Assert.Equal(1, logger.TimedOperationsCompleted);
    }

    [Fact]
    public void Generate_PitchedSlide_ShiftsAnchorToDestinationFret()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        // Source at fret 5 sliding up to fret 12: destination anchor must
        // shift by (12 - 5) = +7 from the source anchor root.
        level.Notes.Add(SlideNote(0, 5, slideTo: 12));
        // A separate landing note so the destination beat actually anchors.
        level.Notes.Add(Note(2, 12));

        var anchors = svc.GenerateAnchors(level);

        // First anchor covers fret 5; later anchor must cover fret 12
        // because the slide forces the shift.
        Assert.NotEmpty(anchors);
        var src = anchors[0];
        Assert.True(src.Fret <= 5 && src.Fret + src.Width > 5,
            $"Source anchor [{src.Fret}..{src.Fret + src.Width}] should cover fret 5.");
        var dest = anchors[anchors.Count - 1];
        Assert.True(dest.Fret <= 12 && dest.Fret + dest.Width > 12,
            $"Slide destination anchor [{dest.Fret}..{dest.Fret + dest.Width}] should cover fret 12.");
    }

    [Fact]
    public void Generate_NewPositionFurtherUpNeck_AddsSecondAnchor()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 3));
        level.Notes.Add(Note(0, 4, stringIdx: 1));
        level.Notes.Add(Note(8, 14));
        level.Notes.Add(Note(8, 15, stringIdx: 1));

        var anchors = svc.GenerateAnchors(level);

        Assert.True(anchors.Count >= 2,
            $"Expected at least 2 anchors for two distant fret clusters; got {anchors.Count}.");
        var first = anchors[0];
        var last = anchors[anchors.Count - 1];
        Assert.True(first.Fret < last.Fret,
            $"Second cluster (last anchor at fret {last.Fret}) should be higher up the neck than the first ({first.Fret}).");
    }

    [Fact]
    public void Generate_AnchorRootNeverNegativeOrAboveMaxFret()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        // Edge values across the fretboard
        level.Notes.Add(Note(0, 1));
        level.Notes.Add(Note(2, 22));
        level.Notes.Add(Note(4, 0));

        var anchors = svc.GenerateAnchors(level);

        Assert.All(anchors, a =>
        {
            Assert.InRange(a.Fret, 1, 22);
            Assert.True(a.Width >= 1, $"Width must be >= 1, got {a.Width}.");
        });
    }

    [Fact]
    public void Generate_SequentialRunBeyondHandSpan_MovesTheHand_NotOneWideAnchor()
    {
        // Frets 2 → 7 → 8 → 9 across time: the hand must MOVE UP (a new anchor
        // near fret 7), never stretch a single anchor from 2 to 7 (owner report
        // 2026-07-19: "a stretch from 2 to 7 is quite big, nobody would play that").
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 2));
        level.Notes.Add(Note(1, 7));
        level.Notes.Add(Note(2, 8));
        level.Notes.Add(Note(3, 9));

        var anchors = svc.GenerateAnchors(level);

        var first = anchors[0];
        Assert.Equal(2, first.Fret);
        Assert.True(first.Width <= 4, $"low-neck anchor width {first.Width} should not exceed 4");
        Assert.True(first.Fret + first.Width - 1 < 7, "the fret-2 anchor must not reach fret 7");
        Assert.Contains(anchors, a => a.Fret >= 6 && a.Fret + a.Width - 1 >= 9);
    }

    [Fact]
    public void Generate_WideLetRingHandShape_DoesNotLockToOneOverWideAnchor()
    {
        // The exact reported case: a wide let-ring shape spanning a run that
        // climbs 2 → 9 used to lock to ONE anchor {2, width 8}. It must instead
        // break so the hand climbs through the run.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 2));
        level.Notes.Add(Note(1, 7));
        level.Notes.Add(Note(2, 8));
        level.Notes.Add(Note(3, 9));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(4),
            TemplateId = -1
        });

        var anchors = svc.GenerateAnchors(level);

        Assert.True(anchors.Count >= 2, $"wide shape should break into multiple anchors; got {anchors.Count}");
        Assert.DoesNotContain(anchors, a => a.Fret == 2 && a.Fret + a.Width - 1 >= 7);
        Assert.Contains(anchors, a => a.Fret >= 6);
    }

    [Fact]
    public void Generate_WideSimultaneousChord_KeepsItsOwnSpanUncapped()
    {
        // A genuine stretch chord struck at ONCE (frets 2 and 7 together) is a
        // physical necessity — its anchor keeps the full span, unlike a
        // sequential run. Log Charter leaves a chord's own min..max uncapped.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(0) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 5, Fret = 7 });
        level.Chords.Add(chord);

        var anchors = svc.GenerateAnchors(level);

        var a = Assert.Single(anchors);
        Assert.True(a.Fret <= 2 && a.Fret + a.Width - 1 >= 7,
            $"simultaneous 2+7 chord anchor [{a.Fret}..{a.Fret + a.Width - 1}] must cover both frets");
    }

    [Fact]
    public void Generate_HandShapeRange_AnchorsLockToShapeRoot()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 7));
        level.Notes.Add(Note(0, 8, stringIdx: 1));
        level.Notes.Add(Note(2, 7));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(4),
            TemplateId = -1
        });

        var anchors = svc.GenerateAnchors(level);

        Assert.NotEmpty(anchors);
        // Within the handshape every anchor should share the same root —
        // that is the whole point of P1 in the priority hierarchy.
        var inShape = anchors
            .Where(a => a.Position >= new FractionalPosition(0) && a.Position < new FractionalPosition(4))
            .ToList();
        if (inShape.Count > 1)
        {
            var root = inShape[0].Fret;
            Assert.All(inShape, a => Assert.Equal(root, a.Fret));
        }
    }

    // --- Capo ---------------------------------------------------------------
    //
    // Frets are ABSOLUTE on the un-capoed neck; fret 0 = open, which a capo
    // stops at the capo fret. So under a capo a sound at or below the capo is
    // OPEN (it never constrains the hand) and the lowest legal anchor is
    // capo + 1 — an anchor on or below the capo is inspector rule L02.

    /// <summary>A busy level exercising notes, chords, hand shapes and a slide.</summary>
    private static ChartLevel BuildMixedLevel()
    {
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 2));
        level.Notes.Add(Note(0, 3, stringIdx: 1));
        level.Notes.Add(Note(1, 0, stringIdx: 2));
        level.Notes.Add(SlideNote(2, 5, slideTo: 12));
        level.Notes.Add(Note(4, 12));
        level.Notes.Add(Note(6, 14, stringIdx: 3));

        var chord = new Chord { Position = new FractionalPosition(8) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 7 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 9 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 9 });
        level.Chords.Add(chord);

        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(8),
            EndPosition = new FractionalPosition(10),
            TemplateId = -1
        });

        return level;
    }

    [Fact]
    public void Generate_Capo0_IsAStrictNoOp()
    {
        // The overwhelming majority of charts have no capo — passing 0 must
        // produce byte-identical anchors to not passing the argument at all.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());

        var baseline = svc.GenerateAnchors(BuildMixedLevel());
        var withZero = svc.GenerateAnchors(BuildMixedLevel(), null, null, null, 0);

        Assert.Equal(baseline.Count, withZero.Count);
        for (var i = 0; i < baseline.Count; i++)
        {
            Assert.Equal(baseline[i].Position, withZero[i].Position);
            Assert.Equal(baseline[i].Fret, withZero[i].Fret);
            Assert.Equal(baseline[i].Width, withZero[i].Width);
        }
    }

    [Fact]
    public void Generate_NegativeCapo_IsTreatedAsNoCapo()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());

        var baseline = svc.GenerateAnchors(BuildMixedLevel());
        var negative = svc.GenerateAnchors(BuildMixedLevel(), null, null, null, -3);

        Assert.Equal(baseline.Count, negative.Count);
        for (var i = 0; i < baseline.Count; i++)
            Assert.Equal(baseline[i].Fret, negative[i].Fret);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void Generate_UnderACapo_NoAnchorSitsOnOrBelowTheCapo(int capo)
    {
        // Directly encodes inspector rule L02 (AnchorOnOrBelowCapo), which the
        // generator used to violate because it never consulted the capo.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());

        var anchors = svc.GenerateAnchors(BuildMixedLevel(), null, null, null, capo);

        Assert.NotEmpty(anchors);
        Assert.All(anchors, a =>
            Assert.True(a.Fret > capo,
                $"Anchor at fret {a.Fret} sits on or below the capo (fret {capo}) — inspector L02."));
    }

    [Fact]
    public void Generate_UnderACapo_SoundsAtOrBelowTheCapoDoNotDragTheAnchorDown()
    {
        // Frets 1 and 2 are behind a capo on fret 3 — they are open strings, so
        // they must not anchor the hand at fret 1 the way they do uncapoed.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 1));
        level.Notes.Add(Note(1, 2, stringIdx: 1));
        level.Notes.Add(Note(2, 6, stringIdx: 2));

        var uncapoed = new FretAnchorGenerationService(new FakeAppLogger()).GenerateAnchors(level);
        var capoed = svc.GenerateAnchors(level, null, null, null, 3);

        // Uncapoed, fret 1 is a real fretted note and anchors the hand there.
        Assert.Contains(uncapoed, a => a.Fret == 1);
        // Capoed, only fret 6 is genuinely fretted.
        Assert.All(capoed, a => Assert.True(a.Fret > 3, $"anchor {a.Fret} must clear the capo"));
        Assert.Contains(capoed, a => a.Fret == 6);
    }

    [Fact]
    public void Generate_CapoedBarre_AnchorsOnTheBarreFret()
    {
        // A barre entirely above the capo is untouched by it: the anchor lands
        // on the barre fret with or without the capo behind it.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(0) };
        for (var s = 0; s < 6; s++)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 5 });
        level.Chords.Add(chord);

        var capoed = svc.GenerateAnchors(level, null, null, null, 2);
        var uncapoed = new FretAnchorGenerationService(new FakeAppLogger()).GenerateAnchors(level);

        var a = Assert.Single(capoed);
        Assert.Equal(5, a.Fret);
        Assert.True(a.Fret > 2, "the barre anchor must clear the capo");
        Assert.Equal(Assert.Single(uncapoed).Fret, a.Fret);
    }

    [Fact]
    public void Generate_CapoedBarreOnTheCapoItself_StillAnchorsAboveTheCapo()
    {
        // Degenerate/malformed input: a "barre" charted AT the capo fret is
        // really the capo itself. It must never emit an anchor on the capo.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        var chord = new Chord { Position = new FractionalPosition(0) };
        for (var s = 0; s < 6; s++)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 8 });
        level.Chords.Add(chord);

        var anchors = svc.GenerateAnchors(level, null, null, null, 3);

        Assert.All(anchors, a => Assert.True(a.Fret > 3, $"anchor {a.Fret} must clear the capo (3)"));
    }

    [Fact]
    public void Generate_UnderACapo_SlideDestinationAnchorStillClearsTheCapo()
    {
        // A downward slide could otherwise push the destination anchor behind
        // the capo via the (slideTo - sourceFret) shift: from an anchor at 9,
        // sliding 9 -> 4 shifts by -5 and lands on fret 4, behind a capo on 5.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        var slide = SlideNote(0, 9, slideTo: 4);
        slide.Sustain = new FractionalPosition(2);
        level.Notes.Add(slide);

        var anchors = svc.GenerateAnchors(level, null, null, null, 5);

        // Source anchor at 0, forced slide-destination anchor at beat 2.
        Assert.Equal(2, anchors.Count);
        Assert.All(anchors, a => Assert.True(a.Fret > 5, $"anchor {a.Fret} must clear the capo (5)"));
        // Uncapoed the same slide is free to land on fret 4.
        var uncapoed = new FretAnchorGenerationService(new FakeAppLogger()).GenerateAnchors(level);
        Assert.Contains(uncapoed, a => a.Fret == 4);
    }

    [Fact]
    public void Generate_AbsurdlyHighCapo_DoesNotThrowAndStaysOnTheNeck()
    {
        // MinAnchorFret is clamped into [1, MaxFret] so a nonsense capo can
        // never invert a Math.Clamp(value, min, max) range (which throws).
        var svc = new FretAnchorGenerationService(new FakeAppLogger());

        var anchors = svc.GenerateAnchors(BuildMixedLevel(), null, null, null, 99);

        Assert.All(anchors, a => Assert.InRange(a.Fret, 1, 22));
    }

    // ─── Hand-abandonment re-anchor (bass looks 6 ahead, guitar 14) ─────────

    [Fact]
    public void Generate_Bass_ReAnchorsUpWhenTheHandAbandonsTheLowPosition()
    {
        // Fret 2 plants the box at [2..5]; the phrase then sits on 4/5 for well
        // past the abandonment look-ahead. On a BASS part the fret hand has
        // clearly moved up, so the anchor re-plants onto fret 4 instead of
        // sticking at the low-water mark 2.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 2));
        for (var b = 1; b <= 8; b++) level.Notes.Add(Note(b, b % 2 == 1 ? 4 : 5));

        var anchors = svc.GenerateAnchors(level, capo: 0, bassArrangement: true);

        Assert.Equal(2, anchors[0].Fret);
        Assert.Contains(anchors, a => a.Position == new FractionalPosition(1) && a.Fret == 4);
    }

    [Fact]
    public void Generate_GuitarLooksFurtherAhead_AShortClimbKeepsTheOneAnchor()
    {
        // The identical phrase on a non-bass part: a lead/rhythm hand anchors a
        // short climbing run once rather than following every note up — eight
        // notes are well inside the guitar look-ahead (HandAbandonment.GuitarHold = 14),
        // so the box stays at fret 2 (bassArrangement defaults to false). The
        // long-phrase case is FretAnchorContextTests.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 2));
        for (var b = 1; b <= 8; b++) level.Notes.Add(Note(b, b % 2 == 1 ? 4 : 5));

        var anchors = svc.GenerateAnchors(level);

        Assert.Single(anchors);
        Assert.Equal(2, anchors[0].Fret);
    }

    [Fact]
    public void Generate_Bass_HoldsTheLowAnchorWhenTheRiffDipsBack()
    {
        // The low fret is revisited within the look-ahead (an oscillating riff,
        // not a genuine hand shift), so even on a bass part the anchor must HOLD
        // at fret 2 rather than chase the reach up.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 2));
        int[] frets = [4, 5, 4, 2, 4, 5, 4, 5];
        for (var b = 0; b < frets.Length; b++) level.Notes.Add(Note(b + 1, frets[b]));

        var anchors = svc.GenerateAnchors(level, capo: 0, bassArrangement: true);

        Assert.All(anchors, a => Assert.Equal(2, a.Fret));
    }

    [Fact]
    public void Generate_ArpeggioHandShape_AnchorsOnTheLowestFret_NotTheFirstNote()
    {
        // A let-ring arpeggio is single notes spread over time: the first note
        // struck is fret 5, but the shape reaches down to fret 3, so the hand
        // must anchor at 3 to cover it (owner report: it anchored on 5). A chord
        // shape is unaffected — this only applies when the shape starts on a
        // single note.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 5, 1)); // first note struck: fret 5
        level.Notes.Add(Note(1, 3, 0)); // the shape reaches down to fret 3
        level.Notes.Add(Note(2, 5, 2));
        level.Notes.Add(Note(3, 5, 3));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(4),
            TemplateId = 0
        });

        var anchors = svc.GenerateAnchors(level);

        var start = anchors.First(a => a.Position == new FractionalPosition(0));
        Assert.Equal(3, start.Fret);
        Assert.True(start.Fret + start.Width > 5, "the anchor window must still cover fret 5");
    }

    [Fact]
    public void Generate_ArpeggioBeforeLowerChord_DoesNotBorrowTheChordsRoot()
    {
        // Real owner report: a let-ring arpeggio {5,3,5,5} is immediately followed
        // by a lower {2,2,2} chord. ClampHandShapes ends the arpeggio's shape
        // EXACTLY at the chord's position, so the chord sits on the shape's
        // (exclusive) End. Folding it into the arpeggio's root re-anchored the whole
        // run onto the chord's fret 2 — the arpeggio must keep its own fret-3 root
        // and the chord must get its OWN fret-2 anchor after it.
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 5, 1)); // first note struck: fret 5
        level.Notes.Add(Note(1, 3, 0)); // the shape reaches down to fret 3
        level.Notes.Add(Note(2, 5, 2));
        level.Notes.Add(Note(3, 5, 3));

        var chord = new Chord { Position = new FractionalPosition(4) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 2 });
        level.Chords.Add(chord);

        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(4), // clamped to the following chord
            TemplateId = 0
        });

        var anchors = svc.GenerateAnchors(level);

        var start = anchors.First(a => a.Position == new FractionalPosition(0));
        Assert.Equal(3, start.Fret);
        Assert.True(start.Fret + start.Width > 5, "the anchor window must still cover fret 5");

        var atChord = anchors.First(a => a.Position == new FractionalPosition(4));
        Assert.Equal(2, atChord.Fret); // the chord gets its own lower anchor
    }

    // ─── Retroactive group descend ──────────────────────────────────────────
    //
    // An anchor's root is not fixed when it is emitted. While its group is still
    // open, a note reaching BELOW it moves that already-written anchor down
    // instead of planting a second one — which is what the generator behind the
    // hand-corrected TestData/EOF corpus does
    // (eof_generate_efficient_hand_positions_logic keeps a running current_low
    // and writes it at the group's FIRST note). Fixing the root at the first note
    // instead was the single largest disagreement with that corpus: 75.7% ->
    // 78.8% exact agreement over its 12,279 comparable points, and a +0.16-fret
    // "we sit higher than the charter" bias erased to -0.01.

    [Fact]
    public void Generate_ALowerNoteInsideTheOpenGroup_PullsThatGroupsAnchorDown()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 7));
        level.Notes.Add(Note(1, 7));
        level.Notes.Add(Note(2, 5)); // the hand was at 5 all along, reaching up to 7

        var anchors = svc.GenerateAnchors(level);

        // ONE hand position, planted at the group's first note, on fret 5 — not
        // a fret-7 anchor followed by a second one at fret 5.
        var anchor = Assert.Single(anchors);
        Assert.Equal(new FractionalPosition(0), anchor.Position);
        Assert.Equal(5, anchor.Fret);
        Assert.True(anchor.Fret + anchor.Width > 7, "the widened window must still cover fret 7");
    }

    [Fact]
    public void Generate_ALowerNoteBeyondTheDescendReach_PlantsItsOwnAnchor()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 7));
        level.Notes.Add(Note(1, 7));
        level.Notes.Add(Note(2, 7));
        level.Notes.Add(Note(3, 7));
        level.Notes.Add(Note(4, 5)); // five struck positions later — the hand MOVED

        var anchors = svc.GenerateAnchors(level);

        Assert.Equal(2, anchors.Count);
        Assert.Equal(new FractionalPosition(0), anchors[0].Position);
        Assert.Equal(7, anchors[0].Fret);
        Assert.Equal(new FractionalPosition(4), anchors[1].Position);
        Assert.Equal(5, anchors[1].Fret);
    }

    [Fact]
    public void Generate_ADescendThatWouldOutreachTheHand_PlantsItsOwnAnchor()
    {
        var svc = new FretAnchorGenerationService(new FakeAppLogger());
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 10));
        level.Notes.Add(Note(1, 5)); // merging would ask for a 6-fret reach

        var anchors = svc.GenerateAnchors(level);

        Assert.Equal(2, anchors.Count);
        Assert.Equal(10, anchors[0].Fret);
        Assert.Equal(5, anchors[1].Fret);
    }
}
