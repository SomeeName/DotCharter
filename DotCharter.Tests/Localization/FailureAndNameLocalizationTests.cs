using System.Text.RegularExpressions;
using DotCharter.Presentation;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Editing.Selection.Patterns;

namespace DotCharter.Tests.Localization;

/// <summary>
///     #351/#352/#353: the built-in match-pattern names and the stem-separation failure text reach
///     the user, so each resolves through the resx; and a shell-open <c>Process</c> is disposed.
/// </summary>
public sealed class FailureAndNameLocalizationTests
{
    public static TheoryData<string> BuiltInIds()
    {
        var data = new TheoryData<string>();
        foreach (var p in MatchPatternCatalog.BuiltIns()) data.Add(p.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInIds))]
    public void EveryBuiltInPatternName_ReadsInGermanAndJapanese(string id)
    {
        var pattern = MatchPatternCatalog.BuiltIns().Single(p => p.Id == id);
        var en = MatchPatternCatalog.DisplayName(pattern, FakeLocalization.English());
        var de = MatchPatternCatalog.DisplayName(pattern, FakeLocalization.For("de-DE"));
        var ja = MatchPatternCatalog.DisplayName(pattern, FakeLocalization.For("ja-JP"));

        Assert.Equal(pattern.Name, en);
        Assert.NotEqual(en, de);
        Assert.NotEqual(en, ja);
        Assert.NotEqual(de, ja);
    }

    [Fact]
    public void ARenamedBuiltIn_KeepsTheUsersText()
    {
        var pattern = MatchPatternCatalog.BuiltIns()[0];
        pattern.Name = "My chords";

        Assert.Equal("My chords", MatchPatternCatalog.DisplayName(pattern, FakeLocalization.For("de-DE")));
    }

    [Fact]
    public void ThePatternRow_ShowsGerman_ButStoresTheShippedName()
    {
        var loc = FakeLocalization.For("de-DE");
        var pattern = MatchPatternCatalog.BuiltIns().Single(p => p.Id == MatchPatternCatalog.SameLickId);
        var row = new MatchPatternRowViewModel(pattern, PatternChoiceSet.Build(loc), loc);

        Assert.Equal("Gleicher Lick (Lauf)", row.Name);
        Assert.Equal("Same lick (run)", row.ToPattern().Name);
        row.Name = "Mine";
        Assert.Equal("Mine", row.ToPattern().Name);
    }

    [Theory]
    [InlineData(StemFailureKind.ModelMissing, "Es ist kein htdemucs-ggml-Modell installiert.")]
    [InlineData(StemFailureKind.NativeNotCompiled, "Die native Stem-Trennung ist in diesem Build nicht enthalten.")]
    [InlineData(StemFailureKind.OnnxNotEngaged, "Das ONNX-Backend wurde nicht verwendet")]
    public void AStemFailure_ReadsInGerman(StemFailureKind kind, string expectedStart)
    {
        var result = StemSeparationResult.Failure("English log text", kind);

        var text = StemFailureText.Resolve(result, FakeLocalization.For("de-DE"));

        Assert.StartsWith(expectedStart, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AStemFailureWithArguments_FillsThem()
    {
        var result = StemSeparationResult.Failure("x", StemFailureKind.InputMissing, "C:\a.wav");

        Assert.Equal("Eingabe-Audio nicht gefunden: 'C:\a.wav'.", StemFailureText.Resolve(result, FakeLocalization.For("de-DE")));
    }

    [Fact]
    public void AnUncodedFailure_ShowsItsRawMessage() =>
        Assert.Equal("boom", StemFailureText.Resolve(StemSeparationResult.Failure("boom"), FakeLocalization.For("de-DE")));

    [Fact]
    public void EveryCodedFailureKind_HasAKey()
    {
        foreach (var kind in Enum.GetValues<StemFailureKind>().Where(k => k != StemFailureKind.Other))
            Assert.NotNull(StemFailureText.KeyFor(kind));
    }

    [Fact]
    public async Task ACompositeWithNoModel_SurfacesTheModelMissingCode()
    {
        var composite = new CompositeStemSeparationService([new ModelMissingBackend()]);

        var result = await composite.SeparateAsync("in.wav", "out", null, TestContext.Current.CancellationToken);

        Assert.Equal(StemFailureKind.ModelMissing, result.FailureKind);
    }

    /// <summary>No stem-service failure may be written as a bare English literal: it needs a kind.</summary>
    [Fact]
    public void NoStemServiceFailure_IsAnUncodedLiteral()
    {
        var dir = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services", "Audio", "StemSeparation");
        var calls = 0;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "IStemSeparationService.cs") continue;
            var source = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(source, @"StemSeparationResult\.Failure\((?<args>[^;]*?)\);"))
            {
                calls++;
                var args = m.Groups["args"].Value;
                if (args.TrimStart().StartsWith("\"", StringComparison.Ordinal) || args.TrimStart().StartsWith("$\"", StringComparison.Ordinal))
                {
                    if (!args.Contains("StemFailureKind.", StringComparison.Ordinal)) offenders.Add($"{Path.GetFileName(file)}: {args.Trim()[..Math.Min(60, args.Trim().Length)]}");
                }
            }
        }

        Assert.True(calls >= 6, $"only {calls} Failure(...) calls found; the pattern no longer fits");
        Assert.True(offenders.Count == 0, "stem failures written as uncoded literals:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>A discarded <c>Process.Start</c> result leaks a handle per click.</summary>
    [Theory]
    [InlineData("DotCharter.Tone.UI/Platform/DesktopShellLauncher.cs", 3)]
    [InlineData("DotCharter.Tone/Online/Tone3000/Tone3000AuthService.cs", 1)]
    public void ShellOpenProcesses_AreDisposed(string relative, int expectedStarts)
    {
        var source = File.ReadAllText(Path.Combine([TestPaths.RepoRoot(), .. relative.Split('/')]));
        var code = source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();
        var starts = code.Count(l => l.Contains("Process.Start(", StringComparison.Ordinal));
        var disposed = code.Count(l => Regex.IsMatch(l, @"using var \w+ = Process\.Start\("));

        Assert.Equal(expectedStarts, starts);
        Assert.Equal(starts, disposed);
    }

    private sealed class ModelMissingBackend : IStemSeparationService
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<string> DescribeBackendAsync(CancellationToken ct = default) => Task.FromResult("fake");

        public Task<StemSeparationResult> SeparateAsync(string inputAudioPath, string outputDirectory,
            IProgress<double>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(StemSeparationResult.Failure("no model", StemFailureKind.ModelMissing));
    }
}
