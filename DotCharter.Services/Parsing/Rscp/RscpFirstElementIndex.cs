using System.Xml.Linq;

namespace DotCharter.Services.Parsing.Rscp;

/// <summary>
///     One pre-order pass over a <c>.dcproj</c> document that records the FIRST
///     element carrying each of a known set of names, so the header parser can
///     answer thirty <c>doc.Descendants(name).FirstOrDefault()</c> questions with
///     thirty dictionary lookups.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> <see cref="XContainer.Descendants(XName)" /> is
///         a lazy pre-order walk of the WHOLE tree filtered by name — not a
///         root-child lookup. A name the document does not carry costs a full
///         walk, and so does any name the writer emits AFTER
///         <c>&lt;arrangements&gt;</c> (<c>bookmarks</c>, <c>dcAnnotations</c>,
///         <c>dcToneDefinitions</c>, <c>text</c>), because the walk has to step
///         past every note, chord, hand shape and anchor in the project first.
///         <c>RscpHeaderParser</c> asked thirty such questions independently.
///     </para>
///     <para>
///         <b>Why it is exactly equivalent.</b> The pass enumerates
///         <c>doc.Descendants()</c>, which is the same pre-order document-order
///         sequence <c>Descendants(name)</c> filters, and keeps the FIRST hit per
///         name — which is what <c>FirstOrDefault()</c> returns. It deliberately
///         does NOT restrict itself to root children: a header name nested
///         somewhere inside an arrangement resolves here exactly as it did
///         before. The walk stops as soon as every requested name has been seen,
///         so a well-formed project (whose header names are all near the top)
///         never reaches the note data at all.
///     </para>
/// </remarks>
internal sealed class RscpFirstElementIndex
{
    private readonly Dictionary<XName, XElement> _first;

    private RscpFirstElementIndex(Dictionary<XName, XElement> first) => _first = first;

    /// <summary>Indexes the first element for each name in <paramref name="names" />.</summary>
    public static RscpFirstElementIndex Build(XDocument doc, IReadOnlyCollection<string> names)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(names);

        var wanted = new HashSet<XName>(names.Select(XName.Get));
        var first = new Dictionary<XName, XElement>(wanted.Count);

        foreach (var element in doc.Descendants())
        {
            if (!wanted.Contains(element.Name)) continue;
            if (!first.TryAdd(element.Name, element)) continue;
            // Everything asked for has been seen; the rest of the document
            // cannot change any answer.
            if (first.Count == wanted.Count) break;
        }

        return new RscpFirstElementIndex(first);
    }

    /// <summary>
    ///     The first element with this name in document order, or null — exactly
    ///     what <c>doc.Descendants(name).FirstOrDefault()</c> returned. Only names
    ///     passed to <see cref="Build" /> are answered; anything else is null,
    ///     which is why the name set lives beside the parser that uses it.
    /// </summary>
    public XElement? First(string name) => _first.GetValueOrDefault(XName.Get(name));

    /// <summary>The first such element's <see cref="XElement.Value" />, or null.</summary>
    public string? Value(string name) => First(name)?.Value;
}
