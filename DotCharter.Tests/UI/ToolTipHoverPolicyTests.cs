using Avalonia.Controls;
using DotCharter.UI.Platform;

namespace DotCharter.Tests.UI;

/// <summary>
///     The guard that stops Avalonia opening a tooltip WITHOUT ITS DELAY once
///     one has already been shown (see
///     <see cref="ToolTipHoverPolicy" />). Owner report: "hover long enough that
///     the tooltip showed once, then fast move left and right over the toolbar
///     and it stutters the whole 2D timeline and 3D view."
///     <para>
///         Default gate: no window, no platform, no dispatcher — the guard is a
///         property write driven by a static property-changed observable, so it
///         is testable exactly where it lives. The REAL-markup half (every
///         tipped control in the editor chrome) is
///         <c>Perf/ToolTipChurnUiPerfSmokeTests</c>, which needs a shown window.
///     </para>
/// </summary>
/// <remarks>
///     <b>It joins <c>LocalizationSingleton</c> and it UNINSTALLS the guard,
///     and both halves are load-bearing.</b>
///     <c>ToolTipHoverPolicy.Apply()</c> registers a handler on
///     <c>ToolTip.TipProperty.Changed</c> — a process-global Avalonia
///     observable — and nothing removed it, so for the rest of the run it fired
///     for every control in the process on whatever thread raised the change.
///     <c>LocalizationService.SetConfiguredCulture</c> raises
///     <c>PropertyChanged("Item[]")</c> from its caller's xunit worker; a live
///     <c>{localization:Loc}</c> tooltip binding re-evaluates and writes
///     <c>ToolTip.Tip</c>; the guard's <c>SetBetweenShowDelay</c> then hits
///     <c>AvaloniaObject.VerifyAccess()</c> and throws "The calling thread
///     cannot access this object" — out of <c>SetConfiguredCulture</c>, i.e.
///     inside whichever culture-writing case was unlucky. Measured on
///     2026-09-04: one default-gate run in three lost all 19 cases of
///     <c>OnboardingWizardViewModelTests</c> that way, and the same collision
///     the other way round is what took 15 localization cases down.
///     <para>
///         Disposing bounds the leak to this class; the collection makes the
///         remaining window unreachable, because every culture writer — direct
///         or through product code, which <c>CultureCollectionLintTests</c> now
///         checks for — is serialized against it.
///     </para>
/// </remarks>
[Collection("LocalizationSingleton")]
public sealed class ToolTipHoverPolicyTests : IDisposable
{
    /// <summary>Takes the process-global class handler back off.</summary>
    public void Dispose() => ToolTipHoverPolicy.ResetForTesting();

    // ── the premise: Avalonia's default is the one that chains ──────────────

    /// <summary>
    ///     If this ever stops holding, the guard is unnecessary rather than
    ///     wrong — and we want to be told, not to keep shipping dead code.
    /// </summary>
    [Fact]
    public void AvaloniasOwnDefault_IsTheValueThatAllowsAnInstantChainedOpen()
    {
        var stock = ToolTip.BetweenShowDelayProperty.GetDefaultValue(typeof(Button));
        Assert.Equal(100, stock);
        Assert.True(ToolTipHoverPolicy.AllowsInstantChainedOpen(stock));
    }

    /// <summary>
    ///     <b>Why the constant must stay NEGATIVE.</b> This models
    ///     <c>ToolTipService.OnTipControlChanged</c> verbatim:
    ///     <code>
    ///     betweenShowDelay &gt;= 0 &amp;&amp; (closedPreviousTip || msSinceLastClose &lt;= betweenShowDelay)
    ///     </code>
    ///     A value of <c>0</c> looks like "no chaining window" and is the obvious
    ///     simplification — but <c>closedPreviousTip</c> short-circuits the time
    ///     comparison, so 0 chains on EVERY sweep. Only a negative value makes
    ///     the branch unreachable.
    /// </summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 5)]
    [InlineData(true, 5000)]
    [InlineData(false, 0)]
    [InlineData(false, 5)]
    [InlineData(false, 5000)]
    public void TheConfiguredValue_MakesAvaloniasInstantOpenBranchUnreachable(
        bool closedPreviousTip, double msSinceLastClose)
    {
        Assert.False(OpensWithNoDelay(ToolTipHoverPolicy.ChainedOpenDisabled, closedPreviousTip, msSinceLastClose));

        // …and 0 would not have been enough, which is the trap this pins.
        Assert.True(OpensWithNoDelay(0, closedPreviousTip: true, msSinceLastClose));
        return;

        static bool OpensWithNoDelay(int betweenShowDelay, bool closedPreviousTip, double msSinceLastClose)
            => betweenShowDelay >= 0 && (closedPreviousTip || msSinceLastClose <= betweenShowDelay);
    }

    // ── the guard ───────────────────────────────────────────────────────────

    [Fact]
    public void AControlThatGainsATip_IsOptedOutOfTheInstantChainedOpen()
    {
        ToolTipHoverPolicy.Apply();

        var button = new Button();
        Assert.True(ToolTipHoverPolicy.AllowsInstantChainedOpen(ToolTip.GetBetweenShowDelay(button)));

        ToolTip.SetTip(button, "Play");

        Assert.False(ToolTipHoverPolicy.AllowsInstantChainedOpen(ToolTip.GetBetweenShowDelay(button)));
    }

    /// <summary>
    ///     The guard keys on the same signal Avalonia's own service does
    ///     (<c>ToolTip.TipProperty</c>), so it must not care HOW the tip
    ///     arrived — markup, a style setter and a binding all land here.
    /// </summary>
    [Fact]
    public void ATipThatArrivesLater_IsGuardedToo()
    {
        ToolTipHoverPolicy.Apply();

        var border = new Border();
        ToolTip.SetTip(border, null);
        Assert.True(ToolTipHoverPolicy.AllowsInstantChainedOpen(ToolTip.GetBetweenShowDelay(border)));

        ToolTip.SetTip(border, new TextBlock { Text = "later" });
        Assert.False(ToolTipHoverPolicy.AllowsInstantChainedOpen(ToolTip.GetBetweenShowDelay(border)));
    }

    /// <summary>An author who set the delay themselves keeps it — the guard
    ///     only fills the DEFAULT in, which is also what makes it idempotent
    ///     per control.</summary>
    [Fact]
    public void AnExplicitAuthoredDelay_IsLeftAlone()
    {
        ToolTipHoverPolicy.Apply();

        var button = new Button();
        ToolTip.SetBetweenShowDelay(button, 250);
        ToolTip.SetTip(button, "Stop");

        Assert.Equal(250, ToolTip.GetBetweenShowDelay(button));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        ToolTipHoverPolicy.Apply();
        ToolTipHoverPolicy.Apply();
        ToolTipHoverPolicy.Apply();

        var button = new Button();
        ToolTip.SetTip(button, "Pause");
        Assert.Equal(ToolTipHoverPolicy.ChainedOpenDisabled, ToolTip.GetBetweenShowDelay(button));
    }

    // ── the A/B seam ────────────────────────────────────────────────────────

    /// <summary>
    ///     The owner verifies UI perf work by running the app twice, so the
    ///     guard has an off switch in the same family as
    ///     <c>DOTCHARTER_NO_SURFACE_PREWARM</c>. Only an exact <c>"1"</c> may
    ///     disable it — a stray or empty value silently shipping the stutter is
    ///     the failure mode worth pinning.
    /// </summary>
    [Theory]
    [InlineData("1", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData(" 1", false)]
    public void OnlyAnExplicitOne_TurnsTheGuardOff(string? value, bool disabled)
        => Assert.Equal(disabled, ToolTipHoverPolicy.IsDisabledBy(value));

    /// <summary>The seam is only real if <c>Apply</c> actually consults it.</summary>
    [Fact]
    public void Apply_ConsultsTheOffSwitch()
    {
        var source = SourceText.StripCommentLines(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "Platform", "ToolTipHoverPolicy.cs"))));
        var apply = source[source.IndexOf("public static void Apply()", StringComparison.Ordinal)..];
        var body = apply[..apply.IndexOf("private static void Guard", StringComparison.Ordinal)];

        Assert.Contains("IsDisabledBy(", body, StringComparison.Ordinal);
        Assert.Contains("DisableEnvVar", body, StringComparison.Ordinal);
        Assert.Equal("DOTCHARTER_NO_TOOLTIP_HOVER_GUARD", ToolTipHoverPolicy.DisableEnvVar);
    }

    // ── reachability ────────────────────────────────────────────────────────

    /// <summary>
    ///     <b>A policy nobody applies is a dead feature</b> (root <c>AGENTS.md</c>),
    ///     and this one is invisible when it is missing: tooltips still work,
    ///     they just chain again. It must be applied from <c>App.Initialize</c>
    ///     and BEFORE <c>AvaloniaXamlLoader.Load</c> — the subscription only
    ///     catches tips assigned after it is installed.
    ///     <para>
    ///         Comments are stripped FIRST. Written without that, this lint
    ///         passed against a call commented out one character at a time — a
    ///         commented call is exactly as inert as a <c>see cref</c>, and this
    ///         file's own break-test caught it.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheAppInstallsTheGuard_BeforeAnyMarkupIsParsed()
    {
        var source = SourceText.StripCommentLines(
            UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "App.axaml.cs"))));
        var body = source[source.IndexOf("public override void Initialize()", StringComparison.Ordinal)..];

        var apply = body.IndexOf("ToolTipHoverPolicy.Apply()", StringComparison.Ordinal);
        var load = body.IndexOf("AvaloniaXamlLoader.Load(this)", StringComparison.Ordinal);

        Assert.True(apply >= 0, "App.Initialize no longer installs ToolTipHoverPolicy — every tooltip chains again");
        Assert.True(load >= 0, "App.Initialize no longer loads App.axaml — this lint's anchor moved");
        Assert.True(apply < load,
            "ToolTipHoverPolicy.Apply() must run BEFORE the markup is parsed, or the tips already assigned " +
            "by App.axaml are never guarded");
    }
}
