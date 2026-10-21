using System.Reflection;

namespace DotCharter.Tests.Infrastructure;

/// <summary>Release identity retains prerelease text while binary versions stay SDK-derived.</summary>
public sealed class AppVersionTests
{
    [Fact]
    public void Public_release_has_binary_and_informational_versions()
    {
        var ui = typeof(DotCharter.UI.App).Assembly;
        Assert.Equal(new Version(1, 1, 0, 0), ui.GetName().Version);
        Assert.Equal("1.1.0", DotCharter.Services.Infrastructure.AppVersionInfo.FromAssembly(ui));
        Assert.Equal("1.1.0", DotCharter.Services.Infrastructure.AppVersionInfo.Current);
        Assert.Equal("1.1.0", DotCharter.Services.Parsing.Rscp.RscpFormatContract.CurrentWriterVersion);
        Assert.False(DotCharter.Services.Parsing.Rscp.RscpFormatContract.IsNewerThanCurrent("1.0.0-beta.2"));
        Assert.True(DotCharter.Services.Parsing.Rscp.RscpFormatContract.IsNewerThanCurrent("1.1.1"));
        Assert.False(DotCharter.Services.Parsing.Rscp.RscpFormatContract.IsNewerThanCurrent("1.0.0+other"));
        var crashVersion = typeof(DotCharter.Services.Infrastructure.CrashReporting.CrashReportService)
            .GetMethod("ResolveAppVersion", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        Assert.Equal("1.1.0", crashVersion);
        Assert.StartsWith("DotCharter/1.1.0 ",
            DotCharter.Services.Online.MusicBrainz.MusicBrainzClient.DefaultUserAgent(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ui_assembly_version_matches_the_informational_version()
    {
        var ui = typeof(DotCharter.UI.App).Assembly;

        var assemblyVersion = ui.GetName().Version;
        Assert.NotNull(assemblyVersion);

        // InformationalVersion is <Version> (+ a git-sha suffix from SourceLink).
        var informational = ui.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(informational));

        var declared = informational!.Split('+', '-')[0]; // strip sha / prerelease
        Assert.True(Version.TryParse(declared, out var declaredVersion),
            $"could not parse <Version> from InformationalVersion '{informational}'");

        Assert.Equal(declaredVersion!.Major, assemblyVersion!.Major);
        Assert.Equal(declaredVersion.Minor, assemblyVersion.Minor);
        Assert.Equal(declaredVersion.Build, assemblyVersion.Build);
    }
}
