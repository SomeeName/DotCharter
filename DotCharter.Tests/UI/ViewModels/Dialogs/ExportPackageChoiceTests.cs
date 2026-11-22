using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>
///     Stock package choices are Encrypted and Plain. Without keys the disabled picker
///     shows Plain; extended content fixes that state through its selection policy.
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

        vm.SelectedPackageOption = vm.PackageOptions.First(o => o.Plain);

        Assert.True(vm.WritesPlain);
    }

    [Fact]
    public void WithoutKeys_PlainIsTheOnlyChoice_AndThePickerIsDisabled()
    {
        using var keys = TestRocksmithKeys.UseNoKeys();
        var vm = Create();

        Assert.True(vm.SelectedPackageOption.Plain);
        Assert.True(vm.WritesPlain);
        Assert.False(vm.CanChoosePackage);
        Assert.Single(vm.PackageOptions);
        Assert.All(vm.PackageOptions, o => Assert.True(o.Plain));

        vm.SelectedPackageOption = new ExportPackageChoice(false, "Encrypted");
        Assert.True(vm.WritesPlain);
    }
}
