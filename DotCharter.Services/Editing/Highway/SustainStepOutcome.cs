namespace DotCharter.Services.Editing.Highway;

/// <summary>What stopped a sustain step short of the full grid step.</summary>
public enum SustainStepLimit
{
    /// <summary>The step landed in full.</summary>
    None,

    /// <summary>The sustain stopped at zero.</summary>
    Zero,

    /// <summary>The sustain stopped before the next sound (its spacing gap).</summary>
    NextSound,

    /// <summary>The sustain stopped at the end of the audio.</summary>
    AudioEnd,

    /// <summary>A linked sustain is the gap to the next note and never steps.</summary>
    Linked
}

/// <summary>
///     What one <c>]</c>/<c>[</c> sustain step does to one selected sound, worked out before it writes:
///     whether its sustain changes (on a chord: on any string), and what cut the step short (on a chord:
///     the first string that was cut). <see cref="IHighwayEditingService.PreviewSelectedNotesSustain" />
///     and <see cref="IHighwayEditingService.AdjustSelectedNotesSustain" /> share the one rule.
/// </summary>
public sealed record SustainStepOutcome(ITimedElement Sound, bool Changes, SustainStepLimit Limit);
