using System.Diagnostics;

namespace DotCharter.Tone.Audio.Engine;

/// <summary>
///     What the "Process priority" switch in Settings ▸ Audio actually means.
///     <para>
///         <b>It meant nothing at all until now.</b>
///         <c>RigConfig.EnableHighProcessPriority</c> shipped ON by default, was
///         loaded by <c>AudioSettingsViewModel</c>, saved by it, and even forced a
///         full engine restart when toggled — and no line anywhere in the app ever
///         read it to set a priority. Its row promises "Helps avoid glitches /
///         dropouts when running a VST effect. Turn off if the app makes the rest
///         of your system laggy", which is a capability the app could not honour
///         and yet showed as live.
///     </para>
///     <para>
///         <b>Why <see cref="ProcessPriorityClass.AboveNormal" /> and not
///         <c>High</c>.</b> JUCE already runs the audio callback on a
///         time-critical THREAD, so the process class only has to keep our
///         ordinary threads — the UI, the decoders, the render thread that feeds
///         the callback — ahead of background work. <c>High</c> outranks most of
///         the desktop's own responsiveness machinery, which is the "makes the
///         rest of your system laggy" outcome the row offers as the reason to turn
///         the switch OFF, not as what it does when ON.
///     </para>
///     Pure: the current class is passed in, so the decision is unit-tested
///     without touching the test host's own priority.
/// </summary>
public static class ProcessPriorityPolicy
{
    /// <summary>The class the app runs at while the switch is on.</summary>
    public const ProcessPriorityClass Raised = ProcessPriorityClass.AboveNormal;

    /// <summary>…and with it off. Not "whatever it was": the switch has to be
    /// reversible inside one session, and the only defined resting value is the
    /// one Windows starts a desktop app at.</summary>
    public const ProcessPriorityClass Resting = ProcessPriorityClass.Normal;

    public static ProcessPriorityClass DesiredFor(bool enableHigh) => enableHigh ? Raised : Resting;

    /// <summary>
    ///     True when the process should actually be written to. Writing an
    ///     unchanged class is a no-op that still costs a kernel call on every
    ///     settings apply and every watchdog recovery rebuild, and a process the
    ///     user (or a launcher) parked at <c>RealTime</c> or <c>Idle</c>
    ///     deliberately is left alone — neither is a value this switch produces,
    ///     so overwriting it would be this policy taking a decision it was never
    ///     given.
    /// </summary>
    public static bool ShouldApply(ProcessPriorityClass current, ProcessPriorityClass desired)
    {
        if (current == desired) return false;
        return current is Raised or Resting;
    }
}
