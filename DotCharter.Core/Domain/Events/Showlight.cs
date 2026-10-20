using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Events;

public class Showlight : ITimedElement
{
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;

    /// <summary>
    /// Canonical rscharter showlight type identifier. Empty when only a raw
    /// XML note is known and no catalog mapping is available.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Raw MIDI note number used by RS-XML showlights (<c>note</c> attribute).
    /// 0 = unset / not applicable.
    /// </summary>
    public int Note { get; set; }

    public double GetTimeMs(IList<BeatMarker> beats) => Position.GetPositionMs(beats);
}
