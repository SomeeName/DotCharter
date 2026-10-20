namespace DotCharter.UI.Editing.Tracks;

/// <summary>
///     Decides when the 2D renderer must republish its
///     <see cref="TimelineGutterLayout" /> to the gutter beside it.
///     <para>
///         <b>Why this is not just <see cref="TimelineGutterLayout.SameShapeAs" />.</b>
///         The gutter prints two different things: a lane GEOMETRY (which the
///         layout describes) and a lane NAME per string (which comes from the
///         arrangement's tuning). Gating the publish on the shape alone is
///         correct for the geometry and silently wrong for the names — swapping a
///         six-string E-standard chart for a six-string Drop C# one moves no lane,
///         so the event never fired, the gutter never invalidated, and it went on
///         painting the PREVIOUS song's string names. That is exactly what a
///         freshly loaded CDLC looked like: a Drop C# chart under an
///         "E A D G B E" gutter, inherited from the default project the app
///         started on.
///     </para>
///     <para>
///         So the gate carries BOTH halves. The comparison is exact (no hashing,
///         no stamp string) and allocation-free once warm: the cached vectors are
///         reused whenever their length is unchanged, because
///         <c>ChartRenderer</c> calls this once per compositor tick.
///     </para>
/// </summary>
public sealed class GutterPublishGate
{
    private bool _published;
    private TimelineGutterLayout _lastLayout;

    private ArrangementType _lastType;
    private int _lastStringCount;
    private int _lastCapo;
    private string? _lastTuningType;
    private int[] _lastSteps = [];
    private string?[] _lastNames = [];

    /// <summary>
    ///     Whether this frame's layout/tuning pair differs from the last one that
    ///     was published. STATEFUL — call exactly once per frame, and only when
    ///     the caller intends to publish on a true result.
    /// </summary>
    public bool ShouldPublish(TimelineGutterLayout layout, Arrangement? arrangement)
    {
        // Not short-circuited: the tuning snapshot must be taken on every frame,
        // or a change that arrives in the same frame as a shape change would be
        // swallowed by the next comparison.
        var tuningChanged = TuningChanged(arrangement);
        var shapeChanged = !_published || !_lastLayout.SameShapeAs(layout);

        _published = true;
        _lastLayout = layout;
        return shapeChanged || tuningChanged;
    }

    /// <summary>
    ///     Everything the gutter can PRINT about a string, compared exactly and
    ///     cached. <c>TuningPitches</c> is in here as well as the note letters
    ///     because that is what <c>TuningCheckTargets</c> reads first — an
    ///     arrangement whose offsets changed while its letters did not still
    ///     renames every lane.
    /// </summary>
    private bool TuningChanged(Arrangement? arrangement)
    {
        var changed = false;

        var type = arrangement?.ArrangementType ?? default;
        var stringCount = arrangement?.StringCount ?? 0;
        var capo = arrangement?.Capo ?? 0;
        if (type != _lastType || stringCount != _lastStringCount || capo != _lastCapo) changed = true;
        _lastType = type;
        _lastStringCount = stringCount;
        _lastCapo = capo;

        var tuningType = arrangement?.TuningType;
        if (!string.Equals(tuningType, _lastTuningType, StringComparison.Ordinal)) changed = true;
        _lastTuningType = tuningType;

        var pitches = arrangement?.TuningPitches;
        var pitchCount = pitches?.Count ?? 0;
        if (_lastSteps.Length != pitchCount)
        {
            _lastSteps = new int[pitchCount];
            changed = true;
        }

        for (var i = 0; i < pitchCount; i++)
        {
            var step = pitches![i].Step;
            if (_lastSteps[i] != step) changed = true;
            _lastSteps[i] = step;
        }

        var names = arrangement?.Tuning;
        var nameCount = names?.Length ?? 0;
        if (_lastNames.Length != nameCount)
        {
            _lastNames = new string?[nameCount];
            changed = true;
        }

        for (var i = 0; i < nameCount; i++)
        {
            var name = names![i];
            if (!string.Equals(_lastNames[i], name, StringComparison.Ordinal)) changed = true;
            _lastNames[i] = name;
        }

        return changed;
    }
}
