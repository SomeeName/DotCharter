using DotCharter.Core.Domain.Events;

namespace DotCharter.Core.Utilities;

public interface ITimedElement
{
    double GetTimeMs(IList<BeatMarker> beats);
}
