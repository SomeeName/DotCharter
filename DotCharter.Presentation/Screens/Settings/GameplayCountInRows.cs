namespace DotCharter.Presentation.Screens.Settings;

/// <summary>The count-in rows (Off, 2, 4, 8 beats), one map for both surfaces that write <c>AppConfig.GameplayCountInBeats</c>.</summary>
public static class GameplayCountInRows
{
    public static int IndexToBeats(int index) => index switch
    {
        1 => 2,
        2 => 4,
        3 => 8,
        _ => 0
    };

    public static int BeatsToIndex(int beats) => beats switch
    {
        <= 0 => 0,
        <= 2 => 1,
        <= 4 => 2,
        _ => 3
    };
}
