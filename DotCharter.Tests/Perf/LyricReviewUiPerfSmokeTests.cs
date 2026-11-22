using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Core.Domain.Vocals;
using DotCharter.Tests.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor.Inspector;

namespace DotCharter.Tests.Perf;

[Collection("HeadlessUI")]
public sealed class LyricReviewUiPerfSmokeTests
{
    [Fact]
    public async Task ReviewControlsAttachAndMeasureNonZero() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (inspector, editor, selection) = LyricReviewNavigationTests.Build();
            var lyrics = editor.ActiveVocalTrack!.Phrases.Single().Lyrics;
            lyrics[0].ReviewReasons = LyricReviewReason.LowScore;
            lyrics[0].AlignmentConfidence = 0.02;
            lyrics[2].ReviewReasons = LyricReviewReason.Gap;
            lyrics[2].AlignmentConfidence = 0.4;
            selection.SelectedLyric = lyrics[0];
            var view = new LyricsInspectorView { DataContext = inspector };
            var window = new Window { Width = 420, Height = 680, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(420, 680));
                view.Arrange(new Rect(0, 0, 420, 680));
                NarrowWidthLayout.Capture(window, "lyric-review-420x680");
                var controls = ReviewButtons(view);
                Assert.Equal(3, controls.Length);
                Assert.All(controls, b => Assert.True(b.Bounds is { Width: > 0, Height: > 0 }));
                Assert.All(controls, b => Assert.True(b.IsEffectivelyEnabled && b.Focusable, b.Name));
                var count = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LyricReviewCount");
                Assert.Equal(FakeLocalization.English().Format("Inspector_Lyrics_ReviewCount", 2), count.Text);
                Assert.True(count.Bounds is { Width: > 0, Height: > 0 });
            }
            finally { window.Close(); }
        }, CancellationToken.None);

    [Fact]
    public async Task WithNothingToReviewTheButtonsAreDisabledAndTheReasonIsShown() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (inspector, _, _) = LyricReviewNavigationTests.Build();
            var view = new LyricsInspectorView { DataContext = inspector };
            var window = new Window { Width = 420, Height = 680, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(420, 680));
                view.Arrange(new Rect(0, 0, 420, 680));
                var controls = ReviewButtons(view);
                Assert.Equal(3, controls.Length);
                Assert.All(controls, b => Assert.True(b.Bounds is { Width: > 0, Height: > 0 }));
                Assert.All(controls, b => Assert.False(b.IsEffectivelyEnabled, b.Name));
                var count = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LyricReviewCount");
                Assert.Equal(FakeLocalization.English()["Inspector_Lyrics_ReviewNone"], count.Text);
                Assert.True(count.Bounds is { Width: > 0, Height: > 0 });
            }
            finally { window.Close(); }
        }, CancellationToken.None);

    private static Button[] ReviewButtons(LyricsInspectorView view) =>
        [.. view.GetVisualDescendants().OfType<Button>().Where(b => b.Name is "NextLyricReview" or "PreviousLyricReview" or "MarkLyricReviewed")];
}
