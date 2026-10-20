using System.Globalization;

namespace DotCharter.Tests.Core.Utilities;

/// <summary>
///     <para>
///         Pins <see cref="FractionalPositionExtensions.FormatWithMs" />, the one
///         human-readable position string used by undo labels, log lines and
///         dialog captions. Eight files each carried this interpolation privately
///         until 2026-08-28; the copies are gone and the last test here fails if
///         a ninth appears.
///     </para>
///     <para>
///         What is load-bearing: the bar/beat form AND the resolved millisecond
///         together, and the <c>0.###</c> rounding — two edits a few ticks apart
///         must not render as the same undo entry.
///     </para>
/// </summary>
/// <remarks>
///     In the <c>LocalizationSingleton</c> collection because
///     <see cref="FollowsTheAmbientCulture_documentedNotDecided" /> moves
///     <see cref="CultureInfo.CurrentCulture" />, which is process-wide —
///     required by <c>Infrastructure/CultureCollectionLintTests</c>, which
///     caught this class the first time the gate ran over it.
/// </remarks>
[Collection("LocalizationSingleton")]
public class FractionalPositionFormatTests
{
    /// <summary>A steady 120 BPM 4/4 grid: one beat = 500 ms.</summary>
    private static List<BeatMarker> Grid(int count = 8)
        => Enumerable.Range(0, count)
            .Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120.0, BeatsInMeasure = 4 })
            .ToList();

    [Fact]
    public void CarriesBothTheBarBeatFormAndTheResolvedMillisecond()
    {
        var beats = Grid();
        var pos = new FractionalPosition(2, new Fraction(1, 2));

        var text = pos.FormatWithMs(beats);

        Assert.StartsWith(pos.ToString(), text, StringComparison.Ordinal);
        Assert.EndsWith(" ms)", text, StringComparison.Ordinal);
        Assert.Contains("1250", text, StringComparison.Ordinal); // 2.5 beats * 500 ms
    }

    /// <summary>
    ///     Two positions one tick apart must not collapse to the same label — an
    ///     undo list that shows the same text twice is unusable, and that is the
    ///     only reason the millisecond is in the string at all.
    /// </summary>
    [Fact]
    public void NearbyPositionsStayDistinguishable()
    {
        var beats = Grid();
        var a = new FractionalPosition(1, new Fraction(1, 96));
        var b = new FractionalPosition(1, new Fraction(2, 96));

        Assert.NotEqual(a.FormatWithMs(beats), b.FormatWithMs(beats));
    }

    /// <summary>
    ///     <c>0.###</c> means at most three decimals and no trailing zeros — the
    ///     copies all used it and a plain <c>{ms}</c> would print 17 digits.
    /// </summary>
    [Fact]
    public void RoundsToAtMostThreeDecimalsWithNoTrailingZeros()
    {
        var beats = Grid();

        // A whole millisecond prints bare.
        Assert.Contains("(500 ms)", new FractionalPosition(1, Fraction.Zero).FormatWithMs(beats),
            StringComparison.Ordinal);

        // A repeating third of a beat is cut at three decimals, not spelled out.
        var thirds = new FractionalPosition(0, new Fraction(1, 3)).FormatWithMs(beats);
        var inside = thirds[(thirds.IndexOf('(') + 1)..thirds.IndexOf(" ms)", StringComparison.Ordinal)];
        var decimals = inside.Contains('.') || inside.Contains(',')
            ? inside.Length - inside.LastIndexOfAny(['.', ',']) - 1
            : 0;
        Assert.True(decimals <= 3, $"Expected at most 3 decimals, got '{inside}'.");
    }

    /// <summary>
    ///     An empty beat list must not throw: the editor formats a position while
    ///     a project with no beat map is open, and an undo label is never worth a
    ///     crash.
    /// </summary>
    [Fact]
    public void AnEmptyBeatMapFormatsRatherThanThrowing()
    {
        var text = new FractionalPosition(3, new Fraction(1, 4)).FormatWithMs([]);
        Assert.Contains(" ms)", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Records the deliberately-unchanged behaviour: the string follows the
    ///     AMBIENT culture, exactly as all eight private copies did. If this ever
    ///     becomes a problem — a German-locale run writes <c>1234,567 ms</c> into
    ///     a log diffed against an English one — the fix is an explicit decision
    ///     to pass <see cref="CultureInfo.InvariantCulture" />, not a silent one.
    /// </summary>
    [Fact]
    public void FollowsTheAmbientCulture_documentedNotDecided()
    {
        var beats = Grid();
        var pos = new FractionalPosition(0, new Fraction(1, 3));

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var german = pos.FormatWithMs(beats);
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var invariant = pos.FormatWithMs(beats);

            Assert.Contains(",", german, StringComparison.Ordinal);
            Assert.Contains(".", invariant, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    ///     Fails if a file re-grows its own copy. The fault compiles clean and
    ///     breaks no test — it just splits one rendering into two that drift.
    /// </summary>
    [Fact]
    public void NoFileInterpolatesThePositionAndMsItself()
    {
        string[] roots =
        [
            "DotCharter.Core", "DotCharter.Services", "DotCharter.Tone", "DotCharter.UI",
            "DotCharter.Analysis", "DotCharter.Rendering"
        ];
        const string needle = "):0.###} ms)\"";

        var offenders = new List<string>();
        foreach (var project in roots)
        {
            var dir = Path.Combine(TestPaths.RepoRoot(), project);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in UiSourceTree.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                if (Path.GetFileName(file) == "FractionalPositionExtension.cs") continue;
                if (UiSourceTree.ReadAllText(file).Contains(needle, StringComparison.Ordinal))
                    offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Call FractionalPositionExtensions.FormatWithMs instead. Found in: "
            + string.Join(", ", offenders));
    }
}
