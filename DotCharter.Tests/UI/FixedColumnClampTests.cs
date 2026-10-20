using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     Avalonia CLAMPS a measured width into <c>[MinWidth, MaxWidth]</c>, and
///     several Fluent control themes ship a `MinWidth` big enough to defeat a
///     fixed grid column. The result is a control that renders WIDER than the
///     cell it was put in and draws over its neighbour — from a clean build,
///     with no warning, and with every bounds assertion still passing because
///     the control itself measured fine.
///
///     <para>
///         Two shipped this way and are guarded here:
///         <list type="bullet">
///             <item>Fluent's <c>ProgressBar</c> carries <c>MinWidth="200"</c>.
///             Every Fire meter lives in a fixed 80–110 px value column, so the
///             Stats weak-spot rows drew the amber track straight through
///             "100 %".</item>
///             <item>Fluent's <c>TextBox</c> carries <c>MinWidth="64"</c>. The
///             arrangement dialog's tuning fields asked for 48 and rendered at
///             64, so a six-string tuning wrapped 4 + 2 inside its 300 px
///             column.</item>
///         </list>
///     </para>
///     <para>
///         Source-level and platform-free, so it runs in the default gate. The
///         runtime counterparts are the two construction smoke suites, which
///         measure the columns.
///     </para>
/// </summary>
public sealed class FixedColumnClampTests
{
    /// <summary>Every Fire meter is placed in a fixed value column, so the
    /// shared style must cancel Fluent's 200 px floor exactly once, for all of
    /// them.</summary>
    [Fact]
    public void TheFireMeter_CancelsFluentsMinWidthFloor()
    {
        // Fire.axaml is a CONTAINER over `Styles/Fire/` — one XDocument of it
        // holds seven include lines and none of the grammar.
        var elements = StyleFileText.ElementsWithIncludes(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "Fire.axaml"));

        var style = elements
            .SingleOrDefault(e => e.Name.LocalName == "Style" &&
                                  (string?)e.Attribute("Selector") == "ProgressBar.FireMeter");
        Assert.True(style != null,
            "the `ProgressBar.FireMeter` style was renamed — this guard no longer covers any meter");

        var minWidth = style!.Elements()
            .Where(e => e.Name.LocalName == "Setter")
            .FirstOrDefault(e => (string?)e.Attribute("Property") == "MinWidth");

        Assert.True(minWidth != null,
            "ProgressBar.FireMeter has no MinWidth setter: Fluent's theme floors it at 200 px, so every " +
            "meter in a fixed value column will render wider than its cell and draw over the figure " +
            "beside it");
        Assert.Equal("0", ((string?)minWidth!.Attribute("Value"))?.Trim());
    }

    /// <summary>A `Width` smaller than 64 on a TextBox is a request Fluent
    /// silently refuses. Any such field must cancel the floor itself.</summary>
    [Fact]
    public void ANarrowTextBox_AlsoCancelsTheFloor()
    {
        var offenders = new List<string>();

        foreach (var file in UiSourceTree.EnumerateFiles(
                     Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "*.axaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch { continue; }

            offenders.AddRange(NarrowBoxesKeepingTheFloor(doc).Select(w => $"{Path.GetFileName(file)}: TextBox Width=\"{w}\""));
        }

        // Anti-vacuity by PROBE: the arrangement dialog's tuning boxes were the last narrow
        // fixed-width field in the tree and became stretched grid cells (2026-09-22), so the
        // check proves it still sees one on a fixture instead of needing one to exist.
        var probe = XDocument.Parse(
            "<UserControl><TextBox Width=\"42\" /><TextBox Width=\"42\" MinWidth=\"0\" /><TextBox Width=\"80\" /></UserControl>");
        Assert.Equal([42.0], NarrowBoxesKeepingTheFloor(probe));

        Assert.True(offenders.Count == 0,
            $"Avalonia clamps Width up to MinWidth, and Fluent's TextBox floor is {FluentTextBoxMinWidth} px. " +
            "These fields ask for less and will silently render at the floor — add MinWidth=\"0\":\n  " +
            string.Join("\n  ", offenders));
    }

    private const double FluentTextBoxMinWidth = 64;

    /// <summary>The Width of every TextBox asking for less than the floor without cancelling it.</summary>
    private static List<double> NarrowBoxesKeepingTheFloor(XDocument doc)
    {
        var widths = new List<double>();
        foreach (var box in doc.Descendants().Where(e => e.Name.LocalName == "TextBox"))
        {
            if (!double.TryParse((string?)box.Attribute("Width"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var width))
            {
                continue;
            }

            if (width < FluentTextBoxMinWidth && box.Attribute("MinWidth") == null) widths.Add(width);
        }

        return widths;
    }
}
