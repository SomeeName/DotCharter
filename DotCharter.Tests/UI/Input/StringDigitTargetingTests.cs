using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     The three keyboard families that address a string BY NUMBER —
///     <c>Ctrl+1..9</c> (the string-edit mask), bare <c>1..9</c> (the pen) and
///     <c>StringUp</c>'s ceiling — must be bounded by the lanes the editor
///     actually draws, i.e. the ACTIVE arrangement's tuning length.
///     <para>
///         Before this guard they were bounded by <c>StringEditMask.MaxStrings</c>
///         (9 — a property of the KEYBOARD) and, for <c>StringUp</c>, by the
///         PROJECT-level <c>ChartData.Tuning</c>, which is stamped once from the
///         first arrangement. Both produced state nothing on screen can show: a
///         note on a lane that is not drawn, and a mask bit with no lane header.
///     </para>
/// </summary>
public sealed class StringDigitTargetingTests
{
    private static Arrangement WithStrings(int count) =>
        new() { Tuning = Enumerable.Range(0, count).Select(i => "S" + i).ToArray() };

    [Theory]
    [InlineData(4)] // bass
    [InlineData(5)] // 5-string bass
    [InlineData(6)] // guitar
    [InlineData(7)] // 7-string guitar
    public void TheLaneCountComesFromTheTuningVector(int strings) => Assert.Equal(strings, StringDigitTargeting.StringCountOf(WithStrings(strings)));

    [Fact]
    public void AnArrangementWithNoTuningFallsBackToSixRatherThanZero()
    {
        // Zero would make every digit key inert, which is a worse failure than
        // the six the Arrangement model itself defaults to.
        Assert.Equal(6, StringDigitTargeting.StringCountOf(null));
        Assert.Equal(6, StringDigitTargeting.StringCountOf(new Arrangement { Tuning = [] }));
    }

    [Fact]
    public void TheTuningVectorWinsOverTheIndependentStringCountField()
    {
        // Arrangement.StringCount and Arrangement.Tuning are separate settable
        // fields and can disagree; the renderer, the hit tester and the lane
        // headers all lay out from the TUNING, so the keyboard must too.
        var bass = new Arrangement { StringCount = 6, Tuning = ["E", "A", "D", "G"] };
        Assert.Equal(4, StringDigitTargeting.StringCountOf(bass));
    }

    [Theory]
    [InlineData(0, 4, true)]
    [InlineData(3, 4, true)]
    [InlineData(4, 4, false)] // the bug: bare `5` on a 4-string bass
    [InlineData(8, 4, false)] // …and Ctrl+9, the top of the digit family
    [InlineData(5, 6, true)]
    [InlineData(6, 6, false)]
    [InlineData(6, 7, true)]
    [InlineData(-1, 6, false)]
    public void AStringIsAddressableOnlyWhenTheArrangementHasIt(int stringIndex, int stringCount, bool addressable) => Assert.Equal(addressable, StringDigitTargeting.IsAddressable(stringIndex, stringCount));

    [Fact]
    public void EveryDigitOfTheMaskFamilyIsAddressableOnASevenStringAndTheTopTwoAreNotOnAGuitar()
    {
        // Ctrl+1..9 maps digit d to string index d-1 (AppKeybindRegistrar's
        // `Key.D1 + i` loop). On a 6-string that is 1..6 live, 7..9 refused.
        var guitar = WithStrings(6);
        for (var digit = 1; digit <= 9; digit++)
            Assert.Equal(digit <= 6, StringDigitTargeting.IsAddressable(digit - 1, guitar));

        var seven = WithStrings(7);
        for (var digit = 1; digit <= 9; digit++)
            Assert.Equal(digit <= 7, StringDigitTargeting.IsAddressable(digit - 1, seven));
    }

    /// <summary>
    ///     Source lint, the same shape as
    ///     <c>ResizeLyricGestureMergeTests.TheWheelCallSitePassesTheMergeVerb</c>:
    ///     <c>AppKeybindRegistrar</c> needs the whole app booted to construct, so
    ///     the guard above can only be proved pure — this proves it is REACHED.
    ///     A guard nothing calls is the same dead code as a registration nothing
    ///     resolves.
    /// </summary>
    [Fact]
    public void TheThreeStringAddressingCallSitesRouteThroughTheGuard()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Input/AppKeybindRegistrar.cs"));

        // The two digit loops refuse a string the arrangement has not got…
        var refusals = Occurrences(source,
            "if (!StringDigitTargeting.IsAddressable(stringIndex, _projectState.ActiveArrangement)) return;");
        Assert.True(refusals == 2,
            "Both digit families (the Ctrl+n string-edit mask and the pen's bare n) must refuse a string the " +
            $"active arrangement has not got; found {refusals} refusal(s). Without it a bare `6` on a 4-string " +
            "bass creates a note on a lane the renderer does not draw.");

        // …and the StringUp ceiling comes from the ACTIVE ARRANGEMENT.
        Assert.Contains("StringDigitTargeting.StringCountOf(_projectState.ActiveArrangement)", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentChart?.Tuning.Length", source, StringComparison.Ordinal);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
