using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DotCharter.Services.Diagnostics;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     <c>AppConfig.OnboardingCompletedVersion</c> is written by two sites and
///     read by no PRODUCT code — which reads like a dead field, and is not.
///     <para>
///         The "never nagged twice" gate is the BOOLEAN
///         <c>AppConfig.OnboardingCompleted</c> beside it; the version string is
///         support data, and its reader is a human looking at a diagnostics
///         bundle, because <c>appconfig.json</c> travels inside one verbatim.
///         (It also anticipates a "new since you set up" prompt — a FUTURE
///         feature and a product decision, not something to invent here.)
///     </para>
///     <para>
///         So the two claims that keep it from rotting into either "delete this
///         dead field" or "add the missing reader" are pinned below: both writers
///         record the SAME running version, and that version reaches support.
///     </para>
/// </summary>
/// <remarks>Joins the serialized culture collection: the rig drives
/// <c>OnboardingWizardViewModel.CommitWelcome</c>, which writes the
/// process-global <c>LocalizationService.Instance</c> through
/// <c>SetConfiguredCulture</c>. A culture write through PRODUCT code races
/// exactly as a direct one does — see <c>CultureCollectionLintTests</c>.</remarks>
[Collection("LocalizationSingleton")]
public sealed class OnboardingCompletedVersionTests : IDisposable
{
    private readonly string _workDir = Path.Combine(
        Path.GetTempPath(), "dc-onboarding-version-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_workDir);

    [Theory]
    [InlineData("finish")]
    [InlineData("dismiss")]
    [InlineData("grandfathered")]
    public void EveryWriterRecordsTheSameRunningVersion(string route)
    {
        var rig = new OnboardingWizardRig(appVersion: "7.7.7");

        switch (route)
        {
            case "finish":
                rig.Vm.Open();
                rig.Vm.Finish();
                break;
            case "dismiss":
                rig.Vm.Open();
                rig.Vm.RequestClose();
                break;
            default:
                // The grandfathering branch is the SECOND writer, and it is the
                // one an upgrade takes — a config already naming a library was
                // set up by hand and is recorded, not greeted.
                rig.Config.AppConfig.PsarcLibraryPath = @"D:\cdlc";
                Assert.False(rig.Vm.OpenAtStartupIfNeeded());
                break;
        }

        Assert.True(rig.Config.AppConfig.OnboardingCompleted);
        Assert.Equal("7.7.7", rig.Config.AppConfig.OnboardingCompletedVersion);
    }

    [Fact]
    public void TheVersionReachesSupportThroughTheDiagnosticsBundle()
    {
        // This is the field's ONE live consumer. If appconfig.json ever stops
        // travelling in the bundle, or the redactor starts scrubbing this line,
        // the "no product reader is fine" note above becomes false.
        var configRoot = Path.Combine(_workDir, "config");
        var logRoot = Path.Combine(_workDir, "logs");
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(logRoot);

        var config = new AppConfig
        {
            OnboardingCompleted = true,
            OnboardingCompletedVersion = "0.49.7"
        };
        File.WriteAllText(Path.Combine(configRoot, "appconfig.json"),
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

        var archivePath = Path.Combine(_workDir, "bundle.zip");
        new DiagnosticsBundleExporter(configRoot, logRoot).Export(archivePath, "OS: Windows");

        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry("appconfig.json");
        Assert.NotNull(entry);

        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        var bundled = reader.ReadToEnd();
        Assert.Contains("OnboardingCompletedVersion", bundled, StringComparison.Ordinal);
        Assert.Contains("0.49.7", bundled, StringComparison.Ordinal);
    }
}
