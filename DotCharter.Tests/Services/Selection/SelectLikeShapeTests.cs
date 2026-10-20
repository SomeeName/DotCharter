using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Selection;

/// <summary>
///     What "similar" means for a CHORD or a HAND SHAPE:
///     <b>the same string→fret map with no conflicting finger</b> — nothing
///     about technique, and nothing about which <see cref="ChordTemplate" />
///     object the sound points at.
///     <para>
///         Select-like used to key chords on template IDENTITY whenever they had
///         a template, computing a fret signature only for the templateless
///         ones. Re-fingering one chord of eight identical ones mints it a
///         private template, and from then on select-like returned NOTHING: the
///         siblings were on the old id, and the seed no longer qualified for the
///         signature path because it now had an id of its own. Hand shapes were
///         not considered at all, even though a <see cref="HandShape" /> carries
///         a <c>TemplateId</c> exactly like a chord.
///     </para>
/// </summary>
public sealed class SelectLikeShapeTests
{
    private static SelectLikeService NewService() => new(new FakeAppLogger());

    private static Arrangement NewArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }

    private static ChordTemplate Template(int id, string name,
        (int stringIndex, int fret)[] frets, (int stringIndex, int finger)[]? fingers = null)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        foreach (var (stringIndex, finger) in fingers ?? [])
            template.Fingers[stringIndex] = finger;
        return template;
    }

    private static Chord Chord(int templateId, int beat, params (int stringIndex, int fret)[] notes)
    {
        var chord = new Chord { TemplateId = templateId, Position = new FractionalPosition(beat) };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }

    private static HandShape Shape(int templateId, int beat)
    {
        return new HandShape
        {
            TemplateId = templateId,
            Position = new FractionalPosition(beat),
            EndPosition = new FractionalPosition(beat + 2)
        };
    }

    [Fact]
    public void SameShapeOnDifferentTemplateObjects_Matches()
    {
        // The heart of it: two templates describing the SAME shape and the same
        // fingering. Identity says "different chord", the shape says "the same
        // chord" — and the shape is right.
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        arr.ChordTemplates.Add(Template(1, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var seed = Chord(1, 0, (1, 7), (2, 7));
        var sibling = Chord(0, 4, (1, 7), (2, 7));
        var chords = new[] { seed, sibling };

        var matches = NewService().FindLikeShapes(arr, [seed], [], chords, []);

        Assert.Equal([sibling], matches.Chords);
    }

    [Fact]
    public void ARefingeredChord_StillMatchesItsUnfingeredSiblings()
    {
        // The owner's reproduction. Freshly imported chords sit at TemplateId
        // -1 with no fingering at all; re-finger ONE and it gets a template of
        // its own. An unauthored finger is a HOLE, not a value, so the run is
        // still one run — which is what makes "re-finger the rest" reachable.
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 4)]));

        var refingered = Chord(0, 0, (1, 7), (2, 7));
        var siblings = Enumerable.Range(1, 7).Select(i => Chord(-1, i * 4, (1, 7), (2, 7))).ToList();
        var chords = new List<Chord> { refingered };
        chords.AddRange(siblings);

        var matches = NewService().FindLikeShapes(arr, [refingered], [], chords, []);

        Assert.Equal(siblings, matches.Chords);
    }

    [Fact]
    public void AConflictingFingering_DoesNotMatch()
    {
        // Where BOTH sides authored a finger they have to agree: the same frets
        // fingered 1-3 and 1-2 are two different hand shapes, which is exactly
        // why the RS chord template stores the fingers.
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        arr.ChordTemplates.Add(Template(1, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 2)]));

        var seed = Chord(0, 0, (1, 7), (2, 7));
        var other = Chord(1, 4, (1, 7), (2, 7));

        var matches = NewService().FindLikeShapes(arr, [seed], [], new[] { seed, other }, []);

        Assert.Empty(matches.Chords);
    }

    [Fact]
    public void TechniquesAreIrrelevant()
    {
        // Bends, slides, palm mutes, accents and sustains must not split a run.
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var seed = Chord(0, 0, (1, 7), (2, 7));
        var loud = Chord(0, 4, (1, 7), (2, 7));
        loud.Accent = true;
        loud.ChordNotes[0].Mute = MuteType.Palm;
        loud.ChordNotes[1].Sustain = new FractionalPosition(2);
        loud.ChordNotes[1].BendValues.Add(new BendValue { Position = new FractionalPosition(1), Step = 1f });

        var matches = NewService().FindLikeShapes(arr, [seed], [], new[] { seed, loud }, []);

        Assert.Equal([loud], matches.Chords);
    }

    [Fact]
    public void ADifferentShape_NeverMatches()
    {
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)]));

        var seed = Chord(0, 0, (1, 7), (2, 7));
        var moved = Chord(-1, 4, (1, 9), (2, 9));
        var wider = Chord(-1, 8, (1, 7), (2, 7), (3, 7));

        var matches = NewService().FindLikeShapes(arr, [seed], [], new[] { seed, moved, wider }, []);

        Assert.Empty(matches.Chords);
    }

    [Fact]
    public void HandShapesAreSelectedToo()
    {
        // A HandShape carries a TemplateId exactly like a chord; select-like
        // simply never looked at them.
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));
        arr.ChordTemplates.Add(Template(1, "C5", [(1, 3), (2, 5)]));

        var seed = Shape(0, 0);
        var alike = Shape(0, 8);
        var different = Shape(1, 16);

        var matches = NewService().FindLikeShapes(arr, [], [seed], [], new[] { seed, alike, different });

        Assert.Equal([alike], matches.HandShapes);
    }

    [Fact]
    public void AChordSeed_AlsoReachesTheHandShapesOverThatShape()
    {
        // The bar has to come along, or a bulk fingering edit leaves the 3D
        // hand-shape fingering describing the shape the notes no longer play.
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var seed = Chord(0, 0, (1, 7), (2, 7));
        var bar = Shape(0, 0);

        var matches = NewService().FindLikeShapes(arr, [seed], [], new[] { seed }, new[] { bar });

        Assert.Equal([bar], matches.HandShapes);
    }

    [Fact]
    public void AHandShapeWithNoResolvableTemplate_IsNeitherSeedNorMatch()
    {
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)]));

        var dangling = Shape(-1, 0);
        var real = Shape(0, 8);

        Assert.Empty(NewService().FindLikeShapes(arr, [], [dangling], [], new[] { dangling, real }).HandShapes);
        Assert.Empty(NewService().FindLikeShapes(arr, [], [real], [], new[] { real, dangling }).HandShapes);
    }

    [Fact]
    public void ADriftedTemplatesFingering_IsIgnored()
    {
        // The chord's notes are the truth about its frets. A template whose
        // frets no longer describe the chord is describing some OTHER shape, so
        // its fingering must not be read as this chord's.
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "stale", [(1, 12), (2, 12)], [(1, 1), (2, 4)]));
        arr.ChordTemplates.Add(Template(1, "A5", [(1, 7), (2, 7)], [(1, 1), (2, 3)]));

        var drifted = Chord(0, 0, (1, 7), (2, 7));   // points at the 12-12 template
        var honest = Chord(1, 4, (1, 7), (2, 7));

        var matches = NewService().FindLikeShapes(arr, [drifted], [], new[] { drifted, honest }, []);

        Assert.Equal([honest], matches.Chords);
    }

    [Fact]
    public void TheSeedsThemselves_AreNeverReturned()
    {
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)]));

        var a = Chord(0, 0, (1, 7), (2, 7));
        var b = Chord(0, 4, (1, 7), (2, 7));

        var matches = NewService().FindLikeShapes(arr, [a, b], [], new[] { a, b }, []);

        Assert.Empty(matches.Chords);
    }

    [Fact]
    public void NoSeeds_MatchesNothing()
    {
        var arr = NewArrangement();
        arr.ChordTemplates.Add(Template(0, "A5", [(1, 7), (2, 7)]));

        var matches = NewService().FindLikeShapes(arr, [], [], new[] { Chord(0, 0, (1, 7), (2, 7)) }, []);

        Assert.Empty(matches.Chords);
        Assert.Empty(matches.HandShapes);
    }

    [Fact]
    public void ANoteLessChord_IsNeitherSeedNorMatch()
    {
        var arr = NewArrangement();
        var empty = Chord(-1, 0);
        var alsoEmpty = Chord(-1, 4);

        var matches = NewService().FindLikeShapes(arr, [empty], [], new[] { empty, alsoEmpty }, []);

        Assert.Empty(matches.Chords);
    }
}
