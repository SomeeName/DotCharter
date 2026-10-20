using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The analysis monitor's per-string confidence strip (#314): one cell per string of the active
///     arrangement, a meter as wide as the certainty, the "unsure" state, and a tooltip with the number.
/// </summary>
public sealed class LiveStringConfidenceViewModelTests
{
    private static LiveStringConfidenceReading[] Readings(params (int Samples, float Certainty)[] perString) =>
        [.. perString.Select((r, i) => new LiveStringConfidenceReading(i, r.Samples, r.Certainty))];

    [Fact]
    public void BeforeAnyReading_ItShowsSixStandardStrings_WithNoData()
    {
        var vm = new LiveStringConfidenceViewModel(FakeLocalization.English());

        Assert.Equal(["E", "A", "D", "G", "B", "e"], vm.Strings.Select(s => s.Label));
        Assert.All(vm.Strings, s => Assert.False(s.HasData));
        Assert.All(vm.Strings, s => Assert.Equal(0, s.FillWidth));
        Assert.All(vm.Strings, s => Assert.True(s.CellOpacity < 1));
    }

    [Fact]
    public void AReading_SetsTheMeter_TheUnsureState_AndTheTooltip()
    {
        var vm = new LiveStringConfidenceViewModel(FakeLocalization.English());

        vm.Apply(Readings((5, 0.9f), (3, 0.25f), (1, 0.5f), (0, 0f), (0, 0f), (0, 0f)),
            LiveStringConfidenceViewModel.LabelsFor(null));

        var low = vm.Strings[0];
        Assert.True(low.HasData);
        Assert.False(low.IsUnsure);
        Assert.Equal(0.9 * LiveStringConfidenceItem.MeterWidth, low.FillWidth, 3);
        Assert.Equal(1.0, low.CellOpacity);
        Assert.Equal("E string: detector confidence 0.90 (last 5 judged notes)", low.ToolTip);

        var unsure = vm.Strings[1];
        Assert.True(unsure.IsUnsure);
        Assert.StartsWith("A string: detector confidence 0.25 (last 3 judged notes)", unsure.ToolTip, StringComparison.Ordinal);
        Assert.Contains("may be the detector", unsure.ToolTip, StringComparison.Ordinal);

        // One judged note reads in the singular, and exactly 0.5 is not "unsure".
        Assert.Equal("D string: detector confidence 0.50 (1 judged note)", vm.Strings[2].ToolTip);
        Assert.False(vm.Strings[2].IsUnsure);

        Assert.Equal("G string: no judged notes in the last 10 s", vm.Strings[3].ToolTip);

        // Exactly one fill per judged string; none on a string with nothing judged.
        Assert.Equal([true, false, true, false], vm.Strings.Take(4).Select(st => st.IsSure));
        Assert.Equal([false, true, false, false], vm.Strings.Take(4).Select(st => st.IsUnsure));
    }

    [Fact]
    public void TheTooltipKeys_AreTheOnesTheResxCarries()
    {
        var vm = new LiveStringConfidenceViewModel(FakeLocalization.KeysOnly());

        vm.Apply(Readings((2, 0.1f), (1, 0.8f)), ["E", "A"]);

        Assert.StartsWith("LivePlay_Monitor_StringConfidence(2, E, 0.1)", vm.Strings[0].ToolTip, StringComparison.Ordinal);
        Assert.EndsWith("LivePlay_Monitor_StringConfidenceUnsure", vm.Strings[0].ToolTip, StringComparison.Ordinal);
        Assert.Equal("LivePlay_Monitor_StringConfidenceOne(1, A, 0.8)", vm.Strings[1].ToolTip);
    }

    [Fact]
    public void TheCellCount_FollowsTheArrangement()
    {
        var vm = new LiveStringConfidenceViewModel(FakeLocalization.English());
        var bass = new Arrangement { StringCount = 4, Tuning = ["E", "A", "D", "G"] };

        vm.Apply(Readings((1, 1f)), LiveStringConfidenceViewModel.LabelsFor(bass));
        Assert.Equal(["E", "A", "D", "G"], vm.Strings.Select(s => s.Label));

        var sevenString = new Arrangement { StringCount = 7 };
        vm.Apply([], LiveStringConfidenceViewModel.LabelsFor(sevenString));
        Assert.Equal(7, vm.Strings.Count);
        Assert.Equal(Enumerable.Range(0, 7), vm.Strings.Select(s => s.StringIndex));
    }

    [Fact]
    public void ADropTuning_IsLabelledFromTheArrangement()
    {
        var dropD = new Arrangement { StringCount = 6, Tuning = ["D", "A", "D", "G", "B", "e"] };

        Assert.Equal(["D", "A", "D", "G", "B", "e"], LiveStringConfidenceViewModel.LabelsFor(dropD));
    }

    [Fact]
    public void AnUnchangedReading_RaisesNoPropertyChange()
    {
        var vm = new LiveStringConfidenceViewModel(FakeLocalization.English());
        var readings = Readings((4, 0.7f));
        vm.Apply(readings, ["E"]);
        var changes = 0;
        vm.Strings[0].PropertyChanged += (_, _) => changes++;

        vm.Apply(readings, ["E"]);

        Assert.Equal(0, changes);
    }
}
