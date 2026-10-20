using System.Text.RegularExpressions;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Scoring;

namespace DotCharter.Tests.Services.Gameplay;

/// <summary>
///     The ONE zero-attempt rule, and the lint that keeps it one.
///     <para>
///         Six types used to carry their own <c>judged == 0 ? 1.0</c>, and every
///         one of them read as <b>100%</b> on screen for a player who had done
///         nothing. Pure, no clock, no app session.
///     </para>
/// </summary>
public class AccuracyMathTests
{
    [Theory]
    [InlineData(0, 0, 0.0)]
    [InlineData(0, 5, 0.0)]
    [InlineData(3, 1, 0.75)]
    [InlineData(4, 0, 1.0)]
    public void FromCounts_IsHitsOverJudged_AndZeroJudgedIsZero(int hits, int misses, double expected)
        => Assert.Equal(expected, AccuracyMath.FromCounts(hits, misses), 10);

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, true)]
    public void IsMeasured_IsWhetherAnythingHasBeenJudged(int hits, int misses, bool expected)
        => Assert.Equal(expected, AccuracyMath.IsMeasured(hits, misses));

    /// <summary>
    ///     Every type that reports an accuracy agrees at zero. Nothing judged
    ///     must never read as a perfect run anywhere.
    /// </summary>
    [Fact]
    public void EveryAccuracyBearingType_AgreesThatNothingJudgedIsNotPerfect()
    {
        Assert.Equal(0.0, new LivePlayStats().Accuracy);
        Assert.False(new LivePlayStats().IsMeasured);

        Assert.Equal(0.0, new LivePlaySection().Accuracy);
        Assert.False(new LivePlaySection().IsMeasured);

        var section = new SectionResult("intro", 0, 1000, SoundCount: 8, Hits: 0, Misses: 0);
        Assert.Equal(0.0, section.Accuracy);
        Assert.False(section.WasPlayed);

        var phrase = new PhraseMasteryOutcome("riff", Level: 0, Hits: 0, Misses: 0);
        Assert.Equal(0.0, phrase.Accuracy);

    }

    /// <summary>
    ///     A source lint, because the fault this guards is a NEW type quietly
    ///     re-introducing its own zero convention — which compiles, keeps its own
    ///     passing tests, and only ever shows up as a number on a HUD. The six
    ///     originals were each individually reasonable; it was having six of them
    ///     that made two seats of one couch disagree.
    /// </summary>
    [Fact]
    public void NoServiceTypeCarriesItsOwnZeroJudgedConvention()
    {
        var root = Path.Combine(TestPaths.RepoRoot(), "DotCharter.Services");
        Assert.True(Directory.Exists(root), $"Services source not found at {root}");

        // "<a tally> == 0 ? 1.0 :" — the exact shape all six originals had. The
        // identifier is matched with an OPTIONAL PREFIX on purpose
        // (`BandMisses`, `judgedCount`, `_hits`): the first draft of this lint
        // anchored on the bare word and sailed straight past a planted
        // `BandHits + BandMisses == 0 ? 1.0`, which is precisely the rename a
        // real regression would arrive under.
        var pattern = new Regex(
            @"(?i)[\w.]*(judged|hits|misses|attempts)[\w.]*\s*(==|<=)\s*0\s*\?\s*1(\.0)?\s*:",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in new[] { root, Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone") }
                     .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories)))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Strip comments first: the guides and the doc comments name the
                // old shape on purpose, and a `see cref` is exactly as inert.
                var code = line.TrimStart().StartsWith("//", StringComparison.Ordinal) ||
                           line.TrimStart().StartsWith("///", StringComparison.Ordinal) ||
                           line.TrimStart().StartsWith('*')
                    ? ""
                    : line;
                if (pattern.IsMatch(code))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These compute an accuracy-shaped figure with their own \"nothing judged is perfect\" rule. " +
            "Use AccuracyMath.FromCounts and gate the readout on AccuracyMath.IsMeasured:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
