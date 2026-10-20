namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     Editor on Fire's finger/hand-position agreement check, ported per note
///     (<c>EOF_SRC/song.c:12577</c>). EOF refuses a clean save over it and says
///     "This note is specified as using the index finger, but the fret hand
///     position is on a different fret."
///     <para>
///         It is not a style rule. The FHP fret IS the index finger's position:
///         Rocksmith derives the playing finger as
///         <c>1 + (fret − anchor.fret) − (anchor.width − 4)</c>, so a chord
///         fingered 1-on-fret-1 under an anchor on fret 12 makes the game show a
///         fingering the chart does not state. The owner hit exactly this on the
///         first note of their Polkadot Stingray chart.
///     </para>
/// </summary>
public sealed class IndexFingerAnchorRuleTests
{
    private static List<BeatMarker> Beats(int count = 8) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    /// <summary>An arrangement with one chord, its template and one anchor.</summary>
    private static Arrangement Build(int anchorFret, IDictionary<int, int> frets, IDictionary<int, int> fingers,
        int capo = 0)
    {
        var arr = new Arrangement { Capo = capo };
        var level = new ChartLevel();
        arr.Levels.Add(level);

        arr.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Frets = new Dictionary<int, int>(frets),
            Fingers = new Dictionary<int, int>(fingers)
        });

        var chord = new Chord { Position = new FractionalPosition(2), TemplateId = 0 };
        foreach (var (stringIndex, fret) in frets)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        level.Chords.Add(chord);

        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), anchorFret, 4));
        return arr;
    }

    private static IReadOnlyList<ArrangementIssue> Validate(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats());

    /// <summary>The owner's actual chart: a finger-1 barre on fret 1 under an
    ///     anchor on fret 12.</summary>
    [Fact]
    public void AFingerOneBarreUnderADistantAnchorIsReported()
    {
        var arr = Build(12,
            new Dictionary<int, int> { [0] = 1, [1] = 1, [2] = 1 },
            new Dictionary<int, int> { [0] = 1, [1] = 1, [2] = 1 });

        var issue = Assert.Single(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        Assert.Equal("E04", issue.Code);
        Assert.NotNull(issue.Anchor);
        Assert.Equal(12, issue.Anchor!.Fret);
    }

    [Fact]
    public void AFingerOneOnTheAnchorFretIsFine()
    {
        var arr = Build(12,
            new Dictionary<int, int> { [0] = 12, [1] = 14, [2] = 14 },
            new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 4 });

        Assert.DoesNotContain(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
    }

    /// <summary>
    ///     Only the INDEX finger is checked — its position is the one the anchor
    ///     fixes. A third finger two frets above the anchor is ordinary playing,
    ///     and flagging it would bury the real fault in noise.
    /// </summary>
    [Fact]
    public void AnotherFingerOffTheAnchorFretIsNotAFault()
    {
        var arr = Build(12,
            new Dictionary<int, int> { [0] = 14, [1] = 15 },
            new Dictionary<int, int> { [0] = 3, [1] = 4 });

        Assert.DoesNotContain(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
    }

    /// <summary>
    ///     #452, the owner's Am7: fingered 2-3-4 on fret 5 with no finger 1,
    ///     under FHP 5. Finger 2 on fret 5 puts the index on fret 4 — reported,
    ///     and the message says the fret was INFERRED rather than authored.
    /// </summary>
    [Fact]
    public void AChordWithNoFingerOneIsJudgedByItsInferredIndex()
    {
        var arr = Build(5,
            new Dictionary<int, int> { [2] = 5, [3] = 5, [4] = 5 },
            new Dictionary<int, int> { [2] = 2, [3] = 3, [4] = 4 });

        var issue = Assert.Single(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        Assert.Contains("finger 2 on fret 5", issue.Message, StringComparison.Ordinal);
        Assert.Contains("index finger on fret 4 (inferred)", issue.Message, StringComparison.Ordinal);
        Assert.Contains("position is on fret 5", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AChordWithNoFingerOneUnderItsInferredIndexIsFine()
    {
        var arr = Build(4,
            new Dictionary<int, int> { [2] = 5, [3] = 5, [4] = 5 },
            new Dictionary<int, int> { [2] = 2, [3] = 3, [4] = 4 });

        Assert.DoesNotContain(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
    }

    /// <summary>
    ///     A ring-and-pinky grip on one fret is played with the index one fret
    ///     below it as often as two: both anchors agree, only the ring finger's
    ///     own fret is reported (the squeeze the corpus charters use, #452).
    /// </summary>
    [Fact]
    public void ARingAndPinkyGripAllowsTheSqueezeButNotTheRingFingersOwnFret()
    {
        var frets = new Dictionary<int, int> { [3] = 9, [4] = 9 };
        var fingers = new Dictionary<int, int> { [3] = 3, [4] = 4 };

        Assert.DoesNotContain(Validate(Build(8, frets, fingers)),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        Assert.DoesNotContain(Validate(Build(7, frets, fingers)),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        var issue = Assert.Single(Validate(Build(9, frets, fingers)),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        Assert.Contains("index finger on fret 7 or 8 (inferred)", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>An authored finger 1 says nothing about inference.</summary>
    [Fact]
    public void AnAuthoredIndexFindingDoesNotSayInferred()
    {
        var arr = Build(12,
            new Dictionary<int, int> { [0] = 1 },
            new Dictionary<int, int> { [0] = 1 });

        var issue = Assert.Single(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
        Assert.DoesNotContain("inferred", issue.Message, StringComparison.Ordinal);
    }

    /// <summary>An unfingered template makes no claim, so there is nothing to
    ///     contradict — an imported chart carries no fingering at all until the
    ///     first save and must not light up.</summary>
    [Fact]
    public void AnUnfingeredChordIsNotAFault()
    {
        var arr = Build(12,
            new Dictionary<int, int> { [0] = 1, [1] = 1 },
            new Dictionary<int, int>());

        Assert.DoesNotContain(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
    }

    /// <summary>
    ///     An open string is stopped by the CAPO, not by a finger, so it has no
    ///     index position to contradict. EOF treats a finger on an open string as
    ///     its own separate fault (its <c>-3</c>), which
    ///     <c>TemplateAndOverlapRule</c> already reports — double-reporting it
    ///     here would be two findings for one mistake.
    /// </summary>
    [Fact]
    public void AnOpenOrSubCapoStringIsNotCheckedHere()
    {
        var arr = Build(5,
            new Dictionary<int, int> { [0] = 2, [1] = 7 },
            new Dictionary<int, int> { [0] = 1, [1] = 3 },
            capo: 2);

        Assert.DoesNotContain(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
    }

    [Fact]
    public void AChordWithNoAnchorInForceIsNotReported()
    {
        var arr = Build(12,
            new Dictionary<int, int> { [0] = 1 },
            new Dictionary<int, int> { [0] = 1 });
        arr.Levels[0].Anchors.Clear();

        Assert.DoesNotContain(Validate(arr),
            i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
    }

    /// <summary>One finding per chord, not one per barred string — a six-string
    ///     barre is one mistake.</summary>
    [Fact]
    public void ABarreReportsOnceRatherThanOncePerString()
    {
        var arr = Build(12,
            new Dictionary<int, int> { [0] = 1, [1] = 1, [2] = 1, [3] = 1, [4] = 1, [5] = 1 },
            new Dictionary<int, int> { [0] = 1, [1] = 1, [2] = 1, [3] = 1, [4] = 1, [5] = 1 });

        Assert.Single(Validate(arr), i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);
    }
}
