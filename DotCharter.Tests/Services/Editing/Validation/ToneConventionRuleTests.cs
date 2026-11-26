using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     The tone conventions experienced charters follow (D20–D25): no tone change before the
///     first note, a lone tone change, a tone whose name differs from its key, a tone without an
///     amp or cabinet or with more than four effects, two tones under one name, and a change to
///     the tone that is already on.
/// </summary>
public sealed class ToneConventionRuleTests
{
    [Fact]
    public void AToneChangeBeforeTheFirstNote_IsFlagged_ButOneAfterItIsNot()
    {
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "clean", Position = new FractionalPosition(2) });
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "lead", Position = new FractionalPosition(6) });
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "clean", Position = new FractionalPosition(10) });

        var finding = Assert.Single(Findings(project, ArrangementIssueType.ToneChangeBeforeFirstNote));
        Assert.Equal("D20", finding.Code);
        Assert.Equal(1000, finding.TimeMs);
        Assert.Equal(new object[] { "clean" }, finding.MessageArgs);
    }

    [Fact]
    public void AToneChangeOnTheFirstNote_IsI30sCase_NotD20()
    {
        var project = CreateProject();
        project.Arrangements[0].ToneChanges.Add(new ToneChange { ToneName = "lead", Position = new FractionalPosition(4) });
        project.Arrangements[0].ToneChanges.Add(new ToneChange { ToneName = "clean", Position = new FractionalPosition(8) });

        Assert.Empty(Findings(project, ArrangementIssueType.ToneChangeBeforeFirstNote));
    }

    [Fact]
    public void ASingleToneChange_IsFlagged_TwoOrNoneAreNot()
    {
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        Assert.Empty(Findings(project, ArrangementIssueType.SingleToneChange));

        arrangement.ToneChanges.Add(new ToneChange { ToneName = "lead", Position = new FractionalPosition(6) });
        var finding = Assert.Single(Findings(project, ArrangementIssueType.SingleToneChange));
        Assert.Equal("D21", finding.Code);
        Assert.Equal(3000, finding.TimeMs);

        arrangement.ToneChanges.Add(new ToneChange { ToneName = "clean", Position = new FractionalPosition(10) });
        Assert.Empty(Findings(project, ArrangementIssueType.SingleToneChange));
    }

    [Fact]
    public void AToneWhoseNameDiffersFromItsKey_IsFlagged_CaseAndSpacesAside()
    {
        var project = CreateProject();
        project.ToneDefinitions[1].Name = "Lead Tone";
        project.ToneDefinitions[0].Name = " CLEAN ";

        var finding = Assert.Single(Findings(project, ArrangementIssueType.ToneNameKeyMismatch));
        Assert.Equal("D22", finding.Code);
        Assert.Equal(new object[] { "lead", "Lead Tone" }, finding.MessageArgs);
    }

    [Fact]
    public void AToneTheArrangementDoesNotUse_IsNotFlagged()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(new ToneDefinition { Key = "other", Name = "Something else", Json = Gear(amp: false) });

        Assert.Empty(Findings(project, ArrangementIssueType.ToneNameKeyMismatch));
        Assert.Empty(Findings(project, ArrangementIssueType.ToneGearContent));
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 2)]
    public void AToneWithoutAnAmpOrACabinet_IsFlagged(bool amp, bool cab, int effects)
    {
        var project = CreateProject();
        project.ToneDefinitions[1].Json = Gear(amp, cab, effects);

        var finding = Assert.Single(Findings(project, ArrangementIssueType.ToneGearContent));
        Assert.Equal("D23", finding.Code);
        Assert.Equal("Validation_ToneWithoutAmpOrCabinet", finding.MessageKey);
        Assert.Equal(new object[] { "lead" }, finding.MessageArgs);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(8, true)]
    public void MoreThanFourEffects_IsFlagged_FourIsTheLimit(int effects, bool flagged)
    {
        var project = CreateProject();
        project.ToneDefinitions[1].Json = Gear(effects: effects);

        var findings = Findings(project, ArrangementIssueType.ToneGearContent).ToList();
        if (!flagged)
        {
            Assert.Empty(findings);
            return;
        }

        var finding = Assert.Single(findings);
        Assert.Equal("Validation_ToneTooManyEffects", finding.MessageKey);
        Assert.Equal(new object[] { "lead", effects, ToneConventionRule.MaxEffects }, finding.MessageArgs);
    }

    [Fact]
    public void AToneWithNoReadableGear_IsLeftToTheBodyCheck()
    {
        var project = CreateProject();
        project.ToneDefinitions[1].Json = "";

        Assert.Empty(Findings(project, ArrangementIssueType.ToneGearContent));
    }

    [Fact]
    public void TwoTonesUnderOneName_AreFlaggedOnce()
    {
        var project = CreateProject();
        project.ToneDefinitions[1].Name = "Clean";

        var finding = Assert.Single(Findings(project, ArrangementIssueType.DuplicateToneName));
        Assert.Equal("D24", finding.Code);
        Assert.Equal(new object[] { "Clean" }, finding.MessageArgs);
    }

    [Fact]
    public void OneNameInTwoSwitchableSlots_IsFlagged_TheBaseRepeatingSlotAIsNot()
    {
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        Assert.Empty(Findings(project, ArrangementIssueType.DuplicateToneName));

        arrangement.ToneC = "Lead";
        var finding = Assert.Single(Findings(project, ArrangementIssueType.DuplicateToneName));
        Assert.Equal(new object[] { "Lead" }, finding.MessageArgs);
    }

    [Fact]
    public void AChangeToTheToneAlreadyOn_IsFlagged_FromTheStartingToneOn()
    {
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "CLEAN", Position = new FractionalPosition(6) });
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "lead", Position = new FractionalPosition(10) });
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "lead", Position = new FractionalPosition(14) });

        var findings = Findings(project, ArrangementIssueType.RepeatedTone).ToList();
        Assert.Equal([3000.0, 7000.0], findings.Select(f => f.TimeMs!.Value));
        Assert.All(findings, f => Assert.Equal("D25", f.Code));
    }

    [Fact]
    public void AlternatingTones_AreNotRepeats()
    {
        var project = CreateProject();
        var arrangement = project.Arrangements[0];
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "lead", Position = new FractionalPosition(6) });
        arrangement.ToneChanges.Add(new ToneChange { ToneName = "clean", Position = new FractionalPosition(10) });

        Assert.Empty(Findings(project, ArrangementIssueType.RepeatedTone));
    }

    [Fact]
    public void ACleanTwoToneChart_RaisesNoToneConventionFinding()
    {
        var project = CreateProject();
        project.Arrangements[0].ToneChanges.Add(new ToneChange { ToneName = "lead", Position = new FractionalPosition(6) });
        project.Arrangements[0].ToneChanges.Add(new ToneChange { ToneName = "clean", Position = new FractionalPosition(10) });

        var codes = new[] { "D20", "D21", "D22", "D23", "D24", "D25" };
        Assert.DoesNotContain(Validate(project), issue => codes.Contains(issue.Code));
    }

    internal static string Gear(bool amp = true, bool cab = true, int effects = 0)
    {
        var parts = new List<string>();
        if (amp) parts.Add("\"Amp\":{\"Key\":\"Amp_Clean\",\"Type\":\"Amps\",\"KnobValues\":{}}");
        if (cab) parts.Add("\"Cabinet\":{\"Key\":\"Cab_Clean\",\"Type\":\"Cabinets\",\"KnobValues\":{}}");
        string[] slots = ["PrePedal1", "PrePedal2", "PrePedal3", "PrePedal4", "Rack1", "Rack2", "Rack3", "Rack4",
            "PostPedal1", "PostPedal2", "PostPedal3", "PostPedal4"];
        for (var i = 0; i < effects; i++)
            parts.Add($"\"{slots[i]}\":{{\"Key\":\"Pedal_{i}\",\"Type\":\"Pedals\",\"KnobValues\":{{}}}}");
        return "{\"GearList\":{" + string.Join(",", parts) + "}}";
    }

    private static ToneDefinition Tone(string key, string json) => new() { Key = key, Name = key, Json = json };

    private static IEnumerable<ArrangementIssue> Findings(ChartProject project, ArrangementIssueType type) =>
        Validate(project).Where(issue => issue.Type == type);

    private static IReadOnlyList<ArrangementIssue> Validate(ChartProject project) =>
        new ArrangementValidationService(new FakeAppLogger())
            .Validate(project.Arrangements[0], project.Beats, null, project);

    /// <summary>A lead with notes from beat 4 (2 s) to beat 20, starting on "clean" (slot A) with
    /// "lead" in slot B; both tones have an amp, a cabinet and no effects.</summary>
    internal static ChartProject CreateProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 40; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500 });
        var arrangement = new Arrangement
        {
            StartingTone = "clean", ToneBase = "clean", ToneA = "clean", ToneB = "lead"
        };
        var level = new ChartLevel();
        for (var beat = 4; beat <= 20; beat++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), Fret = 5, StringIndex = 2 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);
        project.ToneDefinitions.Add(Tone("clean", Gear()));
        project.ToneDefinitions.Add(Tone("lead", Gear()));
        return project;
    }
}
