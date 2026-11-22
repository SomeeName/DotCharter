using DotCharter.Services.Export;
using DotCharter.Tone.Infrastructure.Storage;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tests.UI.ViewModels.Dialogs;
using DotCharter.UI.Views.Dialogs;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #782 on the REAL export view model in the real view: PC + Mac names both packages under
///     the platform picker; without Mac encryption Mac alone is greyed with its reason in the
///     blocked strip and PC + Mac says it writes the PC package alone; a ticked 7-string guitar
///     rules the Mac package out the same way. Each note is laid out with a size.
/// </summary>
[Collection("HeadlessUI")]
public sealed class RocksmithExportPlatformUiPerfSmokeTests
{
    private static readonly FakeLocalization Loc = FakeLocalization.English();

    /// <summary>The strings the view shows: the headless session's culture (DOTCHARTER_HEADLESS_CULTURE), English when unset.</summary>
    private static readonly FakeLocalization ViewCulture = Environment.GetEnvironmentVariable("DOTCHARTER_HEADLESS_CULTURE") is { Length: > 0 } culture
        ? FakeLocalization.For(culture)
        : FakeLocalization.English();

    [Fact]
    public async Task MainAudioHint_IsMeasuredAndCaptured()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var keys = TestRocksmithKeys.UseNoKeys();
            using var vm = Open(RocksmithExportDialogViewModelTests.CreateValidProject());
            vm.AudioPath = "";
            WithView(vm, view =>
            {
                var input = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == vm.MainAudioWatermark);
                Assert.True(input.Bounds is { Width: > 1, Height: > 1 });
                input.BringIntoView();
                var window = (Window)TopLevel.GetTopLevel(view)!;
                NarrowWidthLayout.Settle(window);
                NarrowWidthLayout.Capture(window, "export-main-audio");
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DefaultPackagePath_IsMeasuredAndCaptured()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var folder = Directory.CreateTempSubdirectory("dotcharter-export-path-").FullName;
            try
            {
                using var keys = TestRocksmithKeys.UseNoKeys();
                var harness = new RocksmithExportDialogViewModelTests.Harness(RocksmithExportDialogViewModelTests.CreateValidProject());
                harness.State.CurrentProjectPath = Path.Combine(folder, "project.dcproj");
                harness.Config.AppConfig.LastExportDir = Directory.CreateDirectory(Path.Combine(folder, "previous")).FullName;
                using var vm = harness.Open();
                WithView(vm, view =>
                {
                    var path = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == vm.OutputPath);
                    Assert.True(path.Bounds is { Width: > 1, Height: > 1 });
                    path.BringIntoView();
                    path.Focus();
                    path.CaretIndex = path.Text!.Length;
                    var window = (Window)TopLevel.GetTopLevel(view)!;
                    NarrowWidthLayout.Settle(window);
                    NarrowWidthLayout.Capture(window, "export-default-path");
                    Assert.Equal("DotCharter_Dialog-Song_v1_p.psarc", Path.GetFileName(vm.OutputPath));
                });
            }
            finally
            {
                BestEffortDelete.Directory(folder);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PcAndMac_NamesBothPackages()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var keys = TestRocksmithKeys.UseSynthetic();
            var vm = Open(RocksmithExportDialogViewModelTests.CreateValidProject());
            vm.OutputPath = @"E:\Rocksmith\dlc\pair_p.psarc";
            WithView(vm, view =>
            {
                Assert.Equal(ExportPlatform.PcAndMac, vm.Target.SelectedPlatformOption.Value);
                AssertNote(view, Loc.Format("Dialog_Export_WritesBoth", "pair_p.psarc", "pair_m.psarc"));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task WithoutMacEncryption_MacIsBlockedWithItsReason_AndPcAndMacWritesPcOnly()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var keys = TestRocksmithKeys.UseNoKeys();
            var vm = Open(RocksmithExportDialogViewModelTests.CreateValidProject());
            vm.OutputPath = @"E:\Rocksmith\dlc\pair_p.psarc";
            vm.AudioPath = @"E:\Rocksmith\audio.wem";
            var reason = Loc["Dialog_Export_MacNeedsEncryption"];
            WithView(vm, view =>
            {
                Pick(vm, ExportPlatform.Mac);
                Settle(view);
                AssertNote(view, reason);
                var blocked = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ExportBlockedText");
                Assert.True(blocked.IsEffectivelyVisible);
                Assert.Contains(Loc.Format("Dialog_Export_MacUnavailable", reason), blocked.Text!.Split('\n'));

                Pick(vm, ExportPlatform.PcAndMac);
                Settle(view);
                AssertNote(view, Loc.Format("Dialog_Export_PcOnlyBecause", reason));
                Assert.DoesNotContain(Loc.Format("Dialog_Export_MacUnavailable", reason), vm.ExportBlockedReason.Split('\n'));
            });
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ATickedSevenStringGuitar_RulesTheMacPackageOut()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var project = RocksmithExportDialogViewModelTests.CreateValidProject();
            project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Seven", StringCount = 7 });
            using var keys = TestRocksmithKeys.UseSynthetic();
            var vm = Open(project);
            vm.OutputPath = @"E:\Rocksmith\dlc\seven_p.psarc";
            WithView(vm, view =>
                AssertNote(view, Loc.Format("Dialog_Export_PcOnlyBecause", Loc["Dialog_Export_RsxrWindowsOnly"])));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ThePackagePicker_IsLaidOut_AndUsableWithoutKeys()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using (TestRocksmithKeys.UseSynthetic())
            {
                var vm = Open(RocksmithExportDialogViewModelTests.CreateValidProject());
                WithView(vm, view =>
                {
                    var picker = PackagePicker(view);
                    Assert.True(picker.IsEffectivelyEnabled);
                    Assert.Equal(2, picker.ItemCount);
                    Assert.False(((ExportPackageChoice)picker.SelectedItem!).Plain);
                    Assert.True(picker.Bounds is { Width: > 1, Height: > 1 }, $"package picker laid out as {picker.Bounds}");
                    // Encrypted: no warning. Picking Plain brings it, through the real binding.
                    Assert.False(PlainWarning(view).IsEffectivelyVisible);
                    vm.Target.SelectedPackageOption = vm.Target.PackageOptions.First(o => o.Plain);
                    Settle(view);
                    Assert.True(PlainWarning(view).IsEffectivelyVisible);
                    Assert.True(PlainWarning(view).Bounds is { Width: > 1, Height: > 1 }, $"warning laid out as {PlainWarning(view).Bounds}");
                    picker.SelectedItem = vm.Target.PackageOptions[^1];
                    Settle(view);
                    Assert.True(vm.Target.SelectedPackageOption.Plain);
                    Assert.True(PlainWarning(view).IsEffectivelyVisible);
                    CapturePicker(view, picker, "rsxr-export-package-picker");
                });
            }

            using (TestRocksmithKeys.UseNoKeys())
            {
                var vm = Open(RocksmithExportDialogViewModelTests.CreateValidProject());
                WithView(vm, view =>
                {
                    var picker = PackagePicker(view);
                    Assert.False(picker.IsEffectivelyEnabled);
                    Assert.Equal(1, picker.ItemCount);
                    Assert.True(picker.Bounds is { Width: > 1, Height: > 1 });
                    picker.SelectedItem = vm.Target.PackageOptions[^1];
                    Settle(view);
                    Assert.True(vm.Target.SelectedPackageOption.Plain);
                    Assert.True(((ExportPackageChoice)picker.SelectedItem!).Plain);
                    Assert.True(PlainWarning(view).IsEffectivelyVisible);
                    CapturePicker(view, picker, "rsxr-export-package-picker-no-keys");
                });
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtendedPackageReasonMeasuresBesideTheDisabledPlainPicker(bool withKeys)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            using var keys = withKeys ? TestRocksmithKeys.UseSynthetic() : TestRocksmithKeys.UseNoKeys();
            var project = RocksmithExportDialogViewModelTests.CreateValidProject();
            project.Arrangements[0].StringCount = 7;
            project.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
            using var vm = Open(project);
            WithView(vm, view =>
            {
                var picker = PackagePicker(view);
                Assert.False(picker.IsEffectivelyEnabled);
                Assert.True(((ExportPackageChoice)picker.SelectedItem!).Plain);
                Assert.False(PlainWarning(view).IsEffectivelyVisible);
                var reason = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ExtendedRangePackageReason");
                Assert.True(reason.IsEffectivelyVisible);
                Assert.True(reason.Bounds is { Width: > 0, Height: > 0 });
                Assert.Equal(ViewCulture["Dialog_Export_ExtendedRangePackage"], reason.Text);
                if (UiSweepRunner.CaptureEnabled)
                    NarrowWidthLayout.Capture((Window)TopLevel.GetTopLevel(view)!, withKeys ? "rsxr-export-extended" : "rsxr-export-extended-no-keys");
            });
        }, CancellationToken.None);
    }

    private static void CapturePicker(RocksmithExportDialogView view, ComboBox picker, string name)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        picker.IsDropDownOpen = true;
        try
        {
            var window = (Window)TopLevel.GetTopLevel(view)!;
            NarrowWidthLayout.Settle(window);
            NarrowWidthLayout.Capture(window, name);
        }
        finally
        {
            picker.IsDropDownOpen = false;
        }
    }

    private static Border PlainWarning(RocksmithExportDialogView view) =>
        view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PlainPackageWarning");

    private static ComboBox PackagePicker(RocksmithExportDialogView view) =>
        view.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "PackageFormPicker");

    private static RocksmithExportViewModel Open(ChartProject project) =>
        new RocksmithExportDialogViewModelTests.Harness(project).Open();

    private static void Pick(RocksmithExportViewModel vm, ExportPlatform platform) =>
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.First(o => o.Value == platform);

    private static void WithView(RocksmithExportViewModel vm, Action<RocksmithExportDialogView> assert)
    {
        var view = new RocksmithExportDialogView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 720, Content = DialogHost.Preview(view) };
        window.Show();
        try
        {
            Settle(view);
            assert(view);
        }
        finally
        {
            window.Close();
        }
    }

    private static void Settle(Control view)
    {
        Dispatcher.UIThread.RunJobs();
        view.UpdateLayout();
    }

    private static void AssertNote(RocksmithExportDialogView view, string expected)
    {
        var note = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PlatformNoteHint");
        Assert.True(note.IsEffectivelyVisible);
        Assert.Equal(expected, note.Text);
        Assert.True(note.Bounds is { Width: > 1, Height: > 1 }, $"platform note laid out as {note.Bounds}");
    }
}
