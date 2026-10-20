using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotCharter.Tests.UI;

/// <summary>
///     A button whose tooltip says exactly what its face says adds a hover and nothing to read:
///     "Browse" over Browse, "Make active" over Make active, and the rail's Home / Library /
///     Settings rows under their own captions (2026-09-24). A tooltip on a button explains what
///     the words do not; a TextBlock that can trim is different and keeps its full-text tooltip,
///     so this reads buttons only. The face is the button's <c>Content</c> or, for an icon over a
///     caption, the one localized TextBlock inside it.
/// </summary>
public sealed class RedundantTooltipLintTests
{
    private static readonly string[] ButtonTypes = ["Button", "ToggleButton", "RadioButton", "RepeatButton", "SplitButton"];
    private static readonly Regex LocKey = new(@"^\{localization:Loc (\w+)\}$");

    [Fact]
    public void NoButtonTooltip_RepeatsTheButtonsOwnWords()
    {
        var root = TestPaths.RepoRoot();
        var resx = UiSourceTree.ReadAllText(Path.Combine(root, "DotCharter.Localization", "Resources.resx"));
        var values = Regex.Matches(resx, @"<data name=""(?<k>[^""]+)"" xml:space=""preserve"">\s*<value>(?<v>.*?)</value>", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups["k"].Value, m => Normalize(WebUtility.HtmlDecode(m.Groups["v"].Value)));

        var captioned = 0;
        var offenders = new List<string>();
        foreach (var file in UiSourceTree.EnumerateFiles(UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Views")), "*.axaml", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var button in doc.Descendants().Where(e => ButtonTypes.Contains(e.Name.LocalName)))
            {
                var face = KeyOf((string?)button.Attribute("Content"));
                if (face is null)
                {
                    var captions = button.Descendants()
                        .Where(e => e.Name.LocalName == "TextBlock")
                        .Select(e => KeyOf((string?)e.Attribute("Text")))
                        .ToList();
                    if (captions.Count == 1) face = captions[0];
                }

                if (face is null || !values.TryGetValue(face, out var faceText)) continue;
                captioned++;
                var tip = KeyOf((string?)button.Attribute(XName.Get("ToolTip.Tip")));
                if (tip is not null && values.TryGetValue(tip, out var tipText) && tipText == faceText)
                    offenders.Add($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)button).LineNumber} \"{faceText}\"");
            }
        }

        Assert.True(captioned > 100, $"only {captioned} captioned buttons found — the walk no longer fits the markup");
        Assert.True(offenders.Count == 0, LintReport.Of(
            "A button's tooltip repeats the button's own caption, so the hover adds nothing to read:",
            offenders,
            "delete the ToolTip.Tip, or point it at a key that says what the caption does not (what happens, or why it is disabled)."));
    }

    private static string? KeyOf(string? attribute) =>
        attribute is null ? null : LocKey.Match(attribute) is { Success: true } m ? m.Groups[1].Value : null;

    private static string Normalize(string value) => Regex.Replace(value, @"[.…:\s]+$", "").Trim().ToLowerInvariant();
}
