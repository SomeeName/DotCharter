namespace DotCharter.Core.Domain.Guitar;

public enum MuteType
{
    None,
    Full,
    Palm
}

public enum HopoType
{
    None,
    HammerOn,
    PullOff,
    Tap
}

public enum HarmonicType
{
    None,
    Normal,
    Pinch
}

/// <summary>
/// Picking / strumming direction. <see cref="None" /> = not stated (the
/// default; exporters then write what they always wrote). RS XML spells it
/// <c>pickDirection</c> 0 = down / 1 = up on a note and <c>strum</c> "down" /
/// "up" on a chord.
/// </summary>
public enum PickDirectionType
{
    None,
    Down,
    Up
}

public enum BassPickingType
{
    None,
    Pop,
    Slap
}
