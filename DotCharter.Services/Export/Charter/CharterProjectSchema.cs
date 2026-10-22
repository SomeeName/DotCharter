using System.Xml.Linq;

namespace DotCharter.Services.Export.Charter;

/// <summary>
///     One node of Charter's project vocabulary: the attributes it reads and the child
///     elements it maps, each child with its own node. A node with neither is a text leaf.
/// </summary>
public sealed class CharterNode(IEnumerable<string>? attributes = null, IReadOnlyDictionary<string, CharterNode>? children = null)
{
    public static readonly CharterNode Text = new();

    public IReadOnlySet<string> Attributes { get; } = new HashSet<string>(attributes ?? [], StringComparer.Ordinal);

    public IReadOnlyDictionary<string, CharterNode> Children { get; } =
        children ?? new Dictionary<string, CharterNode>(StringComparer.Ordinal);
}

/// <summary>
///     The allow-list of everything Charter's <c>.rscp</c> reader knows, as a tree rooted at
///     <c>&lt;project&gt;</c>. The Charter companion (<see cref="CharterRscpProjection" />) keeps
///     exactly what this tree names and drops the rest, because Charter reads the file with
///     XStream: the reflection-mapped classes skip unknown elements, but the hand-written
///     converters for notes, chords, beats and the rest read fixed names, and DotCharter's
///     extensions (<c>dc*</c> nodes, the extra attributes on known elements) mean nothing there.
///     <para>
///         Read from Charter <see cref="CharterVersion" />, source tree
///         <c>src/main/java/log/charter</c>: <c>io/rsc/xml/ChartProject.java</c> and
///         <c>ChartProjectXStreamHandler.java</c>; the model classes it includes
///         (<c>data/song/Arrangement, Level, Beat, Stem, Showlight, EventPoint, Phrase,
///         ToneChange, ChordTemplate, HandShape, FHP, configs/Tuning, notes/ChordOrNote, Note,
///         Chord, vocals/VocalPath, Vocal</c>) with their <c>@XStreamAlias</c> /
///         <c>@XStreamAsAttribute</c> fields; and the converters in
///         <c>io/rsc/xml/converters</c> for the names they write and read. Names a converter
///         reads only from pre-v3 files (<c>position</c>, <c>length</c>, <c>lyric</c>,
///         <c>toneName</c>) are listed too: Charter knows them, and they cost nothing.
///         When Charter's model changes, re-read those files and update this tree with the
///         version named above.
///     </para>
/// </summary>
public static class CharterProjectSchema
{
    /// <summary>The Charter release this allow-list was read from.</summary>
    public const string CharterVersion = "V0.22.44";

    /// <summary>The root element's name.</summary>
    public const string RootName = "project";

    /// <summary>The <c>&lt;project&gt;</c> node.</summary>
    public static CharterNode Root { get; } = BuildRoot();

    /// <summary>
    ///     Every element and attribute of <paramref name="doc" /> this tree does not name,
    ///     as <c>project/arrangements/arrangement/@manual</c>-style paths. Empty for a
    ///     document Charter knows completely.
    /// </summary>
    public static IReadOnlyList<string> Violations(XDocument doc)
    {
        var found = new List<string>();
        if (doc.Root is not { } root) return found;
        if (root.Name.LocalName != RootName)
        {
            found.Add(root.Name.LocalName);
            return found;
        }

        Walk(root, Root, RootName, found);
        return found;
    }

    private static void Walk(XElement element, CharterNode node, string path, List<string> found)
    {
        foreach (var attribute in element.Attributes())
        {
            if (!IsKnown(attribute, node))
                found.Add($"{path}/@{attribute.Name}");
        }

        foreach (var child in element.Elements())
        {
            var childPath = $"{path}/{child.Name}";
            if (ChildOf(node, child) is { } childNode)
                Walk(child, childNode, childPath, found);
            else
                found.Add(childPath);
        }
    }

    /// <summary>Whether Charter reads <paramref name="attribute" /> on an element of <paramref name="node" />.</summary>
    internal static bool IsKnown(XAttribute attribute, CharterNode node) =>
        !attribute.IsNamespaceDeclaration && attribute.Name.Namespace == XNamespace.None &&
        node.Attributes.Contains(attribute.Name.LocalName);

    /// <summary>The node Charter maps <paramref name="child" /> to under <paramref name="node" />, or null.</summary>
    internal static CharterNode? ChildOf(CharterNode node, XElement child) =>
        child.Name.Namespace == XNamespace.None && node.Children.TryGetValue(child.Name.LocalName, out var found) ? found : null;

    private static CharterNode Leaf(params string[] attributes) => new(attributes);

    private static CharterNode Node(string[] attributes, params (string Name, CharterNode Node)[] children) =>
        new(attributes, children.ToDictionary(c => c.Name, c => c.Node, StringComparer.Ordinal));

    private static CharterNode ListOf(params (string Name, CharterNode Node)[] items) => Node([], items);

    private static CharterNode BuildRoot()
    {
        var text = CharterNode.Text;
        var stringList = ListOf(("string", text));

        // ChartProject.stems: List<Stem>, every field an attribute.
        var stems = ListOf(("stem", Leaf("name", "path", "local", "volume", "offset")));

        // BeatConverter writes <beat2 t sig bar anchor>; it still reads the older <beat>.
        var beats = ListOf(
            ("beat2", Leaf("t", "sig", "bar", "anchor")),
            ("beat", Leaf("position", "beatsInMeasure", "noteDenominator", "anchor")));

        var showlights = ListOf(("showlight", Leaf("p", "t")));

        // VocalPath (alias "vocals"): name, a java.awt.Color, and the List<Vocal> also called "vocals".
        var color = Node([], ("red", text), ("green", text), ("blue", text), ("alpha", text));
        var vocalList = ListOf(("vocal", Leaf("p", "ep", "text", "flag", "note", "position", "length", "lyric")));
        var vocalPath = Node([], ("name", text), ("color", color), ("vocals", vocalList));

        var level = Node([],
            ("anchors", ListOf(("anchor", Leaf("p", "fret", "width", "position")))),
            ("sounds", ListOf(
                ("soundNote", Leaf("p", "ep", "string", "fret", "accent", "ignore", "crazy", "bassPicking", "mute",
                    "hopo", "harmonic", "vibrato", "tremolo", "linkNext", "slideTo", "unpitchedSlide", "bendValues",
                    "position", "length")),
                ("soundChord", Node(["p", "templateId", "splitIntoNotes", "forceNoNotes", "accent", "ignore", "crazy",
                        "position"],
                    ("chordNote", Leaf("string", "ep", "mute", "hopo", "harmonic", "vibrato", "tremolo", "linkNext",
                        "slideTo", "unpitchedSlide", "bendValues", "length")))))),
            ("handShapes", ListOf(("handShape", Leaf("p", "ep", "templateId", "position", "length")))));

        // Arrangement is reflection-converted: its @XStreamAsAttribute fields are
        // attributes (ArrangementConverter also reads a legacy baseTone), the rest elements.
        // `tones` is a Set<String>; XStream's `class` type hint on it is understood.
        var arrangement = Node(["type", "subtype", "capo", "centOffset", "startingTone", "pickedBass", "ignore", "baseTone"],
            ("tuning", Leaf("type", "strings", "tuningValues")),
            ("eventPoints", ListOf(("eventPoint", Leaf("p", "section", "phrase", "events", "position")))),
            ("phrases", ListOf(("phrase", Leaf("name", "maxDifficulty", "solo")))),
            ("tones", Node(["class"], ("string", text))),
            ("toneChanges", ListOf(("toneChange", Leaf("p", "tone", "position", "toneName")))),
            ("chordTemplates", ListOf(("chordTemplate", Leaf("chordName", "arpeggio", "forceRS", "fingers", "frets")))),
            ("levels", ListOf(("level", level))));

        return Node([],
            ("chartFormatVersion", text),
            ("editMode", text),
            ("arrangement", text),
            ("level", text),
            ("time", text),
            ("artistName", text),
            ("artistNameSort", text),
            ("title", text),
            ("albumName", text),
            ("albumYear", text),
            ("musicFileName", text),
            ("stems", stems),
            ("selectedStem", text),
            ("arrangementFiles", stringList),
            ("beats", beats),
            ("mainAudioOffsetAdded", text),
            ("barsAdded", text),
            ("showlights", showlights),
            ("vocals", vocalPath),
            ("vocalPaths", ListOf(("vocals", vocalPath))),
            ("arrangements", ListOf(("arrangement", arrangement))),
            ("bookmarks", ListOf(("entry", Leaf("key", "value")))),
            ("text", text));
    }
}
