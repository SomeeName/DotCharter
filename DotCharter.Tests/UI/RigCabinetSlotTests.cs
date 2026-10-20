using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

public sealed class RigCabinetSlotTests
{
    [Fact]
    public void SlotBControlsFollowItsFileWhileSlotARemainsActiveWithBuiltInIr()
    {
        var type = CabinetType();
        var chain = new FakeDspChainService([type]);
        var node = new PedalNodeViewModel(chain, chain.AddNode("cab")!, type, FakeLocalization.English());
        var dock = new RigDockUnit(node);
        var a = dock.CabinetSlots[0];
        var b = dock.CabinetSlots[1];
        Assert.True(a.CanControl);
        Assert.False(b.CanControl);
        var emptyStatus = b.StatusText;
        var changes = new List<string?>();
        b.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        b.File!.Value = "room-b.wav";
        Assert.True(b.CanControl);
        Assert.NotEqual(emptyStatus, b.StatusText);
        Assert.Contains(nameof(RigCabinetSlot.CanControl), changes);
        b.File.ClearCommand.Execute(null);
        Assert.False(b.CanControl);
        Assert.Equal(emptyStatus, b.StatusText);
        Assert.True(a.CanControl);
    }

    [Fact]
    public void BothSlots_ProjectTheirOwnLiveFileKnobsAndPolarity()
    {
        var type = CabinetType();
        var chain = new FakeDspChainService([type]);
        var native = chain.AddNode("cab")!;
        var node = new PedalNodeViewModel(chain, native, type, FakeLocalization.English());
        var dock = new RigDockUnit(node);
        Assert.True(dock.IsCabFace);
        Assert.False(dock.ShowFileHeader);
        Assert.False(dock.ShowSecondaryKnobs);
        Assert.Equal(node.StringParams.Select(p => p.Id).OrderBy(id => id),
            dock.CabinetSlots.Select(slot => slot.File!.Id).OrderBy(id => id));
        foreach (var letter in new[] { "A", "B" })
        {
            var fileId = letter == "A" ? "irPath" : "irPathB";
            var slot = Assert.Single(dock.CabinetSlots, s => s.File?.Id == fileId);
            Assert.Same(Assert.Single(node.StringParams, p => p.Id == fileId), slot.File);
            Assert.Equal(new[] { "delay" + letter + "Ms", "gain" + letter + "Db" }, slot.Knobs.Select(p => p.Id).OrderBy(id => id));
            foreach (var knob in slot.Knobs)
                Assert.Same(Assert.Single(node.Params, p => p.Id == knob.Id), knob);
            Assert.Same(Assert.Single(node.ModelSelectors, p => p.ParamId == "invert" + letter), slot.Polarity);
            slot.File!.ClearCommand.Execute(null);
            Assert.Equal((native.NodeId, fileId, string.Empty), chain.StringSets[^1]);
        }
        Assert.Same(Assert.Single(node.Params, p => p.Id == "blend"), dock.Blend);
        var visible = dock.PrimaryKnobs.Concat(dock.CabinetSlots.SelectMany(s => s.Knobs)).Append(dock.Blend!).ToArray();
        Assert.Equal(node.KnobParams.Select(p => p.Id).OrderBy(id => id), visible.Select(p => p.Id).OrderBy(id => id));
        Assert.Equal(visible.Length, visible.Distinct().Count());
    }

    [Fact]
    public void SharedSlotTemplate_ReachesBothFileCommands_EvenBeforeBIrIsLoaded()
    {
        // Whichever editor family view renders the cabinet slots — the editor is
        // one file per family under RigEditors/.
        var slots = Assert.Single(RigEditorViews.Elements(TestPaths.RepoRoot()), e => e.Attribute("ItemsSource")?.Value == "{Binding CabinetSlots}");
        Assert.Equal("{Binding IsCabFace}", slots.Attribute("IsVisible")?.Value);
        var template = Assert.Single(slots.Descendants(), e => e.Name.LocalName == "DataTemplate" &&
            e.Attributes().Any(a => a.Name.LocalName == "DataType" && a.Value == "vm:RigCabinetSlot"));
        var picker = Assert.Single(template.Descendants(), e => e.Attribute("Command")?.Value == "{Binding File.PickFileCommand}");
        var clear = Assert.Single(template.Descendants(), e => e.Attribute("Command")?.Value == "{Binding File.ClearCommand}");
        Assert.Equal("Button", picker.Name.LocalName);
        Assert.Equal("Button", clear.Name.LocalName);
        Assert.Same(picker.Parent, clear.Parent);
        Assert.Equal("{Binding File.HasValue}", clear.Attribute("IsVisible")?.Value);
        Assert.Equal("{localization:Loc Rig_ClearFile}", clear.Attributes().Single(a => a.Name.LocalName == "AutomationProperties.Name").Value);
        Assert.Equal("{localization:Loc Rig_ClearFile}", clear.Attributes().Single(a => a.Name.LocalName == "ToolTip.Tip").Value);
        // Disabling B's audio controls must never disable the picker needed to load B.
        Assert.DoesNotContain(picker.AncestorsAndSelf().TakeWhile(e => !ReferenceEquals(e, template)),
            e => e.Attribute("IsEnabled") != null || e.Attribute("IsVisible") != null);
    }

    private static DspModuleType CabinetType() => new("cab", "Cabinet",
    [
        new("model", "Model", 0, 1, 0) { ValueLabels = ["One", "Two"] },
        new("mix", "Mix", 0, 1, 1), new("lowCutHz", "Low Cut", 40, 200, 60),
        new("highCutHz", "High Cut", 3000, 12000, 8000), new("outputDb", "Output", -24, 24, 0),
        new("blend", "Blend", 0, 1, 0), new("gainADb", "Gain A", -24, 24, 0), new("gainBDb", "Gain B", -24, 24, 0),
        new("delayAMs", "Delay A", 0, 20, 0), new("delayBMs", "Delay B", 0, 20, 0),
        new("invertA", "Polarity A", 0, 1, 0) { ValueLabels = ["Normal", "Inverted"] },
        new("invertB", "Polarity B", 0, 1, 0) { ValueLabels = ["Normal", "Inverted"] }
    ])
    { StringParams = [new("irPath", "IR A", "*.wav"), new("irPathB", "IR B", "*.wav")] };
}
