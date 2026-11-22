using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #291: Phrases is a section of the Song tab, beside Sections and Tones, and has no tab of
///     its own. Built for real (the app's own <c>InspectorViewModel</c> over a seeded arrangement):
///     the strip holds no Phrases header and its tabs stay contiguous, the Song tab's pager shows
///     the Phrases card from a named page button (design D), its rows lay out and the information
///     card's one Refresh re-reads them.
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongPhrasesSectionUiPerfSmokeTests
{
    [Fact]
    public Task ThePhrasesTab_IsGone_AndTheSongTabsPagerShowsTheCard() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var services = App.Services ?? throw new InvalidOperationException("the headless app session did not boot.");
            var state = services.GetRequiredService<ProjectState>();
            var inspector = services.GetRequiredService<InspectorViewModel>();
            var previous = (state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, inspector.SelectedTabIndex);
            Window? window = null;
            try
            {
                var project = new ChartProject { Chart = { SongName = "Phrases fixture" } };
                for (var i = 0; i < 80; i++)
                    project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
                var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
                var level = new ChartLevel { DifficultyIndex = 0 };
                arrangement.Levels.Add(level);
                foreach (var (beat, name) in new[] { (0, "intro"), (8, "verse"), (24, "chorus"), (40, "verse"), (56, "chorus"), (72, "outro") })
                    arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = name });
                project.Arrangements.Add(arrangement);
                state.ActiveProject = project;
                state.ActiveArrangement = arrangement;
                state.ActiveLevel = level;

                inspector.SelectedTabIndex = InspectorViewModel.SongTabIndex;
                var view = new InspectorView { DataContext = inspector };
                window = NarrowWidthLayout.NewWindow(600, 524, view);
                window.Show();
                Settle(window);

                // No Phrases tab: its name heads no tab, and the tabs are numbered without a gap.
                var phrasesName = Loc("Inspector_Tab_Phrases");
                var tabs = view.FindControl<Grid>("InspectorTabPanels")!;
                var headers = view.GetVisualDescendants().OfType<ToggleButton>()
                    .Where(t => t.Parent is OverflowTabStrip { Name: "InspectorTabRow" } && t.Classes.Contains("ColumnFlowNav") && t.Tag is string { Length: 1 } && !OverflowTabStrip.GetIsOverflowButton(t))
                    .ToList();
                Assert.DoesNotContain(headers, h => AutomationProperties.GetName(h) == phrasesName);
                Assert.Equal(Enumerable.Range(0, headers.Count).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    headers.Select(h => (string)h.Tag!).OrderBy(t => t, StringComparer.Ordinal));
                Assert.Empty(tabs.Children.OfType<PhrasesInspectorView>());
                Assert.Equal(tabs.Children.Count, headers.Count);
                Assert.True(InspectorViewModel.RiffLibraryTabIndex < InspectorViewModel.ChordShapesTabIndex);
                Assert.Equal(InspectorViewModel.ChordShapesTabIndex, headers.Count - 1);

                // The Song tab's cards (design D): Phrases is a page beside Sections and Tones,
                // its button named by the card's title.
                var song = tabs.Children.OfType<SongInspectorView>().Single();
                Assert.True(song.IsEffectivelyVisible, "the Song tab is not the visible panel");
                var card = song.FindControl<ColumnFlowCard>("SongPhrasesCard")!;
                var entry = Assert.Single(song.Cards.Pager.Buttons, b => AutomationProperties.GetName(b) == phrasesName);

                // Its page button shows the card in the pager's column, every phrase a row that measures.
                entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle(window);
                Assert.Contains(card, song.Cards.ShownCards);
                Assert.True(card.Bounds is { Width: > 1, Height: > 1 }, $"the Phrases card laid out {card.Bounds}");
                Assert.DoesNotContain(song.FindControl<Control>("SongSectionsCard")!, song.Cards.ShownCards);
                var section = song.FindControl<PhrasesInspectorView>("SongPhrasesList")!;
                var groups = section.FindControl<ListBox>("PhraseGroups")!;
                Assert.Equal(4, groups.ItemCount); // intro, verse, chorus, outro
                foreach (var name in new[] { "PhraseGroupExpand", "PhraseRenameBox", "PhraseDelete" })
                {
                    var control = section.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);
                    Assert.True(control.IsEffectivelyVisible && control.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out {control.Bounds}");
                }

                // ONE Refresh, in the information card, re-reads the phrases too.
                arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(76), Phrase = "coda" });
                var refresh = song.FindControl<Button>("SongRefresh")!;
                Assert.True(refresh.IsEffectivelyVisible && refresh.Bounds is { Width: > 1, Height: > 1 }, $"Refresh laid out {refresh.Bounds}");
                ((SongOverviewViewModel)song.DataContext!).RefreshCommand.Execute(null);
                Settle(window);
                Assert.Equal(5, groups.ItemCount);
                NarrowWidthLayout.Capture(window, "song-phrases-section");

                // The other tabs kept their meaning after the renumbering.
                inspector.SelectedTabIndex = InspectorViewModel.ChordShapesTabIndex;
                Settle(window);
                Assert.True(tabs.Children.OfType<ShapesPanelView>().Single().IsEffectivelyVisible, "the Chord shapes index no longer shows Chord shapes");
            }
            finally
            {
                window?.Close();
                inspector.SelectedTabIndex = previous.SelectedTabIndex;
                state.ActiveProject = previous.ActiveProject;
                state.ActiveArrangement = previous.ActiveArrangement;
                state.ActiveLevel = previous.ActiveLevel;
            }
        }, CancellationToken.None);

    private static string Loc(string key) => DotCharter.Localization.LocalizationService.Instance[key];

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
