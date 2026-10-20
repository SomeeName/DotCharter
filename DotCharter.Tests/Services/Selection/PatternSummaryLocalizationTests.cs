using System.Globalization;
using DotCharter.Localization;
using DotCharter.Services.Editing.Selection.Patterns;

namespace DotCharter.Tests.Services.Selection;

/// <summary>
///     The Pattern Builder's rail summary — the one line the whole dialog exists
///     to make comparable — was thirty-odd English literals inside
///     <see cref="PatternSummary" />, so a de-DE or ja-JP charter read
///     "Chords · same frets · same strings" beside a fully translated form.
/// </summary>
[Collection("LocalizationSingleton")]
public sealed class PatternSummaryLocalizationTests
{
    /// <summary>A rich pattern, so most clauses are exercised at once.</summary>
    private static MatchPattern Rich() => new MatchPattern
    {
        Scope = PatternScope.Chords,
        Fret = FretRule.Transposable,
        Strings = StringRule.Relative,
        MatchFingering = true,
        Range = PatternRange.SameSection,
        MaxFretDistance = 4,
        Companions = PatternCompanions.CoveringHandShape | PatternCompanions.GoverningAnchor
    }.Normalized();

    /// <summary>
    ///     The UI languages the process may be in when these run: the Windows
    ///     language of the box, or whatever an earlier test left behind. The
    ///     summary's assertions must hold in every one of them (#596: they failed
    ///     on a German Windows).
    /// </summary>
    private static readonly string[] AmbientUiLanguages = ["en-US", "de-DE", "ja-JP"];

    /// <summary>
    ///     Switching the UI language changes the line. Asserted as a DIFFERENCE
    ///     rather than against German literals, so the guard survives a
    ///     re-translation but not a return to hard-coded English.
    /// </summary>
    [Fact]
    public void TheSummary_FollowsTheUiLanguage()
    {
        UnderUiLanguage("en-US", () =>
        {
            var loc = LocalizationService.Instance;
            var english = PatternSummary.Describe(Rich(), loc);

            loc.SetConfiguredCulture("de-DE");
            var german = PatternSummary.Describe(Rich(), loc);

            Assert.NotEqual(english, german);
            Assert.DoesNotContain("same frets", german, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("any position", german, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("this section only", german, StringComparison.OrdinalIgnoreCase);

            // …and it is a real sentence in both, not a run of raw key names.
            foreach (var line in new[] { english, german })
                Assert.DoesNotContain("PatternSummary_", line, StringComparison.Ordinal);
        });
    }

    /// <summary>
    ///     A facet is worded ONCE. The dialog's checkbox and the summary's clause
    ///     come from the same resource key, so they cannot drift — they were two
    ///     hand-kept lists, and the summary's half was the untranslated one.
    /// </summary>
    [Theory]
    [InlineData(TechniqueFacet.Bend)]
    [InlineData(TechniqueFacet.Slide)]
    [InlineData(TechniqueFacet.Hopo)]
    [InlineData(TechniqueFacet.Harmonic)]
    [InlineData(TechniqueFacet.Mute)]
    [InlineData(TechniqueFacet.Vibrato)]
    [InlineData(TechniqueFacet.Tremolo)]
    [InlineData(TechniqueFacet.Accent)]
    [InlineData(TechniqueFacet.LinkNext)]
    [InlineData(TechniqueFacet.BassPicking)]
    [InlineData(TechniqueFacet.Ignored)]
    [InlineData(TechniqueFacet.PassOtherNotes)]
    public void EveryTechniqueFacet_NamesARealResource_AndTheSummaryUsesIt(TechniqueFacet facet)
    {
        var key = PatternSummary.TechniqueKey(facet);
        Assert.False(string.IsNullOrEmpty(key), $"{facet} has no resource key");

        foreach (var language in AmbientUiLanguages)
        {
            UnderUiLanguage(language, () =>
            {
                // The label and the summary come from the SAME service. Reading
                // the label off the singleton compared an English summary with a
                // label in whatever language the process was left in (#596).
                var loc = FakeLocalization.English();
                var label = loc[key];
                Assert.NotEqual(key, label); // a missing resource resolves to the key name

                var summary = PatternSummary.Describe(new MatchPattern
                {
                    Scope = PatternScope.Notes,
                    Techniques = facet
                }.Normalized(), loc);
                Assert.Contains(label, summary, StringComparison.Ordinal);
            });
        }
    }

    /// <summary>Every element kind in scope, same rule.</summary>
    [Theory]
    [InlineData(PatternScope.Notes)]
    [InlineData(PatternScope.Chords)]
    [InlineData(PatternScope.HandShapes)]
    [InlineData(PatternScope.Anchors)]
    public void EveryScopeKind_NamesARealResource(PatternScope kind)
    {
        var key = PatternSummary.ScopeKey(kind);
        Assert.False(string.IsNullOrEmpty(key), $"{kind} has no resource key");
        Assert.NotEqual(key, FakeLocalization.English()[key]);
    }

    /// <summary>
    ///     The one COUNTED clause. It printed "within 1 frets" — in English as
    ///     well as German — and 1 is the tightest and most useful setting that
    ///     facet has.
    /// </summary>
    [Fact]
    public void TheFretDistanceClause_HasASingularForm()
    {
        foreach (var language in AmbientUiLanguages)
        {
            UnderUiLanguage(language, () =>
            {
                var loc = FakeLocalization.English();
                var one = PatternSummary.Describe(new MatchPattern
                {
                    Scope = PatternScope.Notes, MaxFretDistance = 1
                }.Normalized(), loc);
                var many = PatternSummary.Describe(new MatchPattern
                {
                    Scope = PatternScope.Notes, MaxFretDistance = 4
                }.Normalized(), loc);

                Assert.Contains(loc.Plural("PatternSummary_WithinFretsOne", "PatternSummary_WithinFretsFmt", 1),
                    one, StringComparison.Ordinal);
                Assert.Contains(loc.Plural("PatternSummary_WithinFretsOne", "PatternSummary_WithinFretsFmt", 4),
                    many, StringComparison.Ordinal);
                Assert.NotEqual(one, many);
            });
        }
    }

    /// <summary>
    ///     Runs <paramref name="body" /> with the singleton and the ambient UI
    ///     culture on <paramref name="language" />, then puts the ambient cultures
    ///     back FIRST and rests the singleton on en-US explicitly. Restoring its
    ///     configured "system" instead re-resolves against the UI culture this very
    ///     call moved, and strands the singleton in German for every later test
    ///     (#596; the house rule is in <c>Localization/FormattingCultureTests</c>).
    /// </summary>
    private static void UnderUiLanguage(string language, Action body)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        var previousDefaultUi = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            LocalizationService.Instance.SetConfiguredCulture(language);
            body();
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultUi;
            CultureInfo.CurrentUICulture = previousUi;
            CultureInfo.CurrentCulture = previousCulture;
            LocalizationService.Instance.SetConfiguredCulture("en-US");
        }
    }
}
