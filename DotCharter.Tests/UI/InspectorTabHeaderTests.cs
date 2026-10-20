using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The inspector's tab strip is eight <c>ToggleButton.FireSegmentItem</c>
///     headers, each an icon plus a one-word caption. A one-word caption is the
///     case a tooltip exists for — "Sound", "Song", "Phrases" say what the tab is
///     called, not what it holds — so every header carries a
///     <c>ToolTip.Tip</c>.
///     <para>
///         Two of them carried their OWN caption key as the tip
///         (<c>Inspector_Tab_Errors</c>, <c>Panel_PhraseCatalog_Title</c>), which
///         renders a tooltip that repeats the word already under the pointer: the
///         hover costs its dwell and buys nothing, and it reads as done in a diff
///         because the attribute is there. This lint is the difference between
///         "has a tooltip" and "has a tooltip that says something".
///     </para>
///     <para>
///         It is a markup lint because the tab strip cannot be attached in the
///         default gate — <c>InspectorView</c> needs the real Avalonia session
///         (see <see cref="InspectorDisclosureWiringTests" /> for the same
///         reasoning one folder down).
///     </para>
/// </summary>
public sealed class InspectorTabHeaderTests
{
    /// <summary>One tab header: everything from its <c>ToggleButton</c> open
    /// tag to the matching close tag. The class list may go on after
    /// <c>FireSegmentItem</c> (the strip's "More" button wears it too, #573).</summary>
    private static readonly Regex Header =
        new(@"<ToggleButton\b[^>]*?Classes=""FireSegmentItem\b[^""]*""[\s\S]*?</ToggleButton>", RegexOptions.Compiled);

    private static readonly Regex TipKey =
        new(@"ToolTip\.Tip=""\{localization:Loc\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);

    private static readonly Regex CaptionKey =
        new(@"<TextBlock Text=""\{localization:Loc\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);

    [Fact]
    public void EveryTabHeader_CarriesATooltipThatIsNotJustItsOwnCaption()
    {
        var markup = Regex.Replace(
            UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "InspectorView.axaml"))),
            "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var headers = Header.Matches(markup).Select(m => m.Value).ToList();

        // Non-vacuity: the strip is eight headers (Sound, Mixer, Song, Lyrics,
        // Issues, Annotations, Phrases, Riff Library). A regex that stopped
        // matching would otherwise leave this guard asserting nothing.
        Assert.True(headers.Count >= 8,
            $"only {headers.Count} FireSegmentItem headers found in InspectorView.axaml — " +
            "the tab strip moved or the selector changed, and this lint is now blind");

        var offenders = new List<string>();
        foreach (var header in headers)
        {
            var caption = CaptionKey.Match(header);
            if (!caption.Success) continue; // an icon-only header is not this rule's business

            var tip = TipKey.Match(header);
            if (!tip.Success)
                offenders.Add($"{caption.Groups[1].Value}: no ToolTip.Tip");
            else if (tip.Groups[1].Value == caption.Groups[1].Value)
                offenders.Add($"{caption.Groups[1].Value}: the tip IS the caption key");
        }

        Assert.True(offenders.Count == 0,
            "Inspector tab headers whose tooltip says nothing the caption does not:\n  " +
            string.Join("\n  ", offenders));
    }
}
