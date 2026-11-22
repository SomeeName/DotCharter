using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Export;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

public sealed class RocksmithExtendedRangeChoiceTests
{
    [Theory]
    [InlineData(ExportPlatform.Pc)]
    [InlineData(ExportPlatform.Mac)]
    [InlineData(ExportPlatform.PcAndMac)]
    public void Changing_the_package_form_keeps_the_explicit_output_filename(ExportPlatform platform)
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var vm = new RocksmithExportDialogViewModelTests.Harness(RocksmithExportDialogViewModelTests.CreateValidProject()).Open();
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(o => o.Value == platform);
        vm.OutputPath = @"E:\charts\chosen.psarc";

        foreach (var option in vm.Target.PackageOptions)
        {
            vm.Target.SelectedPackageOption = option;
            Assert.Equal(@"E:\charts\chosen.psarc", vm.OutputPath);
        }
    }

    [Fact]
    public void The_gallery_target_offers_stock_package_choices_and_updates_its_warning()
    {
        var target = DotCharter.Presentation.Fakes.Dialogs.FakeRocksmithExportScreen.Default().Target;
        Assert.Equal(2, target.PackageOptions.Count);
        target.SelectedPlatformOption = target.PlatformOptions[0];
        target.SelectedPackageOption = target.PackageOptions[1];
        Assert.Equal("Plain (unencrypted)", target.SelectedPackageOption.Label);
        Assert.True(target.WritesPlainPackage);
        target.SelectedPlatformOption = target.PlatformOptions[2];
        Assert.False(target.WritesPlainPackage);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public void The_picker_offers_stock_encryption_choices_when_keys_are_available(bool encrypted, int count)
    {
        using var keys = encrypted ? TestRocksmithKeys.UseSynthetic() : TestRocksmithKeys.UseNoKeys();
        var target = new ExportTargetViewModel(FakeLocalization.English());
        Assert.Equal(encrypted, target.CanChoosePackage);
        Assert.Equal(count, target.PackageOptions.Count);
        Assert.Equal(FakeLocalization.English()["Dialog_Export_PackagePlain"], target.PackageOptions[^1].Label);
        Assert.False(target.WritesExtendedRange);
        Assert.Equal(!encrypted, target.WritesPlain);
        if (!encrypted) Assert.All(target.PackageOptions, o => Assert.True(o.Plain));
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, true)]
    [InlineData(false, 0, true)]
    public void Each_choice_sets_the_build_options(bool encrypted, int choice, bool plain)
    {
        using var keys = encrypted ? TestRocksmithKeys.UseSynthetic() : TestRocksmithKeys.UseNoKeys();
        var project = RocksmithExportDialogViewModelTests.CreateValidProject();
        var vm = new RocksmithExportDialogViewModelTests.Harness(project).Open();
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(o => o.Value == ExportPlatform.Pc);
        vm.Target.SelectedPackageOption = vm.Target.PackageOptions[choice];

        var options = vm.BuildOptions(project);

        Assert.Equal(plain, options.PlainPackage);
        Assert.EndsWith("_p.psarc", vm.OutputPath);
    }

    [Fact]
    public void Extended_range_blocks_mac_and_reduces_a_combined_target_then_encrypted_restores_it()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var project = RocksmithExportDialogViewModelTests.CreateValidProject();
        project.Arrangements[0].StringCount = 7;
        project.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
        var vm = new RocksmithExportDialogViewModelTests.Harness(project).Open();
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(o => o.Value == ExportPlatform.PcAndMac);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Arrangements[0].IsSelected = false;
        vm.Arrangements[0].IsSelected = true;
        Assert.Contains(nameof(RocksmithExportViewModel.MacBlock), changed);
        Assert.Contains(nameof(RocksmithExportViewModel.PlatformNote), changed);
        Assert.NotNull(vm.MacBlock);
        Assert.True(vm.HasPlatformNote);
        Assert.Equal(ExportPlatform.Pc, vm.BuildOptions(project).Platform);
        Assert.False(vm.Target.WritesPlainPackage);

        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(o => o.Value == ExportPlatform.Mac);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Contains(FakeLocalization.English()["Dialog_Export_RsxrWindowsOnly"], vm.ExportBlockedReason);

        vm.Arrangements[0].IsSelected = false;
        Assert.Null(vm.MacBlock);
        Assert.Equal(ExportPlatform.Mac, vm.BuildOptions(project).Platform);
    }

    [Theory]
    [InlineData(true, 0, ExportPlatform.Pc)]
    [InlineData(true, 0, ExportPlatform.Mac)]
    [InlineData(true, 0, ExportPlatform.PcAndMac)]
    [InlineData(true, 1, ExportPlatform.Pc)]
    [InlineData(true, 1, ExportPlatform.Mac)]
    [InlineData(true, 1, ExportPlatform.PcAndMac)]
    [InlineData(true, 2, ExportPlatform.Pc)]
    [InlineData(true, 2, ExportPlatform.Mac)]
    [InlineData(true, 2, ExportPlatform.PcAndMac)]
    [InlineData(false, 0, ExportPlatform.Pc)]
    [InlineData(false, 0, ExportPlatform.Mac)]
    [InlineData(false, 0, ExportPlatform.PcAndMac)]
    [InlineData(false, 1, ExportPlatform.Pc)]
    [InlineData(false, 1, ExportPlatform.Mac)]
    [InlineData(false, 1, ExportPlatform.PcAndMac)]
    [InlineData(false, 2, ExportPlatform.Pc)]
    [InlineData(false, 2, ExportPlatform.Mac)]
    [InlineData(false, 2, ExportPlatform.PcAndMac)]
    public void Package_and_platform_choices_keep_their_gates_options_and_reasons(bool keysAvailable, int choice, ExportPlatform platform)
    {
        using var keys = keysAvailable ? TestRocksmithKeys.UseSynthetic() : TestRocksmithKeys.UseNoKeys();
        var project = RocksmithExportDialogViewModelTests.CreateValidProject();
        var extended = choice == 2;
        if (extended)
        {
            project.Arrangements[0].StringCount = 7;
            project.Arrangements[0].TuningPitches.Add(new TuningOffset(0));
        }
        var vm = new RocksmithExportDialogViewModelTests.Harness(project).Open();
        var loc = FakeLocalization.English();
        vm.AudioPath = @"E:\charts\song.wem";
        vm.OutputPath = @"E:\charts\song_p.psarc";
        vm.Target.SelectedPackageOption = !keysAvailable && choice == 0
            ? new ExportPackageChoice(false, loc["Dialog_Export_PackageEncrypted"])
            : vm.Target.PackageOptions.Single(o => o.Plain == (choice != 0 || !keysAvailable));
        vm.Target.SelectedPlatformOption = vm.Target.PlatformOptions.Single(o => o.Value == platform);
        var reason = extended ? loc["Dialog_Export_RsxrWindowsOnly"]
            : !keysAvailable ? loc["Dialog_Export_MacNeedsEncryption"] : null;
        var options = vm.BuildOptions(project);

        Assert.Equal(extended ? RocksmithMacBlock.RsxrWindowsOnly
            : !keysAvailable ? RocksmithMacBlock.NoEncryption : null, vm.MacBlock);
        Assert.Equal(platform == ExportPlatform.PcAndMac && reason != null ? ExportPlatform.Pc : platform, options.Platform);
        Assert.Equal((extended || choice == 1 || !keysAvailable) && platform != ExportPlatform.Mac, options.PlainPackage);
        Assert.Equal(extended, vm.Target.WritesExtendedRange);
        Assert.Equal(!extended && platform != ExportPlatform.Mac && (choice != 0 || !keysAvailable), vm.Target.WritesPlainPackage);
        Assert.Equal(platform != ExportPlatform.Mac || reason == null, vm.ExportCommand.CanExecute(null));
        Assert.EndsWith(platform == ExportPlatform.Mac ? "_m.psarc" : "_p.psarc", vm.OutputPath);
        if (platform == ExportPlatform.Pc)
        {
            Assert.Empty(vm.PlatformNote);
        }
        else if (reason != null)
        {
            Assert.Equal(platform == ExportPlatform.Mac ? reason : loc.Format("Dialog_Export_PcOnlyBecause", reason), vm.PlatformNote);
            if (platform == ExportPlatform.Mac) Assert.Contains(loc.Format("Dialog_Export_MacUnavailable", reason), vm.ExportBlockedReason);
        }
        else
        {
            Assert.Equal(platform == ExportPlatform.Mac ? "" : loc.Format("Dialog_Export_WritesBoth", "song_p.psarc", "song_m.psarc"), vm.PlatformNote);
        }
    }
}
