using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Search;
using DotCharter.Services.Ai.Match.Sections;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tests.Ai.Search;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     The set matcher inherits the catalog gate because it delegates to the real
///     <see cref="ToneMatcher" /> — and this pins that it is still the real one.
///     Running it over a fake matcher would make the whole claim vacuous, which is
///     exactly how a per-section feature ships gear the app cannot build.
/// </summary>
public sealed class ToneSetMatcherCatalogGateTests
{
    /// <summary>The one-module chain the fake sampler draws and the fake renderer
    /// understands. A real catalogue module, so a sampled row is a legal rig.</summary>
    private const string ChainModule = "eq";

    private static DcRigPreset Chain() => new([
        new DcRigModule(ChainModule, new Dictionary<string, float>(StringComparer.Ordinal))
    ]);

    [Fact]
    public async Task EverySectionsCandidates_PassTheCatalogGate_AndAPriorOutsideItIsRejectedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var di = SearchFakes.Di(1.2);
        var renderer = new FakeRigRenderer();
        var extractor = new FakeFeatureExtractor();
        var library = await RigLibrary.BuildAsync(
            di, new FakeRigSampler(ChainModule), renderer, extractor,
            new RigLibraryOptions(Count: 8, Seed: 4242), null, ct);

        var matcher = new ToneMatcher(
            renderer, new FakeSimilarity(extractor), new FixedRigLibraryProvider(library), FakeLocalization.English(), null,
            new ToneMatcherOptions(MaxRenders: 24, RefineCandidates: 1,
                SelfDistanceWindow: TimeSpan.FromSeconds(0.5)));

        var stem = await renderer.RenderAsync(di, Chain(), ct);
        var isolator = new SectionFakes.FakeIsolator(stem);

        var bogus = new DcRigPreset([
            new DcRigModule("klon-centaur-gold", new Dictionary<string, float>(StringComparer.Ordinal))
        ]);

        var sections = new[]
        {
            new ToneSection("a", "Verse", ToneStems.Guitar, TimeSpan.Zero, TimeSpan.FromSeconds(6)),
            new ToneSection("b", "Chorus", ToneStems.Guitar, TimeSpan.Zero, TimeSpan.FromSeconds(6),
                new ToneMatchPrior(bogus, "ai:test"))
        };

        var set = new ToneSetMatcher(isolator, matcher, FakeLocalization.English());
        var result = await set.MatchAsync(
            new ToneSetMatchRequest("song.wav", di, sections, CandidatesPerSection: 3), null, ct);

        Assert.Equal(2, result.Sections.Count);
        foreach (var section in result.Sections)
        {
            Assert.Null(section.Error);
            Assert.NotNull(section.Match);
            Assert.NotEmpty(section.Match!.Candidates);
            Assert.All(section.Match.Candidates, c =>
            {
                var fault = RigCatalogValidation.FirstFault(c.Rig);
                Assert.True(fault is null, $"{section.Section.Label}/{c.Origin}: {fault?.Message}");
            });
        }

        var chorus = result.Sections[1].Match!;
        Assert.Contains(chorus.Notes,
            n => n.StartsWith("rejected ", StringComparison.Ordinal) &&
                 n.Contains("klon-centaur-gold", StringComparison.Ordinal));
    }
}
