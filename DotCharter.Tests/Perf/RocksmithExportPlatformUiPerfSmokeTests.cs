using DotCharter.Services.Export;
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
                AssertNote(view, Loc.Format("Dialog_Export_PcOnlyBecause", Loc["Dialog_Export_MacSixStringsOnly"])));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ThePackagePicker_IsLaidOut_AndGreyedWithoutKeys()
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
                    vm.Target.SelectedPackageOption = vm.Target.PackageOptions.Single(o => o.Plain);
                    Settle(view);
                    Assert.True(PlainWarning(view).IsEffectivelyVisible);
                    Assert.True(PlainWarning(view).Bounds is { Width: > 1, Height: > 1 }, $"warning laid out as {PlainWarning(view).Bounds}");
                });
            }

            using (TestRocksmithKeys.UseNoKeys())
            {
                var vm = Open(RocksmithExportDialogViewModelTests.CreateValidProject());
                WithView(vm, view =>
                {
                    var picker = PackagePicker(view);
                    Assert.False(picker.IsEffectivelyEnabled);
                    Assert.True(((ExportPackageChoice)picker.SelectedItem!).Plain);
                    Assert.True(PlainWarning(view).IsEffectivelyVisible);
                });
            }
        }, CancellationToken.None);
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
