namespace DotCharter.UI.Editing.Linking;

/// <summary>
///     Where one timeline last drew its LINKS-row labels (chip and count), in ms, so a pointer on
///     the row picks the group whose label it is over, even where <see cref="LinkRowLayout" />
///     moved that label into another group's span. The layer fills it once per label placement;
///     the arrays are reused, so only a longer span list allocates.
/// </summary>
public sealed class LinkRowLabels
{
    private IReadOnlyList<LinkSpan> _spans = [];
    private double[] _startMs = [];
    private double[] _endMs = [];

    /// <summary>The arrangement the labels were drawn for.</summary>
    public Arrangement? Arrangement { get; private set; }

    /// <summary>Stores the labels <see cref="LinkRowLayout.PlaceLabels" /> placed at <paramref name="zoom" /> (px per ms).</summary>
    public void Set(Arrangement? arrangement, IReadOnlyList<LinkSpan> spans, double zoom, float[] offsets, float[] chipWidths,
        float[] countWidths, bool[] showCount)
    {
        if (_startMs.Length < spans.Count)
        {
            var n = Math.Max(spans.Count, _startMs.Length * 2);
            _startMs = new double[n];
            _endMs = new double[n];
        }

        for (var i = 0; i < spans.Count; i++)
        {
            _startMs[i] = spans[i].StartMs + offsets[i] / zoom;
            _endMs[i] = _startMs[i] + LinkRowLayout.LabelWidth(chipWidths[i], countWidths[i], showCount[i]) / zoom;
        }

        _spans = spans;
        Arrangement = arrangement;
    }

    /// <summary>The span whose label covers <paramref name="timeMs" />, or null (labels never overlap).</summary>
    public LinkSpan? LabelAt(double timeMs)
    {
        for (var i = 0; i < _spans.Count; i++)
        {
            if (timeMs >= _startMs[i] && timeMs < _endMs[i]) return _spans[i];
        }

        return null;
    }
}
