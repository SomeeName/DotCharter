namespace DotCharter.Services.State;

/// <summary>
/// Sets one rule set of the song's chart-synth playing style — the
///     guitar's or the bass's. The style lives on the project and is saved with it,
///     so a change is an undoable edit that marks the project dirty. The target is
///     updated IN PLACE: the restorer and the feeder hold the same instance.
/// </summary>
public sealed class SetSynthPlayStyleAction(ChartSynthPlayRules target, ChartSynthPlayRules newRules) : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    private readonly ChartSynthPlayRules _old = target.Clone();
    private readonly ChartSynthPlayRules _new = newRules.Clone();

    public bool HasEffect => !_old.SameAs(_new);

    public void Execute() => target.CopyFrom(_new);

    public void Undo() => target.CopyFrom(_old);
}
