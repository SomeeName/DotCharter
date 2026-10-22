using DotCharter.Services.Session;

namespace DotCharter.Tests.Infrastructure;

/// <summary>An <see cref="IProjectPathRekey" /> that moves nothing and remembers what it was asked.</summary>
public sealed class FakeProjectPathRekey : IProjectPathRekey
{
    public List<(string From, string To)> Calls { get; } = [];

    public ProjectRekeyResult Rekey(string fromPath, string toPath)
    {
        Calls.Add((fromPath, toPath));
        return new ProjectRekeyResult(0, 0, 0);
    }
}
