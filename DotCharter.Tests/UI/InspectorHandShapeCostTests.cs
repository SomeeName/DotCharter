namespace DotCharter.Tests.UI;

/// <summary>
///     ONE whole-level walk per hand-shape selection change, not three.
///
///     <para>
///         The Span and Shape cards print two counts that are O(CHART), not
///         O(selection): how many sounds the selected shape encloses, and how
///         many enclosed chords a shape edit would rebind. Both used to be
///         computed inside their getters, and three BOUND properties read them —
///         <c>HandShapeEnclosedLabel</c> pulls <c>HandShapeEnclosedCount</c>
///         (a full <c>PlacedNotes</c> + <c>PlacedChords</c> walk), while
///         <c>HandShapeRebindsChords</c> and <c>HandShapeRebindWarning</c> each
///         pull <c>HandShapeRebindsUntemplatedChords</c> (a full
///         <c>PlacedChords</c> walk with a <c>DescribesChordNotes</c> per chord).
///         Avalonia re-evaluates every bound getter on its
///         <c>OnPropertyChanged</c>, so ONE selection change ran three
///         whole-level walks on the UI thread — 20,000 sounds' worth on a real
///         chart, between the click and the next frame.
///     </para>
///     <para>
///         A source lint rather than a counting fake: what it pins is WHERE the
///         walk happens, which no behavioural test of the counts can see. It
///         reads <c>Inspector/HandShapeEditorViewModel.cs</c>, where the cluster
///         lives since it left <c>InspectorViewModel</c>, with the same
///         three-legged "repoint fails rather than going quiet" rule as
///         <see cref="InspectorIssueRebuildTests" /> beside it.
///     </para>
/// </summary>
public sealed class InspectorHandShapeCostTests
{
    /// <summary>
    ///     The two counts are FOLDED once in the sync and READ from a field.
    ///     Three legs: the sync assigns both memo fields, and neither getter
    ///     walks the chart itself.
    /// </summary>
    [Fact]
    public void TheHandShapeCounts_AreFoldedOncePerSync_NotOncePerBoundReader()
    {
        var src = InspectorSource();

        var sync = BodyOf(src, "internal void SyncHandShapeProperties()");
        Assert.Contains("_handShapeEnclosedCount = ComputeHandShapeEnclosedCount();", sync);
        Assert.Contains(
            "_handShapeRebindsUntemplatedChords = ComputeHandShapeRebindsUntemplatedChords();", sync);

        // …and the bound getters are field reads. `PlacedNotes`/`PlacedChords`
        // are the chart-wide collections; naming either from a getter is the
        // per-read walk this exists to refuse.
        foreach (var getter in new[]
                 {
                     "public int HandShapeEnclosedCount",
                     "public int HandShapeRebindsUntemplatedChords"
                 })
        {
            var declaration = DeclarationOf(src, getter);
            Assert.DoesNotContain("PlacedNotes", declaration);
            Assert.DoesNotContain("PlacedChords", declaration);
        }
    }

    /// <summary>
    ///     Non-vacuity floor for the leg above: the walks must still EXIST,
    ///     in the fold helpers. A memo over a computation somebody deleted
    ///     would satisfy every assertion in the first case.
    /// </summary>
    [Fact]
    public void TheFoldHelpers_StillDoTheWholeLevelWalk()
    {
        var src = InspectorSource();

        var enclosed = BodyOf(src, "private int ComputeHandShapeEnclosedCount()");
        Assert.Contains("PlacedNotes", enclosed);
        Assert.Contains("PlacedChords", enclosed);

        var rebinds = BodyOf(src, "private int ComputeHandShapeRebindsUntemplatedChords()");
        Assert.Contains("CountRebindableChords(_chartEditor.PlacedChords", rebinds);
    }

    /// <summary>
    ///     The three properties the markup actually binds still route through
    ///     the two counts — a fold nothing reads is a fold that stopped being
    ///     the answer on screen.
    /// </summary>
    [Fact]
    public void TheBoundLabels_StillReadTheFoldedCounts()
    {
        var src = InspectorSource();

        Assert.Contains("HandShapeRebindsUntemplatedChords > 0",
            DeclarationOf(src, "public bool HandShapeRebindsChords"));
        Assert.Contains("HandShapeRebindsUntemplatedChords",
            DeclarationOf(src, "public string HandShapeRebindWarning"));
        Assert.Contains("HandShapeEnclosedCount",
            DeclarationOf(src, "public string HandShapeEnclosedLabel"));
    }

    // ---- helpers (same shape as InspectorIssueRebuildTests) ------------------

    private static string InspectorSource()
    {
        var path = UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor", "Inspector", "HandShapeEditorViewModel.cs"));
        Assert.True(File.Exists(path), $"{path} does not exist — repoint this lint.");
        // CRLF normalized first: DeclarationOf below ends a member at a blank
        // line, and "\n\n" never matches one in a CRLF file — the lint then ran
        // on past the getter into the next member and passed vacuously.
        return SourceText.StripCommentLines(UiSourceTree.ReadAllText(path).Replace("\r\n", "\n"));
    }

    /// <summary>Everything from a member's signature up to the next blank line
    /// followed by an indented declaration — enough for a one-line expression
    /// body or a short block, without needing balanced braces.</summary>
    private static string DeclarationOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"InspectorViewModel no longer declares '{signature}' — repoint this lint.");

        var end = src.IndexOf("\n\n", start, StringComparison.Ordinal);
        return end < 0 ? src[start..] : src[start..end];
    }

    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"InspectorViewModel no longer declares '{signature}' — repoint this lint.");

        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
