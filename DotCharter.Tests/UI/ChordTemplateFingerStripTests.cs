using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Data;
using DotCharter.UI.Converters;

namespace DotCharter.Tests.UI;

/// <summary>
///     The per-string FINGER control in the chord-template editor is a segmented
///     strip, not a dropdown.
///     <para>
///         Six options that are each one character do not earn a popup: the
///         ComboBox hid five of them behind a click, and — the reason the owner
///         reported it — an open dropdown EATS THE WHEEL, so a notch meant for
///         the chart re-fingered whichever string the pointer sat on. The chart's
///         own wheel is an edit gesture (see <c>TimelineWheelRouterTests</c>), and
///         a control that silently claims it is the same fault one level in.
///     </para>
/// </summary>
public sealed class ChordTemplateFingerStripTests
{
    private static readonly string[] Segments = ["", "T", "1", "2", "3", "4"];

    [Fact]
    public void TheFingerColumn_OffersEverySegmentWithoutADropdown()
    {
        var view = XDocument.Load(ViewPath());
        var ns = view.Root!.Name.Namespace;

        var strip = view.Descendants(ns + "StackPanel")
            .Single(e => (string?)e.Attribute("AutomationProperties.Name") == "{localization:Loc ChordTemplate_Finger}");

        var toggles = strip.Elements(ns + "ToggleButton").ToList();
        Assert.Equal(Segments.Length, toggles.Count);

        // Every segment binds the SAME property through the equality converter,
        // each with its own parameter — that is what makes the strip a radio
        // group instead of six independent toggles.
        foreach (var expected in Segments)
        {
            var wanted = $"ConverterParameter={(expected.Length == 0 ? "''" : expected)}";
            Assert.Contains(toggles, t =>
                ((string?)t.Attribute("IsChecked"))?.Contains(wanted, StringComparison.Ordinal) == true);
        }

        Assert.All(toggles, t => Assert.Equal("Seg", (string?)t.Attribute("Classes")));
    }

    [Fact]
    public void TheFingerColumn_IsNotAComboBoxAnyMore()
    {
        // The wheel-eating half of the report. A ComboBox anywhere in the
        // per-string row template brings it straight back.
        var view = XDocument.Load(ViewPath());
        var ns = view.Root!.Name.Namespace;

        var rowTemplate = view.Descendants(ns + "DataTemplate")
            .Single(e => ((string?)e.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}DataType"))
                ?.EndsWith("StringFingeringInput", StringComparison.Ordinal) == true);

        Assert.Empty(rowTemplate.Descendants(ns + "ComboBox"));
    }

    [Fact]
    public void EveryKeyTheEditorNames_ExistsInAllThreeResxFiles()
    {
        var markup = UiSourceTree.ReadAllText(ViewPath());
        var keys = Regex.Matches(markup, @"localization:Loc\s+(\w+)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(keys.Count > 15, $"the editor should name many keys, saw {keys.Count}");

        foreach (var file in new[] { "Resources.resx", "Resources.de-DE.resx", "Resources.ja-JP.resx" })
        {
            var present = KeysIn(file);
            var missing = keys.Where(k => !present.Contains(k)).ToList();
            Assert.True(missing.Count == 0, $"missing from {file}: {string.Join(", ", missing)}");
        }
    }

    [Theory]
    [InlineData(null, "", true)]
    [InlineData("", "", true)]
    [InlineData("3", "", false)]
    [InlineData("3", "3", true)]
    [InlineData("3", "4", false)]
    [InlineData("T", "T", true)]
    public void TheConverter_LightsExactlyOneSegment(string? value, string parameter, bool expected) =>
        Assert.Equal(expected,
            StringEqualsConverter.Instance.Convert(value, typeof(bool), parameter, CultureInfo.InvariantCulture));

    [Fact]
    public void TheConverter_WritesBackOnlyTheSegmentBeingTurnedOn()
    {
        // Turning a segment OFF must not write: the strip's other segments drive
        // the value, and a false-writes-empty converter would stomp the new
        // choice back to "no finger" as the old segment unchecks.
        Assert.Equal("2",
            StringEqualsConverter.Instance.ConvertBack(true, typeof(string), "2", CultureInfo.InvariantCulture));
        Assert.Same(BindingOperations.DoNothing,
            StringEqualsConverter.Instance.ConvertBack(false, typeof(string), "2", CultureInfo.InvariantCulture));
    }

    private static string ViewPath() =>
        UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Panels", "ChordTemplateEditorView.axaml"));

    private static HashSet<string> KeysIn(string fileName)
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName));
        return doc.Root!.Elements("data")
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => n != null)
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
    }
}
