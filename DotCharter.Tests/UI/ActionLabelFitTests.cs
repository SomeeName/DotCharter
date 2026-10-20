using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     A label that does not fit its slot is sliced mid-word, from a clean build,
///     with no warning and with every bounds assertion still passing — the
///     TextBlock itself measured fine, it is the CELL that is too small. The
///     headless UI sweep measures this and reports it; these are the source-level
///     guards for the two places it found, so a regression fails the default gate
///     instead of waiting for someone to read a sweep report.
/// </summary>
public sealed class ActionLabelFitTests
{
    /// <summary>
    ///     Width of one character in the keybind pill, in px.
    ///     <para>
    ///         The pill's label is <c>FireFontMono</c>, so a chord's width really
    ///         is a character count times a constant — the sweep's measurements
    ///         confirm it exactly: <c>Ctrl+Shift+PageDown</c> (19) needed 159.6 px,
    ///         <c>Alt+Shift+PageDown</c> (18) 151.2, <c>Ctrl+Shift+PageUp</c> (17)
    ///         142.8, <c>Ctrl+Shift+Home</c> (15) 126.0. 159.6 / 19 == 8.4, and
    ///         every one of the others lands on it to the decimal.
    ///     </para>
    /// </summary>
    private const double MonoAdvancePx = 8.4;

    /// <summary>
    ///     Settings → Keybinds is 135 rows of <c>ColumnDefinitions="*,N"</c>, and
    ///     N has to hold the LONGEST DEFAULT chord — otherwise the app ships
    ///     unreadable shortcuts out of the box. It shipped at 132, which leaves a
    ///     110 px content slot after the pill's padding and border, and nine
    ///     defaults are wider than that: <c>Ctrl+Shift+PageDown</c> alone wants
    ///     159.6.
    ///     <para>
    ///         The requirement is derived from <see cref="KeybindConfig" />'s real
    ///         defaults, not from a number written down here, so adding a longer
    ///         default chord fails this test rather than shipping a clipped one.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheKeybindControlColumn_FitsTheLongestDefaultChord()
    {
        var longest = DefaultChords()
            .OrderByDescending(c => c.Length)
            .ThenBy(c => c, StringComparer.Ordinal)
            .First();

        var column = SharedKeybindColumnWidth();
        var (padding, border) = KeybindPillChrome();
        var contentSlot = column - padding - border;
        var required = longest.Length * MonoAdvancePx;

        Assert.True(contentSlot >= required,
            $"the keybind control column is {column} px, which leaves {contentSlot:0.#} px inside the pill " +
            $"({padding} px padding + {border} px border), but the longest DEFAULT chord \"{longest}\" " +
            $"needs {required:0.#} px. Nine default chords were sliced mid-word this way in Settings → " +
            $"Keybinds. Widen the shared column literal in KeybindsSettingsView.axaml.");
    }

    /// <summary>
    ///     The backstop. The pill is placed in a fixed column on every surface
    ///     that hosts it, so a chord that outgrows the column must READ as
    ///     truncated — "Ctrl+Shift+Pag" is not a shortcut, it is a different one.
    /// </summary>
    [Fact]
    public void TheKeybindPill_EllipsizesRatherThanSlicing()
    {
        var theme = KeybindCaptureBoxTheme();
        var label = Regex.Match(theme, @"<TextBlock\s+x:Name=""DisplayLabel""[\s\S]*?/>");

        Assert.True(label.Success,
            "the KeybindCaptureBox template no longer declares a TextBlock#DisplayLabel — this guard is blind");
        Assert.Contains(@"TextTrimming=""CharacterEllipsis""", label.Value, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The two narrow rails that carry Add / Duplicate / Delete must size
    ///     those buttons to their OWN content.
    ///     <para>
    ///         An equal division (a <c>UniformGrid</c>, or a Grid whose columns
    ///         are all <c>*</c>) gives every button the width the NARROWEST label
    ///         needs — "Add" — so "Duplicate" was clipped in ENGLISH: 59.6 px of
    ///         text in a 47 px slot in the Pattern Builder, 52.7 in 36 in the Tone
    ///         Designer. Translation makes it structural rather than a matter of a
    ///         few pixels, which is what
    ///         <see cref="TheGermanActionLabelsAreLongerThanTheEnglishOnes" />
    ///         pins.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("PatternBuilderDialogView.axaml", "Pattern_Duplicate")]
    [InlineData("ToneDesignerDialogView.axaml", "Dialog_Tones_Duplicate")]
    public void TheNarrowRailActionClusters_SizeToTheirContent(string view, string labelKey)
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Dialogs", view));
        var doc = XDocument.Load(path);

        var button = doc.Descendants()
            .SingleOrDefault(e => e.Name.LocalName == "Button" &&
                                  ((string?)e.Attribute("Content"))?.Contains(labelKey, StringComparison.Ordinal) == true);
        Assert.True(button != null, $"{view} no longer has a button bound to {labelKey} — this guard is blind");

        var container = button!.Parent!;
        var siblings = container.Elements().Count(e => e.Name.LocalName == "Button");
        Assert.True(siblings >= 3,
            $"{view}: the {labelKey} button no longer sits in a 3-across cluster — this guard is blind");

        Assert.False(container.Name.LocalName == "UniformGrid",
            $"{view}: the {siblings} actions are back inside a UniformGrid, which sizes every one of them to " +
            "the narrowest label. \"Duplicate\" was sliced in English that way, and German is half again longer.");

        var columns = (string?)container.Attribute("ColumnDefinitions");
        if (columns != null)
        {
            var stars = columns.Split(',').Count(c => c.Trim() == "*");
            Assert.True(stars < siblings,
                $"{view}: the {siblings} actions are back in all-star columns (\"{columns}\"), which divides the " +
                "rail equally and sizes every button to the narrowest label.");
        }
    }

    /// <summary>
    ///     The reason no fixed division of those rails can work. English hides it
    ///     — barely, and it did not — while German does not: this is the same
    ///     asymmetry behind the wrapping-text rule in
    ///     <c>docs/agents/ui/layout-traps.md</c>.
    /// </summary>
    [Theory]
    [InlineData("Pattern_Add", "Pattern_Duplicate", "Pattern_Delete")]
    [InlineData("Dialog_Tones_Add", "Dialog_Tones_Duplicate", "Dialog_Tones_Delete")]
    public void TheGermanActionLabelsAreLongerThanTheEnglishOnes(string add, string duplicate, string delete)
    {
        var en = ResxValues("Resources.resx");
        var de = ResxValues("Resources.de-DE.resx");

        var english = new[] { add, duplicate, delete }.Sum(k => en[k].Length);
        var german = new[] { add, duplicate, delete }.Sum(k => de[k].Length);

        Assert.True(german > english,
            $"the German labels ({german} chars) are no longer longer than the English ones ({english}) — " +
            "if that is a translation change, re-check the rails; if it is a truncation, revert it");
    }

    // ---- sources -----------------------------------------------------------

    /// <summary>Every default chord, rendered exactly as the pill renders it.</summary>
    private static IEnumerable<string> DefaultChords()
    {
        var config = new KeybindConfig();
        var chords = typeof(KeybindConfig)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(KeyChord))
            .Select(p => p.GetValue(config))
            .OfType<KeyChord>()
            .Select(c => c.ToString())
            .ToList();

        // Anti-vacuity: reflection over a renamed/moved config would otherwise
        // yield an empty set and the fit check would pass on nothing.
        Assert.True(chords.Count > 100,
            $"only {chords.Count} default chords found on KeybindConfig — this guard is blind");
        return chords;
    }

    /// <summary>The single control-column width the whole tab uses. The rows
    /// come from ONE template, so the width is one literal — and a second
    /// template with another width is itself a finding.</summary>
    private static double SharedKeybindColumnWidth()
    {
        var markup = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "KeybindsSettingsView.axaml")));

        var widths = Regex.Matches(markup, @"ColumnDefinitions=""\*,(?<w>\d+(?:\.\d+)?)""")
            .Select(m => double.Parse(m.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Assert.True(widths.Count >= 1,
            $"only {widths.Count} keybind row templates found — this guard is blind");
        Assert.True(widths.Distinct().Count() == 1,
            "the keybind rows no longer share ONE control-column width (" +
            string.Join(", ", widths.Distinct().Order()) + "); the pills would step out of line and only " +
            "some of them would fit");

        return widths[0];
    }

    /// <summary>Horizontal padding and border of the pill, straight from its
    /// template — the two things that shrink the column into a content slot.</summary>
    private static (double Padding, double Border) KeybindPillChrome()
    {
        var root = Regex.Match(KeybindCaptureBoxTheme(), @"<Border\s+x:Name=""Root""[\s\S]*?>");
        Assert.True(root.Success, "the KeybindCaptureBox template no longer has a Border#Root — this guard is blind");

        var padding = Regex.Match(root.Value, @"Padding=""(?<l>[\d.]+)\s*,\s*(?<t>[\d.]+)""");
        Assert.True(padding.Success, "Border#Root's Padding is no longer a two-number literal — re-derive the chrome");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return (2 * double.Parse(padding.Groups["l"].Value, inv), 2 * BorderWidthOf(root.Value));
    }

    /// <summary>
    ///     The template's border width — a literal, or the value of the token it
    ///     names. Border weights became tokens on 2026-09-20 so a theme can make
    ///     plates heavier (<c>UI/ShapeTokenLintTests</c>), and this column's
    ///     arithmetic has to follow the token rather than assume a number.
    /// </summary>
    private static double BorderWidthOf(string borderMarkup)
    {
        var declared = Regex.Match(borderMarkup, @"BorderThickness=""(?<t>[^""]+)""");
        Assert.True(declared.Success, "Border#Root no longer states a BorderThickness — this guard is blind");

        var value = declared.Groups["t"].Value.Trim();
        var token = Regex.Match(value, @"^\{(?:Dynamic|Static)Resource\s+(?<k>[^}]+)\}$");
        if (token.Success)
        {
            var metrics = UiSourceTree.ReadAllText(Path.Combine(
                TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "Resources", "Metrics.axaml"));
            var declaration = Regex.Match(metrics,
                $@"<Thickness\s+x:Key=""{Regex.Escape(token.Groups["k"].Value.Trim())}""\s*>\s*(?<v>[^<]+)</Thickness>");
            Assert.True(declaration.Success,
                $"{value} is not declared as a Thickness in Styles/Resources/Metrics.axaml");
            value = declaration.Groups["v"].Value.Trim();
        }

        var single = Regex.Match(value, @"^(?<t>[\d.]+)$");
        Assert.True(single.Success,
            $"Border#Root's border is '{value}', not one width — re-derive the chrome from all four edges");
        return double.Parse(single.Groups["t"].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string KeybindCaptureBoxTheme()
    {
        var styles = UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Styles", "KeybindControlTheme.axaml"));

        var start = styles.IndexOf("TargetType=\"controls:KeybindCaptureBox\"", StringComparison.Ordinal);
        Assert.True(start > 0, "KeybindControlTheme.axaml no longer carries the KeybindCaptureBox ControlTheme");

        var end = styles.IndexOf("</ControlTheme>", start, StringComparison.Ordinal);
        return styles[start..(end < 0 ? styles.Length : end)];
    }

    private static Dictionary<string, string> ResxValues(string fileName)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", fileName);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in XDocument.Load(path).Root!.Elements("data"))
        {
            var name = (string?)data.Attribute("name");
            if (name != null) map[name] = data.Element("value")?.Value ?? string.Empty;
        }

        return map;
    }
}
