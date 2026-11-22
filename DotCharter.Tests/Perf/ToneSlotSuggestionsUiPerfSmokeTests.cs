using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.UI.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #845: the Tone designer's slot fields. The suggestion list is the dialog grammar's card (as wide
///     as its field, bordered, padded), an empty slot shows a placeholder, and the list stays closed
///     when its only match is exactly what is typed. The culture the headless session runs in
///     (<c>DOTCHARTER_HEADLESS_CULTURE</c>) decides en-US, de-DE or ja-JP.
/// </summary>
[Collection("HeadlessUI")]
public sealed class ToneSlotSuggestionsUiPerfSmokeTests
{
    [Theory]
    [InlineData("tone3", new[] { "tone3" }, false)]
    [InlineData("tone", new[] { "tone1", "tone2" }, true)]
    [InlineData("tone", new[] { "tone" }, false)]
    [InlineData("Tone3", new[] { "tone3" }, true)]
    [InlineData("one", new[] { "tone3" }, true)]
    [InlineData("", new[] { "tone3" }, true)]
    [InlineData("tone3", new[] { "tone3", "tone30" }, true)]
    [InlineData("zzz", new[] { "tone3" }, false)]
    public void TheListOpensUnlessItsOnlyMatchIsTheTypedText(string text, string[] keys, bool expected) =>
        Assert.Equal(expected, DeferredAutoCompleteBox.OffersSuggestions(text, keys));

    [Fact]
    public async Task AnOpenList_IsTheDialogCard_AsWideAsItsField_AndAnEmptySlotShowsItsPlaceholder()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var project = SurfaceShowRewarmUiPerfSmokeTests.ToneProject(1, 4);
            project.Arrangements[0].ToneC = "";
            var services = new ServiceCollection();
            services.AddSingleton<DialogsSlot>();
            services.AddScoped<IToneDesignerScreen>(_ => SurfaceShowRewarmUiPerfSmokeTests.ToneDesigner(project));
            using var provider = services.BuildServiceProvider();
            SurfaceShowRewarmUiPerfSmokeTests.WithDialogHost(provider, (host, window) =>
            {
                _ = host.ShowAsync<IToneDesignerScreen, bool>();
                Settle(window);
                var view = host.OpenViews[^1];
                var slots = view.GetVisualDescendants().OfType<DeferredAutoCompleteBox>().Where(s => s.IsEffectivelyVisible).ToList();

                // The empty slot C reads as an input: its resting label is the placeholder.
                var label = slots[3].GetVisualDescendants().OfType<TextBlock>().First();
                Assert.False(string.IsNullOrWhiteSpace(label.Text), "an empty slot shows nothing, a grey block");
                Assert.Equal(slots[3].PlaceholderText, label.Text);

                var box = slots[1].Realize();
                box.Text = "";
                box.IsDropDownOpen = true;
                Settle(window);
                Assert.True(box.IsDropDownOpen, "the list did not open on an empty field");

                var popup = box.GetTemplateDescendants().OfType<Popup>().First();
                var card = Assert.IsType<Border>(popup.Child);
                Assert.Equal(slots[1].Bounds.Width, card.Width, 0.5);
                Assert.Equal(1, card.BorderThickness.Left);
                Assert.True(card.Padding.Left > 0, "the list card has no padding");
                Assert.NotNull(card.Background);
                card.Measure(new Avalonia.Size(2000, 2000));
                Assert.True(card.DesiredSize.Width > 1 && card.DesiredSize.Height > 1,
                    $"the open list measured {card.DesiredSize}");
                Assert.True(card.GetVisualDescendants().OfType<ListBox>().Any(), "the open list has no rows");
                Capture(window, "slots-list-open");
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task TypingTheOnlyMatchingKey_LeavesTheListClosed()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var project = SurfaceShowRewarmUiPerfSmokeTests.ToneProject(1, 4);
            project.Arrangements[0].ToneC = "";
            var services = new ServiceCollection();
            services.AddSingleton<DialogsSlot>();
            services.AddScoped<IToneDesignerScreen>(_ => SurfaceShowRewarmUiPerfSmokeTests.ToneDesigner(project));
            using var provider = services.BuildServiceProvider();
            SurfaceShowRewarmUiPerfSmokeTests.WithDialogHost(provider, (host, window) =>
            {
                _ = host.ShowAsync<IToneDesignerScreen, bool>();
                Settle(window);
                var slots = host.OpenViews[^1].GetVisualDescendants().OfType<DeferredAutoCompleteBox>().Where(s => s.IsEffectivelyVisible).ToList();
                var box = slots[3].Realize();
                box.GetVisualDescendants().OfType<TextBox>().First().Focus();
                box.Text = "";
                Settle(window);

                window.KeyTextInput("tone");
                Settle(window);
                Assert.True(box.IsDropDownOpen, "a partial key offered nothing");

                window.KeyTextInput("3");
                Settle(window);
                Assert.Equal("tone3", box.Text);
                Assert.False(box.IsDropDownOpen, "the list repeats the key that is already typed");
                Assert.False(box.GetTemplateDescendants().OfType<Popup>().First().IsOpen, $"the popup still shows the key that is already typed (open={box.IsDropDownOpen} items={box.ItemsSource?.Cast<object>().Count()} text={box.Text})");
                Capture(window, "slots-typed-key");
            });
        }, CancellationToken.None);
    }

    private static void Capture(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"{name}-{HeadlessSessionFixture.SweptCulture}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
