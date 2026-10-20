// The chart-editing dialogs: song metadata, showlights, the vocal track's lyric colours and the arrangement flag grid.

using Avalonia.Controls;
using Avalonia;
using DotCharter.UI;
using DotCharter.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>
    ///     Song Metadata's three CONTENT decisions, each of which the shared
    ///     grammar checks above would happily pass while the dialog said nothing:
    ///     album and year on ONE row, a Major/Minor MODE SWITCH rather than an
    ///     isolated checkbox, and the genre quick-picks.
    ///     <para>
    ///         The mode switch is asserted BEHAVIOURALLY — flip it and the
    ///         spelled-out key must move — because the whole point of promoting it
    ///         out of a dependent row is that the consequence is visible. A
    ///         two-state control that renders correctly and changes nothing is
    ///         exactly the failure a bounds assertion cannot see.
    ///     </para>
    ///     <para>
    ///         Opened on the dialog host over the fake's Full variant, so the
    ///         rows, the switch and the chips all have real content to lay out.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     The Genre label sits beside its field like every other row's. Centred on the field plus
    ///     its quick-pick chips, it floated halfway down the block, level with nothing (gallery
    ///     capture, 2026-09-24).
    /// </summary>
    [Fact]
    public async Task SongMetadata_TheGenreLabel_SitsBesideItsField()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.ISongMetadataScreen));
            WithHostedDialog(entry, DotCharter.Presentation.Fakes.FakeVariantNames.Full, (dialog, _) =>
            {
                var genre = DotCharter.Localization.LocalizationService.Instance["Inspector_Project_Genre"];
                var field = Descendants<TextBox>(dialog).Single(t => Avalonia.Automation.AutomationProperties.GetName(t) == genre);
                var label = Descendants<TextBlock>(dialog).Single(t => t.Text == genre);
                double TopOf(Avalonia.Visual v) => v.TranslatePoint(default, dialog)!.Value.Y;
                Assert.True(Math.Abs(TopOf(label) - TopOf(field)) < 12,
                    $"the Genre label's top is at {TopOf(label):0.#}, its field's at {TopOf(field):0.#}");
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SongMetadata_LabelsTheYear_SwitchesMode_AndOffersGenreQuickPicks()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // Full: a major key and five genre picks.
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.ISongMetadataScreen));
            WithHostedDialog(entry, DotCharter.Presentation.Fakes.FakeVariantNames.Full, (dialog, _) =>
            {
                var vm = (DotCharter.Presentation.Contracts.Dialogs.ISongMetadataScreen)dialog.DataContext!;

                // ── the year is its own LABELLED row, as in New project (#218) ──
                // Beside the album it was labelled only by its placeholder, which is
                // gone as soon as the field holds a year.
                var year = Descendants<TextBox>(dialog).FirstOrDefault(t => t.Name == "SongMetadataYear");
                Assert.True(year is not null, "the year field (SongMetadataYear) is gone");
                Assert.True(year!.Bounds.Width > 1 && year.Bounds.Height > 1, $"the year field laid out {year.Bounds}");
                var yearRow = Assert.IsType<Grid>(year.Parent);
                Assert.Contains("DlgRow", yearRow.Classes);
                Assert.Single(Descendants<TextBox>(yearRow));
                Assert.Contains(yearRow.Children.OfType<TextBlock>(),
                    t => t.Classes.Contains("DlgLabel") && !string.IsNullOrWhiteSpace(t.Text));

                // ── the mode switch is two states, and it MOVES the key ───────
                var segment = Descendants<Border>(dialog).FirstOrDefault(b => b.Name == "KeyModeSwitch");
                Assert.True(segment is not null, "the Major/Minor switch (KeyModeSwitch) is gone");
                Assert.True(segment!.Bounds.Width > 1 && segment.Bounds.Height > 1,
                    $"the Major/Minor switch laid out {segment.Bounds} — it rendered nothing");
                var modes = Descendants<RadioButton>(segment).ToList();
                Assert.Equal(2, modes.Count);
                Assert.Equal(1, modes.Count(r => r.IsChecked == true));

                var spelled = Descendants<TextBlock>(dialog).FirstOrDefault(t => t.Name == "KeySpelledOut");
                Assert.True(spelled is not null, "the spelled-out key (KeySpelledOut) is gone");
                var asMajor = spelled!.Text;
                modes[1].IsChecked = true;
                dialog.UpdateLayout();
                Assert.True(vm.KeyIsMinor, "clicking Minor did not reach the view model");
                Assert.NotEqual(asMajor, spelled.Text);

                // ── genre quick-picks ────────────────────────────────────────
                var chips = Descendants<Button>(dialog)
                    .Where(b => b.Classes.Contains("DlgChip") && b.IsEffectivelyVisible)
                    .ToList();
                Assert.Equal(vm.GenreSuggestions.Count, chips.Count);
                foreach (var chip in chips)
                {
                    Assert.True(chip.Bounds.Width > 1 && chip.Bounds.Height > 1,
                        $"a genre quick-pick laid out {chip.Bounds}");
                    // A class that matches no selector is a SILENT no-op and the
                    // chip would still measure — the inherited size is what gives
                    // it away. Stated as a literal on purpose (see the type-ladder
                    // case above): reading the token would move both sides at once.
                    Assert.True(Math.Abs(chip.FontSize - FireTypeLadder.Size("FireSizeMeta")) < 0.001,
                        $"a genre quick-pick renders at {chip.FontSize} px — Button.DlgChip did not apply, " +
                        "so it is an ordinary button wearing the ambient size");
                    Assert.True(chip.Command?.CanExecute(chip.CommandParameter) == true,
                        "a genre quick-pick resolved no command — clicking it would do nothing");
                }
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Showlights' position card: the beat and the milliseconds are TWO
    ///     readouts now, and the destructive action sits in the footer's tertiary
    ///     slot rather than beside Save.
    ///     <para>
    ///         The delete link's colour is the interesting half. <c>DlgLink</c>
    ///         paints <c>TextElement.Foreground</c> on its
    ///         <c>/template/ ContentPresenter</c>, so a <c>Foreground</c> set on
    ///         the Button would be inherited over and the link would render in the
    ///         ordinary muted grey — visibly fine, and no longer saying that the
    ///         action destroys something.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Showlights_SplitsBeatFromMilliseconds_AndPutsDeleteInTheTertiarySlot()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // The fake's Default variant targets 336 3/4 / 101 840.909 ms.
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.IShowlightsScreen));
            WithHostedDialog(entry, DotCharter.Presentation.Fakes.FakeVariantNames.Default, (dialog, _) =>
            {
                var card = Descendants<Border>(dialog).FirstOrDefault(b => b.Name == "PositionCard");
                Assert.True(card is not null, "the position readout is no longer its own card");
                Assert.True(card!.Background is not null && card.BorderBrush is not null,
                    "the position card resolved no brushes — DlgSection did not apply");

                var beat = Descendants<TextBlock>(card).First(t => t.Name == "TargetBeat");
                var ms = Descendants<TextBlock>(card).First(t => t.Name == "TargetMs");
                Assert.Equal("336 3/4", beat.Text);
                Assert.Equal("101 840.909 ms", ms.Text);
                foreach (var readout in new[] { beat, ms })
                {
                    Assert.True(readout.Bounds.Width > 1 && readout.Bounds.Height > 1,
                        $"a position readout laid out {readout.Bounds}");
                    Assert.True(readout.FontFamily.Name == "JetBrains Mono",
                        $"a position readout renders in '{readout.FontFamily.Name}' — a figure is mono here");
                }

                Assert.True(beat.FontSize > ms.FontSize,
                    $"the beat ({beat.FontSize}) does not read louder than the ms ({ms.FontSize}) — " +
                    "the beat is the value, the ms is the cross-check");

                // Delete is in the footer's LEFT slot, and it is the only thing there.
                var footer = Descendants<Border>(dialog).First(b => b.Classes.Contains("DialogFooter"));
                var left = Descendants<StackPanel>(footer).First(p => p.Classes.Contains("DlgActionsLeft"));
                var delete = Descendants<Button>(left).Single();
                Assert.Equal("DeletePosition", delete.Name);
                Assert.True(delete.Bounds.Width > 1 && delete.Bounds.Height > 1,
                    $"the delete action laid out {delete.Bounds}");

                var caption = Descendants<TextBlock>(delete).Single();
                Assert.False(string.IsNullOrWhiteSpace(caption.Text), "the delete action says nothing");
                var danger = (Avalonia.Media.ISolidColorBrush?)dialog.FindResource("FireDanger");
                Assert.True(danger is not null, "FireDanger did not resolve inside the dialog host");
                Assert.Equal(danger!.Color, ((Avalonia.Media.ISolidColorBrush)caption.Foreground!).Color);
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The vocal track's colour row: four real swatches that WRITE the same
    ///     property the picker writes, and exactly one of them lit.
    ///     <para>
    ///         Asserted behaviourally — click a swatch, the view model's colour
    ///         must move and the ring must follow — because a strip of coloured
    ///         rectangles that changes nothing renders perfectly and is the whole
    ///         failure mode. The picker stays in the tree so the row costs no
    ///         capability; that is checked too.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task VocalTrack_OffersFourLyricColours_ThatWriteTheSamePropertyAsThePicker()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            // The REAL view model, from a scope of the app's own registrations —
            // one per open, as the dialog host makes it.
            using var scope = App.Services!.CreateScope();
            var vm = scope.ServiceProvider
                .GetRequiredService<DotCharter.Presentation.Contracts.Dialogs.IVocalTrackSettingsScreen>();
            vm.TrackColor = "#FFFFFF";

            var dialog = new VocalTrackSettingsView { DataContext = vm };
            WithLaidOutDialog(dialog, () =>
            {
                var strip = Descendants<ItemsControl>(dialog).First(c => c.Name == "ColorSwatches");
                var swatches = Descendants<Button>(strip).ToList();
                Assert.Equal(vm.ColorSwatches.Count, swatches.Count);
                Assert.True(swatches.Count >= 4, $"only {swatches.Count} lyric colours are offered");

                foreach (var swatch in swatches)
                {
                    Assert.True(swatch.Bounds.Width > 1 && swatch.Bounds.Height > 1,
                        $"a lyric colour swatch laid out {swatch.Bounds}");
                    Assert.True(swatch.Background is Avalonia.Media.ISolidColorBrush,
                        "a lyric colour swatch resolved no fill — it draws as an empty button");
                }

                Assert.Equal(1, Descendants<Border>(strip).Count(b => b.Name == "SwatchRing" && b.IsEffectivelyVisible));

                // …and pressing one is the colour edit.
                var target = vm.ColorSwatches.First(s => !s.IsSelected);
                var button = swatches[vm.ColorSwatches.ToList().IndexOf(target)];
                Assert.True(button.Command?.CanExecute(button.CommandParameter) == true,
                    "a lyric colour swatch resolved no command — clicking it would do nothing");
                button.Command!.Execute(button.CommandParameter);
                dialog.UpdateLayout();
                Assert.Equal(target.Color, vm.TrackColor);
                Assert.True(target.IsSelected, "the picked swatch did not become the selected one");
                Assert.Equal(1, Descendants<Border>(strip).Count(b => b.Name == "SwatchRing" && b.IsEffectivelyVisible));

                // The full picker is still reachable — the swatches are a shortcut,
                // not a restriction.
                var picker = Descendants<ColorPicker>(dialog).SingleOrDefault();
                Assert.True(picker is not null, "the ColorPicker is gone — arbitrary lyric colours became impossible");
                Assert.Equal(vm.TrackColor, DotCharter.UI.Converters.HexColorConverter.ToHex(picker!.Color));
            });
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The arrangement dialog's technique flags tile into THREE even columns.
    ///     They used to be fixed-width chips in a WrapPanel, which is the same
    ///     picture until a translated label is longer than the chip — German runs
    ///     ~40 % longer — and then the grid goes ragged with no warning.
    ///     <para>
    ///         The counts are the load-bearing half: four undecidable flags and
    ///         twenty derived ones is the split
    ///         <c>ArrangementPropertiesDeriver</c> makes, and the derived block
    ///         must be DISABLED while the pin is off — with it enabled, every
    ///         save writes twenty attributes the next export recomputes anyway.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     A seven-string tuning row ends inside its control column. The gallery capture of the
    ///     Full variant (seven strings) showed the seventh box cut at the dialog's right edge
    ///     (2026-09-24): measured here as each box's right edge against the row's, and the row's
    ///     against the 300 px column it sits in.
    /// </summary>
    [Fact]
    public async Task Arrangement_ASevenStringTuningRow_EndsInsideItsColumn()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.IArrangementSettingsScreen));
            WithHostedDialog(entry, DotCharter.Presentation.Fakes.FakeVariantNames.Full, (dialog, _) =>
            {
                var row = Descendants<ItemsControl>(dialog).Single(c => c.Name == "TuningStepsRow");
                var boxes = Descendants<TextBox>(row).ToList();
                Assert.Equal(7, boxes.Count);
                var column = (Control)row.Parent!;
                double RightIn(Control c) => c.TranslatePoint(new Avalonia.Point(c.Bounds.Width, 0), column)!.Value.X;
                // The boxes against the COLUMN they sit in (the row itself is pulled out by half a
                // gap each side on purpose, so its own edge is not the limit).
                var columnRight = Grid.GetColumn(row) == 1 ? column.Bounds.Width : RightIn(row);
                foreach (var box in boxes)
                {
                    Assert.True(RightIn(box) <= columnRight + 0.5,
                        $"a tuning box ends at {RightIn(box):0.#}, past its column's right edge {columnRight:0.#} (row {row.Bounds}, row desired {row.DesiredSize}, box {box.Bounds}, panel {row.ItemsPanelRoot?.Bounds} desired {row.ItemsPanelRoot?.DesiredSize})");
                }
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     #217: every tuning box shows its open string above it and the note it ends up at
    ///     below it, and a screen reader hears a different name per box (all six used to be
    ///     "Half steps from standard").
    /// </summary>
    [Fact]
    public async Task Arrangement_EachTuningBox_NamesItsStringAndItsNote()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.IArrangementSettingsScreen));
            WithHostedDialog(entry, DotCharter.Presentation.Fakes.FakeVariantNames.Full, (dialog, _) =>
            {
                var row = Descendants<ItemsControl>(dialog).Single(c => c.Name == "TuningStepsRow");
                var cells = Descendants<StackPanel>(row).Where(c => c.Name == "TuningStringCell").ToList();
                Assert.Equal(7, cells.Count);
                var names = Descendants<TextBox>(row).Select(Avalonia.Automation.AutomationProperties.GetName).ToList();
                Assert.Equal(names.Count, names.Distinct().Count());
                foreach (var cell in cells)
                {
                    var texts = Descendants<TextBlock>(cell).ToList();
                    Assert.Equal(2, texts.Count);
                    foreach (var text in texts)
                    {
                        Assert.False(string.IsNullOrEmpty(text.Text), "a tuning box has an empty string or note label");
                        Assert.True(text.Bounds.Width > 1 && text.Bounds.Height > 1,
                            $"the tuning label '{text.Text}' laid out {text.Bounds}");
                    }
                }
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Arrangement_TilesItsFlagsInThreeColumns_AndKeepsTheDerivedBlockBehindThePin()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var entry = HostedDialogs.Single(e =>
                e.Contract == typeof(DotCharter.Presentation.Contracts.Dialogs.IArrangementSettingsScreen));

            // Default: the pin is OFF, so the derived block is read-only.
            WithHostedDialog(entry, DotCharter.Presentation.Fakes.FakeVariantNames.Default, (dialog, _) =>
            {
                foreach (var name in new[] { "AuthoredFlagGrid", "DerivedFlagGrid" })
                {
                    var host = Descendants<ItemsControl>(dialog).FirstOrDefault(c => c.Name == name);
                    Assert.True(host is not null, $"the arrangement dialog has no {name}");
                    var panel = Assert.IsType<Avalonia.Controls.Primitives.UniformGrid>(host!.ItemsPanelRoot);
                    Assert.Equal(3, panel.Columns);

                    var boxes = Descendants<CheckBox>(host).ToList();
                    Assert.True(boxes.Count > 0, $"{name} laid out no flags");
                    foreach (var box in boxes)
                    {
                        Assert.True(box.Bounds.Width > 1 && box.Bounds.Height > 1,
                            $"a flag in {name} laid out {box.Bounds}");
                    }

                    // Even columns: every CELL is the same width, which a
                    // fixed-width chip in a WrapPanel cannot promise once its label
                    // is translated. The cells are the panel's own children, not
                    // the checkboxes — a checkbox that sized to its caption would
                    // still be sitting in an even column.
                    var widths = panel.Children.OfType<Control>()
                        .Select(c => Math.Round(c.Bounds.Width)).Distinct().ToList();
                    Assert.True(widths.Count == 1,
                        $"{name} laid its flags out over {widths.Count} different column widths " +
                        $"({string.Join(", ", widths)}) — the columns are not even");
                }

                var derived = Descendants<ItemsControl>(dialog).First(c => c.Name == "DerivedFlagGrid");
                Assert.False(derived.IsEffectivelyEnabled,
                    "the derived flags are editable with the pin OFF — every save would then look authored");

                // …and the footer says what the dialog's scope is.
                var scope = Descendants<TextBlock>(dialog).FirstOrDefault(t => t.Name == "ArrangementScopeNote");
                Assert.True(scope is not null, "the footer's scope note is gone");
                Assert.False(string.IsNullOrWhiteSpace(scope!.Text));
                Assert.True(scope.Bounds.Width > 1, $"the scope note laid out {scope.Bounds}");
            });

            // Full: the pin is ON, so the derived block unlocks.
            WithHostedDialog(entry, DotCharter.Presentation.Fakes.FakeVariantNames.Full, (dialog, _) =>
            {
                var derived = Descendants<ItemsControl>(dialog).First(c => c.Name == "DerivedFlagGrid");
                Assert.True(derived.IsEffectivelyEnabled, "the pin is on and the derived flags are still locked");
            });
        }, CancellationToken.None);
    }
}
