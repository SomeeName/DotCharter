namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D26: a tuning that is none of the arrangement settings' presets (E/Eb/D Standard, Drop D,
///     Drop C, for any string count) and has no name in <see cref="TuningNameResolver" /> at its
///     string count is a custom tuning, which charters double-check because Rocksmith shows it as
///     custom. A named tuning outside the dropdown (B Standard bass, DADGAD) is not (#1031).
/// </summary>
public sealed class CustomTuningRuleTests
{
    [Theory]
    [InlineData(new[] { 0, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { -2, 0, 0, 0, 0, 0 })]
    [InlineData(new[] { -1, -1, -1, -1, -1, -1 })]
    [InlineData(new[] { -4, -2, -2, -2, -2, -2 })]
    public void APresetTuning_DoesNotWarn(int[] offsets) =>
        Assert.Empty(Findings(Guitar(offsets)));

    [Fact]
    public void ACustomTuning_WarnsOnce_NamingTheStrings()
    {
        var finding = Assert.Single(Findings(Guitar([-2, 0, 0, 0, -2, 0])));

        Assert.Equal("D26", finding.Code);
        Assert.Equal(ArrangementIssueSeverity.Warning, finding.Severity);
        Assert.Equal(new object[] { "D A D G A E" }, finding.MessageArgs);
    }

    [Theory]
    [InlineData(new[] { -5, -5, -5, -5 })] // B Standard
    [InlineData(new[] { -4, -4, -4, -4 })] // C Standard
    [InlineData(new[] { -5, -3, -3, -3 })] // Drop B
    [InlineData(new[] { -7, -5, -5, -5 })] // Drop A
    public void ANamedFourStringBassTuningOutsideTheDropdown_DoesNotWarn(int[] offsets) =>
        Assert.Empty(Findings(Bass(4, offsets)));

    [Fact]
    public void AFourStringBassInBStandard_StoredInSixSlots_DoesNotWarn() =>
        Assert.Empty(Findings(Bass(4, [-5, -5, -5, -5, 0, 0])));

    [Theory]
    [InlineData(new[] { -2, 0, 0, 0, -2, -2 })] // DADGAD
    [InlineData(new[] { -2, -2, 0, 0, 0, -2 })] // Open G
    [InlineData(new[] { -5, -5, -5, -5, -5, -5 })] // B Standard
    public void ANamedGuitarTuningOutsideTheDropdown_DoesNotWarn(int[] offsets) =>
        Assert.Empty(Findings(Guitar(offsets)));

    [Fact]
    public void AFourStringBassInDropD_IsAPreset_ButAnOpenBassTuningIsNot()
    {
        Assert.Empty(Findings(Bass(4, [-2, 0, 0, 0])));
        Assert.Single(Findings(Bass(4, [0, 0, 2, 2])));
    }

    [Fact]
    public void AFiveStringBassInStandard_IsAPreset() => Assert.Empty(Findings(Bass(5, ExtendedRangeTunings.StandardOffsets(5, bass: true))));

    [Fact]
    public void Vocals_HaveNoTuning() =>
        Assert.Empty(Findings(new Arrangement { ArrangementType = ArrangementType.Vocal }));

    private static Arrangement Guitar(int[] offsets) => WithOffsets(new Arrangement(), offsets);

    private static Arrangement Bass(int strings, int[] offsets) =>
        WithOffsets(new Arrangement { ArrangementType = ArrangementType.Bass, StringCount = strings }, offsets);

    private static Arrangement WithOffsets(Arrangement arrangement, int[] offsets)
    {
        arrangement.TuningPitches.Clear();
        foreach (var step in offsets) arrangement.TuningPitches.Add(new TuningOffset(step));
        return arrangement;
    }

    private static IEnumerable<ArrangementIssue> Findings(Arrangement arrangement)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 8; i++) beats.Add(new BeatMarker { Time = i * 500 });
        return new ArrangementValidationService(new FakeAppLogger())
            .Validate(arrangement, beats)
            .Where(issue => issue.Type == ArrangementIssueType.CustomTuning);
    }
}
