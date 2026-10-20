// The whole-catalogue grammar sweeps: every overlay dialog builds and lays out, and every converted one carries the scrim/card/head, the shared row rhythm and right edge, one footer with its primary last, resolved state tints and icons, the Fire type ladder and the chamfered Fire primary - plus the keybind capture box's app-scoped theme.

using Avalonia.Controls;
using Avalonia.VisualTree;
using DotCharter.Presentation.Fakes;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>Construct + attach + measure, for EVERY overlay dialog whether
    /// converted or not. This is the half that catches a view which throws in
    /// its constructor (ViewLocator would swallow it into a "Not Found: …"
    /// placeholder), a style setter that throws while the tree is being
    /// attached, and a command whose CanExecute throws once rooted.</summary>
    [Fact]
    public async Task EveryDialog_builds_attaches_and_lays_out()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            foreach (var name in AllOverlayDialogs)
            {
                var dialog = CreateDialog(name);
                WithLaidOutDialog(dialog, () =>
                    Assert.True(dialog.Bounds.Width > 1 && dialog.Bounds.Height > 1,
                        $"{name} laid out as {dialog.Bounds} — it rendered nothing"));
            }

            // …and every dialog on DialogHost, in EVERY variant of its fake:
            // hosted the way the app hosts it, over the shared scrim.
            var hosted = 0;
            foreach (var entry in HostedDialogs)
            {
                foreach (var variant in FakeScreens.VariantsOf(entry.Contract))
                {
                    WithHostedDialog(entry, variant.Name, (view, _) =>
                {
                    AssertMeasured($"{entry.ViewType.Name} ({variant.Name})", view);
                    hosted++;
                });
                }
            }

            Assert.True(hosted >= HostedDialogs.Count * 3, $"only {hosted} hosted dialog variants were laid out");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The shell of every converted dialog: scrim → card → head, with the
    ///     head's icon badge proving its <c>{DynamicResource IconX}</c>
    ///     resolved. DynamicResource fails SILENTLY, so a typo'd key is an empty
    ///     badge nobody notices; this is the only place it shows up.
    /// </summary>
    [Fact]
    public async Task EveryConvertedDialog_HasScrimCardAndHead()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            ForEachConvertedDialog((name, dialog) => AssertShell(name, dialog));
        }, CancellationToken.None);
    }

    /// <summary>Every visible row reaches the shared minimum height and ends on
    /// the row's right edge, and the whole dialog uses ONE control-column width.
    /// Catches a row that kept an old label-column split (its control would stop
    /// short) or lost the DlgRow class entirely (it would collapse).</summary>
    [Fact]
    public async Task EveryConvertedDialog_RowsShareOneRhythmAndOneRightEdge()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            ForEachConvertedDialog((name, dialog) => AssertRows(name, dialog));
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The footer: one hairline rule, one action bar, and AT MOST ONE
    ///     <c>Button.Primary</c> visible in it, rightmost. Two primaries in a
    ///     footer is the single most common "which one do I press" regression,
    ///     and the rule is unenforceable by review across 38 files.
    /// </summary>
    [Fact]
    public async Task EveryConvertedDialog_HasOneFooterWithOnePrimaryActionLast()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            ForEachConvertedDialog((name, dialog) => AssertFooter(name, dialog));
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Every state surface a converted dialog declares must have resolved
    ///     its tint. The <c>Dlg*Fill</c>/<c>Dlg*Stroke</c> brushes live in
    ///     <c>DialogStyles.axaml</c>'s own <c>Styles.Resources</c>, so they only
    ///     resolve when the dialog is inside the host that includes that file —
    ///     which is exactly the wiring this test drives.
    /// </summary>
    [Fact]
    public async Task EveryConvertedDialog_ResolvesEveryStateTintAndIcon()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            ForEachConvertedDialog((name, dialog) => AssertTints(name, dialog));
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The Fire TYPE LADDER really reached the dialogs. The palette and the
    ///     default typeface went global before this conversion, so the only
    ///     thing left that separated a dialog from a Fire screen was its type
    ///     hierarchy — and a style that fails to apply is the quietest failure
    ///     in the theme: the text still renders, in the inherited face, at the
    ///     inherited size, with no warning and no exception.
    ///     <para>
    ///         The expectations are stated as LITERALS on purpose. Deriving them
    ///         from the same token the style reads would move both sides
    ///         together and pass straight through the regression it exists to
    ///         catch (the Get Ready string-chip lesson, fire-design-system.md).
    ///     </para>
    /// </summary>
    [Fact]
    public async Task EveryConvertedDialog_WearsTheFireTypeLadder()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var titles = 0;
            var labels = 0;
            var eyebrows = 0;

            ForEachConvertedDialog((name, dialog) =>
            {
                // The head title is the DISPLAY face at the modal title size.
                foreach (var title in Descendants<TextBlock>(dialog)
                             .Where(t => t.Classes.Contains("DialogTitle")))
                {
                    Assert.True(title.FontFamily.Name == "Atkinson Hyperlegible",
                        $"{name}'s title renders in '{title.FontFamily.Name}' — the Fire display face " +
                        "did not apply, so the dialog opens in the old grammar's type");
                    Assert.Equal(FireTypeLadder.Size("FireSizeTitle"), title.FontSize);
                    titles++;
                }

                // A row label is the UI face at THE row size, bold.
                foreach (var label in Descendants<TextBlock>(dialog)
                             .Where(t => t.Classes.Contains("DlgLabel")))
                {
                    Assert.True(label.FontFamily.Name == "Atkinson Hyperlegible",
                        $"a row label on {name} renders in '{label.FontFamily.Name}'");
                    Assert.Equal(FireTypeLadder.Size("FireSizeRow"), label.FontSize);
                    labels++;
                }

                // A section label is words, so under Graphite it is the UI
                // face (mono is for figures, times and tunings).
                foreach (var eyebrow in Descendants<TextBlock>(dialog)
                             .Where(t => t.Classes.Contains("DlgSectionLabel")))
                {
                    Assert.True(eyebrow.FontFamily.Name == "Atkinson Hyperlegible",
                        $"a section label on {name} renders in '{eyebrow.FontFamily.Name}' — " +
                        "a label is set in the UI face; mono is for figures");
                    Assert.Equal(FireTypeLadder.Size("FireSizeSm"), eyebrow.FontSize);
                    eyebrows++;
                }
            });

            // …and the loop above cannot pass by finding nothing to look at.
            Assert.True(titles >= 30, $"only {titles} dialog titles were checked");
            Assert.True(labels >= 30, $"only {labels} row labels were checked");
            Assert.True(eyebrows >= 10, $"only {eyebrows} section eyebrows were checked");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A dialog's ONE primary action is the layer's brand signature: the
    ///     chamfered <c>FirePrimaryButton</c>, whose 45° cut is a live
    ///     <c>Clip</c> geometry bound to the button's own bounds. Three things
    ///     can silently undo it and none of them fails a build: the
    ///     <c>Theme</c> setter not resolving, a class style repainting the fill
    ///     over the theme (a <c>Style</c> beats a <c>ControlTheme</c>), or the
    ///     clip binding producing null on a zero-sized button. Only the laid-out
    ///     template can tell — the geometry is not reachable from the logical
    ///     tree at all.
    /// </summary>
    [Fact]
    public async Task EveryConvertedDialogFooter_ShowsTheChamferedFirePrimary()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var chamfered = 0;

            ForEachConvertedDialog((name, dialog) =>
            {
                var footer = Descendants<Border>(dialog)
                    .FirstOrDefault(b => b.Classes.Contains("DialogFooter"));
                if (footer == null) return;

                foreach (var primary in Descendants<Button>(footer)
                             .Where(b => b.Classes.Contains("Primary") && b.IsEffectivelyVisible))
                {
                    var cut = primary.GetVisualDescendants().OfType<Border>()
                        .FirstOrDefault(b => b.Name == "Cut");
                    Assert.True(cut is not null,
                        $"{name}'s primary action applied no FirePrimaryButton template — " +
                        "it is not the chamfered Fire primary, it is the old rounded one");
                    Assert.True(cut!.Clip is not null,
                        $"{name}'s primary action has the Fire template but no clip geometry — " +
                        "the chamfer is missing and the button renders as a plain rectangle");
                    chamfered++;
                }
            });

            Assert.True(chamfered >= 20,
                $"only {chamfered} dialogs were found to have a primary action — the check is vacuous");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The DANGER tier really reached the laid-out buttons. The tier is
    ///     colour-only (`UI/DialogDangerTierTests` lints the declaration and the
    ///     class placement), so the one thing left to prove at runtime is that
    ///     the <c>FireDanger</c> foreground RESOLVED on a rooted button — a
    ///     DynamicResource that misses leaves the ghost look with no signal.
    /// </summary>
    [Fact]
    public async Task EveryDangerAction_ResolvesTheDangerForeground()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var seen = 0;
            ForEachConvertedDialog((name, dialog) =>
            {
                var danger = Avalonia.Controls.ResourceNodeExtensions.FindResource(dialog, "FireDanger")
                    as Avalonia.Media.ISolidColorBrush;
                Assert.NotNull(danger);

                foreach (var button in Descendants<Button>(dialog)
                             .Where(b => b.Classes.Contains("Danger")))
                {
                    Assert.True(button.Foreground is Avalonia.Media.ISolidColorBrush fg
                                && fg.Color == danger!.Color,
                        $"a Danger-classed button on {name} kept foreground '{button.Foreground}' — " +
                        "the danger tier did not apply and it renders as a safe button");
                    seen++;
                }
            });

            // Six statically-declared danger actions existed when the tier
            // landed; a scan that finds fewer has gone vacuous.
            Assert.True(seen >= 6, $"only {seen} Danger buttons were checked");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The focus-on-open behaviour really lands focus. The attached
    ///     property watches the scrim's <c>IsVisible</c> and posts at Loaded
    ///     priority; a broken scrim walk or a mis-timed post leaves focus in
    ///     the editor underneath with no exception — only asking the focus
    ///     manager can tell. <c>UI/DialogFocusOnOpenTests</c> lints the wiring;
    ///     this proves the mechanism.
    /// </summary>
    [Fact]
    public async Task EveryFocusOnOpenTarget_ReceivesFocusWhenItsDialogOpens()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var seen = 0;
            ForEachConvertedDialog((name, dialog) =>
            {
                var target = Descendants<Control>(dialog)
                    .FirstOrDefault(DotCharter.UI.Views.Dialogs.FocusOnOpen.GetIsTarget);
                if (target is null) return;

                // A legacy dialog: with no DataContext the scrim's IsVisible
                // binding never produced a value, so the dialog never "opened" —
                // flip it by hand to drive the same edge the product sees. A
                // hosted dialog was really opened by DialogHost, which posted
                // the focus itself; the harness has already run that job.
                if (Descendants<Grid>(dialog).FirstOrDefault(g => g.Classes.Contains("DialogScrim")) is { } scrim)
                {
                    scrim.IsVisible = false;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    scrim.IsVisible = true;
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                }

                var focused = TopLevel.GetTopLevel(dialog)?.FocusManager?.GetFocusedElement();
                var landed = ReferenceEquals(focused, target)
                             || (focused is Avalonia.Visual v
                                 && Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(v).Contains(target));
                Assert.True(landed,
                    $"{name}'s focus-on-open target is {target.GetType().Name} but focus sits on " +
                    $"{focused?.GetType().Name ?? "nothing"} after the open");
                seen++;
            });

            Assert.True(seen >= 6, $"only {seen} focus targets were driven — the check is vacuous");
        }, CancellationToken.None);
    }

    /// <summary>
    ///     A <c>TemplatedControl</c> whose ControlTheme is scoped to ONE view
    ///     renders as literally nothing anywhere else: no template, no template
    ///     children, zero size, no warning and no exception. The Pattern Builder's
    ///     three keybind fields shipped that way in 0.39.0 — the theme lived in
    ///     <c>SettingsView.axaml</c>'s own <c>UserControl.Styles</c> — and the row
    ///     labels rendered over an empty column.
    ///     <para>
    ///         Measuring the control is the only thing that catches it, so this is
    ///         the guard for the theme now being app-scoped in
    ///         <c>Styles/Inputs.axaml</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheKeybindCaptureBox_HasAnAppScopedThemeAndMeasures()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.IPatternBuilderScreen));
            WithHostedDialog(entry, FakeVariantNames.Default, (dialog, _) =>
            {
                var boxes = Descendants<DotCharter.UI.Controls.KeybindCaptureBox>(dialog).ToList();
                Assert.True(boxes.Count >= 3,
                    $"the Pattern Builder laid out {boxes.Count} keybind fields — it declares three");

                foreach (var box in boxes)
                {
                    // Bounds alone are NOT the test: a template-less control still
                    // stretches to fill its cell, so it measures fine and draws
                    // nothing. What is missing is the template, and a template's
                    // children are VISUAL — the logical walk used everywhere else
                    // in this file cannot see them and would pass either way.
                    Assert.True(box.Bounds.Width > 1 && box.Bounds.Height > 1,
                        $"a KeybindCaptureBox laid out as {box.Bounds}");

                    var inTemplate = box.GetVisualDescendants().OfType<Control>().ToList();
                    Assert.True(inTemplate.Any(c => c.Name == "Root"),
                        "a KeybindCaptureBox applied no template — its ControlTheme is not in scope, " +
                        "so the user sees an empty column where the field should be. " +
                        $"[template={(box.Template == null ? "null" : "set")}, " +
                        $"visualChildren={inTemplate.Count}, bounds={box.Bounds}]");
                    Assert.True(inTemplate.OfType<TextBlock>().Any(t => t.Name == "DisplayLabel"),
                        "a KeybindCaptureBox has no DisplayLabel — its chord is not rendered at all");
                }
            });
        }, CancellationToken.None);
    }
}
