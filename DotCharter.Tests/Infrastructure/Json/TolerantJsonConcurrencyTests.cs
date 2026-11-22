using DotCharter.RigLab;

namespace DotCharter.Tests.Infrastructure.Json;

/// <summary>
///     TolerantJson keeps its per-read state thread-static: many threads reading
///     files with and without bad values at once must each see exactly their own
///     result and warnings.
/// </summary>
public sealed class TolerantJsonConcurrencyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tolerant-json-" + Guid.NewGuid().ToString("N"));

    public TolerantJsonConcurrencyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ManyThreadsReading_GoodAndBadFiles_EachGetsItsOwnResultAndWarnings()
    {
        const string good = """{ "LanguageCulture": "ja-JP", "EnableInput": false }""";
        const string bad = """{ "LanguageCulture": "de-DE", "MinimumLogLevel": "FromANewerBuild", "EnableInput": false }""";
        const string wrongType = """{ "LanguageCulture": 7, "EnableInput": false }""";

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 16 }, worker =>
        {
            for (var i = 0; i < 40; i++)
            {
                var kind = (worker + i) % 3;
                var path = Path.Combine(_dir, $"w{worker}-{i}.json");
                File.WriteAllText(path, kind == 0 ? good : kind == 1 ? bad : wrongType);
                using var service = new RigLabConfigService(path);
                var config = service.Load();

                if (config.EnableInput) failures.Add($"{worker}/{i}: EnableInput lost");
                if (kind == 0 && (config.LanguageCulture != "ja-JP" || service.IgnoredSettings.Count != 0))
                    failures.Add($"{worker}/{i}: good file misread");
                if (kind == 1 && (config.LanguageCulture != "de-DE" || service.IgnoredSettings.Count != 1))
                    failures.Add($"{worker}/{i}: bad enum: {config.LanguageCulture} / {service.IgnoredSettings.Count}");
                if (kind == 2 && (config.LanguageCulture != new RigLabConfig().LanguageCulture || service.IgnoredSettings.Count != 1))
                    failures.Add($"{worker}/{i}: wrong type: {config.LanguageCulture} / {service.IgnoredSettings.Count}");
            }
        });

        Assert.Empty(failures);
    }
}
