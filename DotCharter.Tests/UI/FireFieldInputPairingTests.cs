using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>TextBox.FireFieldInput</c> is HALF a control. It deliberately gives up
///     padding, border, background and box height so a
///     <c>Border.FireField</c> can be the frame around it — which means a bare
///     one has no box at all: the text and the caret start at x=0 with nothing
///     around them, no hover, no focus and nothing for a selection to sit in.
///     <para>
///         That is not a hypothetical. v0.44 shipped four of them: the rig's
///         lyrics editor, its NAM search box and its TONE3000 client-id field
///         wore the class with no frame, and Play Mode's song-library search bar
///         hand-copied six of the class's setters inline instead of wearing it.
///         The owner reported all of them as "text and caret too high and too
///         far left, with no padding". Nothing about it fails to build, and a
///         layout test passes straight over it, because an unframed input still
///         arranges — it just has no box.
///     </para>
///     <para>
///         So the pairing is linted here, over the real markup parsed as XML.
///         The measured half — that a framed input actually FILLS its frame, and
///         that focus lights the frame rather than drawing a rectangle inside it
///         — is <c>Perf/FireFieldUiPerfSmokeTests</c>, which needs the headless
///         app session and is therefore outside the default gate.
///     </para>
/// </summary>
public sealed class FireFieldInputPairingTests
{
    private const string Input = "FireFieldInput";
    private const string Frame = "FireField";

    /// <summary>Setters the CLASS owns. A call site that restates one is the
    /// rule-everyone-has-to-remember that produced the bug: seven views wrote
    /// <c>VerticalAlignment="Center"</c> by hand, which pinned the input to one
    /// text line inside a 36 px frame — the opposite of the fix.</summary>
    private static readonly string[] OwnedByTheClass =
    [
        "Padding", "Background", "BorderThickness", "BorderBrush", "Height",
        "VerticalAlignment", "VerticalContentAlignment", "FontFamily", "FontSize", "Foreground"
    ];

    private static IEnumerable<string> ClassesOf(XElement element) =>
        element.Attributes()
            .Where(a => a.Name.LocalName == "Classes" && a.Name.NamespaceName.Length == 0)
            .SelectMany(a => a.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool Wears(XElement element, string cls) =>
        ClassesOf(element).Contains(cls, StringComparer.Ordinal);

    private static IEnumerable<(string File, XElement Element)> Elements(string localName)
    {
        foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml", SearchOption.AllDirectories))
        {
            XDocument document;
            try { document = XDocument.Load(file); }
            catch (System.Xml.XmlException e) { throw new Xunit.Sdk.XunitException($"{file} is not well-formed XML: {e.Message}"); }

            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == localName))
                yield return (Path.GetFileName(file)!, element);
        }
    }

    private static XElement? NearestFrame(XElement input) =>
        input.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Border" && Wears(a, Frame));

    private static string Describe(string file, XElement element) =>
        $"{file}: <{element.Name.LocalName} " +
        string.Join(' ', element.Attributes()
            .Where(a => a.Name.LocalName is "Classes" or "Name" or "PlaceholderText")
            .Select(a => $"{a.Name.LocalName}=\"{a.Value}\"")) + " …>";

    // ── 1. the pairing itself ───────────────────────────────────────────────

    [Fact]
    public void EveryFireFieldInput_SitsInsideAFireFieldFrame()
    {
        var seen = 0;
        var offenders = new List<string>();

        foreach (var (file, box) in Elements("TextBox"))
        {
            if (!Wears(box, Input)) continue;
            seen++;
            if (NearestFrame(box) == null) offenders.Add(Describe(file, box));
        }

        Assert.True(seen >= 15,
            $"only {seen} TextBox.{Input} call sites were found — the scan is looking at the wrong tree, " +
            "and a lint that inspects nothing passes");
        Assert.True(offenders.Count == 0,
            $"TextBox.{Input} outside a Border.{Frame}. The class strips padding, border, background and box " +
            "height because the FRAME supplies them, so a bare one renders as text hard against x=0 with no " +
            "box, no hover, no focus and nothing for a selection to fill. Wrap it in " +
            $"<Border Classes=\"{Frame}\"> (…\"{Frame} Tall\" for a wrapping editor):\n" +
            string.Join("\n", offenders));
    }

    /// <summary>The inverse, which is how the same bug reached Play Mode: a
    /// hand-copy of the class's setters inside a frame, so the pairing lint
    /// above sees nothing to check.</summary>
    [Fact]
    public void EveryTextBoxInsideAFireFieldFrame_WearsTheClassRatherThanCopyingIt()
    {
        var frames = 0;
        var offenders = new List<string>();

        foreach (var (file, frame) in Elements("Border"))
        {
            if (!Wears(frame, Frame)) continue;
            frames++;
            foreach (var box in frame.Descendants().Where(e => e.Name.LocalName == "TextBox"))
            {
                if (!Wears(box, Input))
                    offenders.Add(Describe(file, box));
            }
        }

        Assert.True(frames >= 15, $"only {frames} Border.{Frame} frames were found — the scan found nothing to check");
        Assert.True(offenders.Count == 0,
            $"a TextBox inside a Border.{Frame} that does not wear {Input}. Play Mode's song-library search bar " +
            "restated six of the class's setters inline and got the vertical pair wrong; a second copy cannot be " +
            "fixed by fixing the class:\n" + string.Join("\n", offenders));
    }

    // ── 2. the call site may not restate what the class owns ────────────────

    [Fact]
    public void NoFireFieldInput_RestatesASetterTheClassOwns()
    {
        var offenders = new List<string>();

        foreach (var (file, box) in Elements("TextBox"))
        {
            if (!Wears(box, Input)) continue;
            foreach (var attribute in box.Attributes()
                         .Where(a => a.Name.NamespaceName.Length == 0)
                         .Where(a => OwnedByTheClass.Contains(a.Name.LocalName, StringComparer.Ordinal)))
            {
                offenders.Add($"{Describe(file, box)} — also sets {attribute.Name.LocalName}=\"{attribute.Value}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            $"a call site restating a setter TextBox.{Input} owns. Every one of these is a rule someone has to " +
            "remember, and the rule was got wrong: nine views wrote VerticalAlignment=\"Center\" by hand, which " +
            "pinned the input to a single 18 px text line inside a 36 px frame and left the focus and selection " +
            "highlights covering a strip through the middle of the field. Change the STYLE " +
            "(Styles/Fire/FireInputs.axaml), or declare a variant class:\n" + string.Join("\n", offenders));
    }

    // ── 3. a wrapping editor is a different field, and says so ──────────────

    [Fact]
    public void EveryWrappingFireFieldInput_WearsMultilineInsideATallFrame()
    {
        var multiline = 0;
        var offenders = new List<string>();

        foreach (var (file, box) in Elements("TextBox"))
        {
            if (!Wears(box, Input)) continue;
            var wraps = string.Equals(
                box.Attribute("AcceptsReturn")?.Value, "True", StringComparison.OrdinalIgnoreCase);
            if (!wraps) continue;
            multiline++;

            if (!Wears(box, "Multiline"))
            {
                offenders.Add($"{Describe(file, box)} — AcceptsReturn but no Multiline class, so the app-wide " +
                              "VerticalContentAlignment=Center centres its first line in the box");
            }

            if (NearestFrame(box) is { } frame && !Wears(frame, "Tall"))
            {
                offenders.Add($"{Describe(file, box)} — its Border.{Frame} is not .Tall, so FirePadField's zero " +
                              "vertical inset puts the first line hard against the top edge");
            }
        }

        Assert.True(multiline >= 1,
            "no wrapping FireFieldInput was found at all — this lint has nothing to look at and would pass over " +
            "the rig's lyrics editor being reverted");
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }
}
