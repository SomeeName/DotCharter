namespace DotCharter.Rendering.Data;

/// <summary>
///     The editor chip's text for the frame: rebuilt only when the state at the playhead, the labels or the
///     editor's open flag change, so an ordinary frame allocates nothing. Empty while the chip is hidden
///     (see <see cref="ShowlightChipLabels.ShouldShow" />).
/// </summary>
public sealed class ShowlightChipState
{
    private ShowlightChipLabels? _labels;
    private int _fog = int.MinValue, _beam = int.MinValue, _laser = int.MinValue;
    private bool _editorOpen;
    private string _text = string.Empty;

    /// <summary>No chip this frame (Play Mode hands no labels).</summary>
    public string Clear()
    {
        _labels = null;
        return _text = string.Empty;
    }

    public string Update(ShowlightChipLabels labels, bool editorOpen, int? fogNote, int? beamNote, int? laserNote)
    {
        if (!ShowlightChipLabels.ShouldShow(editorOpen, fogNote, beamNote, laserNote)) return Clear();

        var fog = fogNote ?? -1;
        var beam = beamNote ?? -1;
        var laser = laserNote ?? -1;
        if (ReferenceEquals(labels, _labels) && editorOpen == _editorOpen && fog == _fog && beam == _beam && laser == _laser)
            return _text;

        _labels = labels;
        _editorOpen = editorOpen;
        _fog = fog;
        _beam = beam;
        _laser = laser;
        return _text = labels.Compose(fogNote, beamNote, laserNote);
    }
}
