using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The four shared Fire controls, constructed with NO Avalonia platform.
///     <para>
///         This was briefly moved out of the default gate on the theory that
///         constructing a <c>Control</c> here was killing the testhost mid-run.
///         It was not: the crash persisted with this class gone, and it
///         reproduces on a clean tree with none of this work in it. It is the
///         intermittent shared-session race this repo already documents, and
///         moving a real guard out of the gate to chase it would have cost
///         coverage for nothing.
///     </para>
///     <para>
///         Two rules are pinned here. <b>Repaint only when the rendered state
///         actually changed</b>: a knob is dragged at pointer rate and a meter
///         is fed from audio, so both route every invalidation through one
///         counted path. And <b>state changes are complete</b>: bypass is ONE
///         inherited attached property on the unit card, so a caller cannot dim
///         the knob and leave the meter lit.
///     </para>
/// </summary>
public sealed class FireControlStateTests
{
    // ── The repaint gates ────────────────────────────────────────────────────

    [Fact]
    public void TheKnob_DoesNotRepaintForAValueThatDidNotChange()
    {
        var knob = new FireKnob { Minimum = 0, Maximum = 1, Value = 0.5 };
        var before = knob.RepaintRequests;

        knob.Value = 0.5;
        Assert.Equal(before, knob.RepaintRequests);

        knob.Value = 0.6;
        Assert.Equal(before + 1, knob.RepaintRequests);
    }

    [Fact]
    public void TheKnob_RepaintsWhenItsIdentityChangesWithoutItsValue()
    {
        // The arc colour is render-only state that NO value change announces —
        // exactly the shape a compare-gated control forgets. (The focus ring is
        // the other one; `IsFocused` is read-only, so that half is pinned in
        // `FireControlsUiPerfSmokeTests`, which can really focus the knob.)
        var knob = new FireKnob();
        var before = knob.RepaintRequests;

        FireUnitState.SetCategory(knob, FireUnitCategory.Drive);
        Assert.True(knob.RepaintRequests > before, "re-identifying the knob did not repaint it");

        before = knob.RepaintRequests;
        FireUnitState.SetIsBypassed(knob, true);
        Assert.True(knob.RepaintRequests > before, "bypassing the knob did not repaint it");
    }

    [Fact]
    public void TheMeter_DoesNotRepaintForALevelInsideTheSameBar()
    {
        var meter = new FireLevelMeter { LevelDb = -20.0 };
        var lit = meter.LitSegments;
        var before = meter.RepaintRequests;

        // A level fed from audio moves constantly; only a bar lighting is
        // visible. Both of these land inside the same bar.
        meter.LevelDb = -19.95;
        meter.LevelDb = -19.9;

        Assert.Equal(lit, meter.LitSegments);
        Assert.Equal(before, meter.RepaintRequests);
    }

    [Fact]
    public void TheMeter_RepaintsWhenABarActuallyLights()
    {
        var meter = new FireLevelMeter { LevelDb = -30.0 };
        var before = meter.RepaintRequests;

        meter.LevelDb = -3.0;

        Assert.True(meter.LitSegments > 0);
        Assert.Equal(before + 1, meter.RepaintRequests);
    }

    [Fact]
    public void TheMeter_ReportsTheBandItIsShowing()
    {
        var meter = new FireLevelMeter
        {
            LevelDb = -12.0
        };
        Assert.Equal(FireLevelBand.Good, meter.Band);

        meter.LevelDb = -6.0;
        Assert.Equal(FireLevelBand.Hot, meter.Band);

        meter.LevelDb = -1.0;
        Assert.Equal(FireLevelBand.Over, meter.Band);
    }

    [Fact]
    public void TheSegmentedProgress_DoesNotRepaintInsideAStep()
    {
        var bar = new FireProgress { Segments = 5, Value = 0.4 };
        Assert.Equal(2, bar.FilledSegments);

        var before = bar.RepaintRequests;
        bar.Value = 0.45;
        Assert.Equal(2, bar.FilledSegments);
        Assert.Equal(before, bar.RepaintRequests);

        bar.Value = 0.6;
        Assert.Equal(3, bar.FilledSegments);
        Assert.Equal(before + 1, bar.RepaintRequests);
    }

    [Fact]
    public void TheContinuousProgress_RepaintsForEveryValueChange()
    {
        // It moves by PIXELS, so there is no step to gate on — the segmented
        // gate must not leak onto it and freeze it.
        var bar = new FireProgress { Value = 0.4 };
        var before = bar.RepaintRequests;
        bar.Value = 0.401;
        Assert.Equal(before + 1, bar.RepaintRequests);
    }

    // ── State changes are complete ───────────────────────────────────────────

    [Fact]
    public void BypassingTheCard_GreysEveryControlInsideIt_FromOneWrite()
    {
        // The failure this prevents: a bypassed unit that dims its card and
        // leaves its meter lit. There is exactly ONE switch, and it is on the
        // card.
        var knob = new FireKnob();
        var meter = new FireLevelMeter { LevelDb = -3.0 };
        var bar = new FireProgress { Value = 0.7 };
        var card = new StackPanel { Children = { knob, meter, bar } };

        Assert.False(FireUnitState.IsInactive(knob));
        Assert.False(FireUnitState.IsInactive(meter));
        Assert.Equal(1.0, FireUnitState.OpacityFor(bar));

        FireUnitState.SetIsBypassed(card, true);

        Assert.True(FireUnitState.IsInactive(knob), "the knob stayed lit inside a bypassed card");
        Assert.True(FireUnitState.IsInactive(meter), "the meter stayed lit inside a bypassed card");
        Assert.True(FireUnitState.IsInactive(bar), "the progress bar stayed lit inside a bypassed card");

        Assert.Equal(FireUnitState.BypassedOpacity, FireUnitState.OpacityFor(knob));
        Assert.Equal(FireUnitState.BypassedOpacity, knob.Opacity);
    }

    [Fact]
    public void DisablingAControl_IsDimmerStillAndAlsoCountsAsInactive()
    {
        var knob = new FireKnob { IsEnabled = false };
        Assert.True(FireUnitState.IsInactive(knob));
        Assert.Equal(FireUnitState.DisabledOpacity, FireUnitState.OpacityFor(knob));
    }

    [Fact]
    public void AUnitsIdentityComesFromItsCard_NotFromEachKnob()
    {
        // A knob that took a colour could be handed the wrong one. It takes a
        // CATEGORY, and the category is inherited.
        var knob = new FireKnob();
        var meter = new FireLevelMeter();
        var card = new StackPanel { Children = { knob, meter } };

        FireUnitState.SetCategory(card, FireUnitCategory.Drive);

        Assert.Equal(FireUnitCategory.Drive, FireUnitState.GetCategory(knob));
        Assert.Equal(FireUnitCategory.Drive, FireUnitState.GetCategory(meter));
        Assert.Equal("FireUnitDrive", FireUnitState.TokenKeyFor(FireUnitState.GetCategory(knob)));
    }

    [Fact]
    public void ALoneKnobCanStillDeclareItsOwnIdentity()
    {
        // The gear-list preview has no card above it; a local value beats the
        // inherited default.
        var knob = new FireKnob();
        FireUnitState.SetCategory(knob, FireUnitCategory.DelayReverb);
        Assert.Equal("FireUnitDelay", FireUnitState.TokenKeyFor(FireUnitState.GetCategory(knob)));
    }

    // ── The readout ──────────────────────────────────────────────────────────

    [Fact]
    public void TheCaptionIsUppercased_AndTheReadoutFallsBackToTheValue()
    {
        var knob = new FireKnob { Label = "Low cut", Value = 0.5 };
        Assert.Equal("LOW CUT", knob.CaptionText);
        Assert.Equal("0.5", knob.ReadoutText);

        knob.ValueText = "480 Hz";
        Assert.Equal("480 Hz", knob.ReadoutText);

        // Clearing it falls back again rather than going blank.
        knob.ValueText = null;
        Assert.Equal("0.5", knob.ReadoutText);
    }

    [Fact]
    public void TheKnobsFractionAndAngleFollowItsValue()
    {
        var knob = new FireKnob { Minimum = -12, Maximum = 12, Value = 0 };
        Assert.Equal(0.5, knob.Fraction, 9);
        Assert.Equal(0.0, knob.IndicatorAngleDegrees, 9);

        knob.Value = -12;
        Assert.Equal(-135.0, knob.IndicatorAngleDegrees, 9);

        knob.Value = 12;
        Assert.Equal(135.0, knob.IndicatorAngleDegrees, 9);
    }

    [Fact]
    public void ADegenerateRange_CollapsesToTheMinimumRatherThanThrowing()
    {
        var knob = new FireKnob { Minimum = 5, Maximum = 5, Value = 5 };
        Assert.Equal(0.0, knob.Fraction, 9);
        Assert.Equal(-135.0, knob.IndicatorAngleDegrees, 9);
    }

    // ── The keyboard, and the name a screen reader gets ──────────────────────

    [Fact]
    public void EveryKnobIsFocusable_AndKeyboardStepsCommitThroughTheSameSnap()
    {
        // The point of routing keys through Commit: a stepped parameter cannot
        // be walked off its grid by the keyboard any more than by a drag. 0.35
        // + 0.25 would be 0.60, which is not on a 0.25 grid.
        var knob = new FireKnob { Minimum = 0, Maximum = 1, Step = 0.25, Value = 0.35 };
        Assert.True(knob.Focusable, "a knob nobody can focus has no keyboard at all");

        Press(knob, Key.Up);
        Assert.Equal(0.5, knob.Value, 9);

        Press(knob, Key.PageDown);
        Assert.Equal(0.0, knob.Value, 9);
    }

    [Fact]
    public void TheResetKey_RevertsToTheDefault_TheKeyboardsHalfOfTheDoubleClick()
    {
        // Reverting a param to its catalogue default was a POINTER-ONLY gesture
        // (double-click) — the one value on the dial you cannot otherwise aim at.
        var knob = new FireKnob { Minimum = -12, Maximum = 12, DefaultValue = 0, Value = 7.5 };

        Press(knob, Key.Delete);
        Assert.Equal(0.0, knob.Value, 9);

        knob.Value = -9.0;
        Press(knob, Key.Back);
        Assert.Equal(0.0, knob.Value, 9);
    }

    [Fact]
    public void AKeyTheKnobDoesNotOwn_IsLeftUnhandledForTheScreenBehindIt()
    {
        // Tab must still leave the knob and Space must still reach the card —
        // a knob that swallowed them would trap the keyboard user on one dial.
        var knob = new FireKnob { Minimum = 0, Maximum = 1, Value = 0.5 };

        Assert.False(Press(knob, Key.Tab));
        Assert.False(Press(knob, Key.Space));
        Assert.Equal(0.5, knob.Value, 9);

        // …and neither is a MODIFIED arrow: Ctrl+Home belongs to the board.
        Assert.False(Press(knob, Key.Home, KeyModifiers.Control));
        Assert.Equal(0.5, knob.Value, 9);
    }

    [Fact]
    public void ADisabledKnob_IgnoresTheKeyboardTheWayItIgnoresTheDrag()
    {
        var knob = new FireKnob { Minimum = 0, Maximum = 1, Value = 0.5, IsEnabled = false };
        Assert.False(Press(knob, Key.End));
        Assert.Equal(0.5, knob.Value, 9);
    }

    [Fact]
    public void TheKnobNamesItself_FromItsCaption()
    {
        // Naming knobs at the CALL SITE is a second place to forget, and an
        // unnamed dial reaches every automation tool as an anonymous element.
        // The name keeps the caption's own case — "LOW CUT" is for the dial.
        var knob = new FireKnob();
        Assert.Null(AutomationProperties.GetName(knob));

        knob.Label = "Low cut";
        Assert.Equal("Low cut", AutomationProperties.GetName(knob));
        Assert.Equal("LOW CUT", knob.CaptionText);

        knob.Label = "  ";
        Assert.Null(AutomationProperties.GetName(knob));
    }

    /// <summary>A real <c>KeyDown</c> through the control's own handler, with no
    /// Avalonia platform. Returns whether the knob claimed the key.</summary>
    private static bool Press(FireKnob knob, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            KeyModifiers = modifiers,
            Source = knob
        };
        knob.RaiseEvent(args);
        return args.Handled;
    }
}
