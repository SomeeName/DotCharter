using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     The per-technique switches inside the matcher (#730): a technique switched off is never
///     judged — a sound that asks only for it is a plain full hit and its judge is never called,
///     and a sound that asks for it alongside a switched-on one is ruled on the switched-on one
///     alone. No technique on is no judge at all. A ruling carries the streak its hit was fed to
///     the meters at.
/// </summary>
public sealed class LiveNoteMatcherTechniqueSwitchesTests
{
    private static readonly ChartVerifyOutcome Confirmed = new(true, 0.9f);

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 16).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    // A full-step bend at 1000 ms, peaking 250 ms in, held for a beat; optionally with vibrato.
    private static GuitarNote BentNote(bool vibrato = false) => new()
    {
        Position = new FractionalPosition(2),
        Fret = 5,
        StringIndex = 3,
        Vibrato = vibrato,
        Sustain = new FractionalPosition(1),
        BendValues = { new BendValue(new FractionalPosition(2, new Fraction(1, 2)), 2) }
    };

    // A two-fret slide up at 1000 ms, held for a beat.
    private static GuitarNote SlideNote() => new()
    {
        Position = new FractionalPosition(2), Fret = 5, StringIndex = 3, SlideTo = 7, Sustain = new FractionalPosition(1)
    };

    // A vibrato at 1000 ms, held for two beats.
    private static GuitarNote VibratoNote() => new()
    {
        Position = new FractionalPosition(2), Fret = 5, StringIndex = 3, Vibrato = true, Sustain = new FractionalPosition(2)
    };

    private static Arrangement With(params GuitarNote[] notes)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        foreach (var note in notes) arr.Levels[0].Notes.Add(note);
        return arr;
    }

    private static LiveNoteMatcher Matcher(Arrangement arr, ContourTechniques judged,
        Func<ExpectedSound, TechniqueContourVerdict?> judge)
    {
        var m = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            chartVerify: _ => Confirmed, chartVerifyEarly: _ => Confirmed, judgeContour: judge,
            judgedTechniques: judged);
        m.Load(arr, arr.Levels[0], Beats());
        return m;
    }

    private static List<LiveTechniqueJudgment> Run(LiveNoteMatcher m, double fromMs, double untilMs)
    {
        var rulings = new List<LiveTechniqueJudgment>();
        for (var t = fromMs; t <= untilMs; t += 33)
        {
            m.AdvanceTo(t);
            rulings.AddRange(m.TechniqueJudgments);
        }

        return rulings;
    }

    public static TheoryData<string, ContourTechniques> SwitchedOffTechniques => new()
    {
        { "bend", ContourTechniques.Slide | ContourTechniques.Vibrato },
        { "slide", ContourTechniques.Bend | ContourTechniques.Vibrato },
        { "vibrato", ContourTechniques.Bend | ContourTechniques.Slide }
    };

    [Theory]
    [MemberData(nameof(SwitchedOffTechniques))]
    public void A_switched_off_technique_is_never_judged_and_its_note_is_a_full_hit(string technique,
        ContourTechniques judged)
    {
        var note = technique switch
        {
            "bend" => BentNote(),
            "slide" => SlideNote(),
            _ => VibratoNote()
        };
        var asks = 0;
        var m = Matcher(With(note), judged, _ =>
        {
            asks++;
            return TechniqueContourVerdict.NotMet;
        });

        var rulings = Run(m, 900, 4000);

        Assert.NotNull(m.JudgeContour);
        Assert.Equal(0, asks);
        Assert.Empty(rulings);
        Assert.Equal(LivePlayVerdict.Hit, m.Expected[0].Verdict);
        Assert.Null(m.Expected[0].ContourVerdict);
        Assert.Equal(100, m.Stats.Score);
    }

    [Theory]
    [InlineData("bend", ContourTechniques.Bend)]
    [InlineData("slide", ContourTechniques.Slide)]
    [InlineData("vibrato", ContourTechniques.Vibrato)]
    public void A_switched_on_technique_is_judged(string technique, ContourTechniques judged)
    {
        var note = technique switch
        {
            "bend" => BentNote(),
            "slide" => SlideNote(),
            _ => VibratoNote()
        };
        var m = Matcher(With(note), judged, _ => TechniqueContourVerdict.NotMet);

        var ruling = Assert.Single(Run(m, 900, 4000));

        Assert.Equal(TechniqueContourVerdict.NotMet, ruling.Verdict);
        Assert.Equal(LivePlayVerdict.HitWrongTechnique, m.Expected[0].Verdict);
        Assert.Equal(50, m.Stats.Score);
    }

    /// <summary>The default: bends on, the rest off. A bend with vibrato is ruled on its bend
    /// alone — the judge never sees the vibrato, and the ruling does not wait for it.</summary>
    [Fact]
    public void A_bend_with_vibrato_is_ruled_on_the_bend_alone_when_vibrato_is_off()
    {
        var seen = new List<TechniqueContourExpectation>();
        var askedAt = new List<double>();
        var playhead = 0.0;
        var m = Matcher(With(BentNote(vibrato: true)), ContourTechniques.Bend, s =>
        {
            seen.Add(s.TechniqueContour);
            askedAt.Add(playhead);
            return TechniqueContourVerdict.Met;
        });

        for (playhead = 900; playhead <= 4000; playhead += 33) m.AdvanceTo(playhead);

        var asked = Assert.Single(seen);
        Assert.Equal(ContourTechniques.Bend, asked.Techniques);
        // The bend's evidence (250 ms peak + 250 ms slack) ends at 1500 ms; a vibrato would have
        // held it to the window's end.
        Assert.True(askedAt[0] < 1600, $"the ruling waited for the switched-off vibrato: {askedAt[0]}");
        Assert.True(m.Expected[0].Vibrato, "the chart's own vibrato flag is untouched");
    }

    [Fact]
    public void No_technique_switched_on_is_no_judge_at_all()
    {
        var m = LiveHitWindows.CreateMatcher(LiveHitWindowPreset.Normal,
            chartVerify: _ => Confirmed, chartVerifyEarly: _ => Confirmed,
            judgeContour: _ => TechniqueContourVerdict.NotMet, judgedTechniques: ContourTechniques.None);

        Assert.Null(m.JudgeContour);
    }

    /// <summary>The ruling hands the meters the streak the hit was fed at, so a downgrade takes
    /// back exactly the hit's extra over a half hit (#730).</summary>
    [Fact]
    public void A_ruling_carries_the_streak_its_hit_was_credited_at()
    {
        var plain = new GuitarNote { Position = new FractionalPosition(1), Fret = 3, StringIndex = 1 };
        var m = Matcher(With(plain, BentNote()), ContourTechniques.Bend, _ => TechniqueContourVerdict.NotMet);

        var ruling = Assert.Single(Run(m, 400, 4000));

        Assert.Equal(2, ruling.StreakAtCredit);
        Assert.Equal(2, m.Stats.Streak);
    }
}
