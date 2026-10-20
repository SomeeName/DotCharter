namespace DotCharter.Services.Editing.Anchors;

/// <summary>Which field of an FHP anchor a nudge moves.</summary>
public enum AnchorNudgeTarget
{
    /// <summary>The fret the hand's index finger sits on.</summary>
    Fret,

    /// <summary>
    ///     How many frets the hand spans. Not cosmetic: Rocksmith derives which
    ///     finger plays a fret as <c>1 + (fret - anchor.fret) - (anchor.width - 4)</c>,
    ///     so a width change is a real change to the fingering the game shows.
    /// </summary>
    Width
}
