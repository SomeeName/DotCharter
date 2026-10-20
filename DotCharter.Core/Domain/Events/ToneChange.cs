using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Events;

public class ToneChange : ITimedElement
{
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;
    public string ToneName { get; set; } = "";

    public double GetTimeMs(IList<BeatMarker> beats) => Position.GetPositionMs(beats);
}
