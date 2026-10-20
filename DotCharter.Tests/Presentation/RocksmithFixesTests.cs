using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Export.Validation;
using DotCharter.Tests.Presentation.Dialogs;

namespace DotCharter.Tests.Presentation;

/// <summary>
///     The Fix both the export dialog and the readiness view run (#184): a tone finding opens the
///     Tone designer on ITS arrangement and edits nothing; a chart finding goes to the maintenance
///     verb; an ambiguous arrangement name offers no Fix at all.
/// </summary>
public sealed class RocksmithFixesTests
{
    private sealed class Rig
    {
        public Rig(params string[] names)
        {
            var project = new ChartProject();
            foreach (var name in names) project.Arrangements.Add(new Arrangement { Name = name, ArrangementType = ArrangementType.Lead });
            State.ActiveProject = project;
            State.ActiveArrangement = project.Arrangements[0];
            (var maintenance, Maintenance) = RecordingProxy.Create<IChartMaintenanceService>();
            (var editor, Editor) = RecordingProxy.Create<IChartEditorSurface>();
            (var dialogs, Dialogs) = RecordingProxy.Create<IDialogs>();
            Fixes = new RocksmithFixes(maintenance, editor, State, dialogs, FakeLocalization.KeysOnly(), new FakeAppLogger());
        }

        public ProjectState State { get; } = new();
        public RecordingProxy Maintenance { get; }
        public RecordingProxy Editor { get; }
        public RecordingProxy Dialogs { get; }
        public RocksmithFixes Fixes { get; }
    }

    private static ExportValidationFinding Finding(string arrangement, string key) =>
        new(arrangement, ExportFindingSeverity.Warning, key);

    [Fact]
    public async Task AToneFinding_OpensTheToneDesignerOnItsArrangement_AndEditsNothing()
    {
        var rig = new Rig("Lead", "Rhythm");
        var finding = Finding("Rhythm", ChartExportValidator.KeyToneNotDefined);

        Assert.True(rig.Fixes.CanFix(finding));
        await rig.Fixes.FixAsync(finding);

        var switched = Assert.Single(rig.Editor.Invocations, i => i.Method == nameof(IChartEditorSurface.SetActiveArrangement));
        Assert.Same(rig.State.ActiveProject!.Arrangements[1], switched.Args[0]);
        Assert.Contains(rig.Dialogs.Calls, c => c.StartsWith(nameof(IDialogs.ShowAsync), StringComparison.Ordinal));
        Assert.Empty(rig.Maintenance.Calls);
    }

    /// <summary>#195: one undefined key next to one defined tone nothing plays — the Fix is that
    /// rename, run through the maintenance verb, and the Tone designer stays shut.</summary>
    [Fact]
    public async Task AnUndefinedToneWithOneRepair_IsRenamedInPlace_NotDesigned()
    {
        var rig = new Rig("Lead");
        var project = rig.State.ActiveProject!;
        project.ToneDefinitions.Add(new ToneDefinition { Key = "lead_crunch", Name = "lead_crunch", Json = "{}" });
        project.Arrangements[0].ToneBase = "lead_dist";
        var finding = new ExportValidationFinding("Lead", ExportFindingSeverity.Warning, ChartExportValidator.KeyToneNotDefined,
            Detail: "lead_dist");

        Assert.True(rig.Fixes.CanFix(finding));
        Assert.StartsWith("Export_FixTip_RenameTone", rig.Fixes.Describe(finding), StringComparison.Ordinal);
        await rig.Fixes.FixAsync(finding);

        var call = Assert.Single(rig.Maintenance.Invocations);
        Assert.Equal(nameof(IChartMaintenanceService.RepairUndefinedToneKey), call.Method);
        Assert.Equal("lead_dist", call.Args[0]);
        Assert.Empty(rig.Dialogs.Calls);
        Assert.Empty(rig.Editor.Invocations);
    }

    [Fact]
    public async Task AChartFinding_RunsTheMaintenanceVerb_OnItsArrangement()
    {
        var rig = new Rig("Lead", "Rhythm");

        await rig.Fixes.FixAsync(Finding("Rhythm", ChartExportValidator.KeyNoPhrases));

        var call = Assert.Single(rig.Maintenance.Invocations);
        Assert.Equal(nameof(IChartMaintenanceService.FixExportFinding), call.Method);
        Assert.Equal(ExportFixKind.GenerateStructure, call.Args[0]);
        Assert.Same(rig.State.ActiveProject!.Arrangements[1], call.Args[1]);
        Assert.Empty(rig.Dialogs.Calls);
    }

    [Fact]
    public void TwoArrangementsOfOneName_OfferNoFix()
    {
        var rig = new Rig("Lead", "Lead");

        Assert.False(rig.Fixes.CanFix(Finding("Lead", ChartExportValidator.KeyNoPhrases)));
    }

    [Fact]
    public void AFindingWithNoVerb_OffersNoFix_AndNoTooltip()
    {
        var rig = new Rig("Lead");
        var finding = Finding("Lead", ChartExportValidator.KeyLyricTooLong);

        Assert.False(rig.Fixes.CanFix(finding));
        Assert.Equal("", rig.Fixes.Describe(finding));
    }
}
