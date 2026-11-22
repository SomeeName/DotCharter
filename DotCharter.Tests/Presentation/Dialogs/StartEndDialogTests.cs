using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Tempo map ▸ Start &amp; end… (#494): the five edge operations are ONE dialog. The choice picks the
///     operation; Apply calls the same <c>IChartMaintenanceService</c> member the five dialogs did.
/// </summary>
public sealed class StartEndDialogTests
{
    private static (TempoMapOperationViewModel Vm, RecordingProxy Recorder) Open(TempoMapOperation operation)
    {
        var (maintenance, recorder) = RecordingProxy.Create<IChartMaintenanceService>();
        var vm = new TempoMapOperationViewModel(maintenance, new FakeConfigurationService(), FakeLocalization.English(),
            new FakeAppLogger());
        vm.Configure(new TempoMapOperationRequest(operation));
        return (vm, recorder);
    }

    private static readonly TempoMapOperation[] EdgeOperations =
        [.. Enum.GetValues<TempoMapOperation>().Where(o => o != TempoMapOperation.InsertMeasuresAtPlayhead)];

    [Fact]
    public void The_choice_lists_all_five_operations_and_starts_on_the_one_it_was_opened_for()
    {
        Assert.Equal(5, EdgeOperations.Length);
        foreach (var operation in EdgeOperations)
        {
            var (vm, _) = Open(operation);

            Assert.True(vm.ShowsChoices);
            Assert.Equal(EdgeOperations, vm.Choices.Select(c => c.Operation));
            Assert.Equal(operation, vm.SelectedChoice!.Operation);
            Assert.All(vm.Choices, c => Assert.False(string.IsNullOrWhiteSpace(c.Label)));
        }
    }

    [Theory]
    [InlineData(TempoMapOperation.DefaultStartSilence, "SetDefaultStartSilence(2.5)")]
    [InlineData(TempoMapOperation.AddSilenceAtStart, "AddSilenceAtBeginningAsync(2.5)")]
    [InlineData(TempoMapOperation.AddSilenceAtEnd, "AddSilenceAtEndAsync(2.5)")]
    [InlineData(TempoMapOperation.AddMeasuresAtStart, "AddMeasuresAtStart(3)")]
    [InlineData(TempoMapOperation.AddMeasuresAtEnd, "AddMeasuresAtEnd(3)")]
    public void Picking_an_operation_in_the_open_dialog_and_applying_runs_that_operation(TempoMapOperation picked, string call)
    {
        // Opened on the first operation, as the menu row does; the user then picks another.
        var (vm, recorder) = Open(TempoMapOperation.DefaultStartSilence);
        vm.SelectedChoice = vm.Choices.Single(c => c.Operation == picked);
        vm.Value = picked is TempoMapOperation.AddMeasuresAtStart or TempoMapOperation.AddMeasuresAtEnd ? 2.6 : 2.5;

        vm.ApplyCommand.Execute(null);

        Assert.Equal([call], recorder.Calls);
        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    /// <summary>Insert measures at playhead is not an edge verb. It opens alone, under its own
    /// title, and Apply inserts the measure count at the playhead.</summary>
    [Fact]
    public void Insert_measures_at_playhead_opens_alone_and_applies_the_measure_count()
    {
        var (vm, recorder) = Open(TempoMapOperation.InsertMeasuresAtPlayhead);
        var english = FakeLocalization.English();

        Assert.False(vm.ShowsChoices);
        Assert.Equal([TempoMapOperation.InsertMeasuresAtPlayhead], vm.Choices.Select(c => c.Operation));
        Assert.Equal(english["Dialog_TempoMap_InsertMeasuresAtPlayhead_Title"], vm.Title);
        Assert.Equal(english["Dialog_TempoMap_InsertMeasuresAtPlayhead_Subtitle"], vm.Subtitle);
        Assert.Equal(english["Dialog_TempoMap_MeasureCount_Label"], vm.Label);
        Assert.Equal((1d, 128d, "0"), (vm.Minimum, vm.Maximum, vm.FormatString));

        vm.Value = 2;
        vm.ApplyCommand.Execute(null);

        Assert.True(vm.ShowsMeasureTempo);
        Assert.Equal((4d, 4, 120d), (vm.BeatsPerMeasure, vm.BeatUnit, vm.Bpm));
        Assert.Equal(["PlayheadBarTempo()", "InsertMeasuresAtPlayhead(2, " + new MeasureInsertTempo(4, 4, 120) + ")"],
            recorder.Calls);
    }

    /// <summary>The owner's request: the dialog picks the inserted bars' time signature and tempo, and
    /// Apply hands exactly those to the service.</summary>
    [Fact]
    public void Insert_measures_at_playhead_applies_the_chosen_time_signature_and_tempo()
    {
        var (vm, recorder) = Open(TempoMapOperation.InsertMeasuresAtPlayhead);

        vm.Value = 2;
        vm.BeatsPerMeasure = 7;
        vm.BeatUnit = 8;
        vm.Bpm = 96.5;
        vm.ApplyCommand.Execute(null);

        Assert.Equal("InsertMeasuresAtPlayhead(2, " + new MeasureInsertTempo(7, 8, 96.5) + ")", recorder.Calls[^1]);
    }

    [Fact]
    public void The_edge_operations_hide_the_time_signature_and_tempo_rows()
    {
        foreach (var operation in EdgeOperations)
            Assert.False(Open(operation).Vm.ShowsMeasureTempo);
    }

    [Fact]
    public void Picking_another_operation_refills_the_label_range_and_value()
    {
        var (vm, _) = Open(TempoMapOperation.AddSilenceAtStart);
        var subtitle = vm.Subtitle;
        Assert.Equal(600, vm.Maximum);
        Assert.Equal("0.###", vm.FormatString);

        vm.SelectedChoice = vm.Choices.Single(c => c.Operation == TempoMapOperation.AddMeasuresAtEnd);

        Assert.Equal(128, vm.Maximum);
        Assert.Equal(1, vm.Minimum);
        Assert.Equal(1, vm.Value);
        Assert.Equal("0", vm.FormatString);
        Assert.Equal(FakeLocalization.English()["Dialog_TempoMap_MeasureCount_Label"], vm.Label);
        Assert.NotEqual(subtitle, vm.Subtitle);
    }
}
