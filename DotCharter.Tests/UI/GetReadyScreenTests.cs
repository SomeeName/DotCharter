using System.Text.RegularExpressions;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Tone.Audio.Tuner;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     Default-gate guards for the Get Ready screen (§6.6).
///
///     <para>Everything here is platform-free: the pure scales behind the tuner
///     meter, the fixed string identities, the measured stem-separation cost,
///     the key-hint contract, and source lints over the markup for the
///     structural rules a bounds check cannot see. The construction + attach +
///     layout half lives in
///     <c>Perf/PlayModeScreensUiPerfSmokeTests</c>.</para>
/// </summary>
public sealed class GetReadyScreenTests
{
    // ── The six fixed string identities ──────────────────────────────────────

    /// <summary>
    ///     Index → token key, for all six. A SWAPPED PAIR is the quietest
    ///     possible regression on this screen: both strings still get a colour,
    ///     the row still measures, and only someone who knows the highway would
    ///     notice the tuner disagreeing with it.
    /// </summary>
    [Theory]
    [InlineData(0, "FireString6E")]
    [InlineData(1, "FireString5A")]
    [InlineData(2, "FireString4D")]
    [InlineData(3, "FireString3G")]
    [InlineData(4, "FireString2B")]
    [InlineData(5, "FireString1e")]
    public void EachString_MapsToItsOwnFixedIdentityToken(int index, string expected) =>
        Assert.Equal(expected, FireStringIdentity.TokenKeyFor(index));

    /// <summary>Six DISTINCT keys, and a seventh string gets none — Fire
    /// declares six identities, so a 7-string guitar's low B must render
    /// un-identified rather than wearing some other string's colour.</summary>
    [Fact]
    public void TheSixIdentities_AreDistinct_AndStopAtSix()
    {
        var keys = Enumerable.Range(0, FireStringIdentity.Count)
            .Select(FireStringIdentity.TokenKeyFor).ToList();

        Assert.Equal(6, keys.Count);
        Assert.All(keys, k => Assert.False(string.IsNullOrEmpty(k)));
        Assert.Equal(6, keys.Distinct(StringComparer.Ordinal).Count());

        Assert.Null(FireStringIdentity.TokenKeyFor(6));
        Assert.Null(FireStringIdentity.TokenKeyFor(-1));
        Assert.DoesNotContain(FireStringIdentity.NeutralTokenKey, keys);
    }

    /// <summary>
    ///     …and every one of those keys really exists in <c>FireTokens.axaml</c>
    ///     with the mockup's exact value. Without this the mapping above could be
    ///     six perfectly distinct names for brushes that do not exist, which
    ///     paints six invisible chips and passes every other assertion here.
    /// </summary>
    [Fact]
    public void EveryIdentityToken_IsDeclaredWithTheSpecsExactColour()
    {
        // WITH its includes: FireTokens.axaml is a container over
        // `Styles/FireTokens/`, and the six identities live in its Palette part.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FireString6E"] = "#E05A5A",
            ["FireString5A"] = "#E0C04A",
            ["FireString4D"] = "#4A90E0",
            ["FireString3G"] = "#E08A3C",
            ["FireString2B"] = "#4ADE80",
            ["FireString1e"] = "#A78BFA"
        };

        // Through the shared resolver: since the Fire palette was tokenised
        // (2026-09-20) a brush names a Color token rather than a literal, so
        // reading the brush's own attribute finds "{DynamicResource …}".
        var declared = StyleFileText.BrushesWithIncludes(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens.axaml"));

        for (var i = 0; i < FireStringIdentity.Count; i++)
        {
            var key = FireStringIdentity.TokenKeyFor(i)!;
            Assert.True(declared.TryGetValue(key, out var colour), $"{key} is not declared in FireTokens.axaml");
            Assert.Equal(Avalonia.Media.Color.Parse(expected[key]), colour);
        }

        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone.UI", "Styles", "FireTokens.axaml")));
    }

    // ── The tuner meter's scale ──────────────────────────────────────────────

    /// <summary>The band IS the judge's tolerance, and the scale is derived from
    /// it — not declared beside it. Change the tolerance, and both move.</summary>
    [Theory]
    [InlineData(5.0)]
    [InlineData(2.0)]
    [InlineData(12.0)]
    public void TheScale_IsDerivedFromTheJudgesOwnTolerance(double tolerance)
    {
        Assert.Equal(tolerance * FireTunerScale.ScaleToBandRatio,
            FireTunerScale.HalfScaleCents(tolerance), 6);

        // A reading exactly at the tolerance sits on the band's edge.
        Assert.Equal(0.5 + FireTunerScale.BandHalfFraction,
            FireTunerScale.NeedleFraction(tolerance, tolerance), 6);
        Assert.Equal(0.5 - FireTunerScale.BandHalfFraction,
            FireTunerScale.NeedleFraction(-tolerance, tolerance), 6);
    }

    /// <summary>Dead-on is the centre; the needle clamps rather than leaving the
    /// meter, so a wildly detuned string pins at the edge and still reads as
    /// "a long way off".</summary>
    [Fact]
    public void TheNeedle_CentresOnZero_AndClampsAtTheEdges()
    {
        Assert.Equal(0.5, FireTunerScale.NeedleFraction(0, 5), 6);
        Assert.Equal(1.0, FireTunerScale.NeedleFraction(400, 5), 6);
        Assert.Equal(0.0, FireTunerScale.NeedleFraction(-400, 5), 6);
        Assert.Equal(0.5, FireTunerScale.NeedleFraction(double.NaN, 5), 6);
    }

    /// <summary>
    ///     The three states, and the one that matters: NO READING is not
    ///     "zero cents off". A never-plucked string and a perfect one both have
    ///     <c>Cents == 0</c>, and telling a player their untouched low E is in
    ///     tune is the exact lie this screen exists to avoid.
    /// </summary>
    [Fact]
    public void NotYetPlucked_IsNotTheSameAsPerfectlyInTune()
    {
        Assert.Equal(FireTunerBand.NoReading, FireTunerScale.BandFor(false, 0, 5));
        Assert.Equal(FireTunerBand.InTune, FireTunerScale.BandFor(true, 0, 5));
        Assert.Equal(FireTunerBand.InTune, FireTunerScale.BandFor(true, 5, 5));
        Assert.Equal(FireTunerBand.OutOfTune, FireTunerScale.BandFor(true, 5.01, 5));
        Assert.Equal(FireTunerBand.OutOfTune, FireTunerScale.BandFor(true, -30, 5));

        // …and the three do not share a colour token.
        var tokens = new[] { FireTunerBand.NoReading, FireTunerBand.InTune, FireTunerBand.OutOfTune }
            .Select(FireTunerScale.NeedleTokenKeyFor).ToList();
        Assert.Equal(3, tokens.Distinct(StringComparer.Ordinal).Count());
    }

    // ── The real tuner drives the real rows ──────────────────────────────────

    /// <summary>
    ///     End-to-end through the REAL <see cref="GuidedTuningCheck" />: three
    ///     real frequencies on the low E produce three different row states,
    ///     each visually distinct. Nothing is set by hand — the rows are read
    ///     after pushing pitch into the machine the screen actually runs.
    /// </summary>
    [Fact]
    public void TheRowStates_ComeFromTheRealTuner_AndAreThreeDistinctLooks()
    {
        var vm = new GuidedTuningCheckViewModel(audio: null, FakeLocalization.English(), driveWithTimer: false);
        vm.Begin(StandardTuningArrangement());

        Assert.Equal(6, vm.Strings.Count);
        var low = vm.Strings[0];
        var check = vm.Check;
        Assert.NotNull(check);

        // 1) Nothing heard yet.
        Assert.False(low.HasReading);
        Assert.True(low.IsAwaitingPluck);
        Assert.False(low.IsReadingGood);
        Assert.False(low.IsReadingBad);

        // 2) Dead on the target the machine itself derived.
        var target = check!.Targets[0].FrequencyHz;
        check.Process(target, 0);
        Assert.True(low.HasReading);
        Assert.True(low.IsReadingGood);
        Assert.False(low.IsAwaitingPluck);
        Assert.False(low.IsReadingBad);

        // 3) A quarter-tone flat — far outside any sane tolerance.
        check.Process(target * Math.Pow(2, -50.0 / 1200.0), 100);
        Assert.True(low.HasReading);
        Assert.True(low.IsReadingBad);
        Assert.False(low.IsReadingGood);
        Assert.False(low.IsAwaitingPluck);
        Assert.True(low.Cents < -20, $"expected a clearly flat reading, got {low.Cents:0.0} cents");
    }

    /// <summary>The meter's band is the LIVE check's tolerance, carried onto
    /// every row — not <see cref="FireTunerScale.DefaultInTuneCents" /> read
    /// from a second place.</summary>
    [Fact]
    public void EveryRow_CarriesTheLiveChecksOwnTolerance()
    {
        var vm = new GuidedTuningCheckViewModel(audio: null, FakeLocalization.English(), driveWithTimer: false);
        vm.Begin(StandardTuningArrangement());

        Assert.NotEmpty(vm.Strings);
        Assert.Equal(vm.Check!.InTuneCents, vm.InTuneCents);
        Assert.All(vm.Strings, r => Assert.Equal(vm.Check!.InTuneCents, r.InTuneCents));
    }

    /// <summary>The verified chip counts CONFIRMED rows and nothing else — a
    /// skipped string is not a verified one.</summary>
    [Fact]
    public void TheVerifiedChip_CountsOnlyConfirmedStrings()
    {
        var vm = new GuidedTuningCheckViewModel(audio: null, FakeLocalization.English(), driveWithTimer: false);
        vm.Begin(StandardTuningArrangement());

        Assert.Equal(0, vm.VerifiedCount);
        Assert.False(vm.AllVerified);

        vm.SkipCurrentCommand.Execute(null);
        Assert.Equal(0, vm.VerifiedCount);

        // Hold the (now current) second string in tune long enough to confirm it.
        var check = vm.Check!;
        var target = check.Targets[check.CurrentStringIndex].FrequencyHz;
        for (var t = 0; t < 4000 && vm.VerifiedCount == 0; t += 100)
            check.Process(target, t);

        Assert.Equal(1, vm.VerifiedCount);
        Assert.Equal(6, vm.StringCount);
        Assert.False(vm.AllVerified);
    }

    /// <summary>A restart un-hears every string. Keeping the last needle would
    /// have the meter claiming a reading the restarted check has not taken.</summary>
    [Fact]
    public void Restarting_PutsEveryRowBackToNotYetPlucked()
    {
        var vm = new GuidedTuningCheckViewModel(audio: null, FakeLocalization.English(), driveWithTimer: false);
        vm.Begin(StandardTuningArrangement());
        vm.Check!.Process(vm.Check.Targets[0].FrequencyHz * 1.01, 0);
        Assert.True(vm.Strings[0].HasReading);

        vm.RestartCommand.Execute(null);

        Assert.All(vm.Strings, r =>
        {
            Assert.False(r.HasReading);
            Assert.True(r.IsAwaitingPluck);
        });
    }

    // ── The key hints ────────────────────────────────────────────────────────

    /// <summary>
    ///     Every PRINTED hint resolves to a real action, and every action is
    ///     handled by someone. A printed shortcut that does nothing is worse
    ///     than none, and the failure is silent from both ends.
    /// </summary>
    [Fact]
    public void EveryPrintedKeyHint_ResolvesToARealAction()
    {
        Assert.True(GetReadyKeys.Hints.Count >= 3,
            "the hint list is empty or truncated — this guard would pass vacuously");

        foreach (var hint in GetReadyKeys.Hints)
        {
            Assert.NotEqual(GetReadyKeyAction.None, hint.Action);
            Assert.Equal(hint.Action, GetReadyKeys.Resolve(hint.Key, textInputFocused: false));
            Assert.False(string.IsNullOrWhiteSpace(hint.LabelKey));
        }

        Assert.Equal(GetReadyKeys.Hints.Count,
            GetReadyKeys.Hints.Select(h => h.Action).Distinct().Count());
    }

    /// <summary>The two the SCREEN claims have real commands on the view model;
    /// the one it does not claim is Escape, which the shell's single Escape
    /// chain owns. Both halves matter — claiming Escape here would navigate
    /// back twice.</summary>
    [Fact]
    public void TheClaimedActions_HaveRealCommands_AndEscapeIsLeftToTheShell()
    {
        var claimed = GetReadyKeys.Hints.Where(h => GetReadyKeys.IsClaimedByTheScreen(h.Action)).ToList();
        Assert.Equal(2, claimed.Count);

        var vmType = typeof(PlayModeViewModel);
        Assert.NotNull(vmType.GetProperty("ConfirmTuningCommand"));
        Assert.NotNull(vmType.GetProperty("OpenTunerCommand"));

        var back = GetReadyKeys.Hints.Single(h => h.Action == GetReadyKeyAction.Back);
        Assert.False(GetReadyKeys.IsClaimedByTheScreen(back.Action));

        // The shell's Escape chain really does walk Play Mode back — that is
        // what makes "ESC BACK" true.
        var shell = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            "ViewModels", "Shell", "MainWindowViewModel.cs")));
        Assert.Contains("IsPlayModeVisible", shell);
        Assert.Contains("PlayMode.NavigateBack();", shell);
    }

    /// <summary>Typing stands the whole set down — a bare "T" has to stay
    /// typeable wherever typing is the point.</summary>
    [Fact]
    public void WhileTyping_NoHintKeyIsClaimed() =>
        Assert.All(GetReadyKeys.Hints,
            h => Assert.Equal(GetReadyKeyAction.None,
                GetReadyKeys.Resolve(h.Key, textInputFocused: true)));

    /// <summary>The strip that is DRAWN is the list that is HANDLED — same
    /// keys, same order. Two sources would drift silently.</summary>
    [Fact]
    public void TheHintStripInTheMarkup_IsExactlyTheHintList()
    {
        var strip = Section(ScreenSetMarkup(), "GetReadyKeyHints", "</StackPanel>");
        var printed = Regex.Matches(strip, @"localization:Loc (?<key>PlayMode_GetReady_Hint_\w+)")
            .Select(m => m.Groups["key"].Value).ToList();

        Assert.Equal(GetReadyKeys.Hints.Select(h => h.LabelKey).ToList(), printed);
    }

    /// <summary>…and each printed key exists in BOTH resx files, or the strip
    /// renders as the key name (or blank) in one language.</summary>
    [Fact]
    public void EveryHintKey_IsInBothResx()
    {
        var en = UiSourceTree.ReadAllText(ResxPath("Resources.resx"));
        var de = UiSourceTree.ReadAllText(ResxPath("Resources.de-DE.resx"));

        Assert.All(GetReadyKeys.Hints, h =>
        {
            Assert.Contains($"name=\"{h.LabelKey}\"", en);
            Assert.Contains($"name=\"{h.LabelKey}\"", de);
        });
    }

    // ── The measured stem-separation cost ────────────────────────────────────

    /// <summary>No measurement ⇒ NO estimate. Never a zero, never a guess —
    /// the screen must hide the figure instead.</summary>
    [Fact]
    public void WithNoMeasurement_ThereIsNoEstimate()
    {
        Assert.Null(StemSeparationCost.EstimateSeconds(0, 240));
        Assert.Null(StemSeparationCost.EstimateSeconds(double.NaN, 240));
        Assert.Null(StemSeparationCost.EstimateSeconds(2.0, 0));
    }

    [Fact]
    public void AMeasuredRate_EstimatesProportionally()
    {
        Assert.Equal(120.0, StemSeparationCost.EstimateSeconds(0.5, 240)!.Value, 6);
        Assert.Equal(0.5, StemSeparationCost.RateFrom(120, 240)!.Value, 6);
    }

    /// <summary>A run that says nothing usable is DISCARDED, not averaged in —
    /// a cache hit returning instantly must never teach the app that separation
    /// is free, and one thermally-throttled run must not poison every later
    /// estimate.</summary>
    [Theory]
    [InlineData(0.0, 240.0)]     // instant — a cache hit, not a separation
    [InlineData(120.0, 4.0)]     // audio too short to measure anything
    [InlineData(100_000.0, 240.0)] // absurd — something else was happening
    public void AnUnusableRun_IsNotRecorded(double elapsed, double audio) =>
        Assert.Null(StemSeparationCost.RateFrom(elapsed, audio));

    [Fact]
    public void TheFirstMeasurement_IsTakenWhole_AndLaterOnesAreBlended()
    {
        Assert.Equal(2.0, StemSeparationCost.Blend(0, 2.0), 6);
        Assert.Equal(1.5, StemSeparationCost.Blend(1.0, 2.0), 6);
    }

    // ── Markup structure the bounds checks cannot see ────────────────────────

    /// <summary>
    ///     The path list's percentage column and the tuner's state column are
    ///     FIXED width. An <c>Auto</c> column is individually correct on every
    ///     row and a staircase down the list — the failure this rule exists for
    ///     is invisible to a per-element bounds check.
    /// </summary>
    [Fact]
    public void TheValueColumns_AreFixedWidth_NeverAuto()
    {
        var markup = ScreenSetMarkup();

        var paths = Section(markup, "GetReadyPathList", "</ListBox>");
        Assert.Contains("<ColumnDefinition Width=\"96\" />", paths);
        Assert.DoesNotContain("Width=\"Auto\"", paths);

        // Caption row and row template must declare the SAME columns, or the
        // needles sit on one scale and the captions on another.
        var captions = Section(markup, "GetReadyTunerCaptions", "</Grid>");
        var rows = Section(markup, "GetReadyTunerStrings", "</ItemsControl>");
        Assert.Contains("ColumnDefinitions=\"46,14,*,14,80\"", captions);
        Assert.Contains("ColumnDefinitions=\"46,14,*,14,80\"", rows);
    }

    /// <summary>Every scroll region on the screen opens at its TOP and refuses
    /// to scroll on focus — the rule the Progress hub settled, and the reason a
    /// panel can never be pulled off the top of a rail.</summary>
    [Fact]
    public void EveryScrollRegion_OpensAtItsTop()
    {
        var screen = GetReadyScreenMarkup();
        var scrollers = Regex.Matches(screen, @"<ScrollViewer\b[^>]*>", RegexOptions.Singleline)
            .Select(m => m.Value).ToList();

        Assert.True(scrollers.Count >= 2,
            $"expected the rail and the approximation list to scroll; found {scrollers.Count}");
        Assert.All(scrollers, s =>
            Assert.Contains("BringIntoViewOnFocusChange=\"False\"", s));
        Assert.DoesNotContain("Offset=", screen);
    }

    /// <summary>The screen paints NO ground over the animated backdrop, and one
    /// translucent wash is the only layer under its panels.</summary>
    [Fact]
    public void TheScreen_LetsTheBackdropThrough()
    {
        var screen = GetReadyScreenMarkup();
        Assert.Contains("Classes=\"FireWash UpperLeft\"", screen);
        Assert.DoesNotContain("FireGroundScreen", screen);
        Assert.DoesNotContain("Classes=\"FireGround", screen);
    }

    /// <summary>
    ///     §3.8 — every configuration-changing action states its consequence in
    ///     one sentence beside it. The four the brief names: the tones toggle,
    ///     the per-song override, Skip check and Skip setup.
    /// </summary>
    [Fact]
    public void EveryConfigChangingAction_StatesItsConsequence()
    {
        var screen = GetReadyScreenMarkup();

        // Tones toggle: a sentence for each state, so it says what turning it
        // OFF does too.
        Assert.Contains("PlayMode_GetReady_Tones_Explain", screen);
        Assert.Contains("PlayMode_GetReady_Tones_Off", screen);
        Assert.Contains("PlayMode_GetReady_Tones_Override_Consequence", screen);
        Assert.Contains("PlayMode_GetReady_Tuner_SkipCheck_Consequence", screen);
        Assert.Contains("PlayMode_GetReady_SkipSetup_Consequence", screen);
        Assert.Contains("PlayMode_GetReady_SaveDefault_Consequence", screen);

        // …and each of those is a real sentence in BOTH languages.
        var en = UiSourceTree.ReadAllText(ResxPath("Resources.resx"));
        var de = UiSourceTree.ReadAllText(ResxPath("Resources.de-DE.resx"));
        foreach (var key in new[]
                 {
                     "PlayMode_GetReady_Tones_Explain", "PlayMode_GetReady_Tones_Off",
                     "PlayMode_GetReady_Tones_Override_Consequence",
                     "PlayMode_GetReady_Tuner_SkipCheck_Consequence",
                     "PlayMode_GetReady_SkipSetup_Consequence",
                     "PlayMode_GetReady_SaveDefault_Consequence"
                 })
        {
            Assert.Contains($"name=\"{key}\"", en);
            Assert.Contains($"name=\"{key}\"", de);
        }
    }

    /// <summary>"2.3 s on screen" is DERIVED. A literal would survive every
    /// other gate here and be wrong the moment the highway's draw distance or
    /// the scroll speed moved.</summary>
    [Fact]
    public void TheSecondsOnScreenFigure_IsDerivedFromTheHighwayGeometry()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            "ViewModels", "PlayMode", "PlayModeViewModel.GetReady.cs")));
        Assert.Contains("HighwaySpeedPolicy.HorizonMs", source);
        Assert.Contains("HighwayDrawDistanceZ", source);

        // The mockup's own figure falls out of the defaults: 35 × 1.3 × 50.
        Assert.Equal(2275f,
            DotCharter.Core.Graphics.HighwaySpeedPolicy.HorizonMs(1.3f, 35f), 1);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Arrangement StandardTuningArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead", StringCount = 6 };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        return arrangement;
    }

    /// <summary>PlayModeView.axaml with its lifted screen views inlined (#294).</summary>
    private static string ScreenSetMarkup() => PlayModeMarkup.Text();

    /// <summary>Just the Get Ready screen's own markup — the file holds ~19
    /// screens and a lint over the whole thing proves nothing about this
    /// one.</summary>
    private static string GetReadyScreenMarkup()
    {
        var markup = ScreenSetMarkup();
        var start = markup.IndexOf("x:Name=\"TuningCheckScreenRoot\"", StringComparison.Ordinal);
        Assert.True(start > 0, "TuningCheckScreenRoot is gone from the Play Mode markup");
        var end = markup.IndexOf("x:Name=\"CountdownScreenRoot\"", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the screen after Get Ready");
        var section = markup[start..end];
        Assert.True(section.Length > 4000,
            $"the Get Ready section is only {section.Length} chars — this lint would pass vacuously");
        return section;
    }

    private static string Section(string markup, string anchorName, string closing)
    {
        var start = markup.IndexOf($"x:Name=\"{anchorName}\"", StringComparison.Ordinal);
        Assert.True(start > 0, $"'{anchorName}' is not in the Play Mode markup");
        var end = markup.IndexOf(closing, start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find '{closing}' after '{anchorName}'");
        return markup[start..(end + closing.Length)];
    }

    private static string ResxPath(string file) =>
        Path.Combine(TestPaths.RepoRoot(), "DotCharter.Localization", file);
}
