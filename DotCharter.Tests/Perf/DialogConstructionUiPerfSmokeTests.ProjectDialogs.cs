// The project-entry dialogs: the new-project audio-route chooser, Audio from YouTube, and the window-hosted (non-overlay) dialogs.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Fakes;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>
    ///     New Project's chooser stage: the audio route says WHY it is the
    ///     recommended one, the second route is a real button beside it, and the
    ///     footer names the formats the picker will accept.
    ///     <para>
    ///         The formats line is the part worth pinning: it is derived from
    ///         <c>AudioFileTypes</c>, the single "is this song audio?" list the
    ///         picker filter is also built from, so a hand-written "MP3 · WAV ·
    ///         FLAC · OGG" cannot come back and start lying about what opens.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task NewProject_Chooser_ExplainsTheAudioRoute_AndNamesTheFormatsItAccepts()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.INewProjectScreen));
            WithHostedDialog(entry, "Chooser", (dialog, _) =>
            {
                var choose = Descendants<Button>(dialog).First(b => b.Name == "ChooseAudio");
                Assert.True(choose.IsEffectivelyVisible);
                Assert.True(choose.Bounds.Height >= 60,
                    $"the audio route laid out {choose.Bounds.Height:0} px tall — it is the hero of this stage");
                var sub = Descendants<TextBlock>(choose).First(t => t.Name == "ChooseAudioSub");
                Assert.False(string.IsNullOrWhiteSpace(sub.Text),
                    "the audio route no longer says what it reads off the file");
                Assert.True(sub.Bounds.Width > 1 && sub.Bounds.Height > 1,
                    $"the audio route's second line laid out {sub.Bounds}");

                var empty = Descendants<Button>(dialog).First(b => b.Name == "StartWithoutAudio");
                Assert.True(empty.Bounds.Height >= 40 && empty.Bounds.Width > 100,
                    $"'start without audio' laid out {empty.Bounds} — it is a second route, not a link");

                var formats = Descendants<TextBlock>(dialog).First(t => t.Name == "AcceptedFormats");
                Assert.True(formats.IsEffectivelyVisible, "the chooser's format line is hidden");
                Assert.True(formats.Bounds.Width > 1, $"the format line laid out {formats.Bounds}");
                foreach (var extension in DotCharter.Services.Import.Audio.AudioFileTypes.Extensions)
                {
                    Assert.Contains(extension.TrimStart('.').ToUpperInvariant(), formats.Text!,
                        StringComparison.Ordinal);
                }
            });
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(typeof(ISongMetadataScreen), "songmetadata-inset")]
    [InlineData(typeof(INewProjectScreen), "newproject-inset")]
    public Task AdvancedMetadata_AlignsWithTheHeaderAndClearsTheScrollbar(Type contract, string capture) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e => e.Contract == contract);
            WithHostedDialog(entry, FakeVariantNames.Full, (view, _) =>
            {
                var window = Assert.IsType<Window>(TopLevel.GetTopLevel(view));
                var metadata = Assert.IsAssignableFrom<ISongNamesFields>(view.DataContext);
                metadata.ShowJapaneseFields = true;
                view.DataContext = null;
                view.DataContext = metadata;
                var advanced = NarrowWidthLayout.Named<Expander>(view, "AdvancedMetadata");
                advanced.IsExpanded = true;
                NarrowWidthLayout.Settle(window);
                var scroll = advanced.FindAncestorOfType<ScrollViewer>();
                Assert.NotNull(scroll);
                var scrollbar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                    .Single(bar => bar.Name == "PART_VerticalScrollBar" && bar.TemplatedParent == scroll);
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                Assert.True(scrollbar.IsEffectivelyVisible && scrollbar.Bounds.Width > 0,
                    $"The dialog scrollbar must be visible and measured, got {scrollbar.Bounds}");

                if (UiSweepRunner.CaptureEnabled)
                {
                    scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y
                        + NarrowWidthLayout.TopIn(advanced, scroll));
                    NarrowWidthLayout.Settle(window);
                    NarrowWidthLayout.Capture(window, capture);
                }

                var headerCaption = Assert.IsType<string>(advanced.Header);
                var headerText = advanced.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Text == headerCaption);
                var japanese = NarrowWidthLayout.Named<Grid>(advanced, "JapaneseFields");
                var label = japanese.Children.OfType<TextBlock>().First();
                Assert.True(label.IsEffectivelyVisible && label.Bounds is { Width: > 1, Height: > 1 });
                var headerLeft = NarrowWidthLayout.Span(headerText, view).Left;
                var labelLeft = NarrowWidthLayout.Span(label, view).Left;
                var borderRight = NarrowWidthLayout.Span(advanced, view).Right;
                var scrollbarLeft = NarrowWidthLayout.Span(scrollbar, view).Left;

                Assert.Equal(headerLeft, labelLeft, precision: 0);
                var fields = advanced.GetVisualDescendants().OfType<TextBox>().Cast<Control>()
                    .Append(NarrowWidthLayout.Named<TextBlock>(advanced, "ToolVersionText"));
                Assert.All(fields, field =>
                {
                    Assert.True(field.IsEffectivelyVisible && field.Bounds is { Width: > 1, Height: > 1 });
                    var fieldRight = NarrowWidthLayout.Span(field, view).Right;
                    Assert.True(fieldRight < borderRight && fieldRight < scrollbarLeft,
                        $"Field right edge {fieldRight}, expander right edge {borderRight}, scrollbar left edge {scrollbarLeft}");
                });
            });
        }, CancellationToken.None);

    /// <summary>
    ///     File ▸ Audio from YouTube (#820) on first open: the link box, the one-time
    ///     rights notice (#821) with its accept button, and the footer's Download all
    ///     measure non-zero; an unstyled strip would still arrange but measure zero.
    /// </summary>
    [Fact]
    public async Task YoutubeAudio_LinkBoxAndRightsNotice_ConstructAndLayOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeYoutubeAudioScreen.Empty();
            Assert.True(screen.ShowRightsNotice);

            var dialog = new YoutubeAudioDialogView { DataContext = screen };
            WithLaidOutDialog(dialog, () =>
            {
                dialog.UpdateLayout();

                var box = Descendants<TextBox>(dialog).First(t => t.Name == "YoutubeUrlBox");
                Assert.True(box.Bounds.Width > 100 && box.Bounds.Height > 1, $"the link box laid out {box.Bounds}");
                var notice = Descendants<Border>(dialog).First(p => p.Name == "YoutubeRightsNotice");
                Assert.True(notice.IsEffectivelyVisible, "the rights notice is not shown");
                Assert.True(notice.Bounds.Width > 1 && notice.Bounds.Height > 1, $"the notice laid out {notice.Bounds}");
                var accept = Descendants<Button>(dialog).First(p => p.Name == "AcceptYoutubeRightsButton");
                Assert.True(accept.Bounds.Width > 1 && accept.Bounds.Height > 1, $"the accept button laid out {accept.Bounds}");
                var download = Descendants<Button>(dialog).First(p => p.Name == "YoutubeDownloadButton");
                Assert.True(download.Bounds.Width > 1 && download.Bounds.Height > 1, $"Download laid out {download.Bounds}");
                Assert.False(Descendants<Border>(dialog).First(p => p.Name == "YoutubeToolsMissing").IsEffectivelyVisible,
                    "the tools hint shows although the tools are there");
            });
        }, CancellationToken.None);
    }

    /// <summary>A running download says what it is doing and how far it has got (yt-dlp's percentage).</summary>
    [Fact]
    public async Task YoutubeAudio_Download_NamesItsProgress()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeYoutubeAudioScreen.Busy();

            var dialog = new YoutubeAudioDialogView { DataContext = screen };
            WithLaidOutDialog(dialog, () =>
            {
                dialog.UpdateLayout();

                var bar = Descendants<ProgressBar>(dialog).First(p => p.Name == "YoutubeProgress");
                Assert.True(bar.IsEffectivelyVisible, "the progress bar is not shown while the download runs");
                Assert.True(bar.Bounds.Width > 1 && bar.Bounds.Height > 0, $"the bar laid out {bar.Bounds}");
                Assert.False(bar.IsIndeterminate, "a reported percentage still renders as a spinner");
                Assert.Equal(screen.Progress, bar.Value, 3);
                var label = Descendants<TextBlock>(dialog).First(t => t.Name == "YoutubeProgressText");
                Assert.Equal(screen.ProgressText, label.Text);
                Assert.True(label.Bounds.Width > 1 && label.Bounds.Height > 1, $"the progress label laid out {label.Bounds}");
            });
        }, CancellationToken.None);
    }

    /// <summary>The tools hint is a notice and a failure is an error strip, drawn in different tints.</summary>
    [Fact]
    public async Task YoutubeAudio_ToolsHintAndFailure_AreSeparateStrips()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = FakeYoutubeAudioScreen.Full();

            var dialog = new YoutubeAudioDialogView { DataContext = screen };
            WithLaidOutDialog(dialog, () =>
            {
                dialog.UpdateLayout();

                var tools = Descendants<Border>(dialog).First(p => p.Name == "YoutubeToolsMissing");
                Assert.True(tools.IsEffectivelyVisible && tools.Bounds.Height > 1, $"the tools hint laid out {tools.Bounds}");
                var error = Descendants<TextBlock>(dialog).First(t => t.Name == "YoutubeError");
                Assert.Equal(screen.ErrorText, error.Text);
                Assert.True(error.Bounds.Width > 1 && error.Bounds.Height > 1, $"the failure strip laid out {error.Bounds}");
                var errorStrip = Descendants<Border>(dialog).First(b => b.Classes.Contains("DlgStrip") && b.Classes.Contains("Error"));
                Assert.NotNull(tools.Background);
                Assert.NotNull(errorStrip.Background);
                Assert.NotEqual(tools.Background!.ToString(), errorStrip.Background!.ToString());
            });
        }, CancellationToken.None);
    }

    /// <summary>The dialogs that are their own top-level Window and so carry
    /// their own StyleInclude. They cannot be parented into the overlay host, so
    /// they are shown as windows.</summary>
    [Fact]
    public async Task DialogWindows_build_and_lay_out()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            foreach (var (name, factory) in new (string, Func<Window>)[]
                     {
                         (nameof(DotCharter.UI.Views.Diagnostics.DiagnosticsWindow),
                             () => new DotCharter.UI.Views.Diagnostics.DiagnosticsWindow())
                     })
            {
                var window = factory();
                window.Show();
                try
                {
                    // NOT window.Width/Height: a SizeToContent window's Height
                    // is NaN and Measure rejects it.
                    var content = Assert.IsAssignableFrom<Control>(window.Content);
                    content.Measure(new Size(HostWidth, HostHeight));
                    content.Arrange(new Rect(0, 0, HostWidth, HostHeight));

                    Assert.True(content.Bounds.Width > 1 && content.Bounds.Height > 1,
                        $"{name} laid out as {content.Bounds} — it rendered nothing");
                    foreach (var icon in Descendants<PathIcon>(content))
                    {
                        Assert.True(icon.Data is not null,
                            $"a PathIcon on {name} resolved no geometry — its DynamicResource key is wrong");
                    }
                }
                finally
                {
                    window.Close();
                }
            }
        }, CancellationToken.None);
    }
}
