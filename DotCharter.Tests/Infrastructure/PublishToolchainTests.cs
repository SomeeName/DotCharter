using System.Xml.Linq;

namespace DotCharter.Tests.Infrastructure;

public sealed class PublishToolchainTests
{
    [Fact]
    public void PublishToolchainPinIsAtMostTheRunningRuntimeVersion()
    {
        var targets = XDocument.Load(Path.Combine(TestPaths.RepoRoot(), "Directory.Build.targets"));
        var pin = Version.Parse(Assert.Single(targets.Descendants("DotCharterPublishToolchainVersion")).Value);
        var runtime = Environment.Version;

        Assert.True(pin <= runtime,
            $"Directory.Build.targets pins the publish toolchain to {pin}, but the tests are running on .NET {runtime}. "
            + "Install the matching .NET SDK or lower the pin and regenerate the lock files.");
    }
}
