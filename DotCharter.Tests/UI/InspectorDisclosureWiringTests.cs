using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

/// <summary>
///     The inspector's <c>Disclosure</c> idiom — a <c>ToggleButton</c> plus a
///     sibling whose <c>IsVisible</c> binds <c>{Binding #ThatToggle.IsChecked}</c>
///     — replaced every <c>Expander</c> in these tabs, because an Expander's
///     collapsed <c>ContentPresenter</c> never realises its child (the same trap
///     the Settings cross-tab search rule names).
///     <para>
///         Its one failure mode is a NAME that does not match: an element-name
///         binding to a name nothing carries resolves to nothing, silently, and
///         the section simply never appears — no build error, no exception, no
///         log line. Copy-pasting a section is exactly how that happens, and this
///         rework copy-pasted nine of them.
///     </para>
///     <para>
///         A markup lint because the alternative cannot run: rooting these views
///         needs the real Avalonia session (outside the default gate), and the
///         Mixer tab cannot even be attached there — its <c>MasonryPanel</c>
///         measure throws under the headless platform, on the original markup as
///         well as the reworked one.
///     </para>
/// </summary>
public sealed class InspectorDisclosureWiringTests
{
    private static IEnumerable<string> InspectorViews() =>
        UiSourceTree.EnumerateFiles(
            UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector")), "*.axaml");

    [Fact]
    public void EveryDisclosureBodyNamesAToggleThatExists()
    {
        var offenders = new List<string>();
        var checkedBodies = 0;

        foreach (var file in InspectorViews())
        {
            var markup = UiSourceTree.ReadAllText(file);

            // Names carried by any element in this file.
            var declared = Regex.Matches(markup, @"x:Name=""(\w+)""")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match body in Regex.Matches(markup, @"IsVisible=""\{Binding #(\w+)\.IsChecked\}"""))
            {
                checkedBodies++;
                var name = body.Groups[1].Value;
                if (!declared.Contains(name))
                    offenders.Add($"{Path.GetFileName(file)}: no element named '{name}'");
            }
        }

        // If this collapses the lint has stopped looking at anything. Since #601 the
        // element-name gates are the section rails' (a body and an accent bar per
        // entry: Mixer 5, Song 4, Lyrics 3) plus the Placement section's "More". A
        // floor rather than an equality so adding a section is free.
        Assert.True(checkedBodies >= 12,
            $"expected the inspector's disclosure bodies to be found, saw {checkedBodies}");
        Assert.True(offenders.Count == 0,
            "these disclosure bodies bind an element name nothing declares, so they are " +
            "invisible forever: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     The other half: a toggle nobody reads. A <c>Disclosure</c> whose name is
    ///     never bound is a header that does nothing when clicked — the same
    ///     silence from the opposite direction.
    /// </summary>
    [Fact]
    public void EveryNamedDisclosureToggleIsRead()
    {
        var offenders = new List<string>();

        foreach (var file in InspectorViews())
        {
            var markup = UiSourceTree.ReadAllText(file);

            var read = Regex.Matches(markup, @"\{Binding #(\w+)\.IsChecked\}")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Match toggle in Regex.Matches(markup,
                         @"<ToggleButton[^>]*Classes=""Disclosure""[^>]*x:Name=""(\w+)""", RegexOptions.Singleline))
            {
                var name = toggle.Groups[1].Value;
                if (!read.Contains(name))
                    offenders.Add($"{Path.GetFileName(file)}: '{name}' toggles nothing");
            }
        }

        Assert.True(offenders.Count == 0,
            "these Disclosure toggles are never read, so clicking them does nothing: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    ///     The rework's actual claim: Mixer and Song no longer use
    ///     <c>Expander</c>. Nine of them are why those two tabs read as unfinished
    ///     next to the Sound tab, and every one hid its rows from the visual tree
    ///     while collapsed.
    /// </summary>
    [Theory]
    [InlineData("MixerInspectorView.axaml")]
    [InlineData("SongInspectorView.axaml")]
    public void TheReworkedTabsUseNoExpander(string fileName)
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Editor", "Inspector", fileName));

        // Strip comments: this file's own explanation of why Expanders are gone
        // names them, and must neither trip the check nor be the only thing it sees.
        var markup = Regex.Replace(UiSourceTree.ReadAllText(path), @"<!--.*?-->", string.Empty, RegexOptions.Singleline);

        Assert.DoesNotContain("<Expander", markup, StringComparison.Ordinal);
        // Since #601 the sections are behind the tab's rail (InspectorSectionRail), not disclosures.
        Assert.Contains("RailEntries\"", markup, StringComparison.Ordinal);
    }
}
