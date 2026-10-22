using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The hand-shape suggestions and the repeat-chord set are whole-level decisions, so they are
///     computed once per level and edit by the latches the editor owns
///     (<c>ChartEditorViewModel.SharedHandShapeSuggestions</c>, <c>SharedRepeatChords</c>) and read by
///     every surface. A reader that builds a latch of its own brings back one full recompute per reader
///     per edit — the timeline, each stacked row, the song map, the hand-shape tool and the 3D driver
///     were five or six runs of a 4 ms detector for one keystroke. The counts are pinned by
///     <c>HandShapeSuggestionLatchTests</c> and <c>RepeatChordLatchTests</c>; this pins who reads what.
/// </summary>
public sealed partial class SharedEditLatchReadersTests
{
    /// <summary>Any use of a latch type, in whatever spelling: <c>new Latch()</c>, a typed field with a
    /// target-typed <c>new()</c>, a parameter. Only the editor's own file may name them.</summary>
    [GeneratedRegex(@"\b(HandShapeSuggestionLatch|RepeatChordLatch)\b")]
    private static partial Regex OwnLatch();

    [GeneratedRegex("//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [Fact]
    public void NoSurface_KeepsALatchOfItsOwn()
    {
        var root = TestPaths.RepoRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "DotCharter.UI"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "ChartEditorViewModel.cs")
            .ToList();
        Assert.True(files.Count > 200, $"only {files.Count} UI file(s) scanned — the lint is pointed at the wrong place");

        var offenders = files
            .Where(f => OwnLatch().IsMatch(LineComment().Replace(File.ReadAllText(f), string.Empty)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0, "names a latch type instead of reading the editor's: " + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("DotCharter.UI/Controls/ChartRenderer.cs", "SharedHandShapeSuggestions.Get(")]
    [InlineData("DotCharter.UI/Controls/ChartRenderer.cs", "SharedRepeatChords.Get(")]
    [InlineData("DotCharter.UI/Controls/MinimapOverviewControl.cs", "SharedHandShapeSuggestions.Get(")]
    [InlineData("DotCharter.UI/Input/Tools/HandShapeInteractionTool.cs", "SharedHandShapeSuggestions.Get(")]
    [InlineData("DotCharter.UI/Controls/Preview3DRenderDriver.cs", "SharedRepeatChords.Get(")]
    public void EverySurface_ReadsTheEditorsLatch(string file, string call)
    {
        var source = LineComment().Replace(File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), file)), string.Empty);

        Assert.Contains(call, source);
    }

    [Theory]
    [InlineData("private readonly HandShapeSuggestionLatch _suggested = new HandShapeSuggestionLatch();")]
    [InlineData("private readonly HandShapeSuggestionLatch _suggested = new();")]
    [InlineData("var latch = new RepeatChordLatch ();")]
    [InlineData("void Draw(RepeatChordLatch latch)")]
    public void Probe_APrivateLatchIsCaught(string code) => Assert.Matches(OwnLatch(), code);
}
