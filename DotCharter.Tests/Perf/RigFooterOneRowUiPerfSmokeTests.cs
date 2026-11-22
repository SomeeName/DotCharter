using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using DotCharter.Localization;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #342: at 1586 px the rig footer (tuner, tempo, jack, drop tune, output, CPU) no longer
///     fitted one row, so the INPUT meter and the OUTPUT cluster fell onto a second row flush
///     against the card's bottom border. The footer is measured here in every shipped culture,
///     with every cluster the app can show, and must stay one row: both meters share one Y and
///     the bar is as tall as it is at 1920 px, where it is known to be one row.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigFooterOneRowUiPerfSmokeTests(ITestOutputHelper output)
{
    private static readonly string[] Cultures = ["en-US", "de-DE", "ja-JP"];

    private static (double Height, double InputY, double OutputY, double SlackBelow) Measure(double width, bool every)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        using var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(catalogue), new FakeDspTonePresetService(),
            LocalizationService.Instance);
        foreach (var id in new[] { "overdrive", "amp", "cab" })
            rig.AddGear(catalogue.Single(m => m.Id == id));
        rig.SelectNodeAt(0);

        var dropSemis = 0;
        var model = new AmpFxRigViewModel(rig)
        {
            DropTune = every ? new RigDropTuneViewModel(() => dropSemis, v => dropSemis = v) : null,
            CallbackLoadProvider = every ? () => new AudioCallbackLoad(0.14, 0.31) : null
        };
        var view = new AmpFxRigView { DataContext = model };
        var window = new Window { Width = width, Height = 1000, Content = view };
        window.Show();
        try
        {
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
            double TopOf(string name)
            {
                var c = view.FindControl<Control>(name)!;
                return c.TranslatePoint(default, view)!.Value.Y;
            }

            var footer = view.FindControl<Border>("RigFooter")!;
            var lowest = new[] { "RigTunerStrip", "RigTempoStrip", "RigInputChannelPicker", "RigDropTuneStrip", "RigOutputStrip", "RigCpuStrip" }
                .Select(n => view.FindControl<Control>(n)).OfType<Control>().Where(c => c.IsVisible)
                .Max(c => c.TranslatePoint(new Point(0, c.Bounds.Height), footer)!.Value.Y);
            return (view.FindControl<Border>("RigFooter")!.Bounds.Height, TopOf("RigInputMeter"), TopOf("RigOutputMeter"), footer.Bounds.Height - lowest);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The clusters the headless UI sweep shows: no drop pedal, no CPU reader.</summary>
    [Fact]
    public Task TheFooter_StaysOneRow_At1586px_InEveryCulture() =>
        HeadlessSessionFixture.Dispatch(() => InEveryCulture(culture =>
        {
            var wide = Measure(1920, false);
            var narrow = Measure(1586, false);
            output.WriteLine($"{culture}: 1920 h={wide.Height:F0} in={wide.InputY:F0} out={wide.OutputY:F0}; " +
                             $"1586 h={narrow.Height:F0} in={narrow.InputY:F0} out={narrow.OutputY:F0}");
            Assert.True(Math.Abs(narrow.InputY - narrow.OutputY) < 3,
                $"{culture}: at 1586 px the Input meter (y={narrow.InputY:F0}) and the Output meter (y={narrow.OutputY:F0}) are on different rows");
            Assert.Equal(wide.Height, narrow.Height, 1);
        }), TestContext.Current.CancellationToken);

    /// <summary>With every cluster the app can show the bar may wrap, but the second row is padded like the first.</summary>
    [Fact]
    public Task AWrappedFooter_KeepsTheSamePaddingBelowItsLastRow_AsAboveItsFirst() =>
        HeadlessSessionFixture.Dispatch(() => InEveryCulture(culture =>
        {
            var m = Measure(1300, true);
            output.WriteLine($"{culture}: 1300 h={m.Height:F0} slack below={m.SlackBelow:F1}");
            Assert.True(m.Height > 60, $"{culture}: the bar did not wrap at 1300 px, so the case proves nothing");
            Assert.True(m.SlackBelow >= 3.5, $"{culture}: the last row sits {m.SlackBelow:F1} px above the card's bottom edge");
        }), TestContext.Current.CancellationToken);

    private static void InEveryCulture(Action<string> body)
    {
        var restore = LocalizationService.Instance.ConfiguredCulture;
        try
        {
            foreach (var culture in Cultures)
            {
                LocalizationService.Instance.SetConfiguredCulture(culture);
                body(culture);
            }
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture(restore);
            CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
        }
    }
}
