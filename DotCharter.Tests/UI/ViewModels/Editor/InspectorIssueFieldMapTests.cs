using System.Reflection;
using System.Text.RegularExpressions;
using DotCharter.Services.Export.Validation;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The two pure tables behind field-level validation
///     (04-inspector-redesign.md §9). Both are exhaustive over
///     <see cref="ArrangementIssueType" /> BY TEST rather than by a switch the
///     compiler checks — the point is that a NEW code must be given an explicit
///     home rather than silently degrading to <c>Card</c> (or to "blocks
///     nothing") forever.
/// </summary>
public class InspectorIssueFieldMapTests
{
    private static IEnumerable<ArrangementIssueType> AllTypes =>
        Enum.GetValues<ArrangementIssueType>();

    [Fact]
    public void EveryIssueTypeHasAnExplicitFieldEntry()
    {
        var source = UiSourceTree.ReadAllText(RepoPath(UiSourceTree.Resolve(Path.Combine(
            "DotCharter.UI", "ViewModels", "Editor", "Inspector", "InspectorIssueFieldMap.cs"))));
        var mapBody = Between(source, "public static InspectorField Map(", "public static bool BlocksExport");

        var missing = AllTypes
            .Where(t => !Regex.IsMatch(mapBody, $@"ArrangementIssueType\.{t}\b"))
            .ToList();

        Assert.True(missing.Count == 0,
            "InspectorIssueFieldMap.Map has no explicit entry for: " + string.Join(", ", missing) +
            ". An unmapped code degrades to Card forever, which is how a finding stops " +
            "landing on the field that causes it.");
    }

    [Fact]
    public void EveryIssueTypeHasAnExplicitExportSeverityEntry()
    {
        var source = UiSourceTree.ReadAllText(RepoPath(UiSourceTree.Resolve(Path.Combine(
            "DotCharter.UI", "ViewModels", "Editor", "Inspector", "InspectorIssueFieldMap.cs"))));
        var blockingBody = Between(source, "public static bool BlocksExport", "BlockingExportKeys");

        // Only the BLOCKING ones need naming — the rest are the documented
        // default. What must not happen is a blocking code being invisible, so
        // assert the set is exactly what the export validator errors on.
        var declaredBlocking = AllTypes.Where(InspectorIssueFieldMap.BlocksExport).ToList();
        foreach (var t in declaredBlocking)
            Assert.Matches($@"ArrangementIssueType\.{t}\b", blockingBody);
    }

    [Fact]
    public void TheBlockingKeyListMatchesTheExportValidatorsOwnErrorConstants()
    {
        // The pairing the design asks to pin so the two validators cannot drift
        // apart silently. Read off ChartExportValidator's own constants.
        var declared = InspectorIssueFieldMap.BlockingExportKeys.ToHashSet();

        var errorKeys = typeof(ChartExportValidator)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, FieldType.FullName: "System.String" })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        // Every key we call blocking must actually exist on the validator.
        foreach (var key in declared)
            Assert.Contains(key, errorKeys);

        Assert.Equal(12, declared.Count);
    }

    [Theory]
    [InlineData(ArrangementIssueType.NoteFretBelowCapo, InspectorField.Fret)]
    [InlineData(ArrangementIssueType.FretNumberMoreThan24, InspectorField.Fret)]
    // It is the SUSTAIN that overlaps, not the placement — this one is easy to
    // get wrong and lands the marker on the wrong control.
    [InlineData(ArrangementIssueType.OverlappingNotes, InspectorField.Length)]
    [InlineData(ArrangementIssueType.SlideFromOpenString, InspectorField.SlideTo)]
    [InlineData(ArrangementIssueType.BendStrengthTooHigh, InspectorField.Bend)]
    [InlineData(ArrangementIssueType.FingerMissingOnFrettedString, InspectorField.Fingering)]
    [InlineData(ArrangementIssueType.IncorrectLinkNext, InspectorField.LinkNext)]
    [InlineData(ArrangementIssueType.HammerOnOnOpenString, InspectorField.Hopo)]
    [InlineData(ArrangementIssueType.DoubleHarmonic, InspectorField.Harmonic)]
    [InlineData(ArrangementIssueType.MutedStringInNonMutedChord, InspectorField.Mute)]
    [InlineData(ArrangementIssueType.NoteWithoutAnchor, InspectorField.Anchor)]
    [InlineData(ArrangementIssueType.OverlappingHandShapes, InspectorField.Span)]
    [InlineData(ArrangementIssueType.NoteAfterSongEnd, InspectorField.Position)]
    public void WorkedEntriesLandWhereTheDesignSaysTheyDo(
        ArrangementIssueType type, InspectorField expected) =>
        Assert.Equal(expected, InspectorIssueFieldMap.Map(type));

    [Fact]
    public void L01IsMarkedBlockingIncludingTheChordNoteCase() =>
        // The export validator flags a chord note below the capo exactly like a
        // single note (fixed on this branch's base), so the inspector's "blocks
        // export" marking on L01 is TRUE, not an optimistic guess.
        Assert.True(InspectorIssueFieldMap.BlocksExport(ArrangementIssueType.NoteFretBelowCapo));

    [Fact]
    public void AdviceDoesNotMasqueradeAsAWall()
    {
        // A charter must be able to trust the marking in BOTH directions.
        Assert.False(InspectorIssueFieldMap.BlocksExport(ArrangementIssueType.PossiblyWrongChordFingering));
        Assert.False(InspectorIssueFieldMap.BlocksExport(ArrangementIssueType.SeventhFretHarmonicWithSustain));
        Assert.False(InspectorIssueFieldMap.BlocksExport(ArrangementIssueType.HopoIntoSameNote));
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' is gone from InspectorIssueFieldMap.");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"Could not find the end of '{start}'.");
        return source[from..to];
    }

    private static string RepoPath(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DotCharter.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
