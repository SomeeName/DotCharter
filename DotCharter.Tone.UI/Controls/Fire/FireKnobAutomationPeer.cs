using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// The <see cref="FireKnob"/>'s accessibility face: a
/// <see cref="AutomationControlType.Slider"/> over
/// <see cref="IRangeValueProvider"/>.
///
/// <para><b>A name alone is not enough for a knob.</b> Without a peer a
/// custom-drawn <c>Control</c> reports as <c>None</c>, so a screen reader could
/// read the caption and never the VALUE — and an automation client could not
/// set it at all, which on this screen means the whole rig is unreachable
/// without a pointer. The range pattern is what makes "Drive, 6.4 of 10"
/// speakable and settable.</para>
///
/// <para><b>Every write goes through <see cref="FireKnob.Commit"/></b> — the
/// same clamp + step snap the drag and the keyboard use. An automation client
/// writing the property raw is exactly how a stepped parameter ends up between
/// two grid points and the DSP is handed a value it will not run.</para>
///
/// <para>The steps it advertises are the KEYBOARD's steps
/// (<see cref="FireKnobGeometry.ArrowStep"/> /
/// <see cref="FireKnobGeometry.PageStep"/>), not a second ladder — a client
/// that increments by <c>SmallChange</c> must land where Up would have put
/// it.</para>
/// </summary>
internal sealed class FireKnobAutomationPeer : ControlAutomationPeer, IRangeValueProvider
{
    public FireKnobAutomationPeer(FireKnob owner) : base(owner)
    {
    }

    private new FireKnob Owner => (FireKnob)base.Owner;

    public bool IsReadOnly => !Owner.IsEffectivelyEnabled;

    public double Minimum => Owner.Minimum;

    public double Maximum => Owner.Maximum;

    public double Value => Owner.Value;

    public double SmallChange => FireKnobGeometry.ArrowStep(Owner.Minimum, Owner.Maximum, Owner.Step);

    public double LargeChange => FireKnobGeometry.PageStep(Owner.Minimum, Owner.Maximum, Owner.Step);

    public void SetValue(double value) => Owner.Commit(value);

    /// <summary>Raised by the control when its value moved, so a listening
    /// client hears an arrow press. Only reached when a peer already exists.</summary>
    internal void RaiseValueChanged(double oldValue, double newValue)
        => RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override string GetClassNameCore() => nameof(FireKnob);
}
