namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
/// Tally of the minimal-diff work <see cref="DcRigApplier.Apply"/> performed.
/// <see cref="NoOp"/> is true when the live chain already matched the target
/// preset (zero native calls) — the common case on a re-apply after a seek /
/// pre-roll, so the coordinator can safely re-apply the active tone whenever the
/// playhead is re-evaluated. <see cref="FailedOps"/> counts native ops
/// (add / remove / move / param / bypass) that reported failure — the applier
/// degrades and continues, but a non-zero count means the live chain does NOT
/// fully match the preset (each failure is also logged).
/// </summary>
public readonly record struct DcRigApplyResult(
    int Added,
    int Removed,
    int Moved,
    int ParamsSet,
    int BypassSet,
    bool NoOp,
    int FailedOps = 0);
