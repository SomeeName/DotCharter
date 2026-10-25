using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using DotCharter.Presentation.Fakes.Settings;
using DotCharter.UI.Views.Settings;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Settings ▸ Audio's OUTPUT PAIR row, attached to a window and laid out: one
///     named row per pair of the device, on WASAPI as well as ASIO (the row used to
///     be two ASIO-only number boxes), and a pick made in the view reaches the
///     view model. A combo that stayed hidden on WASAPI, or collapsed to nothing,
///     builds clean and fails only here.
/// </summary>
[Collection("HeadlessUI")]
public sealed class AudioOutputPairUiPerfSmokeTests
{
    private const double TabWidth = 960;
    private const double TabHeight = 1400;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheOutputPairRow_LaysOutOnEveryDriver_WithNamedRows(bool asio)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var screen = asio ? FakeAudioSettingsScreen.Full() : FakeAudioSettingsScreen.Default();
            var tab = new AudioSettingsView { DataContext = screen };
            WithLaidOutTab(tab, () =>
            {
                var combo = tab.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "AudioOutputPair");
                Assert.Equal(asio, screen.IsAsioOutput);
                Assert.True(combo.IsEffectivelyVisible, $"the output pair row must show with asio={asio}");
                Assert.True(combo.Bounds is { Width: > 1, Height: > 1 }, $"the output pair picker laid out {combo.Bounds}");
                Assert.Equal(screen.OutputPairOptions.Count, combo.ItemCount);
                // The ASIO fixture's driver names its channels; WASAPI's never does.
                Assert.Equal(asio ? "Out 3 + 4 · Headphones" : "Out 1 + 2", combo.SelectedItem?.ToString());
                Capture(tab, asio ? "audio-output-pair-asio" : "audio-output-pair-wasapi");
            });
        }, CancellationToken.None);
    }

    /// <summary>The REAL view model behind the view: a pick in the combo writes both channels.</summary>
    [Fact]
    public async Task APickInTheCombo_WritesBothOutputChannels_OfTheRealViewModel()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var rig = new OnboardingWizardRig();
            var audio = rig.AudioSettings;
            audio.AsioOutputChannelLeft = 2;
            audio.AsioOutputChannelRight = 3;
            audio.RefreshOutputPairOptions(8);
            var tab = new AudioSettingsView { DataContext = audio };
            WithLaidOutTab(tab, () =>
            {
                var combo = tab.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "AudioOutputPair");
                Assert.True(combo.IsEffectivelyVisible);
                Assert.Equal(4, combo.ItemCount);
                Assert.Equal(1, combo.SelectedIndex);

                combo.SelectedIndex = 3;

                Assert.Equal((6, 7), (audio.AsioOutputChannelLeft, audio.AsioOutputChannelRight));
            });
        }, CancellationToken.None);
    }

    /// <summary>Before the probe lands the picker is empty, and the row still lays out.</summary>
    [Fact]
    public async Task WithNoDevices_TheRowStillLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tab = new AudioSettingsView { DataContext = FakeAudioSettingsScreen.Empty() };
            WithLaidOutTab(tab, () =>
            {
                var combo = tab.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "AudioOutputPair");
                Assert.True(combo.IsEffectivelyVisible && combo.Bounds is { Width: > 1, Height: > 1 }, $"laid out {combo.Bounds}");
                Assert.Equal(0, combo.ItemCount);
            });
        }, CancellationToken.None);
    }

    private static void Capture(Control tab, string name)
    {
        var window = (Window)TopLevel.GetTopLevel(tab)!;
        NarrowWidthLayout.Settle(window);
        NarrowWidthLayout.Capture(window, name);
    }

    private static void WithLaidOutTab(Control tab, Action assert)
    {
        var window = new Window { Width = TabWidth, Height = 900, Content = tab };
        window.Show();
        try
        {
            tab.Measure(new Size(TabWidth, TabHeight));
            tab.Arrange(new Rect(0, 0, TabWidth, TabHeight));
            assert();
        }
        finally
        {
            window.Close();
        }
    }
}
