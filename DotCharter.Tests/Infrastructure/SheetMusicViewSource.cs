namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The sheet strip's SOURCE, for every lint that reads it.
///     <para>
///         <c>Controls/Sheet/SheetMusicView</c> is one class with its collaborators beside it:
///         <c>SheetMusicViewLayers.cs</c> (the chunk canvas, <c>SheetOverlayLayer</c> and its
///         three overlays, the Skia draw op, since #595/#655) and <c>SheetMusicViewInput.cs</c>
///         (the pointer gestures and GP-style keys, since #655). A lint that kept reading
///         <c>SheetMusicView.cs</c> alone would stop seeing whatever moved, and every "this
///         must NOT be there" rule about the moved code would pass VACUOUSLY.
///     </para>
///     <para>
///         The set is checked both ways: a listed file that is gone, or a
///         <c>SheetMusicView*.cs</c> beside them that is not listed, throws rather than
///         shrinking what the lints see. A new split adds its file to <see cref="Names" />.
///     </para>
/// </summary>
internal static class SheetMusicViewSource
{
    /// <summary>The view first, so a lint that finds the first occurrence of a member finds
    /// the view's own.</summary>
    private static readonly string[] Names = ["SheetMusicView.cs", "SheetMusicViewLayers.cs", "SheetMusicViewInput.cs"];

    /// <summary>Every file of the set, the view first.</summary>
    public static IReadOnlyList<string> Files()
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Controls", "Sheet");
        var missing = Names.Where(n => !File.Exists(Path.Combine(dir, n))).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"SheetMusicView's source set lost [{string.Join(", ", missing)}] under {dir} — the sheet view " +
                "moved again; repoint SheetMusicViewSource.");
        }

        var unlisted = Directory.EnumerateFiles(dir, "SheetMusicView*.cs")
            .Select(f => Path.GetFileName(f))
            .Where(n => !Names.Contains(n, StringComparer.Ordinal))
            .ToList();
        if (unlisted.Count > 0)
        {
            throw new InvalidOperationException(
                $"{dir} holds [{string.Join(", ", unlisted)}], which SheetMusicViewSource does not list — add it, " +
                "or the sheet lints never read it.");
        }

        return Names.Select(n => Path.Combine(dir, n)).ToList();
    }

    /// <summary>The set as one text, the view first.</summary>
    public static string ReadAllText() => string.Join('\n', Files().Select(File.ReadAllText));
}
