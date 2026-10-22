using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     Every reason <see cref="RepeatChords.Explain" /> can give for a chord shipping (or not) as
///     Rocksmith's repeat box (#564). The inspector prints the reason, and the export's set is DERIVED from
///     these verdicts (<see cref="RepeatChords.Compute" /> keeps the ones that <c>Ships</c>), so the line
///     cannot disagree with what ships; <c>Rendering/RepeatChordParityTests</c> pins that set against the
///     real RS-XML writer. Each case is the smallest level that reaches its reason, and
///     <see cref="EveryReason_HasACase" /> fails the day a reason is added without one.
/// </summary>
public sealed class RepeatChordVerdictTests
{
    private const int ShapeA = 1;
    private const int ShapeB = 2;
    private const int OneString = 3;

    private static readonly (int String, int Fret)[] FretsA = [(0, 3), (1, 5), (2, 5)];
    private static readonly (int String, int Fret)[] FretsB = [(1, 2), (2, 4), (3, 4)];
    private static readonly (int String, int Fret)[] FretsOne = [(0, 3)];

    private sealed class Level
    {
        public readonly List<ChordTemplate> Templates = [Template(ShapeA, FretsA), Template(ShapeB, FretsB),
            Template(OneString, FretsOne)];

        public readonly List<GuitarNote> Notes = [];
        public readonly List<Chord> Chords = [];
        public readonly List<HandShape> HandShapes = [];

        public readonly List<BeatMarker> Beats =
            [.. Enumerable.Range(0, 64).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

        public Chord Add(int beat, int templateId = ShapeA, (int String, int Fret)[]? frets = null)
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
            foreach (var (s, f) in frets ?? (templateId == ShapeB ? FretsB : templateId == OneString ? FretsOne : FretsA))
                chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
            Chords.Add(chord);
            return chord;
        }

        public RepeatChordVerdict VerdictOf(Chord chord) =>
            RepeatChords.Explain(Templates, Notes, Chords, HandShapes, Beats)[chord];
    }

    private static ChordTemplate Template(int id, (int String, int Fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = $"T{id}" };
        foreach (var (s, f) in frets) template.Frets[s] = f;
        return template;
    }

    /// <summary>One level per reason: the chord to ask about, and the string the reason names (or -1).</summary>
    private static readonly Dictionary<RepeatChordReason, Func<(Level Level, Chord Target, int String)>> Cases = new()
    {
        [RepeatChordReason.Repeat] = () => Pair((_, _) => { }),
        [RepeatChordReason.ForcedNoNotes] = () =>
        {
            var l = new Level();
            l.Add(1);
            var c = l.Add(2, ShapeB);
            c.ForceNoNotes = true;
            return (l, c, -1);
        },
        [RepeatChordReason.FirstChord] = () =>
        {
            var l = new Level();
            return (l, l.Add(1), -1);
        },
        [RepeatChordReason.DifferentTemplate] = () =>
        {
            var l = new Level();
            l.Add(1);
            return (l, l.Add(2, ShapeB), -1);
        },
        [RepeatChordReason.SingleString] = () =>
        {
            var l = new Level();
            l.Add(1, OneString);
            return (l, l.Add(2, OneString), -1);
        },
        [RepeatChordReason.LinkNext] = () => Pair((_, c) =>
        {
            // Into empty space a beat later, so no chord is landed on.
            c.ChordNotes[1].LinkNext = true;
            c.ChordNotes[1].Sustain = new FractionalPosition(1);
        }),
        [RepeatChordReason.Slide] = () => Pair((_, c) => c.ChordNotes[1].SlideTo = 7, 1),
        [RepeatChordReason.Vibrato] = () => Pair((_, c) => c.ChordNotes[1].Vibrato = true, 1),
        [RepeatChordReason.Tremolo] = () => Pair((_, c) => c.ChordNotes[1].Tremolo = true, 1),
        [RepeatChordReason.Hopo] = () => Pair((_, c) => c.ChordNotes[1].Hopo = HopoType.HammerOn, 1),
        [RepeatChordReason.Harmonic] = () => Pair((_, c) => c.ChordNotes[1].Harmonic = HarmonicType.Normal, 1),
        [RepeatChordReason.BassPicking] = () => Pair((_, c) => c.ChordNotes[1].BassPicking = BassPickingType.Slap, 1),
        [RepeatChordReason.Bend] = () => Pair((_, c) => c.ChordNotes[1].BendValues.Add(new BendValue(c.Position, 2)), 1),
        // EOF's crazy status: the exact repeat of the chord before it, held at the full box.
        [RepeatChordReason.PassOtherNotes] = () => Pair((_, c) => c.PassOtherNotes = true),
        [RepeatChordReason.ExplicitSustain] = () => Pair((_, c) => c.ChordNotes[1].ExplicitSustain = true, 1),
        [RepeatChordReason.PerStringAccent] = () => Pair((_, c) => c.ChordNotes[2].Accent = true, 2),
        [RepeatChordReason.PerStringIgnore] = () => Pair((_, c) => c.ChordNotes[2].Ignore = true, 2),
        [RepeatChordReason.MixedMute] = () => Pair((_, c) => c.ChordNotes[1].Mute = MuteType.Palm, 1),
        [RepeatChordReason.NoEarlierSound] = () =>
        {
            // Two strums at one position: neither is BEFORE the other.
            var l = new Level();
            l.Add(1);
            return (l, l.Add(1), -1);
        },
        [RepeatChordReason.PreviousIsChordSlide] = () => Pair((first, _) => first.ChordNotes[0].SlideTo = 5),
        [RepeatChordReason.FirstInHandShape] = () => Pair((_, _) => { }, shape: (2, 4)),
        [RepeatChordReason.DifferentFrets] = () =>
        {
            // One template id, but the strum is fretted differently from the chord before it.
            var l = new Level();
            l.Add(1);
            return (l, l.Add(2, ShapeA, [(0, 3), (1, 5), (2, 6)]), -1);
        },
        [RepeatChordReason.PreviousSoundIsNote] = () =>
        {
            var l = new Level();
            l.Add(1);
            l.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
            return (l, l.Add(3), -1);
        },
        [RepeatChordReason.TooFarFromPrevious] = () =>
        {
            // Beat 40 is 19.5 s after the first strum ends; the threshold is 10 s.
            var l = new Level();
            l.Add(1);
            return (l, l.Add(40), -1);
        },
        [RepeatChordReason.LinkLanding] = () => Pair((first, _) =>
        {
            first.ChordNotes[1].LinkNext = true;
            first.ChordNotes[1].Sustain = new FractionalPosition(1);
        }),
        // "Split notes" ships the chord as single notes: never a box, whatever the strum before it.
        [RepeatChordReason.SplitIntoNotes] = () => Pair((_, c) => c.SplitIntoNotes = true),
        [RepeatChordReason.UnresolvedTemplate] = () =>
        {
            // No live template AND no notes: the export cannot synthesize one, so it drops the chord.
            // (With notes it synthesizes a template from them — see RsChordTemplateExport.)
            var l = new Level();
            l.Add(1);
            return (l, l.Add(2, templateId: 99, frets: []), -1);
        }
    };

    /// <summary>Shape A twice, a beat apart; <paramref name="edit" /> changes (first, second).</summary>
    private static (Level, Chord, int) Pair(Action<Chord, Chord> edit, int stringIndex = -1,
        (int From, int To)? shape = null)
    {
        var l = new Level();
        var first = l.Add(1);
        var second = l.Add(2);
        edit(first, second);
        if (shape is { } s)
        {
            l.HandShapes.Add(new HandShape
            {
                Position = new FractionalPosition(s.From), EndPosition = new FractionalPosition(s.To),
                TemplateId = ShapeA
            });
        }

        return (l, second, stringIndex);
    }

    public static TheoryData<RepeatChordReason> AllReasons() => [.. Enum.GetValues<RepeatChordReason>()];

    [Theory]
    [MemberData(nameof(AllReasons))]
    public void EachReason_IsWhatTheRuleAnswers(RepeatChordReason reason)
    {
        var (level, target, stringIndex) = Cases[reason]();

        var verdict = level.VerdictOf(target);

        Assert.Equal(reason, verdict.Reason);
        Assert.Equal(stringIndex, verdict.StringIndex);
    }

    /// <summary>The export's set IS the verdicts that ship — on every case above, both ways.</summary>
    [Theory]
    [MemberData(nameof(AllReasons))]
    public void TheShippedSet_IsExactlyTheVerdictsThatShip(RepeatChordReason reason)
    {
        var (level, target, _) = Cases[reason]();

        var shipped = RepeatChords.Compute(level.Templates, level.Notes, level.Chords, level.HandShapes, level.Beats);
        var verdicts = RepeatChords.Explain(level.Templates, level.Notes, level.Chords, level.HandShapes, level.Beats);

        Assert.Equal(level.Chords.Count, verdicts.Count);
        foreach (var chord in level.Chords)
            Assert.Equal(verdicts[chord].Ships, shipped.Contains(chord));
        Assert.Equal(reason is RepeatChordReason.Repeat or RepeatChordReason.ForcedNoNotes, shipped.Contains(target));
    }

    /// <summary>
    ///     The EOF trick: one hand shape opens on a single note, holds a chord, single notes, then the SAME
    ///     chord again. That chord is a repeat box unless crazy ("Pass other") brings the full box back,
    ///     as EOF_SRC/rs.c:4935-4936 does — and the hand shape stays one piece either way.
    /// </summary>
    [Theory]
    [InlineData(false, RepeatChordReason.Repeat)]
    [InlineData(true, RepeatChordReason.PassOtherNotes)]
    public void TheSameChordLaterInOneHandShape_ShowsItsFullBoxOnlyWhenCrazy(bool crazy, RepeatChordReason expected)
    {
        var l = new Level();
        l.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(1), EndPosition = new FractionalPosition(8), TemplateId = ShapeA
        });
        l.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 3, Fret = 9 });
        l.Add(2);
        l.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 3, Fret = 9 });
        l.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 3, Fret = 9 });
        var again = l.Add(5);
        again.PassOtherNotes = crazy;

        Assert.Equal(expected, l.VerdictOf(again).Reason);
        Assert.Single(l.HandShapes);
    }

    /// <summary>
    ///     Accent and ignore are WHOLE-chord flags: the importers, the whole-chord toggle and the spacing
    ///     enforcer set them on the chord and leave every chord note's own flag false. The compact tag says
    ///     the chord's flag for every string, so such a strum still ships as the repeat box.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AWholeChordFlag_OnBothStrums_StillShipsTheSecondAsARepeat(bool accent, bool ignore)
    {
        var (level, second, _) = Pair((first, c) =>
        {
            foreach (var chord in new[] { first, c })
            {
                chord.Accent = accent;
                chord.Ignore = ignore;
            }
        });

        Assert.Equal(RepeatChordReason.Repeat, level.VerdictOf(second).Reason);
        Assert.Contains(second, RepeatChords.Compute(level.Templates, level.Notes, level.Chords, level.HandShapes, level.Beats));
    }

    /// <summary>A per-string flag UNDER a whole-chord flag adds nothing the tag does not already say.</summary>
    [Fact]
    public void AStringFlag_UnderTheSameChordFlag_StillShipsAsARepeat()
    {
        var (level, second, _) = Pair((first, c) =>
        {
            first.Accent = c.Accent = true;
            c.ChordNotes[2].Accent = true;
        });

        Assert.Equal(RepeatChordReason.Repeat, level.VerdictOf(second).Reason);
    }

    [Fact]
    public void EveryReason_HasACase() =>
        Assert.Equal(Enum.GetValues<RepeatChordReason>().Order(), Cases.Keys.Order());
}
