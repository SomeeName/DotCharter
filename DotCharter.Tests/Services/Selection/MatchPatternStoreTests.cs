using System.Text.Json;
using Avalonia.Input;
using DotCharter.Services.Editing.Selection.Patterns;

namespace DotCharter.Tests.Services.Selection;

/// <summary>
///     The pattern store. Two things matter here and neither is CRUD: the starter
///     set must arrive exactly ONCE (an empty list is a legitimate state — the
///     user deleted them), and a pattern must survive the config round trip with
///     every facet intact, because a facet that silently reads back as its
///     default is a pattern that changes meaning on restart.
/// </summary>
public sealed class MatchPatternStoreTests
{
    private static MatchPatternStore NewStore(out FakeConfigurationService config)
    {
        config = new FakeConfigurationService();
        return new MatchPatternStore(config);
    }

    [Fact]
    public void EnsureSeeded_WritesTheStarterSetOnce()
    {
        var store = NewStore(out var config);

        var first = store.EnsureSeeded();
        Assert.Equal(MatchPatternCatalog.BuiltIns().Count, first.Count);
        Assert.True(config.AppConfig.MatchPatternsSeeded);

        var second = store.EnsureSeeded();
        Assert.Equal(first.Count, second.Count);
    }

    /// <summary>
    ///     The whole reason the seeded FLAG exists: without it, "delete every
    ///     pattern" would be undone by the next launch and the user could never
    ///     get rid of the starter set.
    /// </summary>
    [Fact]
    public void OnceSeeded_AnEmptyListStaysEmpty()
    {
        var store = NewStore(out _);
        store.EnsureSeeded();

        foreach (var pattern in store.List()) store.Remove(pattern.Id);
        Assert.Empty(store.List());

        Assert.Empty(store.EnsureSeeded());
    }

    /// <summary>Replacing the list is an explicit statement about what should
    ///     exist, so it counts as seeded too — otherwise the builder's "delete
    ///     them all and Save" is undone on the next launch.</summary>
    [Fact]
    public void ReplaceAll_CountsAsSeeded()
    {
        var store = NewStore(out var config);

        store.ReplaceAll([]);

        Assert.True(config.AppConfig.MatchPatternsSeeded);
        Assert.Empty(store.EnsureSeeded());
    }

    [Fact]
    public void Save_UpsertsByIdRatherThanAppending()
    {
        var store = NewStore(out _);
        var saved = store.Save(new MatchPattern { Name = "mine" });

        saved.Name = "renamed";
        store.Save(saved);

        var stored = Assert.Single(store.List());
        Assert.Equal("renamed", stored.Name);
    }

    [Fact]
    public void ANamelessPattern_IsNormalizedRatherThanStoredBlank()
    {
        var store = NewStore(out _);

        var saved = store.Save(new MatchPattern { Name = "   ", Scope = PatternScope.None });

        Assert.False(string.IsNullOrWhiteSpace(saved.Id));
        Assert.False(string.IsNullOrWhiteSpace(saved.Name));
        // An empty scope looks perfectly configured and matches nothing forever.
        Assert.NotEqual(PatternScope.None, saved.Scope);
    }

    [Fact]
    public void ListAndGet_HandBackClonesRatherThanTheStoredInstance()
    {
        var store = NewStore(out _);
        var saved = store.Save(new MatchPattern { Name = "mine", Sustain = SustainRule.SameLength });

        var listed = Assert.Single(store.List());
        listed.Sustain = SustainRule.Ignore;

        Assert.Equal(SustainRule.SameLength, store.Get(saved.Id)!.Sustain);
    }

    /// <summary>
    ///     Every facet through the real serializer. A pattern is persisted inside
    ///     <c>AppConfig</c>, and this repo has shipped one-sided fields before —
    ///     a facet that round-trips to its default would silently change what a
    ///     saved pattern means the next time the app starts.
    /// </summary>
    [Fact]
    public void EveryFacet_SurvivesTheJsonRoundTrip()
    {
        var options = new JsonSerializerOptions { Converters = { new KeyChordJsonConverter() } };

        var original = new MatchPattern
        {
            Id = "round-trip",
            Name = "Everything",
            Scope = PatternScope.Chords | PatternScope.Anchors,
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
            Techniques = TechniqueFacet.Bend | TechniqueFacet.Slide | TechniqueFacet.Ignored,
            MatchAnchorWidth = false,
            MatchAnchorSpan = false,
            Range = PatternRange.SamePhrase,
            MaxFretDistance = 7,
            Companions = PatternCompanions.All,
            SelectKey = new KeyChord(Key.F1, KeyModifiers.Control),
            NextKey = new KeyChord(Key.F2, KeyModifiers.Shift | KeyModifiers.Alt),
            PreviousKey = new KeyChord(Key.F3)
        };

        var restored = JsonSerializer.Deserialize<MatchPattern>(
            JsonSerializer.Serialize(original, options), options)!;

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Scope, restored.Scope);
        Assert.Equal(original.Grouping, restored.Grouping);
        Assert.Equal(original.Fret, restored.Fret);
        Assert.Equal(original.Strings, restored.Strings);
        Assert.Equal(original.MatchSize, restored.MatchSize);
        Assert.Equal(original.MatchSoundKind, restored.MatchSoundKind);
        Assert.Equal(original.Sustain, restored.Sustain);
        Assert.Equal(original.MatchFingering, restored.MatchFingering);
        Assert.Equal(original.MatchChordName, restored.MatchChordName);
        Assert.Equal(original.MatchArpeggio, restored.MatchArpeggio);
        Assert.Equal(original.Rhythm, restored.Rhythm);
        Assert.Equal(original.MatchBeatOffset, restored.MatchBeatOffset);
        Assert.Equal(original.Techniques, restored.Techniques);
        Assert.Equal(original.MatchAnchorWidth, restored.MatchAnchorWidth);
        Assert.Equal(original.MatchAnchorSpan, restored.MatchAnchorSpan);
        Assert.Equal(original.Range, restored.Range);
        Assert.Equal(original.MaxFretDistance, restored.MaxFretDistance);
        Assert.Equal(original.Companions, restored.Companions);
        Assert.Equal(original.SelectKey, restored.SelectKey);
        Assert.Equal(original.NextKey, restored.NextKey);
        Assert.Equal(original.PreviousKey, restored.PreviousKey);
    }

    /// <summary>
    ///     A config written by an older build carries none of these properties.
    ///     Every one must read back as its default rather than throwing — the
    ///     absent-tolerance the whole AppConfig depends on.
    /// </summary>
    [Fact]
    public void APatternFromAnOlderConfig_LoadsWithDefaults()
    {
        var options = new JsonSerializerOptions { Converters = { new KeyChordJsonConverter() } };

        var restored = JsonSerializer.Deserialize<MatchPattern>(
            """{"Id":"legacy","Name":"Legacy"}""", options)!;

        Assert.Equal("legacy", restored.Id);
        Assert.Equal(PatternScope.Sounds, restored.Scope);
        Assert.Equal(FretRule.Exact, restored.Fret);
        Assert.False(restored.HasAnyKey);
    }

    [Fact]
    public void Clone_CopiesEveryFacet()
    {
        // A missed field in Clone is invisible: the builder edits a working copy,
        // so a facet the clone drops is silently reset the moment the dialog opens.
        var original = new MatchPattern
        {
            Id = "x", Name = "x", Scope = PatternScope.Anchors, Grouping = PatternGrouping.AsRun,
            Fret = FretRule.Ignore, Strings = StringRule.Relative, MatchSize = false, MatchSoundKind = false,
            Sustain = SustainRule.SameKind, MatchFingering = true, MatchChordName = true, MatchArpeggio = true,
            Rhythm = RhythmRule.Ignore, MatchBeatOffset = true,
            Techniques = TechniqueFacet.All, MatchAnchorWidth = false, MatchAnchorSpan = false,
            Range = PatternRange.SameSection, MaxFretDistance = 3, Companions = PatternCompanions.All,
            SelectKey = new KeyChord(Key.F1), NextKey = new KeyChord(Key.F2), PreviousKey = new KeyChord(Key.F3)
        };

        var clone = original.Clone();

        // The three 0.39.0 compatibility booleans are read-on-load only and are
        // deliberately NOT carried by Clone — Normalized() folds them in first.
        foreach (var property in typeof(MatchPattern).GetProperties()
                     .Where(p => p.CanWrite && p.PropertyType != typeof(bool?)))
        {
            Assert.Equal(property.GetValue(original), property.GetValue(clone));
        }
    }
}
