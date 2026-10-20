using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Editing.Tones;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Services.Editing.Tones;

/// <summary>
///     <see cref="RigToneImport" />: a tone from the rig lands as ONE undo step (the definition and
///     its slot together), goes into Base when Base is empty, replaces a body in place, and a tone
///     the catalogue gate refuses writes nothing.
/// </summary>
public sealed class RigToneImportTests
{
    [Fact]
    public void ANewTone_TakesTheEmptyBaseSlot_AndUndoesAsOneStep()
    {
        var (project, arrangement, history) = Setup(toneBase: "");

        var written = RigToneImport.Apply(project, arrangement, history, Tone(), replaceKey: null);

        Assert.Equal(RigToneImport.NewKeyStem, written.Key);
        Assert.Equal(written.Key, arrangement.ToneBase);
        Assert.Equal(written.Key, arrangement.StartingTone);
        Assert.Single(history.GetUndoSteps());

        history.Undo();
        Assert.Empty(project.ToneDefinitions);
        Assert.Equal("", arrangement.ToneBase);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void ASecondTone_GetsAUniqueKey_AndTheFirstFreeLetterSlot()
    {
        var (project, arrangement, history) = Setup(toneBase: "base");

        RigToneImport.Apply(project, arrangement, history, Tone(), replaceKey: null);
        var second = RigToneImport.Apply(project, arrangement, history, Tone(), replaceKey: null);

        Assert.NotEqual(RigToneImport.NewKeyStem, second.Key);
        Assert.Equal(RigToneImport.NewKeyStem, arrangement.ToneA);
        Assert.Equal(second.Key, arrangement.ToneB);
        Assert.Equal("base", arrangement.ToneBase);
    }

    [Fact]
    public void ACatalogueFault_IsRefused_AndWritesNothing()
    {
        var (project, arrangement, history) = Setup(toneBase: "");
        var bad = Tone() with { Amp = new RsAmp("Amp_DoesNotExist", null, new Dictionary<string, float>()) };

        Assert.Throws<RsToneCatalogException>(() => RigToneImport.Apply(project, arrangement, history, bad, replaceKey: null));

        Assert.Empty(project.ToneDefinitions);
        Assert.Equal("", arrangement.ToneBase);
        Assert.False(history.CanUndo);
    }

    private static RsTone Tone() => RsToneAuthoring.CreateDefaultTone("x", "x", RsGearCatalog.Shared);

    private static (ChartProject, Arrangement, HistoryService) Setup(string toneBase)
    {
        var arrangement = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = toneBase, StartingTone = toneBase };
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        return (project, arrangement, new HistoryService(new FakeAppLogger()));
    }
}
