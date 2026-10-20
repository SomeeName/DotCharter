using System.IO.Compression;
using DotCharter.Services.Update;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Update;

/// <summary>
///     Coverage for the install-layout classifier and payload validator that
///     stop an update from merging a framework-dependent build onto a
///     self-contained install (and vice versa).
/// </summary>
public sealed class AppPayloadTests : IDisposable
{
    // Verbatim shapes emitted by `dotnet publish --self-contained true|false`.
    private const string SelfContainedConfig = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "includedFrameworks": [ { "name": "Microsoft.NETCore.App", "version": "10.0.9" } ]
          }
        }
        """;

    private const string FrameworkDependentConfig = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
          }
        }
        """;

    // Apps targeting several shared frameworks (e.g. WindowsDesktop) get a plural node.
    private const string MultiFrameworkConfig = """
        {
          "runtimeOptions": {
            "tfm": "net10.0",
            "frameworks": [ { "name": "Microsoft.WindowsDesktop.App", "version": "10.0.0" } ]
          }
        }
        """;

    // ---- classification ----------------------------------------------------------

    [Fact]
    public void Classify_SelfContainedConfig()
        => Assert.Equal(InstallLayout.SelfContained, AppPayload.ClassifyRuntimeConfig(SelfContainedConfig));

    [Fact]
    public void Classify_FrameworkDependentConfig()
        => Assert.Equal(InstallLayout.FrameworkDependent, AppPayload.ClassifyRuntimeConfig(FrameworkDependentConfig));

    [Fact]
    public void Classify_MultiFrameworkConfig_IsFrameworkDependent()
        => Assert.Equal(InstallLayout.FrameworkDependent, AppPayload.ClassifyRuntimeConfig(MultiFrameworkConfig));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{}")]                                              // no runtimeOptions
    [InlineData("{\"runtimeOptions\":{\"tfm\":\"net10.0\"}}")]      // neither node
    [InlineData("{\"runtimeOptions\":{\"includedFrameworks\":[]}}")] // empty → not self-contained
    public void Classify_GarbageOrIndeterminate_IsUnknown(string? json)
        => Assert.Equal(InstallLayout.Unknown, AppPayload.ClassifyRuntimeConfig(json));

    [Fact]
    public void ClassifyDirectory_ReadsRuntimeConfigFromDisk()
    {
        var dir = NewTempDir();
        Assert.Equal(InstallLayout.Unknown, AppPayload.ClassifyDirectory(dir));

        WriteFile(dir, AppPayload.RuntimeConfigName, SelfContainedConfig);
        Assert.Equal(InstallLayout.SelfContained, AppPayload.ClassifyDirectory(dir));
    }

    // ---- validation --------------------------------------------------------------

    [Fact]
    public void Validate_MatchingSelfContainedPayload_IsAccepted()
    {
        var dir = SelfContainedInstall();
        Assert.Null(AppPayload.ValidateDirectory(dir, InstallLayout.SelfContained));
    }

    [Fact]
    public void Validate_MatchingFrameworkDependentPayload_IsAccepted()
    {
        var dir = FrameworkDependentInstall();
        Assert.Null(AppPayload.ValidateDirectory(dir, InstallLayout.FrameworkDependent));
    }

    /// <summary>The exact shape that bricked a LAN-updated install: a dev build
    /// (framework-dependent) copied onto a shipped release (self-contained).</summary>
    [Fact]
    public void Validate_FrameworkDependentPayloadOntoSelfContainedInstall_IsRejected()
    {
        var payload = FrameworkDependentInstall();

        var problem = AppPayload.ValidateDirectory(payload, InstallLayout.SelfContained);

        Assert.NotNull(problem);
        Assert.Contains("framework-dependent", problem);
        Assert.Contains("self-contained", problem);
    }

    [Fact]
    public void Validate_SelfContainedPayloadOntoFrameworkDependentInstall_IsRejected()
    {
        var payload = SelfContainedInstall();

        var problem = AppPayload.ValidateDirectory(payload, InstallLayout.FrameworkDependent);

        Assert.NotNull(problem);
        Assert.Contains("unbootable", problem);
    }

    [Theory]
    [InlineData(AppPayload.ExeName)]
    [InlineData(AppPayload.RuntimeConfigName)]
    [InlineData(AppPayload.DepsName)]
    [InlineData(AppPayload.HostFxrName)]
    [InlineData(AppPayload.CoreClrName)]
    public void Validate_MissingCriticalFile_IsRejected(string missing)
    {
        var dir = SelfContainedInstall();
        File.Delete(Path.Combine(dir, missing));

        Assert.NotNull(AppPayload.ValidateDirectory(dir, InstallLayout.SelfContained));
    }

    [Fact]
    public void Validate_UnknownExpectedLayout_SkipsTheMatchCheck()
    {
        var dir = FrameworkDependentInstall();
        Assert.Null(AppPayload.ValidateDirectory(dir, InstallLayout.Unknown));
    }

    // ---- archives ----------------------------------------------------------------

    [Fact]
    public void ResolveArchiveRoot_FlatArchive_HasNoPrefix()
    {
        using var archive = BuildArchive(string.Empty, selfContained: true);
        Assert.Equal(string.Empty, AppPayload.ResolveArchiveRoot(archive));
    }

    [Fact]
    public void ResolveArchiveRoot_UnwrapsSingleTopLevelFolder()
    {
        using var archive = BuildArchive("DotCharter-v0.13.0-win-x64/", selfContained: true);
        Assert.Equal("DotCharter-v0.13.0-win-x64/", AppPayload.ResolveArchiveRoot(archive));
    }

    [Fact]
    public void ValidateArchive_AcceptsWrappedSelfContainedRelease()
    {
        using var archive = BuildArchive("DotCharter-v0.13.0-win-x64/", selfContained: true);
        Assert.Null(AppPayload.ValidateArchive(archive, InstallLayout.SelfContained));
    }

    [Fact]
    public void ValidateArchive_RejectsCrossLayoutPackage()
    {
        using var archive = BuildArchive(string.Empty, selfContained: false);

        var problem = AppPayload.ValidateArchive(archive, InstallLayout.SelfContained);

        Assert.NotNull(problem);
        Assert.Contains("framework-dependent", problem);
    }

    // ---- helpers -----------------------------------------------------------------

    private readonly List<string> _roots = [];

    private string SelfContainedInstall()
    {
        var dir = NewTempDir();
        WriteFile(dir, AppPayload.ExeName, "MZ");
        WriteFile(dir, AppPayload.RuntimeConfigName, SelfContainedConfig);
        WriteFile(dir, AppPayload.DepsName, "{}");
        WriteFile(dir, AppPayload.HostFxrName, "MZ");
        WriteFile(dir, AppPayload.CoreClrName, "MZ");
        return dir;
    }

    private string FrameworkDependentInstall()
    {
        var dir = NewTempDir();
        WriteFile(dir, AppPayload.ExeName, "MZ");
        WriteFile(dir, AppPayload.RuntimeConfigName, FrameworkDependentConfig);
        WriteFile(dir, AppPayload.DepsName, "{}");
        return dir;
    }

    private static ZipArchive BuildArchive(string prefix, bool selfContained)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(zip, prefix + AppPayload.ExeName, "MZ");
            AddEntry(zip, prefix + AppPayload.DepsName, "{}");
            AddEntry(zip, prefix + AppPayload.RuntimeConfigName,
                selfContained ? SelfContainedConfig : FrameworkDependentConfig);
            if (selfContained)
            {
                AddEntry(zip, prefix + AppPayload.HostFxrName, "MZ");
                AddEntry(zip, prefix + AppPayload.CoreClrName, "MZ");
            }
        }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);

        static void AddEntry(ZipArchive zip, string name, string content)
        {
            using var stream = zip.CreateEntry(name).Open();
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dc-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _roots.Add(dir);
        return dir;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
            BestEffortDelete.Directory(root);
    }
}
