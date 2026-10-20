// The Tone designer's Volume as DLC Builder shows it: a positive attenuation over the stored negative dB.
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Presentation.Dialogs;

public sealed partial class ToneDesignerViewModelTests
{
    private static ToneDesignerViewModel OpenWithVolume(Harness harness, ChartProject project, string volume)
    {
        var tone = RsToneAuthoring.CreateDefaultTone("clean", "Clean", RsGearCatalog.Shared);
        var body = RsToneJsonWriter.Write(tone with { VolumeDb = RsToneJsonParser.ParseVolume(volume) });
        project.ToneDefinitions.Add(new ToneDefinition { Key = "clean", Name = "Clean", Json = body });
        return harness.Open();
    }

    [Fact]
    public void TheVolumeIsShownAsAPositiveAttenuation_OverTheStoredNegativeDb()
    {
        var project = CreateProject();
        var vm = OpenWithVolume(new Harness(project), project, "-19.8");

        Assert.Equal(-19.8, vm.Editor.VolumeDb, 3);
        Assert.Equal(19.8, vm.Editor.VolumeAttenuation, 3);
        Assert.Equal((0.1, 36.0), (vm.Editor.VolumeAttenuationMin, vm.Editor.VolumeAttenuationMax));

        vm.Editor.VolumeAttenuation = 12.0;

        Assert.Equal(-12.0, vm.Editor.VolumeDb, 3);
    }

    [Theory]
    [InlineData(0.0, 0.1)]
    [InlineData(-5.0, 0.1)]
    [InlineData(99.0, 36.0)]
    [InlineData(0.1, 0.1)]
    [InlineData(36.0, 36.0)]
    public void TheAttenuationClampsToDlcBuildersRange(double typed, double expected)
    {
        var project = CreateProject();
        var vm = OpenWithVolume(new Harness(project), project, "-12");

        vm.Editor.VolumeAttenuation = typed;

        Assert.Equal(expected, vm.Editor.VolumeAttenuation, 3);
        Assert.Equal(-expected, vm.Editor.VolumeDb, 3);
    }

    [Fact]
    public void ChangingTheStoredVolume_RaisesTheAttenuation_SoTheSpinnerAndTrackFollow()
    {
        var project = CreateProject();
        var vm = OpenWithVolume(new Harness(project), project, "-12");
        var raised = new List<string?>();
        vm.Editor.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Editor.VolumeDb = -7.5;

        Assert.Contains(nameof(ToneEditorViewModel.VolumeAttenuation), raised);
        Assert.Equal(7.5, vm.Editor.VolumeAttenuation, 3);
    }

    [Fact]
    public void ManyAttenuationEdits_FlushAsOneHistoryAction_WithTheNegativeVolumeInTheBody()
    {
        var project = CreateProject();
        var harness = new Harness(project);
        var vm = OpenWithVolume(harness, project, "-12");
        var before = harness.History.GetUndoSteps().Count;

        foreach (var typed in new[] { 3.0, 4.5, 8.0, 14.2 }) vm.Editor.VolumeAttenuation = typed;
        Assert.Equal(before, harness.History.GetUndoSteps().Count);
        vm.CloseCommand.Execute(null);

        Assert.Equal(before + 1, harness.History.GetUndoSteps().Count);
        Assert.True(RsToneJsonParser.TryParse(project.ToneDefinitions[0].Json, out var stored));
        Assert.Equal(-14.2f, stored!.VolumeDb, 0.001);
    }
}
