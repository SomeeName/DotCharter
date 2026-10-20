using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Events;

/// <summary>
/// Per-level fret-hand-mute marker (RS-XML <c>fretHandMute</c> element inside
/// <c>level/fretHandMutes</c>). rscharter writes these as empty self-closing
/// tags identifying a position; DotCharter mirrors that.
/// </summary>
public class FretHandMute
{
    public FractionalPosition Position { get; set; } = FractionalPosition.Zero;

    public FretHandMute() { }

    public FretHandMute(FractionalPosition position) => Position = position;
}
