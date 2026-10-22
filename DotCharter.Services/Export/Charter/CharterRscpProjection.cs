using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Services.Export.Charter;

/// <summary>
///     What the Charter projection dropped: one count per element or attribute name
///     (<c>dcToneDefinitions</c>, <c>arrangement/@manual</c>), so the log names WHAT
///     DotCharter-only data the companion cannot carry, not just how much.
/// </summary>
public sealed class CharterDropReport
{
    private readonly SortedDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, int> Counts => _counts;

    public bool IsEmpty => _counts.Count == 0;

    internal void Add(string key) => _counts[key] = _counts.GetValueOrDefault(key) + 1;

    /// <summary>"dcAnnotations ×1, arrangement/@manual ×3", or "nothing".</summary>
    public string LogLine() =>
        IsEmpty ? "nothing" : string.Join(", ", _counts.Select(kv => $"{kv.Key} ×{kv.Value}"));
}

/// <summary>
///     Turns DotCharter's project XML into the Charter-compatible <c>.rscp</c> companion:
///     the same <see cref="RscpWriter" /> document, cut down to
///     <see cref="CharterProjectSchema" />. Every <c>dc*</c> node, every attribute Charter does
///     not read and every element outside its model is removed; what is left is the
///     RsCharter v3 layout Charter itself writes (<c>chartFormatVersion</c> 3).
/// </summary>
public static class CharterRscpProjection
{
    /// <summary>Builds the companion document for <paramref name="snapshot" />.</summary>
    public static XDocument Build(ChartProject snapshot, out CharterDropReport dropped)
    {
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, snapshot);
        dropped = Project(doc);
        return doc;
    }

    /// <summary>Strips <paramref name="doc" /> in place to what Charter knows, and says what went.</summary>
    public static CharterDropReport Project(XDocument doc)
    {
        var report = new CharterDropReport();
        if (doc.Root is not { } root) return report;
        Strip(root, CharterProjectSchema.Root, report);
        return report;
    }

    private static void Strip(XElement element, CharterNode node, CharterDropReport report)
    {
        foreach (var attribute in element.Attributes().ToList())
        {
            if (CharterProjectSchema.IsKnown(attribute, node)) continue;
            if (!attribute.IsNamespaceDeclaration) report.Add($"{element.Name.LocalName}/@{attribute.Name.LocalName}");
            attribute.Remove();
        }

        foreach (var child in element.Elements().ToList())
        {
            if (CharterProjectSchema.ChildOf(node, child) is { } childNode)
            {
                Strip(child, childNode, report);
                continue;
            }

            report.Add(element.Parent is null ? child.Name.LocalName : $"{element.Name.LocalName}/{child.Name.LocalName}");
            child.Remove();
        }
    }
}
