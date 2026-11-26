using DotCharter.Services.Export.Rocksmith;

namespace DotCharter.Services.Export.DlcBuilder;

/// <summary>Which DLC Builder project a <c>.rs2dlc</c> is.</summary>
public enum Rs2DlcEdition
{
    /// <summary>The one project of a chart without RS Extended Range paths: every arrangement.</summary>
    Single,

    /// <summary>
    ///     The project a stock DLC Builder builds, beside <see cref="ExtendedRange" />: six-string guitar
    ///     and four-string bass paths, and the six-string projection of a 7-/8-string guitar without a stock
    ///     twin (<see cref="Rs2DlcEditions.HasStockTwin" />). Five-/six-string basses are left out (DLC Builder's I36).
    /// </summary>
    Standard,

    /// <summary>The RS Extended Range project: every arrangement, the extended ones at their full width.</summary>
    ExtendedRange
}

/// <summary>An arrangement as one edition carries it: the bundle file and whether it is the six-string projection.</summary>
public readonly record struct Rs2DlcEntry(string Xml, bool Projected);

/// <summary>
///     The split of a DLC Builder export into a standard and an RS Extended Range project, mirroring the
///     <c>_p</c> / <c>_rsxr_p</c> packages. One place decides which arrangement goes in which project and
///     which bundle file it reads, so the bundle writer writes exactly the files the projects name.
/// </summary>
/// <remarks>
///     The bundle's own file for an arrangement (<c>ArrangementFiles[i]</c>) follows
///     <c>AppConfig.RsXmlSixStringProjection</c> as it always has. When an edition needs the other form of
///     a 7-/8-string guitar, it reads a sibling file: <c>_6String</c> for the projection, <c>_RSXR</c> for the
///     full width. Stock arrangements, vocals and showlights are one file shared by both projects.
/// </remarks>
public static class Rs2DlcEditions
{
    /// <summary>The marker the RS Extended Range project's file name carries, as the package's does.</summary>
    public const string ExtendedRangeTag = "_rsxr";

    private const string XmlSuffix = "_RS2.xml";

    /// <summary>The editions a project is written as: two when it has RS Extended Range paths, else one.</summary>
    public static IReadOnlyList<Rs2DlcEdition> For(ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return ExtendedRangePackageName.IsPsxr(project.Arrangements)
            ? [Rs2DlcEdition.Standard, Rs2DlcEdition.ExtendedRange]
            : [Rs2DlcEdition.Single];
    }

    /// <summary><c>Artist-Title_rsxr.rs2dlc</c> for the RS Extended Range edition; the path itself otherwise.</summary>
    public static string PathFor(string rs2dlcPath, Rs2DlcEdition edition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rs2dlcPath);
        if (edition != Rs2DlcEdition.ExtendedRange) return rs2dlcPath;
        var directory = Path.GetDirectoryName(rs2dlcPath) ?? "";
        return Path.Combine(directory, Path.GetFileNameWithoutExtension(rs2dlcPath) + ExtendedRangeTag + Path.GetExtension(rs2dlcPath));
    }

    /// <summary>
    ///     How <paramref name="edition" /> carries arrangement <paramref name="index" />, or null when it leaves it
    ///     out. <paramref name="sixStringProjection" /> is the setting the bundle's own file follows.
    /// </summary>
    public static Rs2DlcEntry? Entry(ChartProject project, int index, Rs2DlcEdition edition, bool sixStringProjection)
    {
        ArgumentNullException.ThrowIfNull(project);
        var arrangement = project.Arrangements[index];
        if (arrangement.ArrangementType == ArrangementType.Vocal) return null;
        var manifest = index < project.ArrangementFiles.Count
            ? project.ArrangementFiles[index]
            : DlcBuilderBundle.ArrangementFileName(index + 1, arrangement);
        var extendedGuitar = Rs7StringLayout.IsExtendedGuitar(arrangement);

        switch (edition)
        {
            case Rs2DlcEdition.Standard when Rs7StringLayout.IsExtendedBass(arrangement):
                return null;
            case Rs2DlcEdition.Standard when extendedGuitar:
                return HasStockTwin(project, arrangement) || RsxrStringLayout.XmlExportRefusal(arrangement, true) != null
                    ? null
                    : new Rs2DlcEntry(FileFor(manifest, sixStringProjection, projected: true), true);
            case Rs2DlcEdition.ExtendedRange when extendedGuitar:
                return RsxrStringLayout.ExportRefusal(arrangement) != null
                    ? null
                    : new Rs2DlcEntry(FileFor(manifest, sixStringProjection, projected: false), false);
            default:
                return RsxrStringLayout.XmlExportRefusal(arrangement, sixStringProjection) != null
                    ? null
                    : new Rs2DlcEntry(manifest, sixStringProjection && extendedGuitar);
        }
    }

    /// <summary>
    ///     Removes the <c>_rsxr</c> project an earlier save or export left beside <paramref name="rs2dlcPath" />
    ///     when <paramref name="project" /> has no RS Extended Range paths, through
    ///     <paramref name="recycle" />, and forgets its stamp. Only a file <see cref="CompanionOwnership" /> says we
    ///     wrote and nobody edited since goes; an absent or unreadable stamp leaves it alone. The XML it read lived in
    ///     the bundle, which every write replaces whole. Returns whether it was removed.
    /// </summary>
    internal static bool RemoveStaleExtendedRangeProject(ChartProject project, string rs2dlcPath, Action<string> recycle)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(recycle);
        if (For(project).Contains(Rs2DlcEdition.ExtendedRange)) return false;
        var stale = PathFor(rs2dlcPath, Rs2DlcEdition.ExtendedRange);
        if (!File.Exists(stale) || !CompanionOwnership.IsUnmodified(stale)) return false;
        recycle(stale);
        if (File.Exists(stale)) return false;
        CompanionOwnership.Forget(stale);
        return true;
    }

    /// <summary>
    ///     The 7-/8-string guitar files the editions of <paramref name="project" /> read besides the bundle's own
    ///     one for arrangement <paramref name="index" />: none for any other arrangement or a single project.
    /// </summary>
    public static IEnumerable<Rs2DlcEntry> SiblingFiles(ChartProject project, int index, bool sixStringProjection)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Rs7StringLayout.IsExtendedGuitar(project.Arrangements[index])) yield break;
        var editions = For(project);
        var own = Entry(project, index, Rs2DlcEdition.Single, sixStringProjection)?.Xml;
        foreach (var edition in editions)
        {
            if (edition == Rs2DlcEdition.Single) continue;
            if (Entry(project, index, edition, sixStringProjection) is { } entry && entry.Xml != own) yield return entry;
        }
    }

    /// <summary>
    ///     In the RS Extended Range project a stock Main on a path that also has an extended Main is
    ///     Alternate, as in the <c>_rsxr</c> package, so each path keeps one Main: the extended one.
    /// </summary>
    public static bool DemotedToAlternate(ChartProject project, Arrangement arrangement, Rs2DlcEdition edition)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        return edition == Rs2DlcEdition.ExtendedRange
               && arrangement.Subtype == ArrangementSubtype.Main
               && !Rs7StringLayout.IsExtended(arrangement)
               && project.Arrangements.Any(a => a.ArrangementType == arrangement.ArrangementType
                                                && a.Subtype == ArrangementSubtype.Main
                                                && Rs7StringLayout.IsExtended(a)
                                                && RsxrStringLayout.ExportRefusal(a) == null);
    }

    /// <summary>
    ///     Whether the 7-/8-string guitar <paramref name="arrangement" /> has a stock twin, which the standard
    ///     project carries instead of its projection: a stock arrangement (six strings or fewer, a supported
    ///     count) of the same type whose name starts with the extended one's, trimmed and case-insensitive
    ///     ("Doloris" and "Doloris (6 String)"). Each stock arrangement is the twin of at most one extended
    ///     guitar; the longest extended name pairs first, so "Lead 2" keeps "Lead 2 (6 String)" from "Lead".
    ///     An unrelated stock arrangement of the same type is no twin.
    /// </summary>
    public static bool HasStockTwin(ChartProject project, Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangement);
        if (!Rs7StringLayout.IsExtendedGuitar(arrangement)) return false;
        var paired = new List<Arrangement>();
        var extended = project.Arrangements
            .Where(a => Rs7StringLayout.IsExtendedGuitar(a) && !string.IsNullOrWhiteSpace(a.Name))
            .OrderByDescending(a => a.Name.Trim().Length);
        foreach (var candidate in extended)
        {
            var name = candidate.Name.Trim();
            var twin = project.Arrangements.FirstOrDefault(a => a.ArrangementType == candidate.ArrangementType
                                                                && !Rs7StringLayout.IsExtended(a)
                                                                && !Rs7StringLayout.HasUnsupportedStringCount(a)
                                                                && !paired.Any(p => ReferenceEquals(p, a))
                                                                && !string.IsNullOrEmpty(a.Name) && a.Name.Trim().StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (twin is null) continue;
            if (ReferenceEquals(candidate, arrangement)) return true;
            paired.Add(twin);
        }
        return false;
    }

    /// <summary>The bundle's own file when it already has the wanted form, else its sibling.</summary>
    private static string FileFor(string manifest, bool sixStringProjection, bool projected)
    {
        if (projected == sixStringProjection) return manifest;
        var tag = projected ? "_6String" : "_RSXR";
        return manifest.EndsWith(XmlSuffix, StringComparison.OrdinalIgnoreCase)
            ? manifest[..^XmlSuffix.Length] + tag + XmlSuffix
            : Path.GetFileNameWithoutExtension(manifest) + tag + Path.GetExtension(manifest);
    }
}
