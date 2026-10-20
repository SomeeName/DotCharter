namespace DotCharter.Services.State;

/// <summary>
/// Pins or unpins a chord template (<see cref="ChordTemplate.Pinned" />) — the
///     inspector's "Authored" box. In place, deliberately: the pin is a statement about this
///     template wherever it is used, not a grip, so there is nothing to resolve-or-create.
/// </summary>
public sealed class SetTemplatePinnedAction(ChordTemplate template, bool pinned) : IChartAction
{
    private readonly bool _was = template.Pinned;

    public bool HasEffect => _was != pinned;

    public void Execute() => template.Pinned = pinned;

    public void Undo() => template.Pinned = _was;
}
