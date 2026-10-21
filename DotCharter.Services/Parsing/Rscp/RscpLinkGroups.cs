using System.Globalization;
using System.Xml.Linq;
using DotCharter.Core.Linking;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     The <c>&lt;dcLinkGroups&gt;</c> root node: linked measures. Written only when the
///     project has groups, read after the arrangements (placements name them by index), and
///     VERIFIED on read (<see cref="LinkGroupVerifier" />).
///     <code>
///     &lt;dcLinkGroups v="1"&gt;
///       &lt;linkGroup id="…" letter="A" name="Chorus" rule="mc2;det;mf64;snap5;AHF" hash="…"&gt;
///         &lt;linkPlacement arr="0" p="16" ep="32"/&gt;
///       &lt;/linkGroup&gt;
///     &lt;/dcLinkGroups&gt;
///     </code>
///     <para>
///         The placements are positions (beats), like every other position in the file. The
///         notes stay in each arrangement's levels — the copies are real — so an older build
///         reads the chart in full and keeps this node verbatim as residue
///         (<c>docs/RSCP-FORMAT-GUARANTEES.md</c>); if it edits one copy, the hash catches it
///         on the next load here. <c>v</c> is the node's own format version: a reader skips a
///         node whose version it does not know (and so keeps no links from it) rather than
///         misreading it.
///     </para>
/// </summary>
public static class RscpLinkGroups
{
    public const string RootElement = "dcLinkGroups";

    /// <summary>The node format this build writes and reads.</summary>
    public const int NodeVersion = 1;

    private const string GroupElement = "linkGroup";
    private const string PlacementElement = "linkPlacement";

    public static void Write(XElement root, ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(project);
        if (project.LinkGroups.Count == 0) return;

        var node = new XElement(RootElement, new XAttribute("v", NodeVersion.ToString(CultureInfo.InvariantCulture)));
        foreach (var group in project.LinkGroups)
        {
            var live = group.Placements.Where(p => LinkRuns.IsLive(project, p)).ToList();
            if (live.Count < 2) continue;

            var element = new XElement(GroupElement,
                new XAttribute("id", group.Id),
                new XAttribute("letter", group.Letter),
                new XAttribute("name", group.Name),
                new XAttribute("rule", group.Rule));
            if (ContentHash(project, group) is { } hash) element.Add(new XAttribute("hash", hash));
            foreach (var placement in live)
            {
                element.Add(new XElement(PlacementElement,
                    new XAttribute("arr", project.Arrangements.IndexOf(placement.Arrangement!).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("p", placement.Position.ToString()),
                    new XAttribute("ep", placement.EndPosition.ToString())));
            }

            node.Add(element);
        }

        if (node.HasElements) root.Add(node);
    }

    /// <summary>
    ///     Reads the node into <paramref name="project" /> (arrangements already parsed) and runs
    ///     the load check. Returns what the check unlinked; empty when the file has no links.
    /// </summary>
    public static IReadOnlyList<LinkLoadNotice> Read(XDocument doc, ChartProject project)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(project);
        var node = doc.Root?.Element(RootElement);
        if (node == null) return [];
        if (!int.TryParse(node.Attribute("v")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version != NodeVersion)
        {
            return [];
        }

        var hashes = new Dictionary<LinkGroup, string>();
        foreach (var element in node.Elements(GroupElement))
        {
            var id = element.Attribute("id")?.Value;
            var group = new LinkGroup
            {
                Id = string.IsNullOrEmpty(id) ? LinkGroup.NewId() : id,
                Letter = element.Attribute("letter")?.Value ?? "",
                Name = element.Attribute("name")?.Value ?? "",
                Rule = element.Attribute("rule")?.Value ?? ""
            };
            foreach (var placement in element.Elements(PlacementElement))
            {
                if (!int.TryParse(placement.Attribute("arr")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    continue;
                group.Placements.Add(new LinkPlacement
                {
                    Arrangement = index < project.Arrangements.Count ? project.Arrangements[index] : null,
                    Position = RscpParsingUtilities.ParseFraction(placement.Attribute("p")?.Value),
                    EndPosition = RscpParsingUtilities.ParseFraction(placement.Attribute("ep")?.Value)
                });
            }

            if (element.Attribute("hash")?.Value is { Length: > 0 } hash) hashes[group] = hash;
            project.LinkGroups.Add(group);
        }

        return LinkGroupVerifier.Verify(project, hashes);
    }

    /// <summary>The hash of the content most placements hold, under the group's rule (ties: the earliest).</summary>
    private static string? ContentHash(ChartProject project, LinkGroup group)
    {
        if (LinkRuns.RuleOf(group) is not { } rule) return null;
        return LinkRuns.Keys(project, group, rule).Values
            .Where(k => k != null)
            .GroupBy(k => k!.Canonical, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.First()!.Hash)
            .FirstOrDefault();
    }
}
