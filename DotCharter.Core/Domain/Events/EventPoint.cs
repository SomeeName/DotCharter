using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Events;

public class EventPoint : ITimedElement
{
    public string Phrase { get; set; } = "";
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;
    public string Section { get; set; } = "";

    /// <summary>
    /// Comma-joined event type names (e.g. "B0,B1"). Compatible with rscharter's `events` attribute.
    /// </summary>
    public string Events { get; set; } = "";

    /// <summary>Charting workflow status of this SECTION marker (v0.29 section
    /// tracker; meaningful only when <see cref="Section" /> is non-empty).
    /// Default <see cref="SectionStatus.None" /> = untracked, not serialized.
    /// DotCharter-only rscp extension (`ss` attribute) — rscharter ignores it.</summary>
    public SectionStatus Status { get; set; } = SectionStatus.None;

    public double GetTimeMs(IList<BeatMarker> beats) => Position.GetPositionMs(beats);
}
