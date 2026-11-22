using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using DotCharter.Localization;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     A finding's sentence is a resource key plus arguments, rendered in the UI language where it is
///     shown; <see cref="ArrangementIssue.Message" /> stays the English text the logs and reports carry.
/// </summary>
public sealed partial class ArrangementIssueLocalizationTests
{
    [Fact]
    public void TranslationMetadata_DoesNotChangeFindingIdentityAcrossValidationPasses()
    {
        var arrangement = ArrangementWithFret(30);
        var beats = Beats();
        var first = Validate(arrangement, beats).First(i => i.Type == ArrangementIssueType.FretNumberMoreThan24);
        var second = Validate(arrangement, beats).First(i => i.Type == ArrangementIssueType.FretNumberMoreThan24);
        var legacy = first with { MessageKey = null, MessageArgs = null };

        Assert.NotSame(first.MessageArgs, second.MessageArgs);
        Assert.Single(new HashSet<ArrangementIssue> { first, second, legacy });
    }

    private static readonly ResourceManager Resources = new(
        "DotCharter.Localization.Resources", typeof(LocalizationService).Assembly);

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo Ja = CultureInfo.GetCultureInfo("ja-JP");

    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 8).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static IReadOnlyList<ArrangementIssue> Validate(Arrangement arrangement, List<BeatMarker> beats) =>
        new ArrangementValidationService(new FakeAppLogger()).Validate(arrangement, beats);

    private static Arrangement ArrangementWithFret(int fret)
    {
        var arrangement = new Arrangement();
        arrangement.Levels.Add(new ChartLevel());
        arrangement.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(1), Fret = fret });
        return arrangement;
    }

    [Fact]
    public void TheNoBeatGridFinding_ShowsTheGermanResxSentence()
    {
        var issue = Assert.Single(Validate(ArrangementWithFret(5), []));

        var german = ArrangementIssueText.Of(issue, FakeLocalization.For("de-DE"));

        Assert.Equal(Resources.GetString("Validation_NoBeatGridSoNoChecksRan", De), german);
        Assert.NotEqual(issue.Message, german);
        Assert.StartsWith("This arrangement has no beat grid", issue.Message);
    }

    [Fact]
    public void ARuleFinding_FormatsItsArgumentsInTheUiLanguage_AndKeepsEnglishInTheMessage()
    {
        var issue = Validate(ArrangementWithFret(30), Beats())
            .First(i => i.Type == ArrangementIssueType.FretNumberMoreThan24);

        Assert.Equal("Fret number 30 exceeds the in-game maximum of 24.", issue.Message);
        Assert.Equal(issue.Message, ArrangementIssueText.Of(issue, FakeLocalization.English()));
        Assert.Equal(
            string.Format(De, Resources.GetString("Validation_NoteFretNumberMoreThan24", De)!, 30),
            ArrangementIssueText.Of(issue, FakeLocalization.For("de-DE")));
        Assert.Equal(
            string.Format(Ja, Resources.GetString("Validation_NoteFretNumberMoreThan24", Ja)!, 30),
            ArrangementIssueText.Of(issue, FakeLocalization.For("ja-JP")));
    }

    [Fact]
    public void AFragmentArgument_IsTranslatedInsideTheSentence()
    {
        var issue = new ArrangementIssue(
            ArrangementIssueType.ToneChangeWithoutMatchingTone,
            ArrangementIssueText.English("Validation_ToneChangeWithoutMatchingTone",
                ["Lead", new IssueTextRef("Validation_ToneChangeWithoutMatchingTone_FreeSlot")]),
            MessageKey: "Validation_ToneChangeWithoutMatchingTone",
            MessageArgs: ["Lead", new IssueTextRef("Validation_ToneChangeWithoutMatchingTone_FreeSlot")]);

        var german = ArrangementIssueText.Of(issue, FakeLocalization.For("de-DE"));

        Assert.Equal(string.Format(De,
            Resources.GetString("Validation_ToneChangeWithoutMatchingTone", De)!, "Lead",
            Resources.GetString("Validation_ToneChangeWithoutMatchingTone_FreeSlot", De)), german);
        Assert.StartsWith("Tone change to 'Lead' is not declared", issue.Message);
        Assert.EndsWith("or run Auto-fix.", issue.Message);
    }

    [Fact]
    public void AFindingWithoutAKey_ShowsItsStoredMessage()
    {
        var issue = new ArrangementIssue(ArrangementIssueType.NoEndPhrase, "stored text");

        Assert.Equal("stored text", ArrangementIssueText.Of(issue, FakeLocalization.For("de-DE")));
    }

    [Fact]
    public void EveryValidationKey_HasAGermanAndAJapaneseTranslation_WithTheSamePlaceholders()
    {
        var neutral = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, true)!;
        var german = Resources.GetResourceSet(De, true, false)!;
        var japanese = Resources.GetResourceSet(Ja, true, false)!;
        var keys = neutral.Cast<System.Collections.DictionaryEntry>()
            .Select(e => (string)e.Key).Where(k => k.StartsWith("Validation_", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            var expected = Placeholders(neutral.GetString(key)!);
            Assert.True(german.GetString(key) is { Length: > 0 } de, $"{key} missing in de-DE");
            Assert.True(japanese.GetString(key) is { Length: > 0 } ja, $"{key} missing in ja-JP");
            Assert.Equal(expected, Placeholders(german.GetString(key)!));
            Assert.Equal(expected, Placeholders(japanese.GetString(key)!));
        }
    }

    [Fact]
    public void TheInspectorFold_ShowsTheLocalizedSentence()
    {
        var chord = new Chord();
        var finding = NoteOutsideAnchorFinding(chord, 0);

        var rows = InspectorFieldIssue.Fold([finding], _ => string.Empty, FakeLocalization.For("de-DE"));

        var row = Assert.Single(rows);
        Assert.Equal(ArrangementIssueText.Of(finding, FakeLocalization.For("de-DE")), row.Message);
        Assert.NotEqual(finding.Message, row.Message);
    }

    private static ArrangementIssue NoteOutsideAnchorFinding(Chord chord, int stringIndex) =>
        new(ArrangementIssueType.NoteOutsideAnchorRange,
            "Fret 10 lies outside the active anchor's range (frets 2–5).",
            Chord: chord, StringIndex: stringIndex,
            MessageKey: "Validation_NoteOutsideAnchorRange", MessageArgs: [10, 2, 5]);

    private static string Placeholders(string text) =>
        string.Join(",", PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order());

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
