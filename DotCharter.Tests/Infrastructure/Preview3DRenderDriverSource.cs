namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The 3D render driver's SOURCE, for every lint that reads it.
///     <para>
///         <c>Controls/Preview3DRenderDriver</c> is one class with a collaborator beside
///         it: <c>LivePlayFeedbackDrain.cs</c> (the Live Play half of the snapshot
///         staging — crowd meter, sticky EARLY/LATE + tier cues, feedback sequence and
///         the <c>IsGrade</c> guard, since #656). A lint that kept reading
///         <c>Preview3DRenderDriver.cs</c> alone would stop seeing whatever moved, and
///         every "this must NOT be there" rule about the moved code would pass
///         VACUOUSLY.
///     </para>
///     <para>
///         The set is checked both ways: a listed file that is gone, or a
///         <c>Preview3DRenderDriver*.cs</c> beside them that is not listed, throws rather
///         than shrinking what the lints see. A new split adds its file to
///         <see cref="Names" />.
///     </para>
/// </summary>
internal static class Preview3DRenderDriverSource
{
    /// <summary>The driver first, so a lint that finds the first occurrence of a member
    /// finds the driver's own.</summary>
    private static readonly string[] Names = ["Preview3DRenderDriver.cs", "LivePlayFeedbackDrain.cs"];

    /// <summary>Every file of the set, the driver first.</summary>
    public static IReadOnlyList<string> Files()
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls");
        var missing = Names.Where(n => !File.Exists(Path.Combine(dir, n))).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Preview3DRenderDriver's source set lost [{string.Join(", ", missing)}] under {dir} — the " +
                "driver moved again; repoint Preview3DRenderDriverSource.");
        }

        var unlisted = Directory.EnumerateFiles(dir, "Preview3DRenderDriver*.cs")
            .Select(f => Path.GetFileName(f))
            .Where(n => !Names.Contains(n, StringComparer.Ordinal))
            .ToList();
        if (unlisted.Count > 0)
        {
            throw new InvalidOperationException(
                $"{dir} holds [{string.Join(", ", unlisted)}], which Preview3DRenderDriverSource does not list — " +
                "add it, or the driver lints never read it.");
        }

        return Names.Select(n => Path.Combine(dir, n)).ToList();
    }

    /// <summary>The set as one text, the driver first.</summary>
    public static string ReadAllText() => string.Join('\n', Files().Select(File.ReadAllText));
}
