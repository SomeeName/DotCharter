namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     The same frets fingered differently by two played templates is no finding (D101 is gone, #1023): a hand
///     that sits differently fingers the same shape differently, and EOF, DLC Builder and Charter flag neither.
/// </summary>
public sealed class SameFretsDifferentFingersTests
{
    private static readonly Dictionary<int, int> SevenSeven = new() { [2] = 7, [3] = 7 };

    [Fact]
    public void TwoTemplatesWithTheSameFretsAndDifferentFingers_RaiseNoFinding()
    {
        var barred = Arrangement(Grip(0, 1, 1), Grip(1, 2, 2), pinned: true);
        var same = Arrangement(Grip(0, 1, 1), Grip(1, 1, 1), pinned: true);

        var findings = Findings(barred);

        Assert.Equal(Codes(Findings(same)), Codes(findings));
        Assert.DoesNotContain(findings, issue => issue.Chord == barred.Levels[0].Chords[1]);
    }

    [Fact]
    public void NoIssueTypeCarriesTheCodeD101() =>
        Assert.DoesNotContain(Enum.GetValues<ArrangementIssueType>(), type => type.GetCode() == "D101");

    private static ChordTemplate Grip(int id, int lowerFinger, int upperFinger) => new()
    {
        Id = id,
        Name = "",
        Frets = new Dictionary<int, int>(SevenSeven),
        Fingers = new Dictionary<int, int> { [2] = lowerFinger, [3] = upperFinger }
    };

    private static Arrangement Arrangement(ChordTemplate first, ChordTemplate second, bool pinned)
    {
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        var level = new ChartLevel();
        arrangement.Levels.Add(level);
        var templates = new[] { first, second };
        for (var i = 0; i < templates.Length; i++)
        {
            templates[i].Pinned = pinned;
            arrangement.ChordTemplates.Add(templates[i]);
            level.Chords.Add(new Chord { TemplateId = templates[i].Id, Position = new FractionalPosition(1 + i) });
        }

        return arrangement;
    }

    private static List<ArrangementIssue> Findings(Arrangement arrangement)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 8; i++) beats.Add(new BeatMarker { Time = i * 500 });
        return new ArrangementValidationService(new FakeAppLogger()).Validate(arrangement, beats).ToList();
    }

    private static List<string> Codes(IEnumerable<ArrangementIssue> issues) =>
        issues.Select(issue => issue.Code).Order(StringComparer.Ordinal).ToList();
}
