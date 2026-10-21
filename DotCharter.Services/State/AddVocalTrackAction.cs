namespace DotCharter.Services.State;

/// <summary>
///     Appends a vocal track to the project's track list; undo removes it again.
///     An UltraStar duet import adds its second voice through this, inside the
///     same composite as the first voice's phrase replacement, so one undo takes
///     the whole import back. It leaves the active track alone.
/// </summary>
public sealed class AddVocalTrackAction(IList<VocalTrack> tracks, VocalTrack track) : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public void Execute() => tracks.Add(track);

    public void Undo() => tracks.Remove(track);
}
