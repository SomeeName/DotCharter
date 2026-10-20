using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Audio.StemSeparation.Gpu;

namespace DotCharter.Tests.UI;

/// <summary>
///     The stem-separation picker shows "DirectML" and "CPU", not the stored names; the
///     stored values themselves are unchanged. When the machine cannot run DirectML it is
///     listed greyed, cannot be picked, and prints WHY — the runtime lacks it, or no GPU
///     has the video memory.
/// </summary>
public sealed class ExecutionProviderLabelTests
{
    [Theory]
    [InlineData("DirectML", "DirectML")]
    [InlineData("cpu", "CPU")]
    public void EachStoredName_ReadsAsPeopleWriteIt(string stored, string shown)
    {
        var option = StemExecutionProviderOptions.For(FakeLocalization.KeysOnly(), null).Single(o => o.Value == stored);
        Assert.Equal(shown, option.Label);
        Assert.True(option.IsAvailable);
        Assert.Equal("", option.UnavailableReason);
    }

    [Theory]
    [InlineData(StemExecutionReason.NoUsableGpu, "Settings_StemModels_DirectMlUnavailable(3 GB)")]
    [InlineData(StemExecutionReason.NoDirectMlInRuntime, "Settings_StemModels_DirectMlMissing")]
    public void DirectMl_IsGreyedWithTheReasonItCannotRun(StemExecutionReason blocker, string reason)
    {
        var options = StemExecutionProviderOptions.For(FakeLocalization.KeysOnly(), blocker);
        var directMl = options.Single(o => o.Value == StemExecutionProviderOptions.DirectMl);
        Assert.False(directMl.IsAvailable);
        Assert.Equal(reason, directMl.UnavailableReason);
        Assert.True(options.Single(o => o.Value == StemExecutionProviderOptions.Cpu).IsAvailable);
    }

    [Theory]
    [InlineData("cpu", true, "cpu")]
    [InlineData("Auto", true, "DirectML")]
    [InlineData("Cuda", true, "DirectML")]
    [InlineData("DirectML", false, "cpu")]
    [InlineData("Auto", false, "cpu")]
    public void AStoredChoice_NormalisesToAnEntryThatCanRun(string stored, bool directMlAvailable, string expected) =>
        Assert.Equal(expected, StemExecutionProviderOptions.Normalize(stored, directMlAvailable));

    [Fact]
    public void ThePicker_GreysAnUnavailableEntry_AndPrintsItsReason()
    {
        var view = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Settings", "StemModelsSettingsView.axaml")));
        var picker = view[view.IndexOf("x:Name=\"StemExecutionProvider\"", StringComparison.Ordinal)..];
        picker = picker[..picker.IndexOf("</ComboBox>", StringComparison.Ordinal)];

        Assert.Contains("SelectedItem=\"{Binding Backend.SelectedExecutionProvider, Mode=TwoWay}\"", picker, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"IsEnabled\" Value=\"{Binding IsAvailable}\" />", picker, StringComparison.Ordinal);
        Assert.Contains("{Binding Label}", picker, StringComparison.Ordinal);
        Assert.Contains("{Binding UnavailableReason}", picker, StringComparison.Ordinal);
    }
}
