using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;

namespace DotCharter.Services.Editing.Snapping;

public enum SnapMode
{
    Beat,
    Measure,
    Milliseconds
}

public interface ISnapService
{
    SnapMode Mode { get; set; }
    double Divisor { get; set; }
    double SnapToGrid(double timeMs, IEnumerable<BeatMarker> beats);
}
