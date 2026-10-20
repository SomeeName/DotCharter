using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing.Highway;

/// <summary>
///     `HandShapeInteractionTool.AutoFillHandShapeTemplate` (now `HandShapeLaneSpan.BuildBar`) did
///     `arr.ChordTemplates.Add(template)` outside any `IChartAction`, so a
///     right-click hand-shape create was only HALF undoable: Ctrl+Z took the
///     shape back out and left the minted template behind forever — and nothing
///     in the app garbage-collects templates, so they accumulate for the life of
///     the file (editing-rework audit 03 §4, "One real bug found while mapping
///     this").
/// </summary>
public sealed class HandShapeTemplateMintTests
{
    [Fact]
    public void AMintedTemplateJoinsTheSameHistoryNodeAsItsHandShape()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var created = new List<ChordTemplate>();
        var template = ChordTemplateResolver.Resolve(
            h.Arrangement, new Dictionary<int, int> { [0] = 3, [1] = 5 },
            ArpeggioFlavour.Plain, preferredName: null, created);
        var hs = new HandShape
        {
            Position = new FractionalPosition(2),
            EndPosition = new FractionalPosition(4),
            TemplateId = template.Id
        };
        Assert.Single(created);
        Assert.Empty(h.Arrangement.ChordTemplates); // Resolve does NOT add.

        h.Service.AddHandShape(hs, created);

        Assert.Single(h.State.HandShapes);
        Assert.Single(h.Arrangement.ChordTemplates);

        h.History.Undo();

        Assert.Empty(h.State.HandShapes);
        Assert.Empty(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void AHandShapeWithNoMintedTemplateStillCommits()
    {
        var h = new ChordKeyboardMutationTests.Harness();
        var hs = new HandShape
        {
            Position = new FractionalPosition(2),
            EndPosition = new FractionalPosition(4)
        };

        h.Service.AddHandShape(hs);

        Assert.Single(h.State.HandShapes);
        h.History.Undo();
        Assert.Empty(h.State.HandShapes);
    }

    /// <summary>
    ///     Source lint: the pointer tools must never write the template list
    ///     directly. Every highway edit is history-backed by construction —
    ///     `IHighwayEditCommand`'s only member takes an `IHistoryService` — and a
    ///     bare collection write is the one way around that.
    /// </summary>
    [Fact]
    public void NoPointerToolAddsAChordTemplateOutsideAnAction()
    {
        var toolsDir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Input"));
        var offenders = UiSourceTree.GetFiles(toolsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => StripComments(UiSourceTree.ReadAllText(f)).Contains("ChordTemplates.Add(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These pointer-input files mutate Arrangement.ChordTemplates directly, so the mint is not " +
            "undoable. Resolve through ChordTemplateResolver and hand the created list to the editing " +
            "service instead: " + string.Join(", ", offenders));
    }

    /// <summary>Drops `//` and `///` line comments so a comment that merely
    /// DESCRIBES the removed call cannot fail the scan. Crude on purpose — over-
    /// stripping can only make the guard stricter.</summary>
    private static string StripComments(string source)
    {
        var kept = source.Split('\n')
            .Select(line =>
            {
                var idx = line.IndexOf("//", StringComparison.Ordinal);
                return idx < 0 ? line : line[..idx];
            });
        return string.Join('\n', kept);
    }
}
