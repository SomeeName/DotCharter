using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>The unsaved-changes gate and the tempo-map operation card: what each verb answers and does.</summary>
public sealed class SimpleDialogTests
{
    [Theory]
    [InlineData("Save", UnsavedChangesChoice.Save)]
    [InlineData("Discard", UnsavedChangesChoice.Discard)]
    [InlineData("Cancel", UnsavedChangesChoice.Cancel)]
    public void Each_unsaved_changes_verb_answers_its_own_choice(string verb, UnsavedChangesChoice expected)
    {
        var vm = new UnsavedChangesViewModel();
        var command = verb switch
        {
            "Save" => vm.SaveCommand,
            "Discard" => vm.DiscardCommand,
            _ => vm.CancelCommand
        };

        command.Execute(null);

        Assert.Equal(expected, ChartEditHarness.Answer(vm.Result));
    }

    /// <summary>
    ///     The close gate's safety property: a dialog closed from OUTSIDE (Esc, the
    ///     host going away) completes with the default, and the default must be
    ///     Cancel — never Save, never Discard.
    /// </summary>
    [Fact]
    public async Task Closing_the_unsaved_changes_gate_from_outside_answers_Cancel()
    {
        var vm = new UnsavedChangesViewModel();
        var stack = new DialogStack();
        var session = stack.Open(vm.Result);

        Assert.True(stack.CloseTop());

        Assert.Equal(UnsavedChangesChoice.Cancel,
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.Equal(UnsavedChangesChoice.Cancel, default);
    }

    [Theory]
    [InlineData(TempoMapOperation.DefaultStartSilence, "SetDefaultStartSilence(2.5)")]
    [InlineData(TempoMapOperation.AddSilenceAtStart, "AddSilenceAtBeginningAsync(2.5)")]
    [InlineData(TempoMapOperation.AddSilenceAtEnd, "AddSilenceAtEndAsync(2.5)")]
    [InlineData(TempoMapOperation.AddMeasuresAtStart, "AddMeasuresAtStart(3)")]
    [InlineData(TempoMapOperation.AddMeasuresAtEnd, "AddMeasuresAtEnd(3)")]
    public void Apply_runs_the_operation_it_was_opened_for_and_answers_true(TempoMapOperation operation, string call)
    {
        var (vm, recorder) = OpenTempoMap(operation);
        vm.Value = operation is TempoMapOperation.AddMeasuresAtStart or TempoMapOperation.AddMeasuresAtEnd ? 2.6 : 2.5;

        vm.ApplyCommand.Execute(null);

        Assert.Equal([call], recorder.Calls);
        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    [Theory]
    [InlineData(TempoMapOperation.AddSilenceAtStart, "Dialog_TempoMap_AddSilenceAtStart_Title",
        "Dialog_TempoMap_SilenceSeconds_Label", 0, 600, "0.###")]
    [InlineData(TempoMapOperation.AddMeasuresAtEnd, "Dialog_TempoMap_AddMeasuresAtEnd_Title",
        "Dialog_TempoMap_MeasureCount_Label", 1, 128, "0")]
    public void Configure_fills_the_card_for_the_operation(TempoMapOperation operation, string titleKey, string labelKey,
        double minimum, double maximum, string format)
    {
        var (vm, _) = OpenTempoMap(operation);

        Assert.Equal(FakeLocalization.English()[titleKey], vm.Title);
        Assert.Equal(FakeLocalization.English()[labelKey], vm.Label);
        Assert.False(string.IsNullOrEmpty(vm.Subtitle));
        Assert.Equal(minimum, vm.Minimum);
        Assert.Equal(maximum, vm.Maximum);
        Assert.Equal(format, vm.FormatString);
    }

    [Fact]
    public void Cancelling_a_tempo_map_operation_runs_nothing_and_answers_false()
    {
        var (vm, recorder) = OpenTempoMap(TempoMapOperation.AddSilenceAtEnd);

        vm.CancelCommand.Execute(null);

        Assert.Empty(recorder.Calls);
        Assert.False(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public void Both_conform_to_their_contracts()
    {
        Assert.Empty(ContractConformanceTests.Violations(typeof(IUnsavedChangesScreen), new UnsavedChangesViewModel()));
        Assert.Empty(ContractConformanceTests.Violations(typeof(ITempoMapOperationScreen),
            OpenTempoMap(TempoMapOperation.AddMeasuresAtStart).Vm));
    }

    private static (TempoMapOperationViewModel Vm, RecordingProxy Recorder) OpenTempoMap(TempoMapOperation operation)
    {
        var (maintenance, recorder) = RecordingProxy.Create<IChartMaintenanceService>();
        var vm = new TempoMapOperationViewModel(maintenance, new FakeConfigurationService(), FakeLocalization.English(),
            new FakeAppLogger());
        vm.Configure(new TempoMapOperationRequest(operation));
        return (vm, recorder);
    }
}
