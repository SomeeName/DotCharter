namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>Screens inside the Play Mode surface (0.11.0 Pillar B).</summary>
public enum PlayModeScreen
{
    Start,
    SongSelect,

    TuningCheck,
    Countdown,
    Playing,
    Paused,

    /// <summary>Rock-meter fail-out (0.24 performance stakes): the run is
    /// already ended; the overlay floats over a frozen frame of the highway
    /// exactly like <see cref="Paused"/>.</summary>
    Failed,
    Results

    // History and Trends (the Progress hub) are not part of the app.
}
