using System.Collections.ObjectModel;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Vocals;

public class LyricPhrase : ITimedElement
{
    public FractionalPosition EndPosition { get; set; } = FractionalPosition.Zero;
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;
    public ObservableCollection<LyricEvent> Lyrics { get; set; } = [];

    /// <summary>Start time — windowing by <see cref="TimelineMath.GetVisibleRange{T}"/>
    /// keys on phrase start; consumers cull per phrase by [start, end].</summary>
    public double GetTimeMs(IList<BeatMarker> beats) => Position.GetPositionMs(beats);
}
