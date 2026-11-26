namespace DotCharter.Services.State;

/// <summary>
///     Replaces an arrangement's unchecked audio-check spots (D110), the "Checked" and "All checked" marks. Undo puts
///     the spots back exactly as they were; nothing else in the arrangement changes.
/// </summary>
public sealed class SetAudioCheckSpotsAction(Arrangement arrangement, List<AudioCheckSpot> spots) : IChartAction, ILinkPolicyAction
{
    private readonly List<AudioCheckSpot> _old = [.. arrangement.AudioCheckSpots];

    /// <summary>The spots are review state, outside the linked measure content.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public bool HasEffect => !_old.SequenceEqual(spots);

    public void Execute() => Set(spots);

    public void Undo() => Set(_old);

    private void Set(List<AudioCheckSpot> value)
    {
        arrangement.AudioCheckSpots = [.. value];
        arrangement.MarkEdited();
    }
}
