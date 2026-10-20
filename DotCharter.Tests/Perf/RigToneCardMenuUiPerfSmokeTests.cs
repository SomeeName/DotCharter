using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.Localization;

namespace DotCharter.Tests.Perf;

/// <summary>
///     A tone card's right-click menu, through the REAL rig view: a right-click
///     (and the keyboard's context request) on a card opens a menu for THAT card
///     whose items lay out; on a built-in, Rename / Overwrite / Delete are there
///     but disabled with their reason; Delete… opens the inline confirmation and
///     Rename / edit details… the save dialog in edit mode, and both measure
///     non-zero. The commands' own rules are
///     <c>UI/ViewModels/PlayMode/PresetBrowserViewModelTests</c>.
///     <para>
///         Over a REALISTIC library — the factory catalogue plus several of the
///         player's own tones with long names — because a menu over three short
///         names never shows a clipped header. The capture half is inert unless
///         <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and <c>DOTCHARTER_RIG_CAPTURE_DIR</c>
///         are both set.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class RigToneCardMenuUiPerfSmokeTests(ITestOutputHelper output)
{
    private static DcRigPreset Rig(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new Dictionary<string, float>())).ToList());

    /// <summary>The player's own tones, long names on purpose.</summary>
    private static IReadOnlyList<DspTonePreset> UserTones() =>
    [
        new("u1", "Sunday Worship Swell — Ambient Pad With Long Shimmer Tail", DspToneCategory.Ambient,
            "Volume swells into a shimmer; keep the pick soft.", Rig("compressor", "amp", "cab", "delay", "reverb"),
            DspToneOrigin.User),
        new("u2", "Drop C Chug", DspToneCategory.HighGain, "", Rig("gate", "overdrive", "amp", "cab"),
            DspToneOrigin.User),
        new("u3", "Überdrive Grundton (Proberaum)", DspToneCategory.Crunch,
            "Der Sound aus dem Proberaum, etwas weniger Bass.", Rig("overdrive", "amp", "cab", "eq"),
            DspToneOrigin.User),
        new("u4", "Bass DI + Amp Blend", DspToneCategory.Bass, "Blend of a clean DI and a driven amp.",
            Rig("compressor", "amp", "cab"), DspToneOrigin.User),
        new("u5", "Texas Flood Rhythm (live at the El Mocambo, 1983) — neck pickup", DspToneCategory.Crunch,
            "", Rig("overdrive", "amp", "cab", "reverb"), DspToneOrigin.User),
        new("u6", "Clean Funk", DspToneCategory.Clean, "", Rig("compressor", "amp", "cab"), DspToneOrigin.User)
    ];

    private static void WithTones(
        Action<AmpFxRigView, RigTonesView, PedalboardViewModel, Window> body, bool userRail,
        double width = 1600, double height = 900)
    {
        var metadata = new JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var chain = new FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(
            chain, new FakeDspTonePresetService([.. DspTonePresetLibrary.All, .. UserTones()]), LocalizationService.Instance,
            fileStorage: new FakeFileStorageService());
        foreach (var id in new[] { "compressor", "overdrive", "amp", "cab", "delay", "reverb" })
        {
            if (catalogue.FirstOrDefault(m => m.Id == id) is { } module)
                rig.AddGear(module);
        }

        rig.Browser.SelectedRail = rig.Browser.Rail.First(r =>
            r.Kind == (userRail ? PresetFilterKind.User : PresetFilterKind.All));

        var view = new AmpFxRigView { DataContext = new AmpFxRigViewModel(rig) };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            rig.IsBrowserOpen = true;
            Settle(window);
            body(view, view.FindControl<RigTonesView>("RigTonesMode")!, rig, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Settle(Window window)
    {
        for (var round = 0; round < 3; round++)
        {
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
    }

    private static Control Find(Visual root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"'{name}' is not in the tree");

    private static Button CardButton(Visual view, string id) =>
        view.GetVisualDescendants().OfType<Button>()
            .First(b => b.Classes.Contains("FirePresetCard") && b.IsEffectivelyVisible
                        && b.DataContext is PresetCardViewModel { Id: var cardId } && cardId == id);

    private static void RightClick(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Settle(window);
    }

    private static MenuItem Item(ContextMenu menu, string name) =>
        menu.Items.OfType<MenuItem>().Single(i => i.Name == name);

    private static void AssertMeasured(Control c, string what) =>
        Assert.True(c.IsEffectivelyVisible && c.Bounds is { Width: > 1, Height: > 1 },
            $"{what} laid out as {c.Bounds} (visible: {c.IsEffectivelyVisible})");

    private static void Capture(Window window, string file)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.True(frame is not null,
            "CaptureRenderedFrame returned null — DOTCHARTER_UI_SWEEP_DUMP=1 has to be set "
            + "BEFORE the session starts, or UseHeadlessDrawing is still true");
        frame!.Save(Path.Combine(dir, file), new PngBitmapEncoderOptions());
    }

    private static readonly string[] AllItems =
    [
        "RigToneCardLoad", "RigToneCardEdit", "RigToneCardDuplicate", "RigToneCardOverwrite",
        "RigToneCardExport", "RigToneCardDelete"
    ];

    [Fact]
    public Task ARightClickOnAUserTone_OpensItsMenu_WithEveryItemLive() =>
        HeadlessSessionFixture.Dispatch(() => WithTones((view, tones, rig, window) =>
        {
            var card = CardButton(view, "u1");
            var selectedBefore = rig.Browser.SelectedCard;
            RightClick(window, card);

            var menu = tones.CardMenu;
            Assert.NotNull(menu);
            Assert.True(menu.IsOpen, "a right-click on a tone card opened no menu");
            Assert.Same(card.DataContext, menu.Tag);
            // a right-click is not a selection
            Assert.Same(selectedBefore, rig.Browser.SelectedCard);

            foreach (var name in AllItems)
            {
                var item = Item(menu, name);
                AssertMeasured(item, name);
                Assert.True(item.IsEffectivelyEnabled, $"{name} is disabled on the player's own tone");
                Assert.Same(card.DataContext, item.CommandParameter);
            }

            output.WriteLine($"menu {Item(menu, "RigToneCardLoad").Bounds.Width:F0} px wide");
            Capture(window, "tone-card-menu-user.png");
            menu.Close();
        }, userRail: true), TestContext.Current.CancellationToken);

    [Fact]
    public Task OnABuiltIn_RenameOverwriteAndDelete_AreShownDisabled_WithTheirReason() =>
        HeadlessSessionFixture.Dispatch(() => WithTones((view, tones, _, window) =>
        {
            var builtIn = view.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("FirePresetCard") && b.IsEffectivelyVisible
                            && b.DataContext is PresetCardViewModel { IsUser: false });
            RightClick(window, builtIn);

            var menu = tones.CardMenu!;
            Assert.True(menu.IsOpen);
            foreach (var name in new[] { "RigToneCardEdit", "RigToneCardOverwrite", "RigToneCardDelete" })
            {
                var item = Item(menu, name);
                AssertMeasured(item, name);
                Assert.False(item.IsEffectivelyEnabled, $"{name} is live on a built-in tone");
                Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(item) as string),
                    $"{name} is disabled without saying why");
                Assert.True(ToolTip.GetShowOnDisabled(item), $"{name}'s reason cannot show while it is disabled");
                Assert.True(ToolTip.GetPlacement(item) == PlacementMode.Right,
                    $"{name}'s reason opens over the menu and hides the items above it");
            }

            foreach (var name in new[] { "RigToneCardLoad", "RigToneCardDuplicate", "RigToneCardExport" })
                Assert.True(Item(menu, name).IsEffectivelyEnabled, $"{name} is disabled on a built-in tone");

            Capture(window, "tone-card-menu-builtin.png");
            if (UiSweepRunner.CaptureEnabled)
            {
                // the reason, as the player sees it on hovering a disabled item
                var edit = Item(menu, "RigToneCardEdit");
                ToolTip.SetIsOpen(edit, true);
                Settle(window);
                Capture(window, "tone-card-menu-builtin-reason.png");
                ToolTip.SetIsOpen(edit, false);
            }

            menu.Close();
        }, userRail: false), TestContext.Current.CancellationToken);

    /// <summary>The keyboard's context request (the menu key, Shift+F10) reaches
    /// the same handler: Avalonia raises it as a ContextRequested event with no
    /// pointer, and the menu then opens under the card rather than at a pointer
    /// that is somewhere else.</summary>
    [Fact]
    public Task TheKeyboardsContextRequest_OpensTheSameMenu_UnderTheCard() =>
        HeadlessSessionFixture.Dispatch(() => WithTones((view, tones, _, window) =>
        {
            var card = CardButton(view, "u2");
            card.RaiseEvent(new ContextRequestedEventArgs());
            Settle(window);

            var menu = tones.CardMenu;
            Assert.NotNull(menu);
            Assert.True(menu.IsOpen, "a keyboard context request on a tone card opened no menu");
            Assert.Same(card.DataContext, menu.Tag);
            Assert.Equal(PlacementMode.BottomEdgeAlignedLeft, menu.Placement);
            AssertMeasured(Item(menu, "RigToneCardLoad"), "Load");
            menu.Close();
        }, userRail: true), TestContext.Current.CancellationToken);

    [Fact]
    public Task DeleteFromTheMenu_AsksFirst_InAConfirmationThatLaysOut() =>
        HeadlessSessionFixture.Dispatch(() => WithTones((view, tones, rig, window) =>
        {
            var card = CardButton(view, "u5");
            RightClick(window, card);
            var menu = tones.CardMenu!;
            var delete = Item(menu, "RigToneCardDelete");
            delete.Command!.Execute(delete.CommandParameter);
            menu.Close();
            Settle(window);

            var dialog = Find(view, "RigBrowserDeleteDialog");
            AssertMeasured(dialog, "the delete confirmation");
            AssertMeasured(Find(view, "RigDeleteDialogTitle"), "the confirmation's title");
            AssertMeasured(Find(view, "RigDeleteDialogConfirm"), "its Delete button");
            // …drawn in the danger red, not the Save blue: the FILL the template
            // paints, which is where a style that failed to apply would show.
            var fill = Find(view, "RigDeleteDialogConfirm").GetVisualDescendants().OfType<Border>()
                .First(b => b.Name == "Cut").Background as ISolidColorBrush;
            Assert.True(view.TryFindResource("FireDangerColor", view.ActualThemeVariant, out var danger),
                "FireDangerColor did not resolve");
            Assert.True(fill?.Color == (Color)danger!,
                $"the Delete button is filled {fill?.Color} — not the danger red {danger}");
            AssertMeasured(Find(view, "RigDeleteDialogCancel"), "its Cancel button");
            Assert.Contains(rig.Browser.Cards, c => c.Id == "u5");     // asked, not deleted
            Capture(window, "tone-card-delete-confirm.png");

            ((Button)Find(view, "RigDeleteDialogCancel")).Command!.Execute(null);
            Settle(window);
            Assert.False(dialog.IsVisible, "Cancel did not close the confirmation");
            Assert.Contains(rig.Browser.Cards, c => c.Id == "u5");
        }, userRail: true), TestContext.Current.CancellationToken);

    [Fact]
    public Task RenameFromTheMenu_OpensTheSaveDialog_InEditMode() =>
        HeadlessSessionFixture.Dispatch(() => WithTones((view, tones, rig, window) =>
        {
            var card = CardButton(view, "u3");
            RightClick(window, card);
            var menu = tones.CardMenu!;
            var edit = Item(menu, "RigToneCardEdit");
            edit.Command!.Execute(edit.CommandParameter);
            menu.Close();
            Settle(window);

            var dialog = Find(view, "RigBrowserSaveDialog");
            AssertMeasured(dialog, "the edit dialog");
            var title = (TextBlock)Find(view, "RigSaveDialogTitle");
            AssertMeasured(title, "the edit dialog's title");
            Assert.Equal(rig.Browser.SaveDialogTitle, title.Text);
            Assert.True(rig.Browser.IsEditingDetails);
            Assert.Equal("Überdrive Grundton (Proberaum)", ((TextBox)Find(view, "RigSaveNameBox")).Text);
            Capture(window, "tone-card-edit-dialog.png");

            rig.Browser.CancelSaveCommand.Execute(null);
        }, userRail: true), TestContext.Current.CancellationToken);
}
