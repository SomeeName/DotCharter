using Avalonia.Input;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.UI.ViewModels;

/// <summary>
///     The Pattern Builder's editable ROW, in isolation from the dialog (which
///     needs the whole editor graph). What is under test is the one thing that
///     silently breaks the feature: a facet the row forgets to carry.
///     <para>
///         A dropped facet is invisible — the control renders, it toggles, and
///         the value is thrown away on Save. So the round trip is asserted by
///         REFLECTION over <see cref="MatchPattern" />: a facet added to the
///         model and not to the row fails here rather than shipping as a dead
///         control. The choice lists are likewise built from
///         <c>Enum.GetValues</c>, so a new enum member is carried the moment it
///         exists.
///     </para>
/// </summary>
public sealed class PatternBuilderRowTests
{
    /// <summary>Built from the enums themselves so a new member cannot slip past
    ///     the round-trip assertions below.</summary>
    private static readonly PatternChoiceSet Choices = new()
    {
        Frets = Options<FretRule>(),
        Strings = Options<StringRule>(),
        Grouping = Options<PatternGrouping>(),
        Rhythm = Options<RhythmRule>(),
        Sustain = Options<SustainRule>(),
        Range = Options<PatternRange>(),
        Techniques = Enum.GetValues<TechniqueFacet>()
            .Where(f => f is not TechniqueFacet.None and not TechniqueFacet.All)
            .Select(f => (f, f.ToString()))
            .ToList(),
        Companions = Enum.GetValues<PatternCompanions>()
            .Where(c => c is not PatternCompanions.None and not PatternCompanions.All)
            .Select(c => (c, c.ToString(), c.ToString()))
            .ToList()
    };

    private static IReadOnlyList<PatternChoice<T>> Options<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => new PatternChoice<T>(v, v.ToString())).ToList();

    private static MatchPatternRowViewModel Row(MatchPattern pattern) => new(pattern, Choices);

    private static MatchPattern FullyLoaded() => new()
    {
        Id = "row-test",
        Name = "Everything",
        Scope = PatternScope.Notes | PatternScope.Chords | PatternScope.HandShapes | PatternScope.Anchors,
        Grouping = PatternGrouping.AsRun,
        Fret = FretRule.Transposable,
        Strings = StringRule.Relative,
        MatchSize = false,
        MatchSoundKind = false,
        Sustain = SustainRule.SameKind,
        MatchFingering = true,
        MatchChordName = true,
        MatchArpeggio = true,
        Rhythm = RhythmRule.Proportional,
        MatchBeatOffset = true,
        Techniques = TechniqueFacet.Bend | TechniqueFacet.Slide | TechniqueFacet.Accent,
        MatchAnchorWidth = false,
        MatchAnchorSpan = false,
        Range = PatternRange.SameSection,
        MaxFretDistance = 5,
        Companions = PatternCompanions.CoveringHandShape | PatternCompanions.GoverningAnchor,
        SelectKey = new KeyChord(Key.F1, KeyModifiers.Control),
        NextKey = new KeyChord(Key.F2, KeyModifiers.Shift),
        PreviousKey = new KeyChord(Key.F3, KeyModifiers.Alt)
    };

    /// <summary>Everything <c>Clone</c> carries — i.e. every real facet. The three
    ///     0.39.0 compatibility booleans are read-on-load only and deliberately
    ///     never leave the row.</summary>
    private static IEnumerable<System.Reflection.PropertyInfo> Facets() =>
        typeof(MatchPattern).GetProperties()
            .Where(p => p.CanWrite && p.PropertyType != typeof(bool?));

    [Fact]
    public void EveryFacetSurvivesTheRoundTripThroughTheRow()
    {
        var original = FullyLoaded();

        var restored = Row(original).ToPattern();

        foreach (var property in Facets())
            Assert.Equal(property.GetValue(original), property.GetValue(restored));
    }

    /// <summary>
    ///     The same round trip from the OTHER end of every boolean, so a row that
    ///     hard-codes a facet true (or ignores it) cannot pass by accident.
    /// </summary>
    [Fact]
    public void EveryBooleanFacetSurvivesInBothStates()
    {
        foreach (var property in Facets().Where(p => p.PropertyType == typeof(bool)))
        {
            foreach (var value in new[] { true, false })
            {
                var pattern = FullyLoaded();
                property.SetValue(pattern, value);

                var restored = Row(pattern).ToPattern();

                Assert.Equal(value, (bool)property.GetValue(restored)!);
            }
        }
    }

    /// <summary>Every VALUE of every enum facet, not merely one of them — an
    ///     enum member the row cannot express is a choice the user can pick and
    ///     lose.</summary>
    [Fact]
    public void EveryEnumFacetValueSurvives()
    {
        foreach (var property in Facets().Where(p => p.PropertyType.IsEnum && p.PropertyType != typeof(PatternScope)))
        {
            foreach (var value in Enum.GetValues(property.PropertyType))
            {
                if (property.PropertyType == typeof(TechniqueFacet) && (TechniqueFacet)value == TechniqueFacet.All)
                    continue;
                if (property.PropertyType == typeof(PatternCompanions) &&
                    (PatternCompanions)value == PatternCompanions.All)
                {
                    continue;
                }

                var pattern = FullyLoaded();
                property.SetValue(pattern, value);

                Assert.Equal(value, property.GetValue(Row(pattern).ToPattern()));
            }
        }
    }

    [Fact]
    public void EveryTechniqueFacetIsReachableFromTheRow()
    {
        foreach (var facet in Enum.GetValues<TechniqueFacet>()
                     .Where(f => f is not TechniqueFacet.None and not TechniqueFacet.All))
        {
            var pattern = FullyLoaded();
            pattern.Techniques = facet;

            var row = Row(pattern);
            Assert.True(row.Techniques.Single(t => t.Facet == facet).IsSelected,
                $"{facet} did not arrive ticked in the row");
            Assert.Equal(facet, row.ToPattern().Techniques);
        }
    }

    [Fact]
    public void EveryCompanionIsReachableFromTheRow()
    {
        foreach (var companion in Enum.GetValues<PatternCompanions>()
                     .Where(c => c is not PatternCompanions.None and not PatternCompanions.All))
        {
            var pattern = FullyLoaded();
            pattern.Companions = companion;

            var row = Row(pattern);
            Assert.True(row.Companions.Single(c => c.Companion == companion).IsSelected,
                $"{companion} did not arrive ticked in the row");
            Assert.Equal(companion, row.ToPattern().Companions);
        }
    }

    /// <summary>The dialog re-runs its live match count off this event, so a facet
    ///     that does not raise it is a control the preview silently ignores.</summary>
    [Fact]
    public void FlippingAFacetRaisesFacetChanged()
    {
        var row = Row(FullyLoaded());
        var raised = 0;
        row.FacetChanged += (_, _) => raised++;

        row.MatchSize = !row.MatchSize;
        Assert.True(raised > 0);

        var before = raised;
        row.Techniques[0].IsSelected = !row.Techniques[0].IsSelected;
        Assert.True(raised > before, "a technique toggle did not reach FacetChanged");

        before = raised;
        row.Companions[0].IsSelected = !row.Companions[0].IsSelected;
        Assert.True(raised > before, "a companion toggle did not reach FacetChanged");
    }

    /// <summary>
    ///     <c>Summary</c> and <c>MatchCountLabel</c> are OUTPUTS of a facet change
    ///     — the dialog writes them from inside its own <c>FacetChanged</c>
    ///     handler, so re-raising on them recurses until the stack ends.
    /// </summary>
    [Fact]
    public void WritingTheSummaryDoesNotRaiseFacetChanged()
    {
        var row = Row(FullyLoaded());
        var raised = 0;
        row.FacetChanged += (_, _) => raised++;

        row.Summary = "anything";
        row.MatchCountLabel = "7";

        Assert.Equal(0, raised);
    }

    /// <summary>
    ///     The NAME is not a facet. It still re-DESCRIBES the row (the summary
    ///     prints it nowhere, but the handler that rebuilds the summary is the
    ///     same one), and it must not re-COUNT: `RefreshPreview` runs one full
    ///     `PatternMatcher.Find` over the focused track PER PATTERN IN THE RAIL,
    ///     synchronously on the UI thread — nine sweeps per character typed into
    ///     the name box.
    /// </summary>
    [Fact]
    public void WritingTheNameRaisesADescriptionOnlyFacetChange()
    {
        var row = Row(FullyLoaded());
        var counting = 0;
        var total = 0;
        row.FacetChanged += (_, e) =>
        {
            total++;
            if (e.AffectsMatchCount) counting++;
        };

        row.Name = "Renamed";

        Assert.Equal(1, total);
        Assert.Equal(0, counting);

        // …while a real facet still asks for a recount.
        row.MatchFingering = !row.MatchFingering;
        Assert.Equal(1, counting);
    }

    /// <summary>The rhythm picker is dead weight outside a run, and the anchor
    ///     facets outside an anchor scope — the view hides them off these.</summary>
    [Fact]
    public void ConditionalSectionsFollowTheirFacets()
    {
        var pattern = FullyLoaded();
        pattern.Grouping = PatternGrouping.Individually;
        pattern.Scope = PatternScope.Notes;

        var row = Row(pattern);
        Assert.False(row.IsRun);
        Assert.False(row.ShowsAnchorFacets);
        Assert.False(row.ShowsTemplateFacets);

        row.SelectedGrouping = Choices.Grouping.Single(g => g.Value == PatternGrouping.AsRun);
        row.ScopeAnchors = true;
        row.ScopeChords = true;

        Assert.True(row.IsRun);
        Assert.True(row.ShowsAnchorFacets);
        Assert.True(row.ShowsTemplateFacets);
    }

    [Fact]
    public void TheKeyLabelListsEveryBoundVerb()
    {
        var row = Row(FullyLoaded());
        Assert.True(row.HasKeys);
        Assert.Contains("Ctrl+F1", row.KeyLabel);
        Assert.Contains("Shift+F2", row.KeyLabel);
        Assert.Contains("Alt+F3", row.KeyLabel);

        row.SelectKey = KeyChord.None;
        row.NextKey = KeyChord.None;
        row.PreviousKey = KeyChord.None;

        Assert.False(row.HasKeys);
        Assert.Equal(string.Empty, row.KeyLabel);
    }

    /// <summary>A stored pattern whose enum value is not in the option list must
    ///     not blow up — it falls back to the first choice rather than leaving a
    ///     null selection the ComboBox would bind to.</summary>
    [Fact]
    public void AnUnknownEnumValueFallsBackToTheFirstOption()
    {
        var pattern = FullyLoaded();
        pattern.Fret = (FretRule)99;
        pattern.Grouping = (PatternGrouping)99;

        var row = Row(pattern);

        Assert.Equal(Choices.Frets[0].Value, row.SelectedFretRule.Value);
        Assert.Equal(Choices.Grouping[0].Value, row.SelectedGrouping.Value);
    }
}
