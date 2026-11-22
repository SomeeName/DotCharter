using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Editor;
using Microsoft.Extensions.DependencyInjection;
using static DotCharter.Tests.Perf.NoteSectionStripUiPerfSmokeTests;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #751 (inspector design D, phase 1 review): the Note tab's pinned footer in the REAL <see cref="InspectorView" />
///     over <see cref="NoteSectionStripUiPerfSmokeTests" />' templated 240-sound chart.
///     <list type="bullet">
///         <item>At 480 x 370 and 530 x 370, in en-US, de-DE and ja-JP, with a preview-active rung and the refused
///         reshape hint showing: the footer is ONE row (the "Apply to" picker and a short reach), the #485 preview and
///         the hint stand in their own strip over it, outside the card scroller, nothing overlaps and no text is
///         cut.</item>
///         <item>The footer prints the playhead as time only and follows it.</item>
///         <item>At the dock's 320 px floor the place in the queue gives up its width first and nothing spills out of
///         the footer.</item>
///         <item>The keyboard reaches the "Apply to" ladder before the card's controls.</item>
///     </list>
///     Width-shaped, so this class is in the release's de-DE / ja-JP layout filter (<c>$HeadlessLayoutFilter</c>).
///     With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> it writes PNGs to <c>TestResults/note-section-strip/</c>.
/// </summary>
[Collection("HeadlessUI")]
public sealed class NoteFooterUiPerfSmokeTests
{
    private static readonly string[] Cultures = ["en-US", "de-DE", "ja-JP"];

    [Theory]
    [InlineData(480)]
    [InlineData(530)]
    public async Task TheFooter_IsOneRow_WithThePreviewAndTheHintPinnedOverIt(int width) =>
        await HeadlessSessionFixture.Dispatch(() => InEveryCulture(culture => WithPane(width, 370, pane =>
        {
            var at = $"at {width}x370 ({culture})";
            var panel = pane.Vm.ChordTemplateEditor;
            Select(pane, MostUsedShapeChord(pane));
            Open(pane, SoundSection.Technique);
            panel.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
            panel.Ladder.ShowsReshapeHint = true;
            Settle(pane.Window);
            Capture(pane, $"footer-{width}-{culture}");

            var sound = Sound(pane);
            Assert.True(panel.Preview.IsActive, $"{at} the rung previews nothing - this case tests nothing");
            var footer = Named<Border>(sound, "NoteFooter");
            var rail = Named<FooterRail>(sound, "NoteFooterRail");
            var preview = Named<Border>(sound, "NoteApplyPreviewStrip");
            var hint = Named<Border>(sound, "NoteReshapeHint");
            var bar = Named<Border>(sound, "SoundApplyBar");
            Assert.False(rail.IsStacked, $"{at} the footer takes two rows");
            Assert.InRange(footer.Bounds.Height, 1, 36);
            Assert.True(bar.IsEffectivelyVisible && bar.Bounds.Width > 1, $"{at} the ladder is not in the footer");

            // Card, preview, hint, footer: top to bottom, none over the next, the footer on the pane's foot.
            var card = OpenCard(sound);
            var bands = new (string Name, Control Part)[] { ("the card", card), ("the preview", preview), ("the hint", hint), ("the footer", footer) };
            foreach (var (name, part) in bands)
                Assert.True(part.IsEffectivelyVisible && part.Bounds is { Width: > 1, Height: > 1 }, $"{at} {name} laid out as {part.Bounds}");
            for (var i = 1; i < bands.Length; i++)
            {
                var above = Bottom(bands[i - 1].Part, pane.Window);
                var top = Top(bands[i].Part, pane.Window);
                Assert.True(above <= top + 0.5, $"{at} {bands[i - 1].Name} ends at {above:0.#}, under {bands[i].Name}'s top at {top:0.#}");
            }

            Assert.DoesNotContain(preview.GetVisualAncestors(), a => a is ScrollViewer);
            Assert.Equal(pane.Window.Bounds.Height, Bottom(footer, pane.Window), 1);
            AssertNoTextCut([footer, preview, hint], at);
        })), CancellationToken.None);

    [Fact]
    public async Task TheFooter_PrintsThePlayheadTime_AndFollowsIt() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 370, pane =>
        {
            var state = App.Services!.GetRequiredService<ProjectState>();
            var time = Named<TextBlock>(Sound(pane), "NoteFooterTime");
            state.CurrentTimeMs = 84_370;
            Settle(pane.Window);
            Assert.Equal("1:24.37", time.Text);
            Assert.True(time.IsEffectivelyVisible && time.Bounds.Width > 1, $"the playhead time laid out as {time.Bounds}");

            state.CurrentTimeMs = 5_020;
            Settle(pane.Window);
            Assert.Equal("0:05.02", time.Text);
        }), CancellationToken.None);

    /// <summary>
    ///     Short of room, the footer drops the place in the queue first, then the playhead time, then the ladder's short
    ///     reach, and only then takes a second row; the buttons, the ladder and the Shape page's whole Save set (Undo save,
    ///     Save with its count, Save and next, Save &amp; next missing, #751 P5 review H4) stay whole and nothing reaches
    ///     past the footer, down to the dock's 320 px floor. Save and next / Save &amp; next missing carry where they go
    ///     under their captions; with room Save and next's caption and line show.
    /// </summary>
    [Theory]
    [InlineData(320)]
    [InlineData(400)]
    [InlineData(480)]
    [InlineData(530)]
    [InlineData(640)]
    public async Task AtNarrowWidths_ThePlaceInTheQueueGivesWayFirst_AndNothingSpills(int width) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, 370, pane =>
        {
            var at = $"at {width}x370";
            Select(pane, MostUsedShapeChord(pane));
            Open(pane, SoundSection.Shape);
            var sound = Sound(pane);
            var rail = Named<FooterRail>(sound, "NoteFooterRail");
            var footer = Named<Border>(sound, "NoteFooter");
            Capture(pane, $"footer-{width}");

            var position = Named<TextBlock>(sound, "NoteFooterPosition");
            var time = Named<TextBlock>(sound, "NoteFooterTime");
            // Over Shape the footer also holds the page's whole Save set. 480 and up: one row (the Shape card keeps its
            // height, SoundShapeViewUiPerfSmokeTests). 400 and 320: the queue place, the time, the reach and the Save
            // set's captions go (their icons stay) before the ladder and the Save set take their own row, and the
            // navigation row gets the full width back.
            Assert.True(width > 400 || rail.IsStacked || (rail.Dropped >= 1 && !position.IsVisible),
                $"{at} the place in the queue stayed in a full row (dropped {rail.Dropped}, stacked {rail.IsStacked})");
            Assert.True(width <= 400 || !rail.IsStacked, $"{at} the footer takes two rows");
            Assert.True(!rail.IsStacked || rail.DropsBeforeStacking == rail.MaxDroppable,
                $"{at} the footer took two rows with {rail.MaxDroppable - rail.DropsBeforeStacking} optional part(s) still to drop");
            Assert.True(rail.MaxDroppable >= 7, $"{at} the footer drops only {rail.MaxDroppable} parts (queue place, time, reach, three captions, help)");
            Assert.True(time.IsVisible || !position.IsVisible, $"{at} the time went before the place in the queue");
            Assert.False(string.IsNullOrEmpty(Named<TextBlock>(sound, "SoundShapeSaveCount").Text), $"{at} Save does not say what it reaches");
            foreach (var (sub, stack) in new[]
                     {
                         ("SoundShapeSaveNextDestination", "SoundShapeSaveNextLabelStack"),
                         ("SoundShapeSaveNextMissingDestination", "SoundShapeSaveNextMissingLabelStack")
                     })
            {
                // Where Save and next / Save & next missing go, on the button (not only in its tooltip) while its caption shows.
                var line = Named<TextBlock>(sound, sub);
                Assert.False(string.IsNullOrEmpty(line.Text), $"{at} '{sub}' does not say where the button goes");
                // With room (640 px) Save and next's caption and line show (it outlasts the other captions); narrower, and
                // Save & next missing's, drop with the caption (the icon, the name and the tooltip stay), never cut.
                Assert.True(width < 640 || stack != "SoundShapeSaveNextLabelStack" || Named<Control>(sound, stack).IsVisible,
                    $"{at} '{stack}' was dropped (dropped {rail.Dropped} of {rail.MaxDroppable})");
                Assert.True(!Named<Control>(sound, stack).IsVisible || line.Bounds is { Width: > 1, Height: > 1 },
                    $"{at} '{sub}' laid out as {line.Bounds}");
            }

            var railRight = rail.Bounds.Width;
            var saveSet = new[] { "SoundShapeUndoSave", "SoundShapeSave", "SoundShapeSaveNext", "SoundShapeSaveNextMissing" };
            foreach (var name in saveSet)
                Assert.True(Named<Control>(sound, name).IsEffectivelyVisible, $"{at} '{name}' is not shown on the footer");
            var parts = new[] { "NoteFooterFilter", "NoteFooterPrevious", "NoteFooterNext", "NoteFooterHelp", "SoundApplyPicker", "SoundApplyReach" }
                .Concat(saveSet).Select(n => Named<Control>(sound, n)).Append(time).Where(c => c.IsVisible);
            foreach (var part in parts)
            {
                var name = part.Name;
                var left = part.TranslatePoint(default, rail)!.Value.X;
                Assert.True(part.IsEffectivelyVisible && part.Bounds.Width > 1, $"{at} '{name}' laid out as {part.Bounds}");
                Assert.True(left >= -0.5 && left + part.Bounds.Width <= railRight + 0.5,
                    $"{at} '{name}' spans {left:0.#}..{left + part.Bounds.Width:0.#} in a {railRight:0.#} px footer");
            }

            AssertNoTextCut([footer], at);
            Assert.Equal(pane.Window.Bounds.Height, Bottom(footer, pane.Window), 1);
        }), CancellationToken.None);

    /// <summary>#805: without Settings ▸ Editor's Apply step (the default) the footer shows no Apply button and a
    /// Technique toggle is in the chart at once (no draft); with it the Apply button is back, laid out.</summary>
    [Fact]
    public async Task TheApplyButton_ShowsOnlyWithTheApplyStep_AndWithoutIt_AnEditWritesAtOnce() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 370, pane =>
        {
            var state = App.Services!.GetRequiredService<ProjectState>();
            var restore = state.InspectorApplyStep;
            try
            {
                state.InspectorApplyStep = false;
                Select(pane, MostUsedShapeChord(pane));
                Open(pane, SoundSection.Technique);
                var sound = Sound(pane);
                var apply = Named<Button>(sound, "NoteFooterApplyButton");
                var footer = Named<Border>(sound, "NoteFooter");
                Settle(pane.Window);
                Assert.True(footer.IsEffectivelyVisible && footer.Bounds is { Width: > 1, Height: > 1 }, $"the footer laid out as {footer.Bounds}");
                Assert.False(apply.IsEffectivelyVisible, "the Apply button shows without the Apply step");

                var technique = pane.Vm.Staged.ChordTechnique;
                var accent = !technique.Accent;
                technique.Accent = accent;
                Settle(pane.Window);
                Assert.False(pane.Vm.Staged.Engine.HasAnyDraft, "the toggle was held as a draft");
                Assert.Equal(accent, technique.Accent);

                state.InspectorApplyStep = true;
                Settle(pane.Window);
                Assert.True(apply.IsEffectivelyVisible && apply.Bounds is { Width: > 1, Height: > 1 }, $"the Apply button laid out as {apply.Bounds}");
            }
            finally
            {
                state.InspectorApplyStep = restore;
            }
        }), CancellationToken.None);

    /// <summary>Tab from the section strip: the "Apply to" picker comes before any control in the open card, as it
    /// did before design D (the ladder row sat over the sections).</summary>
    [Fact]
    public async Task TheKeyboard_ReachesTheLadderBeforeTheCard() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 370, pane =>
        {
            Select(pane, MostUsedShapeChord(pane));
            Open(pane, SoundSection.Technique);
            var sound = Sound(pane);
            var picker = Named<ComboBox>(sound, "SoundApplyPicker");
            var card = OpenCard(sound);
            var entry = Named<ToggleButton>(sound, "NoteSectionTechniqueEntry");
            entry.Focus(NavigationMethod.Tab);
            Settle(pane.Window);
            Assert.Same(entry, pane.Window.FocusManager?.GetFocusedElement());
            var order = new List<string>();
            int? pickerAt = null, cardAt = null;
            for (var i = 0; i < 80 && (pickerAt is null || cardAt is null); i++)
            {
                pane.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                pane.Window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
                Settle(pane.Window);
                if (pane.Window.FocusManager?.GetFocusedElement() is not Control control) break;
                order.Add(control.Name ?? control.GetType().Name);
                if (ReferenceEquals(control, picker)) pickerAt ??= i;
                if (control.GetVisualAncestors().Contains(card)) cardAt ??= i;
            }

            Assert.True(pickerAt is not null && cardAt is not null, "Tab never reached the picker or the card: " + string.Join(" > ", order));
            Assert.True(pickerAt < cardAt, $"Tab reaches the card ({cardAt}) before the 'Apply to' picker ({pickerAt}): " + string.Join(" > ", order));
        }), CancellationToken.None);

    /// <summary>
    ///     Ported from the old chord-template editor's smoke (#485, #484): a rung that reaches past the selection shows
    ///     the bulk-edit preview strip over the footer, measured, with its summary and both steppers enabled and named;
    ///     a reshape refused with nothing selected shows its hint.
    /// </summary>
    [Fact]
    public async Task ThePreviewStrip_StepsAndIsNamed_AndARefusedReshape_ShowsItsHint() =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(530, 370, pane =>
        {
            Select(pane, MostUsedShapeChord(pane));
            Open(pane, SoundSection.Shape);
            var sound = Sound(pane);
            var editor = pane.Vm.ChordTemplateEditor;
            editor.ChooseApplyRungCommand.Execute(ChordApplyRung.EveryUse);
            Settle(pane.Window);
            var strip = Named<Border>(sound, "NoteApplyPreviewStrip");
            Assert.True(editor.Preview.IsActive && strip.IsEffectivelyVisible, "every use reaches past the selection: no preview strip");
            Assert.True(strip.Bounds is { Width: > 1, Height: > 1 }, $"the preview strip laid out as {strip.Bounds}");
            Assert.Contains(strip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == editor.Preview.Summary);
            foreach (var (name, key) in new[] { ("NoteApplyPreviewPrevious", "ChordTemplate_Preview_Previous"), ("NoteApplyPreviewNext", "ChordTemplate_Preview_Next") })
            {
                var button = Named<Button>(sound, name);
                Assert.True(button.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {button.Bounds}");
                Assert.True(button.IsEffectivelyEnabled, $"{name}: the level on screen has marks to step to");
                Assert.Equal(LocalizationService.Instance[key], Avalonia.Automation.AutomationProperties.GetName(button));
            }

            var hint = Named<Border>(sound, "NoteReshapeHint");
            Assert.False(hint.IsEffectivelyVisible);
            pane.Selection.ClearSelections();
            Settle(pane.Window);
            editor.StringInputs[2].FretText = "9";
            Settle(pane.Window);
            Assert.True(editor.Ladder.ShowsReshapeHint, "the fixture's reshape was not refused - repoint this case");
            Assert.True(hint.IsEffectivelyVisible && hint.Bounds is { Width: > 1, Height: > 1 },
                $"a refused reshape shows no hint ({pane.Vm.Sections.Section}, laid out as {hint.Bounds})");
        }), CancellationToken.None);

    /// <summary>The section strip's More keeps the Findings count on its row (the rows used to drop every badge). A
    /// chord's four sections fit at the dock's 320 px floor, so the case narrows the pane until Findings overflows: 200 px,
    /// where Japanese's shorter section names still fit at 240.</summary>
    [Theory]
    [InlineData(200)]
    public async Task TheSectionStripsMoreRow_CarriesTheFindingsCount(int width) =>
        await HeadlessSessionFixture.Dispatch(() => WithPane(width, 370, pane =>
        {
            Select(pane, FindingChord(pane));
            Open(pane, SoundSection.Shape);
            var sound = Sound(pane);
            var strip = Named<OverflowTabStrip>(sound, "NoteSectionEntries");
            var findings = Named<ToggleButton>(sound, "NoteSectionFindingsEntry");
            Assert.True(strip.OverflowTabs.Contains(findings), $"Findings stands in the strip at {width} px - this case tests nothing; repoint it");
            var more = Named<ToggleButton>(sound, "NoteSectionMore");
            var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            menu.ShowAt(more);
            Settle(pane.Window);
            try
            {
                var row = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "7"));
                InspectorTabStripUiPerfSmokeTests.AssertRowCarriesBadge(row, pane.Vm.SelectionIssueCount.ToString(CultureInfo.InvariantCulture), "the Findings row");
            }
            finally
            {
                menu.Hide();
            }
        }), CancellationToken.None);

    private static double Top(Visual v, Visual root) => v.TranslatePoint(default, root)!.Value.Y;

    private static double Bottom(Visual v, Visual root) => Top(v, root) + v.Bounds.Height;

    /// <summary>No visible text is cut: a wrapping text has the room it laid out in, and every other one — an
    /// ellipsized one included, so a trimmed reach or rung counts as cut — needs no more than its slot.</summary>
    private static void AssertNoTextCut(IEnumerable<Control> roots, string at)
    {
        var cut = new List<string>();
        foreach (var text in roots.SelectMany(r => r.GetVisualDescendants().OfType<TextBlock>()))
        {
            if (string.IsNullOrEmpty(text.Text) || !text.IsEffectivelyVisible || text.Bounds.Width <= 1) continue;
            if (text.TextWrapping != TextWrapping.NoWrap) continue;
            var slot = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
            var needed = new TextLayout(text.Text, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight), text.FontSize, null).WidthIncludingTrailingWhitespace;
            if (needed > slot + 1) cut.Add($"\"{text.Text}\" needs {needed:0.#} px in {slot:0.#}");
        }

        Assert.True(cut.Count == 0, $"{at} cuts text:\n  " + string.Join("\n  ", cut));
    }

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
