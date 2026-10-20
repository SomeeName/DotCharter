using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>TextWrapping="Wrap"</c> is a NO-OP inside a horizontal
///     <c>StackPanel</c>. The panel measures every child with infinite width,
///     so the text never reaches a wrap point — it runs straight off the edge
///     of whatever contains the panel and is sliced mid-word, with no warning,
///     no exception and no failing bounds assertion (the TextBlock measured
///     fine; it is the CONTAINER that clipped it).
///
///     <para>
///         This shipped twice on one screen: the LAN modal's firewall hint ran
///         off the card, and its footer's subnet list ran straight through the
///         Close button. Both were invisible in English and obvious the moment
///         the app ran in German, which is ~40 % longer.
///     </para>
///     <para>
///         The rule: a wrapping TextBlock inside a horizontal StackPanel must
///         carry its own width bound (<c>MaxWidth</c> or <c>Width</c>), or the
///         panel should be a <c>Grid</c> with a star column. Source-level and
///         platform-free, so it runs in the default gate.
///     </para>
/// </summary>
public sealed class WrappingTextLayoutTests
{
    [Fact]
    public void NoWrappingTextBlock_SitsUnboundedInAHorizontalStackPanel()
    {
        var offenders = new List<string>();
        var scanned = 0;
        var wrappingAnywhere = 0;

        foreach (var file in ViewFiles())
        {
            scanned++;
            XDocument doc;
            try
            {
                doc = XDocument.Load(file);
            }
            catch (Exception ex)
            {
                Assert.Fail($"{Path.GetFileName(file)} is not parseable as XML: {ex.Message}");
                return;
            }

            wrappingAnywhere += doc.Descendants().Count(e =>
                e.Name.LocalName == "TextBlock" &&
                (string?)e.Attribute("TextWrapping") is "Wrap" or "WrapWithOverflow");

            foreach (var panel in doc.Descendants()
                         .Where(e => e.Name.LocalName == "StackPanel" &&
                                     (string?)e.Attribute("Orientation") == "Horizontal"))
            {
                foreach (var text in panel.Elements().Where(e => e.Name.LocalName == "TextBlock"))
                {
                    var wrap = (string?)text.Attribute("TextWrapping");
                    if (wrap is not ("Wrap" or "WrapWithOverflow")) continue;

                    if (text.Attribute("MaxWidth") != null || text.Attribute("Width") != null) continue;

                    offenders.Add(
                        $"{Path.GetFileName(file)}: TextWrapping=\"{wrap}\" on " +
                        $"Text=\"{Truncate((string?)text.Attribute("Text"))}\"");
                }
            }
        }

        // Anti-vacuity: without these the scan could silently stop finding
        // files (or stop finding wrapping text at all) and still pass.
        Assert.True(scanned > 40, $"only {scanned} .axaml files scanned — the walk lost the view tree");
        // The last horizontal-panel wrapper went with the LAN lobby (2026-09-19),
        // so the attribute-name half is proved on wrapping text anywhere.
        Assert.True(wrappingAnywhere > 0, "no wrapping TextBlock was found at all — the attribute names " +
                                          "changed and this guard is blind");

        Assert.True(offenders.Count == 0,
            "TextWrapping cannot fire inside a horizontal StackPanel (it measures with infinite width). " +
            "Give the TextBlock a MaxWidth, or make the panel a Grid with a star column:\n  " +
            string.Join("\n  ", offenders));
    }

    private static string Truncate(string? value)
        => value is null ? "" : value.Length <= 48 ? value : value[..48] + "…";

    private static IEnumerable<string> ViewFiles()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI");
        foreach (var file in UiSourceTree.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }
}
