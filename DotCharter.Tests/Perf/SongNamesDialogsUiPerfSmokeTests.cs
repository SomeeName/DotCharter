using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #292: the New project and Song metadata dialogs carry DLC Builder's fields: the Japanese and sort
///     rows sit behind their toggles, and DLC Key, Version, Author and the read-only Tool version close the
///     form. Built over the gallery's fakes, attached and laid out in the run's culture: every row the
///     toggles show measures non-zero, the ones they hide are hidden, and the non-Latin offer shows only
///     when the main name holds non-Latin text.
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongNamesDialogsUiPerfSmokeTests
{
    [Fact]
    public Task NewProject_WithTheToggles_ShowsOrHidesTheJapaneseAndSortRows() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var plain = FakeNewProjectScreen.Default();
            var plainView = new NewProjectDialogView { DataContext = plain };
            WithWindow(plainView, "newproject-default", window =>
            {
                AssertMeasured(plainView, "ShowJapaneseFields", "ShowSortFields", "DlcKeyBox", "VersionBox", "AuthorBox", "ToolVersionText");
                Assert.False(Named(plainView, "OriginalScriptOffer").IsVisible, "the offer shows for Latin names");
                Assert.False(Row(plainView, "Inspector_Project_JapaneseArtist").IsEffectivelyVisible, "the Japanese rows show with the toggle off");
                Assert.False(Row(plainView, "Dialog_ArtistSort").IsEffectivelyVisible, "the sort rows show with the toggle off");

                // The toggles open their rows.
                plain.ShowJapaneseFields = true;
                plain.ShowSortFields = true;
                plainView.DataContext = null;
                plainView.DataContext = plain;
                Settle(window);
                AssertRowMeasured(plainView, "Inspector_Project_JapaneseArtist");
                AssertRowMeasured(plainView, "Inspector_Project_JapaneseSong");
                AssertRowMeasured(plainView, "Dialog_ArtistSort");
                AssertRowMeasured(plainView, "Dialog_TitleSort");
                AssertRowMeasured(plainView, "Dialog_AlbumSort");
            });

            var offer = FakeNewProjectScreen.NonLatinNames();
            var offerView = new NewProjectDialogView { DataContext = offer };
            WithWindow(offerView, "newproject-nonlatin", _ =>
            {
                AssertMeasured(offerView, "OriginalScriptOffer");
                AssertRowMeasured(offerView, "Inspector_Project_JapaneseArtist");
            });

            var full = FakeNewProjectScreen.Full();
            var fullView = new NewProjectDialogView { DataContext = full };
            WithWindow(fullView, "newproject-full", _ =>
            {
                AssertRowMeasured(fullView, "Dialog_ArtistSort");
                AssertRowMeasured(fullView, "Dialog_TitleSort");
            });
        }, CancellationToken.None);

    [Fact]
    public Task SongMetadata_WithTheToggles_ShowsOrHidesTheJapaneseAndSortRows() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var plain = FakeSongMetadataScreen.Default();
            var plainView = new SongMetadataDialogView { DataContext = plain };
            WithWindow(plainView, "songmetadata-default", _ =>
            {
                AssertMeasured(plainView, "ShowJapaneseFields", "ShowSortFields", "DlcKeyBox", "VersionBox", "AuthorBox", "ToolVersionText");
                // The key estimate, its "Use estimate" button and the lock.
                AssertMeasured(plainView, "KeyEstimateText", "UseKeyEstimateButton", "KeyLockedToggle");
                Assert.False(Named(plainView, "OriginalScriptOffer").IsVisible, "the offer shows for Latin names");
                Assert.False(Row(plainView, "Dialog_ArtistSort").IsEffectivelyVisible, "the sort rows show with the toggle off");
            });

            var full = FakeSongMetadataScreen.Full();
            var fullView = new SongMetadataDialogView { DataContext = full };
            WithWindow(fullView, "songmetadata-full", _ =>
            {
                AssertRowMeasured(fullView, "Dialog_ArtistSort");
                AssertRowMeasured(fullView, "Dialog_AlbumSort");
            });

            var longText = FakeSongMetadataScreen.LongText();
            var longView = new SongMetadataDialogView { DataContext = longText };
            WithWindow(longView, "songmetadata-longtext", _ =>
            {
                AssertMeasured(longView, "ShowJapaneseFields", "ToolVersionText", "KeyEstimateText", "UseKeyEstimateButton", "KeyLockedToggle");
                AssertRowMeasured(longView, "Inspector_Project_JapaneseArtist");
            });
        }, CancellationToken.None);

    private static void WithWindow(Control view, string capture, Action<Window> check)
    {
        var window = NarrowWidthLayout.NewWindow(760, 1000, view);
        // The dialog host paints the text; a bare window has to, for the capture to be readable.
        window.Foreground = Avalonia.Media.Brushes.Gainsboro;
        window.Show();
        try
        {
            Settle(window);
            NarrowWidthLayout.Capture(window, capture);

            // The Japanese, sort and DLC fields sit behind ONE collapsed "Advanced" expander.
            var advanced = (Expander)Named(view, "AdvancedMetadata");
            Assert.False(advanced.IsExpanded, "the Advanced expander starts open");
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), c => c.Name == "ShowJapaneseFields" && c.IsEffectivelyVisible);
            advanced.IsExpanded = true;
            Settle(window);
            NarrowWidthLayout.Capture(window, capture + "-advanced");
            check(window);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertMeasured(Control view, params string[] names)
    {
        foreach (var name in names)
        {
            var control = Named(view, name);
            Assert.True(control.IsEffectivelyVisible && control.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out {control.Bounds}");
        }
    }

    /// <summary>A control by <c>x:Name</c>, looked up through the shared block's own namescope too.</summary>
    private static Control Named(Control view, string name) =>
        view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"{name} is not in the view");

    /// <summary>The form row whose label is the resx <paramref name="labelKey" />.</summary>
    private static Control Row(Control view, string labelKey)
    {
        var label = LocalizedLabel(labelKey);
        return view.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label && t.Parent is Grid);
    }

    private static void AssertRowMeasured(Control view, string labelKey)
    {
        var row = Row(view, labelKey);
        Assert.True(row.IsEffectivelyVisible && row.Bounds is { Width: > 1, Height: > 1 }, $"the '{labelKey}' row laid out {row.Bounds}");
    }

    private static string LocalizedLabel(string key) => DotCharter.Localization.LocalizationService.Instance[key];

    private static void Settle(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
        }
    }
}
