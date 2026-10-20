namespace DotCharter.Services.State;

/// <summary>
///     Appends a vocal track to the project's track list; undo removes it again.
///     An UltraStar duet import adds its second voice through this, inside the
///     same composite as the first voice's phrase replacement, so one undo takes
///     the whole import back. It leaves the active track alone.
/// </summary>
public sealed class AddVocalTrackAction(IList<VocalTrack> tracks, VocalTrack track) : IChartAction
{
    public void Execute() => tracks.Add(track);

    public void Undo() => tracks.Remove(track);
}
