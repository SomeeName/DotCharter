using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using DotCharter.UI;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #343: the labels of disabled checkboxes on the dark rig cards (the VST rack's
///     "Virtual instrument" card) drew near-black, so the user could not tell what was
///     disabled. The checkbox is hosted on a real Fire panel card and the label's
///     RESOLVED foreground, dimmed by the checkbox's own opacity, must keep a minimum
///     contrast against the card.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigDisabledCheckBoxUiPerfSmokeTests
{
    private const double MinimumContrast = 3.0;

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color Blend(Color top, double alpha, Color under) => Color.FromRgb(
        (byte)Math.Round(top.R * alpha + under.R * (1 - alpha)),
        (byte)Math.Round(top.G * alpha + under.G * (1 - alpha)),
        (byte)Math.Round(top.B * alpha + under.B * (1 - alpha)));

    [Theory]
    [InlineData("AmpFxChartSynthToggle")]
    [InlineData("AmpFxInstrumentThroughRigToggle")]
    public Task ADisabledCheckBoxLabel_StaysReadable_OnTheVirtualInstrumentCard(string name) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var shell = (App.Services ?? throw new InvalidOperationException("the headless app session did not boot."))
                .GetRequiredService<MainWindowViewModel>();
            var panel = new AmpFxPanel { DataContext = shell.Transport };
            var window = new Window { Width = 520, Height = 900, Content = panel };
            window.Show();
            try
            {
                for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
                var box = panel.GetVisualDescendants().OfType<CheckBox>().First(c => c.Name == name);
                box.IsEnabled = false;
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                var card = panel.GetVisualDescendants().OfType<Border>().First(b => b.Name == "AmpFxInstrumentSection");
                var label = box.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                var fore = Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color;
                // the card's own fill is a translucent wash: flatten every ancestor's background
                // over the app's panel ground to get what the eye sees behind the label
                Assert.True(Application.Current!.TryFindResource("PanelBackgroundColor", out var ground), "PanelBackgroundColor missing");
                var back = (Color)ground!;
                foreach (var fill in card.GetVisualAncestors().Reverse().Append(card).OfType<Border>().Select(b => b.Background).OfType<ISolidColorBrush>())
                    back = Blend(fill.Color, fill.Color.A / 255.0, back);
                var shown = Blend(fore, fore.A / 255.0 * box.Opacity, back);
                var hi = Math.Max(Luminance(shown), Luminance(back));
                var lo = Math.Min(Luminance(shown), Luminance(back));
                var ratio = (hi + 0.05) / (lo + 0.05);
                Assert.True(ratio >= MinimumContrast,
                    $"{name}: disabled label {fore} at opacity {box.Opacity} on {back} reads at {ratio:F2}:1, under {MinimumContrast}:1");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
}
