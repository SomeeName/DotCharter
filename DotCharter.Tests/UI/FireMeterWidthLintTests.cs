using System.Globalization;
using System.Xml.Linq;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI;

/// <summary>
///     A <see cref="FireLevelMeter" /> given a fixed <c>Width</c> must be wide enough for the
///     segments it DRAWS: its Render paints every segment from x=0 whatever its bounds, so a
///     14-segment meter (82 px at the defaults) in a 72 px slot overdrew 10 px into its neighbour
///     (the rig's input and output meters, 2026-09-24). See the drawn-control trap in
///     docs/agents/ui/layout-traps.md.
/// </summary>
public sealed class FireMeterWidthLintTests
{
    [Fact]
    public void EveryFixedWidthMeter_FitsTheSegmentsItDraws()
    {
        var views = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views"));
        var offenders = new List<string>();
        var checkedCount = 0;
        foreach (var file in UiSourceTree.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var meter in doc.Descendants().Where(e => e.Name.LocalName == "FireLevelMeter"))
            {
                if (meter.Attribute("Width") is not { } width) continue;
                checkedCount++;
                var count = Int(meter, "SegmentCount", FireLevelScale.DefaultSegmentCount);
                var bar = Num(meter, "BarWidth", FireLevelScale.DefaultBarWidth);
                var gap = Num(meter, "BarGap", FireLevelScale.DefaultBarGap);
                var drawn = FireLevelScale.RowWidth(count, bar, gap);
                var slot = double.Parse(width.Value, CultureInfo.InvariantCulture);
                if (drawn > slot + 0.5)
                {
                    offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)meter).LineNumber} " +
                                  $"{(string?)meter.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? "(unnamed)"}: " +
                                  $"{count} segments draw {drawn:0.#} px in a {slot:0.#} px slot");
                }
            }
        }

        Assert.True(checkedCount >= 2, $"only {checkedCount} fixed-width meters found — the scan no longer reaches them");
        Assert.True(offenders.Count == 0, LintReport.Of(
            "A FireLevelMeter with a fixed Width draws past it: Render paints every segment from x=0 whatever its bounds, " +
            "so the extra pixels land on the neighbouring control.",
            offenders,
            "widen Width to at least the drawn width, or lower SegmentCount / BarWidth / BarGap until the row fits.",
            "docs/agents/ui/layout-traps.md (a drawn Fire control)"));
    }

    private static int Int(XElement e, string name, int fallback) =>
        e.Attribute(name) is { } a ? int.Parse(a.Value, CultureInfo.InvariantCulture) : fallback;

    private static double Num(XElement e, string name, double fallback) =>
        e.Attribute(name) is { } a ? double.Parse(a.Value, CultureInfo.InvariantCulture) : fallback;
}
