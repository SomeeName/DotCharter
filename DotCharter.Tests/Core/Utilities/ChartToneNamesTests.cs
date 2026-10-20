namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     <see cref="ChartToneNames" /> is the ONE answer to "what tones does this
///     chart actually have". It exists because a tone name only reaches the rig
///     when the arrangement DECLARES it and the project owns a BODY for it — two
///     collections, edited in two places, whose disagreement fails silently.
/// </summary>
public sealed class ChartToneNamesTests
{
    private static ChartProject ProjectWithBodies(params (string Key, string Name)[] tones)
    {
        var project = new ChartProject();
        foreach (var (key, name) in tones)
            project.ToneDefinitions.Add(new ToneDefinition { Key = key, Name = name, Json = "{}" });
        return project;
    }

    [Fact]
    public void Defined_IndexesABodyUnderBothItsKeyAndItsName()
    {
        // The coordinator resolves a ToneChange.ToneName against BOTH, so anything
        // stricter here reports a tone as missing that the rig then plays.
        var project = ProjectWithBodies(("yyz_dist", "YYZ Distortion"));

        Assert.Equal(["yyz_dist", "YYZ Distortion"], ChartToneNames.Defined(project));
    }

    [Fact]
    public void Defined_CollapsesAKeyThatEqualsItsName() => Assert.Equal(["clean"], ChartToneNames.Defined(ProjectWithBodies(("clean", "clean"))));

    [Fact]
    public void Declared_ReadsEverySlotButNotTheToneChanges()
    {
        var arr = new Arrangement
        {
            StartingTone = "intro",
            ToneBase = "base",
            ToneA = "a",
            ToneB = "b",
            ToneC = "c",
            ToneD = "d"
        };
        arr.ToneNames.Add("extra");
        // A change naming an undeclared tone is the FAULT, not a declaration of it —
        // counting it would make the validation rule unable to see the fault at all.
        arr.ToneChanges.Add(new ToneChange { ToneName = "typo" });

        Assert.Equal(["intro", "base", "a", "b", "c", "d", "extra"], ChartToneNames.Declared(arr));
    }

    [Fact]
    public void Offerable_PutsWhatTheArrangementDeclaresFirst()
    {
        var project = ProjectWithBodies(("unused_body", "unused_body"), ("base", "base"));
        var arr = new Arrangement { ToneBase = "base" };
        arr.ToneChanges.Add(new ToneChange { ToneName = "from_a_change" });
        project.Arrangements.Add(arr);

        // Declared → other bodies → names already used by tone changes.
        Assert.Equal(
            ["base", "unused_body", "from_a_change"],
            ChartToneNames.Offerable(project, arr));
    }

    [Theory]
    [InlineData("YYZ_dist", true)]   // exact
    [InlineData("  yyz_DIST ", true)] // trimmed + case-insensitive, like the rig's lookup
    [InlineData("YYZ Distortion", true)] // by NAME, not just key
    [InlineData("yyz_clean", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void HasBody_MatchesTheWayTheRigResolvesAToneChange(string needle, bool expected)
    {
        var project = ProjectWithBodies(("YYZ_dist", "YYZ Distortion"));

        Assert.Equal(expected, ChartToneNames.HasBody(project, needle));
    }

    [Fact]
    public void HasBody_IsFalseWithNoProject() => Assert.False(ChartToneNames.HasBody(null, "anything"));

    /// <summary>
    ///     The distinction the whole type exists for: a chart can declare a tone
    ///     on a slot and have NO body for it. That combination validates clean
    ///     against the slots and plays silent — it was the state of every
    ///     psarc-opened chart before the loader imported bodies.
    /// </summary>
    [Fact]
    public void DeclaredAndHasBodyAreIndependent()
    {
        var project = ProjectWithBodies(("has_body", "has_body"));
        var arr = new Arrangement { ToneBase = "declared_only" };

        Assert.True(ChartToneNames.IsDeclared(arr, "declared_only"));
        Assert.False(ChartToneNames.HasBody(project, "declared_only"));

        Assert.False(ChartToneNames.IsDeclared(arr, "has_body"));
        Assert.True(ChartToneNames.HasBody(project, "has_body"));
    }

    [Fact]
    public void EverythingIsEmptyRatherThanThrowingOnANullChart()
    {
        Assert.Empty(ChartToneNames.Defined(null));
        Assert.Empty(ChartToneNames.Declared(null));
        Assert.Empty(ChartToneNames.Offerable(null, null));
        Assert.False(ChartToneNames.IsDeclared(null, "x"));
    }
}
