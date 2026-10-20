using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Configuration;

/// <summary>
///     #730: technique judging is one switch per technique. Bends are on by default, slides and
///     vibrato off; each switch round-trips on its own; and the 0.68 single switch
///     (<c>LivePlayJudgeTechniqueContours</c>) is migrated once — <c>true</c> was a choice and
///     keeps all three on, <c>false</c> was the default and gets the new defaults — and is never
///     written back.
///     Same isolation as <see cref="JsonConfigurationServiceTests" />: a throwaway CWD and
///     <c>WritablePaths</c> roots, held for the class's lifetime.
/// </summary>
[Collection("CwdSerialized")]
public sealed class TechniqueJudgingSwitchesConfigTests : IDisposable
{
    private readonly string _originalCwd;
    private readonly string _tempDir;
    private readonly List<JsonConfigurationService> _services = [];

    public TechniqueJudgingSwitchesConfigTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
        WritablePaths.OverrideRootsForTesting(_tempDir, _tempDir);
    }

    private string AppConfigFile => Path.Combine(_tempDir, "appconfig.json");

    [Fact]
    public void AFreshConfig_JudgesBendsOnly()
    {
        foreach (var config in new[] { new AppConfig(), NewService().LoadAppConfig() })
        {
            Assert.True(config.LivePlayJudgeBends);
            Assert.False(config.LivePlayJudgeSlides);
            Assert.False(config.LivePlayJudgeVibrato);
            Assert.Equal(ContourTechniques.Bend, Switched(config));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public void EachSwitch_RoundTripsOnItsOwn(bool bends, bool slides, bool vibrato)
    {
        var svc = NewService();
        svc.SaveAppConfig(new AppConfig
        {
            LivePlayJudgeBends = bends,
            LivePlayJudgeSlides = slides,
            LivePlayJudgeVibrato = vibrato
        });
        svc.Flush();

        var loaded = NewService().LoadAppConfig();

        Assert.Equal(bends, loaded.LivePlayJudgeBends);
        Assert.Equal(slides, loaded.LivePlayJudgeSlides);
        Assert.Equal(vibrato, loaded.LivePlayJudgeVibrato);
        Assert.Equal(TechniqueContourJudge.Switched(bends, slides, vibrato), Switched(loaded));
    }

    [Fact]
    public void TheOldSingleSwitch_On_KeepsAllThreeOn_AndIsNotWrittenBack()
    {
        File.WriteAllText(AppConfigFile, """{"LivePlayJudgeTechniqueContours":true}""");

        var svc = NewService();
        var loaded = svc.LoadAppConfig();

        Assert.Equal(ContourTechniques.All, Switched(loaded));
        Assert.Null(loaded.LivePlayJudgeTechniqueContours);

        svc.SaveAppConfig(loaded);
        svc.Flush();
        Assert.DoesNotContain("LivePlayJudgeTechniqueContours", File.ReadAllText(AppConfigFile), StringComparison.Ordinal);
        Assert.Equal(ContourTechniques.All, Switched(NewService().LoadAppConfig()));
    }

    /// <summary>The 0.68 switch was off by default, so <c>false</c> is a default, not a choice.</summary>
    [Fact]
    public void TheOldSingleSwitch_Off_GetsTheNewDefaults()
    {
        File.WriteAllText(AppConfigFile, """{"LivePlayJudgeTechniqueContours":false}""");

        var loaded = NewService().LoadAppConfig();

        Assert.Equal(ContourTechniques.Bend, Switched(loaded));
        Assert.Null(loaded.LivePlayJudgeTechniqueContours);
    }

    /// <summary>Once migrated, a bends opt-out sticks: the old key is gone, so nothing re-applies.</summary>
    [Fact]
    public void ABendsOptOut_AfterTheMigration_Sticks()
    {
        File.WriteAllText(AppConfigFile, """{"LivePlayJudgeBends":false,"LivePlayJudgeSlides":true}""");

        Assert.Equal(ContourTechniques.Slide, Switched(NewService().LoadAppConfig()));
    }

    [Fact]
    public void Switched_MapsEachSwitchToItsOwnTechnique()
    {
        Assert.Equal(ContourTechniques.None, TechniqueContourJudge.Switched(false, false, false));
        Assert.Equal(ContourTechniques.Bend, TechniqueContourJudge.Switched(true, false, false));
        Assert.Equal(ContourTechniques.Slide, TechniqueContourJudge.Switched(false, true, false));
        Assert.Equal(ContourTechniques.Vibrato, TechniqueContourJudge.Switched(false, false, true));
        Assert.Equal(ContourTechniques.All, TechniqueContourJudge.Switched(true, true, true));
    }

    private static ContourTechniques Switched(AppConfig c) =>
        TechniqueContourJudge.Switched(c.LivePlayJudgeBends, c.LivePlayJudgeSlides, c.LivePlayJudgeVibrato);

    private JsonConfigurationService NewService()
    {
        var service = new JsonConfigurationService(TimeSpan.Zero, null);
        _services.Add(service);
        return service;
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        WritablePaths.OverrideRootsForTesting(null, null);
        Directory.SetCurrentDirectory(_originalCwd);
        BestEffortDelete.Directory(_tempDir);
    }
}
