using System.ComponentModel;
using System.Reflection;
using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     The editor preview builds a <see cref="ShowlightChipLabelSource" /> on every
///     <c>SetEditor</c> (each attach of the 3D canvas), and the source follows the
///     process-wide localization service. Without an unsubscribe, every re-attach
///     left one more source rooted by that service for the process lifetime.
/// </summary>
public sealed class ShowlightChipLabelSourceLifetimeTests
{
    private static readonly FieldInfo PropertyChangedField =
        typeof(FakeLocalization).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("FakeLocalization.PropertyChanged has no backing field any more.");

    [Fact]
    public void DisposingASourceUnsubscribesItFromTheLanguage()
    {
        var localization = FakeLocalization.English();

        var sources = Enumerable.Range(0, 5).Select(_ => new ShowlightChipLabelSource(localization)).ToList();
        Assert.Equal(5, Subscribers(localization));

        foreach (var source in sources) source.Dispose();
        Assert.Equal(0, Subscribers(localization));
    }

    [Fact]
    public void ALiveSourceRebuildsAfterALanguageChange()
    {
        var localization = FakeLocalization.English();
        using var source = new ShowlightChipLabelSource(localization);

        var before = source.Current();
        Assert.Same(before, source.Current());

        // A language switch, raised straight on the fake's event: no culture moves.
        RaiseLanguageChanged(localization);
        Assert.NotSame(before, source.Current());
    }

    [Fact]
    public void TheRenderDriverDisposesTheSourcesItReplacesAndDetaches()
    {
        var localization = FakeLocalization.English();
        var logger = new FakeAppLogger();
        var state = new ProjectState();
        var selection = new DotCharter.Services.Editing.Selection.SelectionService();
        var config = new FakeConfigurationService();
        var transport = new DotCharter.UI.ViewModels.Editor.TransportViewModel(
            new FakeAudioPlaybackService(), state, config, localization);
        var tracks = new DotCharter.Services.Editing.Tracks.TrackContextRegistry(state, selection);
        var editor = new DotCharter.UI.ViewModels.Editor.ChartEditorViewModel(state, transport,
            new HistoryService(logger), selection, new TempoService(logger), tracks, localization);
        var baseline = Subscribers(localization);
        var driver = new Preview3DRenderDriver(() => false);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                driver.SetEditor(editor);
                Assert.Equal(baseline + 1, Subscribers(localization));
            }

            driver.SetEditor(null);
            Assert.Equal(baseline, Subscribers(localization));
            driver.SetEditor(editor);
            Assert.Equal(baseline + 1, Subscribers(localization));
            driver.DetachWakeSources();
            Assert.Equal(baseline, Subscribers(localization));
        }
        finally
        {
            driver.SetEditor(null);
            driver.DetachWakeSources();
        }
    }

    private static void RaiseLanguageChanged(FakeLocalization localization) =>
        (PropertyChangedField.GetValue(localization) as PropertyChangedEventHandler)?.Invoke(
            localization, new PropertyChangedEventArgs(nameof(FakeLocalization.Culture)));

    private static int Subscribers(FakeLocalization localization) =>
        (PropertyChangedField.GetValue(localization) as Delegate)?.GetInvocationList().Length ?? 0;

}
