using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Localization;
using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Selection;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Song, Lyrics, Annotations and Riff library tabs in inspector design D's card form (#751), built for real:
///     the app's own inspector view model over a seeded project with long names, attached through
///     <see cref="InspectorView" /> at 530 x 370 (the design's primary size) and 480 x 370, in English, German and
///     Japanese. Every card of the tab, paged in turn, measures non-zero and holds its text (nothing runs past the
///     card, no line is clipped); the footer is pinned (no scroller above it) and laid out inside the tab. Width-shaped,
///     so the class is in the release's de-DE / ja-JP layout filter. Captures <c>p7b-&lt;tab&gt;-&lt;culture&gt;-&lt;size&gt;</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class InspectorProjectTabsUiPerfSmokeTests
{
    private static readonly string[] Cultures = ["en-US", "de-DE", "ja-JP"];

    public static TheoryData<string> Tabs => ["song", "lyrics", "annotations", "riffs"];

    [Theory]
    [MemberData(nameof(Tabs))]
    public Task TheTab_LaysOutEveryCard_AndPinsItsFooter_InEveryCulture(string tab) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var spec = Spec(tab);
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var restore = LocalizationService.Instance.ConfiguredCulture;
            var editor = services.GetRequiredService<ChartEditorViewModel>();
            var selection = services.GetRequiredService<ISelectionService>();
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex);
            var previousVocals = editor.ActiveVocalTrack;
            try
            {
                Seed(state);
                // what opening a project does to the editor: its first vocal track is the active one;
                // a picked syllable fills the Lyrics tab's syllable card.
                editor.ActiveVocalTrack = state.ActiveProject!.VocalTracks[0];
                selection.SelectedLyric = editor.ActiveVocalTrack.Phrases[0].Lyrics[2];
                inspector.SelectedTabIndex = spec.Index;
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    foreach (var (width, height) in new[] { (530.0, 370.0), (480.0, 370.0) })
                    {
                        var view = new InspectorView { DataContext = inspector };
                        var window = NarrowWidthLayout.NewWindow(width, height, view);
                        window.Show();
                        try
                        {
                            NarrowWidthLayout.Settle(window);
                            var at = $"{tab} in {culture} at {width:0}x{height:0}";
                            var host = view.FindControl<Grid>("InspectorTabPanels")!.Children.Single(c => c.IsVisible);
                            Assert.Equal(spec.View, host.GetType());
                            NarrowWidthLayout.Capture(window, $"p7b-{tab}-{culture}-{width:0}x{height:0}");
                            var cards = NarrowWidthLayout.Named<ColumnFlowPanel>(host, spec.Panel);
                            var all = cards.Children.Where(c => c != cards.Pager && c != cards.PagerCueControl && c.IsVisible).ToList();
                            Assert.True(all.Count >= 2, $"{at}: {all.Count} cards — repoint this test");
                            foreach (var card in all)
                            {
                                cards.ShowPage(card);
                                NarrowWidthLayout.Settle(window);
                                Assert.Contains(card, cards.ShownCards);
                                foreach (var shown in cards.ShownCards)
                                    Assert.True(shown.Bounds is { Width: > 40, Height: > 40 }, $"{at}: {shown.Name} laid out {shown.Bounds}");
                                AssertNothingOverflows(cards, $"{at}, {card.Name} on screen");
                            }

                            if (spec.Footer is { } footerName)
                            {
                                var footer = NarrowWidthLayout.Named<Control>(host, footerName);
                                Assert.DoesNotContain(footer.GetVisualAncestors(), a => a is ScrollViewer);
                                var bottom = footer.TranslatePoint(new Point(0, footer.Bounds.Height), host)!.Value.Y;
                                Assert.True(footer.Bounds is { Height: >= 28, Width: > 40 } && bottom <= host.Bounds.Height + 0.5,
                                    $"{at}: the footer laid out {footer.Bounds}, its bottom at {bottom:0.#} in a {host.Bounds.Height:0.#} px tab");
                                AssertNothingOverflows(footer, footer, $"{at}, footer");
                            }
                        }
                        finally
                        {
                            window.Close();
                        }
                    }
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
                selection.SelectedLyric = null;
                editor.ActiveVocalTrack = previousVocals;
                (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex) = previous;
            }
        }, CancellationToken.None);

    /// <summary>Long riff names in the three languages the app speaks.</summary>
    private static readonly string[] RiffNames =
    [
        "Second verse fill with the long slide into the chorus",
        "Überleitung zum Refrain mit dem langen Bending und Vibrato",
        "サビ前のスライドとビブラートを含む長いフレーズ"
    ];

    /// <summary>Riff names of every length the library meets (English, German, Japanese), a picked riff and a
    ///     highway selection: every Riff library card holds its text whole (no ellipsized line, nothing cut at its
    ///     bottom outside the riff list's own scroller, nothing past its right edge) at 530 x 370, 480 x 370 and in
    ///     the 1280 x 720 editor window, in every culture.</summary>
    [Fact]
    public Task TheRiffLibrary_HoldsLongNamesAndASelection_InEveryCard() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var catalog = services.GetRequiredService<IPhraseCatalogService>();
            var selection = services.GetRequiredService<ISelectionService>();
            var riffs = inspector.RiffLibrary ?? throw new InvalidOperationException("the app built no Riff library.");
            var restore = LocalizationService.Instance.ConfiguredCulture;
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex);
            var added = new List<PhraseCatalogEntry>();
            try
            {
                Seed(state);
                var level = state.ActiveLevel!;
                for (var i = 0; i < 6; i++)
                    level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), StringIndex = i, Fret = 3 + i });
                foreach (var name in RiffNames)
                    added.Add(catalog.Add(name, state.ActiveArrangement!, level.Notes, [], [], []) ?? throw new InvalidOperationException($"the catalog stored no riff named {name}."));
                riffs.EnsureLoaded();
                riffs.SelectedEntry = riffs.Entries.First(e => e.Name == RiffNames[1]);
                foreach (var note in level.Notes.Take(3)) selection.SelectedNotes.Add(note);
                inspector.SelectedTabIndex = InspectorViewModel.RiffLibraryTabIndex;
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    foreach (var (width, height) in new[] { (530.0, 370.0), (480.0, 370.0) })
                    {
                        var view = new InspectorView { DataContext = inspector };
                        var window = NarrowWidthLayout.NewWindow(width, height, view);
                        window.Show();
                        try
                        {
                            AssertEveryRiffCardHoldsItsText(window, view, $"riffs in {culture} at {width:0}x{height:0}");
                        }
                        finally
                        {
                            window.Close();
                        }
                    }
                }

                // the editor window at a laptop size: the inspector is a third of its bottom dock. Never closed here
                // (closing the SHELL ends the testhost); the dispatch releases it.
                var shell = services.GetRequiredService<MainWindowViewModel>();
                shell.IsGameplayPresentation = false;
                shell.SelectedMainTabIndex = 0;
                shell.IsInspectorVisible = true;
                var main = new MainWindow { DataContext = shell, Width = 1280, Height = 720 };
                main.Show();
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    AssertEveryRiffCardHoldsItsText(main, main, $"riffs in {culture} in the 1280x720 editor");
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
                selection.ClearSelections();
                riffs.SelectedEntry = null;
                foreach (var entry in added) catalog.Remove(entry);
                (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex) = previous;
            }
        }, CancellationToken.None);

    private static void AssertEveryRiffCardHoldsItsText(Window window, Visual root, string at)
    {
        NarrowWidthLayout.Settle(window);
        var view = root.GetVisualDescendants().OfType<PhraseCatalogView>().Single(v => v.IsEffectivelyVisible);
        NarrowWidthLayout.Capture(window, $"p7b-riff-texts-{at.Replace(' ', '-')}");
        var cards = NarrowWidthLayout.Named<ColumnFlowPanel>(view, "RiffCards");
        var all = cards.Children.Where(c => c != cards.Pager && c != cards.PagerCueControl && c.IsVisible).ToList();
        Assert.True(all.Count >= 3, $"{at}: {all.Count} cards, repoint this test");
        foreach (var card in all)
        {
            cards.ShowPage(card);
            NarrowWidthLayout.Settle(window);
            Assert.Contains(card, cards.ShownCards);
            foreach (var shown in cards.ShownCards)
            {
                Assert.True(shown.Bounds is { Width: > 40, Height: > 40 }, $"{at}: {shown.Name} laid out {shown.Bounds}");
                AssertNothingOverflows(shown, shown, $"{at}, {shown.Name}");
                AssertNothingTrimmedOrCut(shown, $"{at}, {shown.Name}");
            }
        }

        var rows = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "RiffRowLength" && t.IsEffectivelyVisible).ToList();
        Assert.True(rows.Count >= 1 && rows.All(r => r.Text is { Length: > 0 }), $"{at}: {rows.Count} riff rows stamp a length");
        var count = NarrowWidthLayout.Named<TextBlock>(view, "RiffSelectionCount");
        Assert.Contains("3", count.Text);
    }

    /// <summary>No visible line is ellipsized, and no text or button outside a scroller ends below its card.</summary>
    private static void AssertNothingTrimmedOrCut(Control card, string what)
    {
        var problems = new List<string>();
        foreach (var part in card.GetVisualDescendants().OfType<Control>()
                     .Where(c => c is TextBlock or Button && c.IsEffectivelyVisible && c.Bounds.Width > 1))
        {
            if (part is TextBlock { Text.Length: > 0 } text && text.TextLayout.TextLines.Any(l => l.HasCollapsed))
                problems.Add($"'{text.Text}' is ellipsized at {text.Bounds.Width:0.#} px");
            if (part.GetVisualAncestors().TakeWhile(a => a != card).Any(a => a is ScrollViewer)) continue;
            var bottom = part.TranslatePoint(new Point(0, part.Bounds.Height), card)?.Y ?? 0;
            if (bottom > card.Bounds.Height + 0.5)
                problems.Add($"{part.GetType().Name} {part.Name} '{(part as TextBlock)?.Text}' ends at {bottom:0.#} in a {card.Bounds.Height:0.#} px card");
        }

        Assert.True(problems.Count == 0, $"{what}: {string.Join("; ", problems.Take(8))}");
    }

    private sealed record TabSpec(int Index, Type View, string Panel, string? Footer);

    private static TabSpec Spec(string tab) => tab switch
    {
        "song" => new(InspectorViewModel.SongTabIndex, typeof(SongInspectorView), "SongCards", "SongFooter"),
        "lyrics" => new(InspectorViewModel.LyricsTabIndex, typeof(LyricsInspectorView), "LyricsCards", "LyricsFooter"),
        "annotations" => new(5, typeof(NotesInspectorView), "NotesCards", "NotesFooter"),
        "riffs" => new(InspectorViewModel.RiffLibraryTabIndex, typeof(PhraseCatalogView), "RiffCards", "RiffFooter"),
        _ => throw new ArgumentOutOfRangeException(nameof(tab), tab, null)
    };

    /// <summary>A project whose names are long enough to find a clipped label: a long title, sections, tones and
    ///     phrases, a vocal track with long syllables and three annotations (one resolved).</summary>
    internal static void Seed(ProjectState state)
    {
        var project = new ChartProject
        {
            Chart =
            {
                SongName = "Northern Lights (Extended Live Version)", Artist = "The Night Shift Orchestra", AlbumName = "Afterglow",
                AlbumYear = "2019"
            }
        };
        for (var i = 0; i < 80; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6, StartingTone = "lead_clean" };
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);
        foreach (var (beat, name) in new[] { (0, "intro"), (8, "verse"), (24, "chorus"), (40, "verse"), (56, "chorus"), (72, "outro") })
            arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = name, Section = name });
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "lead_distortion_for_the_long_solo" });
        project.Arrangements.Add(arrangement);

        var phrase = new LyricPhrase();
        foreach (var (beat, text) in new[] { (4, "Hold"), (5, "through"), (6, "Ev-"), (7, "erlasting-"), (8, "Sing") })
            phrase.Lyrics.Add(new LyricEvent { Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1), Text = text, Note = 61 });
        project.VocalTracks.Add(new VocalTrack { Phrases = [phrase] });

        project.Annotations.Add(new ChartAnnotation { TimeMs = 14_000, Text = "Check the bend release on the second verse before export" });
        project.Annotations.Add(new ChartAnnotation { TimeMs = 36_000, Text = "Chorus starts here", Kind = ChartAnnotationKind.Mark });
        project.Annotations.Add(new ChartAnnotation { TimeMs = 63_000, Text = "Re-time the phrase", Resolved = true });

        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        state.ActiveLevel = level;
    }

    private static void AssertNothingOverflows(ColumnFlowPanel cards, string what)
    {
        foreach (var card in cards.ShownCards) AssertNothingOverflows(card, card, what);
    }

    /// <summary>No visible text needs more height than it has, and no text, button or box runs past the card's
    ///     right edge (a scrollbar's parts are the theme's, not content).</summary>
    private static void AssertNothingOverflows(Control card, Control root, string what)
    {
        var problems = new List<string>();
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>()
                     .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Bounds.Width > 1)
                     .Where(t => !t.GetVisualAncestors().OfType<TextBox>().Any()))
        {
            if (text.TextLayout.Height > text.Bounds.Height - text.Padding.Top - text.Padding.Bottom + 1)
                problems.Add($"'{text.Text}' needs {text.TextLayout.Height:0.#} px of height in {text.Bounds.Height:0.#}");
        }

        foreach (var part in root.GetVisualDescendants().OfType<Control>()
                     .Where(c => c is TextBlock or Button or ToggleButton or TextBox or ComboBox && c.IsEffectivelyVisible && c.Bounds.Width > 0)
                     .Where(c => !c.GetVisualAncestors().OfType<ScrollBar>().Any() && !c.GetVisualAncestors().OfType<TextBox>().Any()))
        {
            var right = part.TranslatePoint(new Point(part.Bounds.Width, 0), card)?.X ?? 0;
            if (right > card.Bounds.Width + 0.5)
                problems.Add($"{part.GetType().Name} {part.Name} '{(part as TextBlock)?.Text}' ends at {right:0.#} in a {card.Bounds.Width:0.#} px card");
        }

        Assert.True(problems.Count == 0, $"{what}: {string.Join("; ", problems.Take(8))}");
    }
}
