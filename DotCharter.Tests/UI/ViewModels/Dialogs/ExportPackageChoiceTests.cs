using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     #804: the export's Package choice is Encrypted unless Plain is picked, and without keys
///     only Plain is possible and the picker says so.
/// </summary>
public sealed class ExportPackageChoiceTests
{
    private static ExportTargetViewModel Create() => new(FakeLocalization.English());

    [Fact]
    public void WithKeys_TheDefaultIsEncrypted_AndThePickerIsLive()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var vm = Create();

        Assert.False(vm.SelectedPackageOption.Plain);
        Assert.False(vm.WritesPlain);
        Assert.True(vm.CanChoosePackage);
        Assert.Equal([false, true], vm.PackageOptions.Select(o => o.Plain));
    }

    [Fact]
    public void PickingPlain_WritesPlain()
    {
        using var keys = TestRocksmithKeys.UseSynthetic();
        var vm = Create();

        vm.SelectedPackageOption = vm.PackageOptions.Single(o => o.Plain);

        Assert.True(vm.WritesPlain);
    }

    [Fact]
    public void WithoutKeys_OnlyPlainIsPossible_AndThePickerIsDisabled()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var vm = Create();

        Assert.True(vm.SelectedPackageOption.Plain);
        Assert.True(vm.WritesPlain);
        Assert.False(vm.CanChoosePackage);

        // Even a preset that says Encrypted cannot write it.
        vm.SelectedPackageOption = vm.PackageOptions.Single(o => !o.Plain);
        Assert.True(vm.WritesPlain);
    }
}
