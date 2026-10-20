// The tone designer's slot marks (#195): a Base / A–D field that names a key with no definition carries the no-definition mark, measured, under that field only.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    private static readonly string[] SlotMarkNames =
        ["ToneBaseNotDefinedMark", "ToneANotDefinedMark", "ToneBNotDefinedMark", "ToneCNotDefinedMark", "ToneDNotDefinedMark"];

    /// <summary>
    ///     Lead's slot B names <c>lead_dist</c>, which the project does not define; every other slot
    ///     names a defined tone or nothing. Exactly that field shows the mark, it MEASURES (a mark
    ///     whose style never applied still arranges, with no size), it sits under its own field, and
    ///     typing a defined key into the slot takes it away.
    /// </summary>
    [Fact]
    public async Task ToneDesigner_MarksTheSlotThatNamesAnUndefinedKey_AndOnlyThatOne()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = CreateToneDesignerViewModel(lead =>
            {
                lead.ToneBase = "clean";
                lead.ToneA = "clean";
                lead.ToneB = "lead_dist";
            });
            var dialog = new ToneDesignerDialogView { DataContext = vm };

            WithLaidOutDialog(dialog, () =>
            {
                CaptureToneDesigner(dialog, "tone-designer-slot-marks.png");
                var lead = vm.Arrangements.Single(r => r.Name == "Lead");
                Assert.True(lead.IsToneBUndefined, "the view model does not mark lead_dist as undefined");

                List<Border> VisibleMarks() =>
                [
                    .. Descendants<Border>(dialog).Where(b => SlotMarkNames.Contains(b.Name) && b.IsEffectivelyVisible)
                ];

                var marks = VisibleMarks();
                var mark = Assert.Single(marks);
                Assert.Equal("ToneBNotDefinedMark", mark.Name);
                Assert.Same(lead, mark.DataContext);
                Assert.True(mark.Bounds is { Width: > 1, Height: > 1 }, $"the slot mark laid out as {mark.Bounds}");
                Assert.True(mark.Classes.Contains("DlgStrip") && mark.Classes.Contains("Notice"),
                    "the slot mark is not the dialog grammar's notice strip");
                Assert.True(mark.BorderThickness.Left >= 2, "the notice strip's bar never applied — DlgStrip matched no selector");
                var text = Descendants<TextBlock>(mark).Single();
                Assert.Equal(DotCharter.Localization.LocalizationService.Instance["Inspector_Tones_NotDefined"], text.Text);
                Assert.True(text.Bounds is { Width: > 1, Height: > 1 }, $"the mark's reason laid out as {text.Bounds}");

                // Under ITS field: the slot B box is the mark's sibling, directly above it.
                var field = Assert.IsType<StackPanel>(mark.Parent).Children.OfType<DeferredAutoCompleteBox>().Single();
                Assert.Equal("lead_dist", field.Text);
                var fieldBottom = field.TranslatePoint(new Point(0, field.Bounds.Height), dialog)!.Value.Y;
                var markTop = mark.TranslatePoint(default, dialog)!.Value.Y;
                Assert.True(markTop >= fieldBottom - 0.5, $"the mark (y {markTop:0}) is not under its field (bottom {fieldBottom:0})");

                lead.ToneB = "dist";
                dialog.UpdateLayout();
                Assert.Empty(VisibleMarks());
            });

            vm.CloseCommand.Execute(null);
        }, CancellationToken.None);
    }

    private static void CaptureToneDesigner(Control dialog, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir) || TopLevel.GetTopLevel(dialog) is not Window window) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, file), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
