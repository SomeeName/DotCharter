namespace DotCharter.Services.Gameplay.Setlists;

/// <summary>
///     A named, ordered list of songs a player plays back to back in Play Mode.
/// SOLO only: LAN setlists and voting are not
///     this and must not come back through it. Not the rig's TONE setlists
///     (<c>RigSetlistDto</c>), which are ordered tone presets.
/// </summary>
public sealed class PlaySetlist
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public List<PlaySetlistEntry> Entries { get; set; } = [];

    /// <summary>A deep copy, so a save handed to a background writer never shares
    /// a list with the one the UI keeps editing.</summary>
    public PlaySetlist Clone() => new() { Id = Id, Name = Name, Entries = Entries.Select(e => e.Clone()).ToList() };
}

/// <summary>
///     One song of a <see cref="PlaySetlist" />. <see cref="SongKey" /> is what Play
///     Mode resolves the song by (a library song's identity, or an own chart's
///     project path); <see cref="Arrangement" /> is the path label the run opens on;
///     <see cref="Title" /> and <see cref="Artist" /> are display copies taken when
///     the entry was added, so a song that has since left the library can still be
///     NAMED when it is skipped.
/// </summary>
public sealed class PlaySetlistEntry
{
    public const int MinSpeedPercent = 25;
    public const int MaxSpeedPercent = 100;

    public string SongKey { get; set; } = "";

    public string Arrangement { get; set; } = "";

    public int SpeedPercent { get; set; } = MaxSpeedPercent;

    public string Title { get; set; } = "";

    public string Artist { get; set; } = "";

    /// <summary>The speed a run actually uses: a hand-edited or damaged value is
    /// held inside the practice range rather than handed to the transport.</summary>
    public int EffectiveSpeedPercent => Math.Clamp(SpeedPercent, MinSpeedPercent, MaxSpeedPercent);

    public PlaySetlistEntry Clone() => new()
    {
        SongKey = SongKey, Arrangement = Arrangement, SpeedPercent = SpeedPercent, Title = Title, Artist = Artist
    };
}
