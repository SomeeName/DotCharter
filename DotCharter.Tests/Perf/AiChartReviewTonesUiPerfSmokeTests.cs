using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Ai.Chart;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Ai.Chart;
using DotCharter.Tests.Ai.Chat;
using DotCharter.Tests.Ai.Generate;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.UI.Views.Shell;
using static DotCharter.Tests.Perf.AiChartReviewHarness;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The review card over the AI chat's Tones proposals (#614), in the dense row (#613): one row per tone —
///     its name, its song window, the proposed chain in short, Audition, ✓ / ✕ — each MEASURED and inside the
///     card, the row's tooltip carrying the whole proposed chain beside the current one, and a tone with no
///     body in the project yet showing Audition disabled. The REAL review view model in each shipped culture.
/// </summary>
[Collection("HeadlessUI")]
public sealed class AiChartReviewTonesUiPerfSmokeTests
{
    /// <summary>Every shipped resx: German is the longest, Japanese the widest per glyph.</summary>
    private static readonly string[] Cultures = ["en-US", "de-DE", "ja-JP"];

    [Fact]
    public Task AToneReview_ShowsEachTone_AsADenseRowWithAudition_InEveryShippedCulture() =>
        HeadlessSessionFixture.Dispatch(async () =>
        {
            var restore = LocalizationService.Instance.ConfiguredCulture;
            try
            {
                foreach (var culture in Cultures)
                {
                    LocalizationService.Instance.SetConfiguredCulture(culture);
                    var (vm, run, f) = NewToneReview();
                    await run.RunInstructionAsync(new AiChartInstructedRun(AiChartJob.Tones,
                        new AiChartInstruction("darker"), ToneChanges: f.All().ToneChanges));
                    Assert.True(vm.IsOpen, culture + ": no tone review held");
                    Assert.Equal(3, vm.Rows.Count);
                    Assert.Contains(LocalizationService.Instance["AiChartReview_HintTones"], vm.HintText, StringComparison.Ordinal);

                    var view = new AiChartReviewView { DataContext = vm };
                    var (window, _) = NewTimelineHost(720, DefaultTimelineHeight, view);
                    window.Show();
                    try
                    {
                        NarrowWidthLayout.Settle(window);
                        var card = view.FindControl<Border>("AiChartReviewCard")!;
                        AssertMeasured(card);
                        var rows = Rows(view);
                        Assert.Equal(vm.Rows.Count, rows.Count);
                        for (var i = 0; i < rows.Count; i++)
                        {
                            var row = rows[i];
                            var tone = vm.Rows[i].Tone!;
                            AssertMeasured(row);
                            Assert.True(row.Bounds.Height <= 30, $"{culture}: a tone row is {row.Bounds.Height} px tall, not one dense line");
                            var buttons = row.GetVisualDescendants().OfType<Button>().ToList();
                            Assert.Equal(["AiChartReviewRowGo", "AiChartReviewAudition", "AiChartReviewRowAccept", "AiChartReviewRowReject"],
                                buttons.Select(b => b.Name));
                            foreach (var button in buttons)
                            {
                                AssertInsideCard(button, card);
                                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"{button.Name} has no name");
                                Assert.NotNull(ToolTip.GetTip(button));
                            }

                            var detail = Named<TextBlock>(row, "AiChartReviewRowDetail");
                            Assert.Equal(tone.ShortText, detail.Text);
                            AssertInsideCard(Named<TextBlock>(row, "AiChartReviewRowName"), card);
                            Assert.Equal(vm.Rows[i].Where, Named<TextBlock>(row, "AiChartReviewRowSpan").Text);
                            var audition = Named<Button>(row, "AiChartReviewAudition");
                            Assert.Equal(tone.CanAudition, audition.IsEffectivelyEnabled);
                            Assert.Equal(tone.AuditionTip, ToolTip.GetTip(audition));
                            // The whole chain, knobs and all, beside the current one: the row's tooltip.
                            var tip = (string)ToolTip.GetTip(Named<Button>(row, "AiChartReviewRowGo"))!;
                            Assert.Contains(tone.ProposedText, tip, StringComparison.Ordinal);
                            Assert.Contains(tone.CurrentText, tip, StringComparison.Ordinal);
                        }

                        // Lead_Dist and Clean have bodies; the third tone (no definition yet) has none to preview.
                        Assert.Equal([true, true, false], vm.Rows.Select(r => r.Tone!.CanAudition));
                        NarrowWidthLayout.Capture(window, $"ai-chart-review-tones-{culture}");
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                LocalizationService.Instance.SetConfiguredCulture(restore);
                CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
            }
        }, CancellationToken.None);

    private static (AiChartReviewViewModel Vm, AiChartPassRun Run, AiChatToneFixture F) NewToneReview()
    {
        var f = new AiChatToneFixture();
        // A third placed tone whose name has no definition yet: its row can be applied but not auditioned.
        f.Unnamed.ToneName = "Solo_Delay";
        var logger = new FakeAppLogger();
        var selection = new SelectionService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), f.State, new FakeConfigurationService(), FakeLocalization.KeysOnly());
        var editor = new ChartEditorViewModel(f.State, transport, f.History, selection, new TempoService(logger),
            new TrackContextRegistry(f.State, selection), FakeLocalization.KeysOnly());
        var run = f.Run(new FakeChartModel((_, _) => GenerateFakes.ValidReply()));
        return (new AiChartReviewViewModel(run, editor, f.History, f.State, LocalizationService.Instance, new NoAudition()), run, f);
    }

    private sealed class NoAudition : IAiToneAudition
    {
        public string? Start(string key, string json, double fromMs, double toMs) => null;

        public void Stop()
        {
        }
    }
}
