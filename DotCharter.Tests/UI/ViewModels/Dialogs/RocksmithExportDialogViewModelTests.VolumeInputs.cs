using System.Reflection;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.UI.ViewModels.Dialogs;

/// <summary>Pending work is reusable for its captured measurement inputs, including source content.</summary>
public sealed partial class RocksmithExportDialogViewModelTests
{
    [Theory]
    [InlineData("same", true)]
    [InlineData("text", true)]
    [InlineData("typed", true)]
    [InlineData("editor-length", true)]
    [InlineData("save-as", true)]
    [InlineData("start", false)]
    [InlineData("audio-path", false)]
    [InlineData("audio-content", false)]
    [InlineData("preview-path", false)]
    [InlineData("preview-content", false)]
    [InlineData("manual", false)]
    public async Task Pending_measurement_identity_uses_actual_inputs(string change, bool matches)
    {
        var audio = AutomaticVolumeFixture.WriteMono(_tempDir);
        var other = Path.Combine(_tempDir, "other.wav");
        File.Copy(audio, other);
        var project = CreateValidProject();
        var h = new Harness(project);
        using var vm = h.Open();
        var pending = (Task)typeof(RocksmithExportViewModel).GetField("_volumeMeasurement", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        await BoundedWait.ForAsync(pending, "the empty-source prefill before input comparisons", TestContext.Current.CancellationToken);
        vm.AudioPath = audio;
        if (change == "preview-content") vm.PreviewAudioPath = other;
        var before = new RocksmithExportVolumeInput(vm, project.Chart, h.Logger);
        await BoundedWait.ForAsync(before.Ready, "capturing original source identities", TestContext.Current.CancellationToken);
        switch (change)
        {
            case "text": vm.PreviewStartText = "0:32.000"; break;
            case "typed":
                vm.AudioVolume = -3;
                vm.PreviewVolume = -4;
                break;
            case "editor-length": h.State.TotalAudioLengthMs = 64_000; break;
            case "save-as":
                h.State.CurrentProjectPath = Path.Combine(_tempDir, "saved-as.dcproj");
                h.State.CurrentAudioFileName = other;
                break;
            case "start": project.Chart.PreviewStartMs = 32_000; break;
            case "audio-path": vm.AudioPath = other; break;
            case "audio-content": File.AppendAllText(audio, "changed source bytes"); break;
            case "preview-path": vm.PreviewAudioPath = other; break;
            case "preview-content": File.AppendAllText(other, "changed preview bytes"); break;
            case "manual": project.Chart.PreviewVolumeManual = true; break;
        }
        var after = new RocksmithExportVolumeInput(vm, project.Chart, h.Logger);
        Assert.Equal(matches, await BoundedWait.ForAsync(after.MatchesAsync(before), "comparing pending measurement inputs", TestContext.Current.CancellationToken));
        await BoundedWait.ForAsync(after.Ready, "finishing source identity capture", TestContext.Current.CancellationToken);
    }
}
