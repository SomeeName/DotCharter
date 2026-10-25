using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes.Dialogs;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The Editor on Fire track picker shows each track's name as written. A
///     string checkbox Content becomes <see cref="AccessText" />, which takes the
///     "_" of <c>PART REAL_GUITAR</c> as an access key and draws
///     "PART REALGUITAR" (gallery capture for #232).
/// </summary>
[Collection("HeadlessUI")]
public sealed class EofImportDialogUiPerfSmokeTests
{
    [Fact]
    public async Task EveryTrackName_IsDrawnAsWritten_UnderscoresIncluded() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var view = new EofImportDialogView { DataContext = FakeEofImportScreen.Default() };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();
            try
            {
                window.UpdateLayout();
                var toggles = view.GetVisualDescendants().OfType<CheckBox>().Where(c => c.Name == "EofTrackToggle").ToList();
                Assert.Equal(3, toggles.Count);
                var texts = toggles.SelectMany(t => t.GetVisualDescendants().OfType<TextBlock>()).ToList();

                Assert.DoesNotContain(texts, t => t is AccessText);
                var guitar = Assert.Single(texts, t => t.Text == "PART REAL_GUITAR (812 notes)");
                Assert.True(guitar.Bounds.Width > 1, $"the track name laid out {guitar.Bounds}");
                Assert.Contains(texts, t => t.Text == "PART VOCALS (214 lyrics)");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
}
