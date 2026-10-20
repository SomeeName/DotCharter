// The tone designer dialog: its three columns, its marked rows and the gear slot the picker opens - with the view model the case builds for it.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Dialogs;

namespace DotCharter.Tests.Perf;

public sealed partial class DialogConstructionUiPerfSmokeTests
{
    /// <summary>
    ///     The Tone Designer's rebuilt BODY (the owner's mockup, tone()): three
    ///     columns side by side — the project's tones, the selected tone, and
    ///     the arrangement slots + imported definitions that used to be two
    ///     COLLAPSED expanders under the editor, i.e. the two things the dialog
    ///     exists to produce were the two things it hid.
    ///     <para>
    ///         Three faults this catches that nothing else can. The columns
    ///         could stack (a Grid whose ColumnDefinitions never applied still
    ///         arranges, one region on top of another). The mono row mark could
    ///         silently miss its selector — a class that matches nothing renders
    ///         in the ambient face at the ambient size with no warning. And the
    ///         gear PICKER is now revealed per row through
    ///         <c>$parent[ListBoxItem].IsSelected</c>: if that ancestor binding
    ///         resolves to nothing, every row stays collapsed and the dialog
    ///         becomes a read-only list of gear nobody can change — a working
    ///         layout with the feature gone.
    ///     </para>
    ///     <para>
    ///         Needs a DataContext (most cases here deliberately have none):
    ///         the columns are lists, and a list with no items cannot prove a
    ///         row measured. The view model is built from real types with the
    ///         test fakes, so no service graph and no audio device is involved.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task ToneDesigner_LaysOutThreeColumns_MarksItsRows_AndOpensThePickedGearSlot()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = CreateToneDesignerViewModel();
            var dialog = new ToneDesignerDialogView { DataContext = vm };

            WithLaidOutDialog(dialog, () =>
            {
                // ── the five regions of the body all measured ────────────────
                Control Region(string name)
                {
                    var control = Descendants<Control>(dialog).FirstOrDefault(c => c.Name == name);
                    Assert.True(control is not null, $"the Tone Designer has no {name}");
                    Assert.True(control!.Bounds.Width > 1 && control.Bounds.Height > 1,
                        $"{name} laid out {control.Bounds} — it rendered nothing");
                    return control;
                }

                var tones = Region("ToneListFrame");
                var editor = Region("ToneEditor");
                var gear = Region("GearListFrame");
                var slots = Region("ArrangementSlotsCard");
                var imported = Region("ImportedTonesCard");

                // ── …side by side, in the mockup's order ─────────────────────
                double LeftIn(Control c) => (c.TranslatePoint(new Point(0, 0), dialog) ?? default).X;
                Assert.True(LeftIn(tones) < LeftIn(editor) && LeftIn(editor) < LeftIn(slots),
                    "the Tone Designer's three columns are not laid out left to right " +
                    $"(tones {LeftIn(tones):0}, editor {LeftIn(editor):0}, slots {LeftIn(slots):0}) — " +
                    "the body's ColumnDefinitions never reached it");
                Assert.Equal(LeftIn(slots), LeftIn(imported), 1.0);
                Assert.Equal(212, tones.Bounds.Width, 1.0);

                // ── the project tones: one row each, and the picked one is the
                //    view model's ─────────────────────────────────────────────
                var toneRows = Descendants<Border>(tones)
                    .Where(b => b.Classes.Contains("DlgListRow")).ToList();
                Assert.Equal(vm.ProjectTones.Count, toneRows.Count);
                foreach (var row in toneRows)
                {
                    Assert.True(row.Bounds.Width > 1 && row.Bounds.Height > 1,
                        $"a project-tone row laid out {row.Bounds}");
                }

                var toneList = Descendants<ListBox>(tones).Single();
                Assert.Same(vm.SelectedTone, toneList.SelectedItem);

                // ── the row's mono mark really applied. Stated as literals on
                //    purpose (see the type-ladder case above): reading the token
                //    the style reads would move both sides together. ──────────
                var meta = Descendants<TextBlock>(gear)
                    .Where(t => t.Classes.Contains("DlgRowMeta") && t.IsEffectivelyVisible)
                    .ToList();
                Assert.True(meta.Count >= 3,
                    $"only {meta.Count} gear rows printed what is loaded in them");
                foreach (var value in meta)
                {
                    Assert.True(value.FontFamily.Name == "JetBrains Mono",
                        $"a gear row's value renders in '{value.FontFamily.Name}' — TextBlock.DlgRowMeta " +
                        "matched no selector, so it is ambient text pretending to be a readout");
                    Assert.Equal(FireTypeLadder.Size("FireSizeMono"), value.FontSize);
                }

                // ── exactly ONE gear slot is open, and it is the selected one ─
                List<DeferredChoiceBox> OpenPickers() => Descendants<DeferredChoiceBox>(gear)
                    .Where(c => c.DataContext is DotCharter.Presentation.Screens.Dialogs.ToneGearSlot
                                && c.IsEffectivelyVisible)
                    .ToList();

                var pickers = OpenPickers();
                Assert.True(pickers.Count == 1,
                    $"{pickers.Count} gear pickers are visible — the picked row is supposed to be the " +
                    "only one that opens (0 means $parent[ListBoxItem].IsSelected resolved to nothing " +
                    "and the gear can no longer be changed at all)");
                Assert.Same(vm.Editor.SelectedGearSlot, pickers[0].DataContext);
                Assert.True(pickers[0].Bounds.Width > 1 && pickers[0].Bounds.Height > 1,
                    $"the open gear slot's picker laid out {pickers[0].Bounds}");

                // …and it FOLLOWS the selection rather than being pinned to row 0.
                vm.Editor.SelectedGearSlot = vm.Editor.GearSlots[2];
                dialog.UpdateLayout();
                pickers = OpenPickers();
                Assert.True(pickers.Count == 1, $"{pickers.Count} gear pickers after switching slot");
                Assert.Same(vm.Editor.GearSlots[2], pickers[0].DataContext);

                // ── the footer's quiet two-state action drives the real toggle ─
                var preview = Assert.IsType<ToggleButton>(
                    Descendants<Control>(dialog).FirstOrDefault(c => c.Name == "PreviewToggle"));
                Assert.True(preview.Bounds.Width > 1 && preview.Bounds.Height > 1,
                    $"the preview action laid out {preview.Bounds}");
                Assert.True(preview.IsEnabled, "the preview action is disabled over a live chain");
                preview.IsChecked = true;
                Assert.True(vm.IsPreviewOn, "pressing Preview did not reach the view model");
                preview.IsChecked = false;
                Assert.False(vm.IsPreviewOn);
            });

            vm.CloseCommand.Execute(null);
        }, CancellationToken.None);
    }

    /// <summary>
    ///     The gear list's scroll extent is the SAME at the top and at the bottom. Its rows
    ///     differ wildly in height (the picked one opens into a picker and its knobs), and a
    ///     virtualizing panel estimates the rows it has not realized from the ones it has: the
    ///     extent then moved as the list scrolled, so the thumb shrank on the way up and the
    ///     content jumped under the wheel (owner report, 2026-10-18).
    /// </summary>
    [Fact]
    public async Task ToneDesigner_GearList_KeepsOneScrollExtent_WhileItScrolls()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = CreateToneDesignerViewModel();
            var dialog = new ToneDesignerDialogView { DataContext = vm };

            WithLaidOutDialog(dialog, () =>
            {
                var frame = Descendants<Control>(dialog).First(c => c.Name == "GearListFrame");
                var scroller = Descendants<ListBox>(frame).Single().GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(scroller.Extent.Height > scroller.Viewport.Height,
                    $"the gear list does not overflow ({scroller.Extent.Height:0} in {scroller.Viewport.Height:0}), so this proves nothing");

                var extents = new List<double>();
                for (var y = scroller.Extent.Height; y >= 0; y -= scroller.Viewport.Height / 3)
                {
                    scroller.Offset = new Vector(0, y);
                    dialog.UpdateLayout();
                    extents.Add(scroller.Extent.Height);
                }

                scroller.Offset = default;
                dialog.UpdateLayout();
                extents.Add(scroller.Extent.Height);
                Assert.True(extents.Max() - extents.Min() < 1,
                    "the gear list's scroll extent moved while scrolling: " + string.Join(", ", extents.Select(e => e.ToString("0"))));
            });

            vm.CloseCommand.Execute(null);
        }, CancellationToken.None);
    }

    /// <summary>
    ///     Without the package reader (#887) the Tone Designer has no "Import from PSARC"
    ///     chip and no IMPORTED TONES section — absent, not greyed — while its three columns
    ///     still lay out and Generate (which reads the project's own audio) stays.
    /// </summary>
    [Fact]
    public async Task ToneDesigner_WithoutThePackageReader_HasNoPackageImport_AndStillLaysOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var off = CreateToneDesignerViewModel(features: DotCharter.Services.Plugins.PackageFeatures.None);
            var dialog = new ToneDesignerDialogView { DataContext = off };
            WithLaidOutDialog(dialog, () =>
            {
                Control Named(string name) => Descendants<Control>(dialog).First(c => c.Name == name);

                foreach (var name in new[] { "ToneListFrame", "ToneEditor", "GearListFrame", "ArrangementSlotsCard" })
                    Assert.True(Named(name).Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out {Named(name).Bounds}");
                Assert.False(Named("ImportFromPackageChip").IsVisible, "the package import chip is still offered");
                Assert.False(Named("ImportedTonesCard").IsVisible, "the imported-tones section is still offered");
                Assert.True(off.GenerateTonesFromAudioCommand.CanExecute(null));
            });
            off.CloseCommand.Execute(null);

            var on = CreateToneDesignerViewModel();
            var withReader = new ToneDesignerDialogView { DataContext = on };
            WithLaidOutDialog(withReader, () =>
            {
                Assert.True(Descendants<Control>(withReader).First(c => c.Name == "ImportFromPackageChip").IsEffectivelyVisible);
                Assert.True(Descendants<Control>(withReader).First(c => c.Name == "ImportedTonesCard").IsEffectivelyVisible);
            });
            on.CloseCommand.Execute(null);
        }, CancellationToken.None);
    }

    /// <summary>
    ///     "Import tone file…" sits in the tones rail in EVERY build (it reads a file the charter
    ///     picked, not a game package): the chip lays out inside the rail, and pressing it drives
    ///     the real command — a DLC Builder tone file lands in the project's tone list.
    /// </summary>
    [Fact]
    public async Task ToneDesigner_OffersTheToneFileImport_InEveryBuild_AndItDrivesTheCommand()
    {
        var file = Path.Combine(Path.GetTempPath(), "dc-tonefile-" + Guid.NewGuid().ToString("N") + ".tone2014.xml");
        await File.WriteAllTextAsync(file, DotCharter.Tests.Audio.Dsp.Tones.ToneFileFixtures.ToolkitXml, CancellationToken.None);
        try
        {
            await HeadlessSessionFixture.Dispatch(() =>
            {
                var storage = new FakeFileStorageService { NextOpenFile = file };
                var vm = CreateToneDesignerViewModel(features: DotCharter.Services.Plugins.PackageFeatures.None, storage: storage);
                var dialog = new ToneDesignerDialogView { DataContext = vm };
                WithLaidOutDialog(dialog, () =>
                {
                    var chip = Assert.IsType<Button>(Descendants<Control>(dialog).First(c => c.Name == "ImportToneFileChip"));
                    Assert.True(chip.IsEffectivelyVisible, "the tone-file import chip is not offered");
                    Assert.True(chip.Bounds is { Width: > 1, Height: > 1 }, $"the chip laid out {chip.Bounds}");
                    var rail = Descendants<Control>(dialog).First(c => c.Name == "ToneListFrame");
                    Assert.True(chip.TranslatePoint(new Point(chip.Bounds.Width, 0), rail)?.X <= rail.Bounds.Width + 1,
                        "the chip runs past the right edge of the tones rail");
                    Assert.Same(vm.ImportToneFileCommand, chip.Command);

                    var before = vm.ProjectTones.Count;
                    // The command resumes on this dispatcher after reading the file: pump it, never block it.
                    chip.Command!.Execute(null);
                    for (var i = 0; i < 500 && vm.ProjectTones.Count == before; i++)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                        Thread.Sleep(10);
                    }
                    Assert.Equal(before + 1, vm.ProjectTones.Count);
                    Assert.Equal("Test", vm.SelectedTone?.Key);
                });
                vm.CloseCommand.Execute(null);
            }, CancellationToken.None);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    ///     The Volume row is DLC Builder's: a spinner showing the attenuation as a POSITIVE number
    ///     (12.0 for -12 dB) and a track between a quiet and a loud speaker, both over the one stored
    ///     value. Both must lay out and each must drive the editor — a binding that missed would
    ///     leave a control that renders and does nothing.
    /// </summary>
    [Fact]
    public async Task ToneDesigner_VolumeRow_ShowsThePositiveAttenuation_AndBothControlsDriveTheStoredDb()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = CreateToneDesignerViewModel();
            var dialog = new ToneDesignerDialogView { DataContext = vm };
            WithLaidOutDialog(dialog, () =>
            {
                var spinner = Assert.IsType<NumericUpDown>(Descendants<Control>(dialog).First(c => c.Name == "VolumeInput"));
                var slider = Assert.IsType<Slider>(Descendants<Control>(dialog).First(c => c.Name == "VolumeSlider"));
                foreach (var control in new Control[] { spinner, slider })
                    Assert.True(control.Bounds is { Width: > 1, Height: > 1 }, $"{control.Name} laid out {control.Bounds}");
                Assert.True(Descendants<Avalonia.Controls.PathIcon>(dialog).Count(i => i.Parent == slider.Parent) == 2,
                    "the quiet and loud speaker icons flank the track");
                Assert.Equal((0.1m, 36m), (spinner.Minimum, spinner.Maximum));
                Assert.Equal((0.1, 36.0), (slider.Minimum, slider.Maximum));

                vm.Editor.VolumeDb = -12;
                dialog.UpdateLayout();
                Assert.Equal(12.0m, spinner.Value);
                Assert.Equal(12.0, slider.Value, 3);

                slider.Value = 7.5;
                Assert.Equal(-7.5, vm.Editor.VolumeDb, 3);
                spinner.Value = 3.2m;
                Assert.Equal(-3.2, vm.Editor.VolumeDb, 3);
                Assert.Equal(3.2, slider.Value, 3);
            });
            vm.CloseCommand.Execute(null);
        }, CancellationToken.None);
    }

    /// <summary>A Tone Designer over a real project (two authored tones, two
    /// instrumental arrangements) with the test fakes behind it — no DI graph,
    /// no audio device. Preview applies run inline so the assertions are
    /// synchronous. <paramref name="lead" /> sets Lead's tone fields before the dialog reads them.</summary>
    private static DotCharter.Presentation.Screens.Dialogs.ToneDesignerViewModel CreateToneDesignerViewModel(
        Action<Arrangement>? lead = null, DotCharter.Services.Plugins.IPackageFeatures? features = null,
        FakeFileStorageService? storage = null)
    {
        var project = new ChartProject { Chart = { SongName = "Tone Song", Artist = "DotCharter" } };
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead" });
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass" });
        lead?.Invoke(project.Arrangements[0]);
        foreach (var key in new[] { "clean", "dist" })
        {
            var tone = RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared);
            project.ToneDefinitions.Add(new ToneDefinition { Key = key, Name = key, Json = RsToneJsonWriter.Write(tone) });
        }

        // The modules the converter emits for a pedal-less tone, so a preview
        // apply lands instead of hitting an empty catalogue.
        var catalog = new[] { "amp", "cab", "gain" }
            .Select(id => new DspModuleType(id, id, new List<DspParamInfo> { new("level", "level", -60f, 12f, 0f) }))
            .ToList();
        var chain = new FakeDspChainService(catalog);
        var logger = new FakeAppLogger();
        var history = new HistoryService(logger);
        var state = new ProjectState { ActiveProject = project };
        var services = new DotCharter.Presentation.Screens.Dialogs.ChartEditDialogServices(
            DotCharter.Tests.Presentation.Dialogs.RecordingProxy.Create<DotCharter.Services.Editing.IChartEditorSurface>().Proxy,
            history, state, new FakeConfigurationService(), DotCharter.Localization.LocalizationService.Instance, logger);
        var vm = new DotCharter.Presentation.Screens.Dialogs.ToneDesignerViewModel(services, storage ?? new FakeFileStorageService(),
            FakeTonelabSession.Targets(new DotCharter.Presentation.Screens.Dialogs.TonePreviewSession(chain, new DcRigApplier(chain))),
            new DotCharter.Presentation.Screens.Dialogs.ToneDesignerTools(new DotCharter.Services.Editing.Tones.ToneGenerationRun(history,
                new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter(), logger), new DotCharter.Presentation.Dialogs.DialogsSlot()),
            new DotCharter.Presentation.Screens.Dialogs.DialogMemory(), features ?? DotCharter.Services.Plugins.PackageFeatures.All);
        vm.Preview.OffThreadRunner = action =>
        {
            action();
            return Task.CompletedTask;
        };
        return vm;
    }
}
