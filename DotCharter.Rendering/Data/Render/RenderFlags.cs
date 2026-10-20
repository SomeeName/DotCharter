namespace DotCharter.Rendering.Data.Render;

[Flags]
public enum NoteFlags : ushort
{
    None = 0,
    HasSlide = 1 << 0,
    UnpitchedSlide = 1 << 1,
    Vibrato = 1 << 2,
    Tremolo = 1 << 3,
    LinkNext = 1 << 4,
    LinkPrevious = 1 << 5,
    HasBends = 1 << 6,
    IsSelected = 1 << 7,
    Accent = 1 << 8,
    IsHovered = 1 << 9,
    IsGhost = 1 << 10,
    /// <summary>
    /// Set on a (chord) note that has <see cref="LinkNext"/> AND whose link
    /// target carries a sustain-relevant technique (slide / vibrato /
    /// tremolo / bend). The sustain renderer keeps drawing the tail in this
    /// case so the chord visually feeds into the technique on the next
    /// note, even when the chord note itself is plain.
    /// </summary>
    LinkTargetHasTechnique = 1 << 11,
    /// <summary>A linked continuation whose vibrato / tremolo the sound before it
    /// did not have: the wobble eases in from the centre line instead of starting
    /// mid-swing, so the two tails join.</summary>
    RampIn = 1 << 12,
    /// <summary>A sound whose vibrato / tremolo the linked note after it does not
    /// have: the wobble eases out so the tails join.</summary>
    RampOut = 1 << 13
}

[Flags]
public enum ChordFlags : byte
{
    None = 0,
    IsSelected = 1 << 0,
    Accent = 1 << 1,
    IsHovered = 1 << 2,
    IsGhost = 1 << 3,
    /// <summary>
    /// Set when the chord position lies inside an active handshape.
    /// Inside a handshape, the chord renderer suppresses the trailing
    /// sustain because the handshape itself already conveys the held
    /// duration; the only exception is when a chord-note (or its
    /// link-target) carries a sustain-relevant technique.
    /// </summary>
    InHandShape = 1 << 4,
    /// <summary>
    /// The export ships this chord WITHOUT chord notes, so Rocksmith draws it as
    /// the frame-only repeat box. Data, never derived here: the snapshot copies it
    /// from <see cref="SnapshotUpdateContext.RepeatChords" />, which the app fills
    /// from the export's own rule (<c>DotCharter.Services.Export.Rocksmith.RepeatChords</c>).
    /// </summary>
    Repeat = 1 << 5,

    /// <summary>
    /// A pending bulk chord edit would change this chord — the chord panel's
    /// preview. Data: copied from <see cref="SnapshotUpdateContext.EditTargetChords" />.
    /// </summary>
    EditTarget = 1 << 6,

    /// <summary>
    /// The chord is split into notes (<c>Chord.SplitIntoNotes</c>, EOF's split
    /// status): every export ships it as one note per string with no chord frame,
    /// so the highway draws single-note heads and tails and no box (a Guitar
    /// Pro pick scrape on two strings is one).
    /// </summary>
    SplitIntoNotes = 1 << 7
}
