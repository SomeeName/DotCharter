using DotCharter.Services.Editing;

namespace DotCharter.Services.State;

/// <summary>
///     Appends a vocal track AND makes it the active one; undo removes it and
///     restores the previous pick. A lyrics import into a project without any vocal
///     track puts this in the same composite as the phrase replacement, so one undo
///     takes the track back out and a failed import leaves nothing behind.
/// </summary>
public sealed class AddActiveVocalTrackAction(IChartEditorSurface editor, IList<VocalTrack> tracks, VocalTrack track)
    : IChartAction, ILinkPolicyAction
{
    private readonly VocalTrack? _previousActive = editor.ActiveVocalTrack;

    /// <summary>Linked measures: cannot change linked content in beats.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public void Execute()
    {
        tracks.Add(track);
        editor.ActiveVocalTrack = track;
    }

    public void Undo()
    {
        tracks.Remove(track);
        editor.ActiveVocalTrack = _previousActive;
    }
}
