using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI.ViewModels.CommandPalette;
using DotCharter.UI.Views.CommandPalette;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The command palette, laid out with REAL rows behind it.
/// </summary>
/// <remarks>
///     <para>
///         The palette is the one overlay whose row template no other test can
///         reach: with no view model <c>Results</c> is empty, so
///         <see cref="ShellConstructionUiPerfSmokeTests" />' case proves the card
///         and the search box and nothing about the list — which is where all the
///         markup is. A real <see cref="CommandPaletteViewModel" /> over a
///         hand-built catalogue is what makes the three-column row, the shortcut
///         key cap and the footer legend actually materialize.
///     </para>
///     <para>
///         The FIXED column is the risk here and the reason for the German case:
///         a category column that fits "FILE" and not "BEARBEITEN" slices the
///         word with no warning, no exception and a passing bounds check — the
///         `ActionLabelFitTests` failure mode, one surface over.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class CommandPaletteUiPerfSmokeTests
{
    private const double W = 1600;
    private const double H = 1000;
    private static readonly Size Slot = new(W, H);

    private static CommandPaletteViewModel Palette(params (string Category, string Title, bool Bound)[] rows)
    {
        var catalogue = rows
            .Select((r, i) => new AppCommand(
                $"cmd.{i}", r.Title, r.Category, () => { },
                r.Bound ? () => new KeyChord(Key.E, KeyModifiers.Control | KeyModifiers.Shift) : null))
            .ToList();

        var vm = new CommandPaletteViewModel(() => catalogue, LocalizationService.Instance);
        vm.Open();
        return vm;
    }

    /// <summary>The row template, with real rows: three columns, and every one
    /// of them measured.</summary>
    [Fact]
    public Task TheResultRows_layOutTheirCategoryNameAndShortcutColumns() =>
        WithPalette(Palette(
            ("FILE", "Import Guitar Pro…", true),
            ("FILE", "Export Rocksmith package…", true),
            ("SELECTION", "Select all matches of pattern", false),
            ("VIEW", "Toggle 3D preview", true)), (view, _) =>
        {
            var list = Assert.IsType<ListBox>(Find(view, "ResultList"));
            var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(4, rows.Count);

            foreach (var row in rows)
            {
                Assert.True(row.Bounds.Height >= 38,
                    $"a palette row laid out {row.Bounds.Height:0.#} px tall — the mockup's row is 38");

                var group = row.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Classes.Contains("PaletteGroup"));
                var name = row.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => t.Classes.Contains("PaletteName"));

                Assert.True(group.Bounds is { Width: > 1, Height: > 1 },
                    $"a row's category column laid out {group.Bounds} — it rendered nothing");
                Assert.True(name.Bounds is { Width: > 1, Height: > 1 },
                    $"a row's command name laid out {name.Bounds} — it rendered nothing");
                // A category is a word, so the UI face (Graphite: mono is for figures).
                Assert.Equal("Atkinson Hyperlegible", group.FontFamily.Name);
            }

            // Three of the four carry a shortcut, and it reads as a KEY CAP.
            var caps = list.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains("PaletteKey") && b.IsEffectivelyVisible)
                .ToList();
            Assert.Equal(3, caps.Count);
            Assert.All(caps, c => Assert.True(c.Bounds is { Width: > 1, Height: > 1 },
                $"a shortcut key cap laid out {c.Bounds}"));
        });

    /// <summary>
    ///     <b>The FIXED category column must fit the longest thing that can land
    ///     in it, and the REAL categories are what "can".</b> They are the
    ///     <c>Keybinds_*</c> resx values, so this reads them out of the shipped
    ///     German file rather than from a number written down anywhere — the
    ///     same shape as <c>UI/ActionLabelFitTests</c>, and for the same reason:
    ///     a sliced category is not a shortened category, it is a different one.
    ///     <para>
    ///         Measured with a PROBE TextBlock, never off the realized one: the
    ///         column states a <c>Width</c>, so a realized cell's
    ///         <c>DesiredSize</c> is clamped to it and comparing the two is
    ///         vacuous (this test passed at 44 px before it was written this
    ///         way).
    ///     </para>
    /// </summary>
    [Fact]
    public Task TheCategoryColumn_fitsEveryRealCategoryInGerman() =>
        WithPalette(Palette(("FILE", "Import MIDI file…", false)), (view, _) =>
        {
            var cell = Find(view, "ResultList").GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Classes.Contains("PaletteGroup"));
            var column = cell.Bounds.Width;
            Assert.True(column > 1, "the category column laid out to nothing");

            var offenders = new List<string>();
            foreach (var category in GermanCategories())
            {
                // The category column no longer forces caps (Graphite dropped
                // the tracked-all-caps look), so this measures the resx value
                // AS RENDERED rather than an artificially widened uppercase form.
                var probe = new TextBlock
                {
                    Text = category,
                    FontFamily = cell.FontFamily,
                    FontSize = cell.FontSize,
                    LetterSpacing = cell.LetterSpacing
                };
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                if (probe.DesiredSize.Width > column + 0.5)
                    offenders.Add($"{category} needs {probe.DesiredSize.Width:0.#} px");
            }

            Assert.True(offenders.Count == 0,
                $"the palette's {column:0.#} px category column cannot hold: " +
                string.Join(", ", offenders) +
                ". A sliced category is a different category — widen the column.");
        });

    /// <summary>
    ///     The category strings <c>CommandCatalog</c> really puts in that column,
    ///     in German: the resx keys are read out of the CATALOGUE's own source, so
    ///     a sixth category cannot be added without this test seeing it.
    /// </summary>
    private static IEnumerable<string> GermanCategories()
    {
        var catalogue = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "CommandPalette", "CommandCatalog.cs")));
        var keys = System.Text.RegularExpressions.Regex
            .Matches(catalogue, "\"(?<k>Keybinds_[A-Za-z0-9_]+)\"")
            .Select(m => m.Groups["k"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(keys.Count >= 5,
            $"only {keys.Count} categories found in CommandCatalog — this probe has gone blind and " +
            "the assertion above is vacuous");

        var resx = UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Localization", "Resources.de-DE.resx"));

        foreach (var key in keys)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                resx, "<data name=\"" + key + "\"[^>]*><value>(?<v>[^<]*)</value>");
            Assert.True(match.Success, $"the palette category key '{key}' is not in the German resx");
            yield return match.Groups["v"].Value
                .Replace("&amp;", "&", StringComparison.Ordinal)
                .Replace("&lt;", "<", StringComparison.Ordinal)
                .Replace("&gt;", ">", StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     The footer legend — three key caps and the match count. The count is
    ///     the one thing that distinguishes "my query narrowed this" from "the
    ///     list is just short", and it is a MultiBinding over a localized
    ///     template, which is the shape that silently produces an empty string
    ///     when a path is wrong.
    /// </summary>
    [Fact]
    public Task TheFooter_showsThreeKeyCapsAndTheLiveMatchCount() =>
        WithPalette(Palette(
            ("FILE", "Import MIDI file…", false),
            ("FILE", "Export as Guitar Pro (.gp)…", false),
            ("VIEW", "Toggle 3D preview", false)), (view, vm) =>
        {
            var card = Find(view, "PaletteCard");
            var legend = card.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("PaletteLegend"))
                .ToList();
            Assert.Equal(3, legend.Count);
            Assert.All(legend, t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t.Text),
                    "a footer legend rendered blank — its localization key does not resolve");
                Assert.True(t.Bounds is { Width: > 1, Height: > 1 }, $"a footer legend laid out {t.Bounds}");
            });

            Assert.Equal(3, vm.ResultCount);
            Assert.Equal(3, vm.TotalCount);

            var count = card.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Text != null && t.Text.Contains('3') && t.Text.Length is > 3 and < 24
                                     && !t.Classes.Contains("PaletteName"));
            Assert.True(count != null,
                "the footer's match count rendered nothing — the MultiBinding over " +
                "CommandPalette_Count resolved to an empty string");

            // …and it MOVES with the query: a count that never changes is a
            // label, not a count.
            vm.Query = "Guitar";
            view.UpdateLayout();
            Assert.Equal(1, vm.ResultCount);
            Assert.Equal(3, vm.TotalCount);
        });

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Task WithPalette(CommandPaletteViewModel vm, Action<Control, CommandPaletteViewModel> assert) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new CommandPaletteView { DataContext = vm };
            var window = new Window { Width = W, Height = H, Content = view };
            window.Show();
            try
            {
                view.Measure(Slot);
                view.Arrange(new Rect(0, 0, W, H));
                view.UpdateLayout();

                Assert.True(view.Bounds is { Width: > 1, Height: > 1 },
                    $"the palette laid out {view.Bounds} — it rendered nothing");
                assert(view, vm);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private static Control Find(Control view, string name)
    {
        var control = view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        Assert.True(control != null, $"'{name}' is not in the tree — the view never realized");
        return control!;
    }
}
