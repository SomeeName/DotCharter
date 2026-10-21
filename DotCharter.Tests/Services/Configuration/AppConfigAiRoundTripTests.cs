using System.Text.Json;
using DotCharter.Tone.Ai;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Configuration;

/// <summary>
///     <c>AppConfig.Ai</c> through the REAL <see cref="JsonConfigurationService" />,
///     in all three states a config file can be in.
///
///     <para>
///         Two of the three are what an EXISTING user's file looks like. A config
///         written by any build before 0.55 has no <c>"Ai"</c> property at all, and
///         a hand-edited or migrated one can carry <c>"Ai": null</c> — which
///         <c>System.Text.Json</c> assigns straight onto a settable reference
///         property, defeating the initializer. The property's declared type is
///         non-nullable, so every reader is entitled to skip a null check: the
///         result was an <c>AppConfig</c> whose non-nullable property was null,
///         handed out of the cache to the whole app. <c>NormalizeAppConfig</c>
///         coerces it back on load, next to the present-mode migration.
///     </para>
///     <para>
///         The load path is deliberately exercised through the SERVICE rather than
///         a bare <c>JsonSerializer.Deserialize</c>: the coercion lives in the load,
///         and a test that deserialized for itself would assert a behaviour the app
///         does not have.
///     </para>
/// </summary>
[Collection("CwdSerialized")]
public sealed class AppConfigAiRoundTripTests : IDisposable
{
    private readonly string _originalCwd;
    private readonly string _tempDir;
    private readonly List<JsonConfigurationService> _services = [];

    public AppConfigAiRoundTripTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterAiCfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
        WritablePaths.OverrideRootsForTesting(_tempDir, _tempDir);
    }

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        WritablePaths.OverrideRootsForTesting(null, null);
        Directory.SetCurrentDirectory(_originalCwd);
        BestEffortDelete.Directory(_tempDir);
    }

    /// <summary>Zero debounce: every test here reads the file it just wrote.</summary>
    private JsonConfigurationService NewService()
    {
        var service = new JsonConfigurationService(TimeSpan.Zero, null);
        _services.Add(service);
        return service;
    }

    private void WriteConfig(string json)
        => File.WriteAllText(Path.Combine(_tempDir, "appconfig.json"), json);

    /// <summary>A populated <c>Ai</c> survives save → load with every field, and
    /// through the service's own serializer options (the string enum converter is
    /// the one that would bite: <c>Kind</c> is written as a NAME).</summary>
    [Fact]
    public void APopulatedAiSection_RoundTripsThroughSaveAndLoad()
    {
        var settings = new AiProviderSettings(AiProviderKind.OpenAiCompatible, "qwen3:8b", "http://localhost:11434");

        var writer = NewService();
        var config = writer.LoadAppConfig();
        config.Ai = settings;
        writer.SaveAppConfig(config);
        writer.Flush();

        // The enum really is on the wire as a name, not as 2 — a config written by
        // this build must stay readable by one whose enum gained a member.
        var json = File.ReadAllText(Path.Combine(_tempDir, "appconfig.json"));
        Assert.Contains("\"OpenAiCompatible\"", json, StringComparison.Ordinal);

        var reloaded = NewService().LoadAppConfig().Ai;

        Assert.Equal(settings, reloaded);
        Assert.True(reloaded.IsEnabled);
        Assert.True(reloaded.UseLibraryTones);
    }

    /// <summary>
    ///     The reasoning level (#603) and the AI chat's remembered fingering picks (#602) survive
    ///     save → load, as NAMES on the wire; and a file written before either loads as Default /
    ///     no pick, which is the behaviour every earlier build had.
    /// </summary>
    [Fact]
    public void TheReasoningLevel_AndTheRememberedFingeringPicks_RoundTrip()
    {
        var writer = NewService();
        var config = writer.LoadAppConfig();
        config.Ai = new AiProviderSettings(AiProviderKind.CodexCli, "gpt-6-sol", null, Effort: AiReasoningEffort.High);
        config.AiFingeringJobGuitar = DotCharter.Services.Ai.Chart.AiChartJob.FhpAndFingering;
        config.AiFingeringJobBass = DotCharter.Services.Ai.Chart.AiChartJob.FingeringOnly;
        writer.SaveAppConfig(config);
        writer.Flush();

        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "appconfig.json"))))
        {
            Assert.Equal("High", document.RootElement.GetProperty("Ai").GetProperty("Effort").GetString());
            Assert.Equal("FhpAndFingering", document.RootElement.GetProperty("AiFingeringJobGuitar").GetString());
        }

        var reloaded = NewService().LoadAppConfig();
        Assert.Equal(AiReasoningEffort.High, reloaded.Ai.Effort);
        Assert.Equal(config.Ai, reloaded.Ai);
        Assert.Equal(DotCharter.Services.Ai.Chart.AiChartJob.FhpAndFingering, reloaded.AiFingeringJobGuitar);
        Assert.Equal(DotCharter.Services.Ai.Chart.AiChartJob.FingeringOnly, reloaded.AiFingeringJobBass);
    }

    [Fact]
    public void AnAiSectionWrittenBeforeTheReasoningLevel_LoadsAsDefault_WithNoFingeringPick()
    {
        WriteConfig("""{ "Ai": { "Kind": "ClaudeCli", "ModelId": "claude-sonnet-5-5", "BaseUrl": null } }""");

        var config = NewService().LoadAppConfig();

        Assert.Equal(AiReasoningEffort.Default, config.Ai.Effort);
        Assert.Null(config.AiFingeringJobGuitar);
        Assert.Null(config.AiFingeringJobBass);
    }

    /// <summary>The shape of EVERY config file written before 0.55.</summary>
    [Fact]
    public void AConfigWithNoAiProperty_LoadsAsDisabled()
    {
        WriteConfig("""{ "PresentMode": "Mailbox" }""");

        var ai = NewService().LoadAppConfig().Ai;

        Assert.Equal(AiProviderSettings.Disabled, ai);
        Assert.False(ai.IsEnabled);
    }

    /// <summary>
    ///     An explicit null. <c>System.Text.Json</c> assigns it over the property
    ///     initializer, so without the coercion in <c>NormalizeAppConfig</c> this
    ///     hands every reader a null in a NON-NULLABLE property — and the readers
    ///     that do not defend (the settings tab did, `ToneDesignService` did, but
    ///     neither is the whole app) throw on a file the user cannot see anything
    ///     wrong with.
    /// </summary>
    [Fact]
    public void AConfigWithAnExplicitNullAi_LoadsAsDisabled_AndNotAsNull()
    {
        WriteConfig("""{ "Ai": null }""");

        var config = NewService().LoadAppConfig();

        Assert.NotNull(config.Ai);
        Assert.Equal(AiProviderSettings.Disabled, config.Ai);
        Assert.False(config.Ai.IsEnabled);
        // The whole point: a caller may read it without a null check, the way the
        // non-nullable declaration promises.
        Assert.Equal(string.Empty, config.Ai.ModelId);
    }

    /// <summary>A null that is SAVED back must come out as a real section, not as
    /// another null — otherwise the coercion only papers over the read and the file
    /// stays broken for every other tool that looks at it.</summary>
    [Fact]
    public void AnExplicitNullAi_IsRewrittenAsDisabledOnTheNextSave()
    {
        WriteConfig("""{ "Ai": null }""");

        var service = NewService();
        var config = service.LoadAppConfig();
        service.SaveAppConfig(config);
        service.Flush();

        var json = File.ReadAllText(Path.Combine(_tempDir, "appconfig.json"));
        using var document = JsonDocument.Parse(json);
        var ai = document.RootElement.GetProperty("Ai");

        Assert.Equal(JsonValueKind.Object, ai.ValueKind);
        Assert.Equal("None", ai.GetProperty("Kind").GetString());
    }

    /// <summary>An older file that predates <c>UseLibraryTones</c> keeps the
    /// feature ON, because the record's parameter default is what a missing
    /// property takes. A user who upgrades must not silently lose the anchor
    /// stage.</summary>
    [Fact]
    public void AnAiSectionWithoutUseLibraryTones_DefaultsItOn()
    {
        WriteConfig("""{ "Ai": { "Kind": "Claude", "ModelId": "claude-opus-5", "BaseUrl": null } }""");

        var ai = NewService().LoadAppConfig().Ai;

        Assert.Equal(AiProviderKind.Claude, ai.Kind);
        Assert.Equal("claude-opus-5", ai.ModelId);
        Assert.True(ai.UseLibraryTones);
    }

    /// <summary>And an explicit false survives the round trip — a flag that cannot
    /// be turned off is not a flag.</summary>
    [Fact]
    public void UseLibraryTonesFalse_RoundTrips()
    {
        var writer = NewService();
        var config = writer.LoadAppConfig();
        config.Ai = new AiProviderSettings(AiProviderKind.Claude, "claude-opus-5", null, UseLibraryTones: false);
        writer.SaveAppConfig(config);
        writer.Flush();

        Assert.False(NewService().LoadAppConfig().Ai.UseLibraryTones);
    }

    /// <summary>Online research: OFF for a file that predates it, and an explicit
    /// true survives.</summary>
    [Fact]
    public void WebResearch_DefaultsOffForAnOlderFile_AndTrueRoundTrips()
    {
        WriteConfig("""{ "Ai": { "Kind": "ClaudeCli", "ModelId": "claude-opus-5", "BaseUrl": null } }""");
        Assert.False(NewService().LoadAppConfig().Ai.WebResearch);

        var writer = NewService();
        var config = writer.LoadAppConfig();
        config.Ai = new AiProviderSettings(AiProviderKind.ClaudeCli, "claude-opus-5", null, WebResearch: true);
        writer.SaveAppConfig(config);
        writer.Flush();

        Assert.True(NewService().LoadAppConfig().Ai.WebResearch);
    }
}
