using System.Globalization;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     The rows of <see cref="FakeSongSelectScreen.Full" />: a library at the size a
///     long-time player's CDLC folder reaches, 2,160 songs. The forty hand-written rows come
///     first; the rest are built from word lists by index arithmetic, so every run of the
///     gallery and the tests sees the same library.
/// </summary>
internal static class FakeSongLibraryFull
{
    /// <summary>The row count the screen has to stay usable at (target-architecture.md § 3).</summary>
    internal const int RowCount = 2_160;

    private const int SongsPerArtist = 3;

    private static readonly string[] Adjectives =
    [
        "Velvet", "Hollow", "Electric", "Silent", "Burning", "Crimson", "Northern", "Broken", "Golden", "Wandering",
        "Midnight", "Iron", "Glass", "Paper", "Neon", "Frozen", "Savage", "Gentle", "Restless", "Distant",
        "Lunar", "Rusty", "Wild", "Solar", "Hidden", "Bitter", "Static", "Lucky", "Sleeping", "Atomic"
    ];

    private static readonly string[] Nouns =
    [
        "Engines", "Wolves", "Lanterns", "Rivers", "Saints", "Machines", "Ravens", "Harbors", "Comets", "Tigers",
        "Shadows", "Kings", "Echoes", "Giants", "Pilots", "Mirrors", "Thieves", "Owls", "Bells", "Sparrows",
        "Gardens", "Ghosts", "Satellites", "Horses"
    ];

    private static readonly string[] TitleHeads =
    [
        "Under", "Beyond", "Into", "Against", "Without", "Across", "Before", "After", "Inside", "Above", "Through", "Behind"
    ];

    private static readonly string[] TitleTails =
    [
        "the Static", "a Paper Sky", "the Undertow", "Cold Water", "the Long Night", "My Own Machine", "the Wire",
        "Red Lights", "the Quiet", "Seven Bridges", "the Last Exit", "Open Fields", "the Floodgate", "Your Ghost",
        "Borrowed Time", "the Pines", "Hard Rain", "the Signal"
    ];

    private static readonly (string Name, string Notes)[] Tunings =
    [
        ("E Standard", "E A D G B E"), ("E Standard", "E A D G B E"), ("E♭ Standard", "D# G# C# F# A# D#"),
        ("Drop D", "D A D G B E"), ("E Standard", "E A D G B E"), ("Drop C", "C G C F A D"),
        ("D Standard", "D G C F A D"), ("Drop C#", "C# G# C# F# A# D#"), ("Open G", "D G D G B D"),
        ("C Standard", "C F A# D# G C"), ("DADGAD", "D A D G A D"), ("Drop B", "B F# B E G# C#")
    ];

    private static readonly string[] Arrangements =
    [
        "Lead · Rhythm · Bass", "Lead · Bass", "Lead · Rhythm · Bass · Combo", "Bass", "Lead", "Lead · Rhythm"
    ];

    private static readonly string[] Grades = ["S", "A", "A", "B", "B", "C", "D"];

    internal static IEnumerable<FakeSongRow> Build()
    {
        var known = FakeSongLibrary.BuildLibrary().ToList();
        foreach (var row in known) yield return row;

        for (var i = 0; i < RowCount - known.Count; i++) yield return Generated(i);
    }

    private static FakeSongRow Generated(int i)
    {
        var band = i / SongsPerArtist;
        var adjective = Adjectives[band % Adjectives.Length];
        var noun = Nouns[band / Adjectives.Length % Nouns.Length];
        var artist = (band % 3) switch
        {
            0 => $"The {adjective} {noun}",
            1 => $"{adjective} {noun}",
            _ => $"{noun} of the {adjective} Coast"
        };
        var title = $"{TitleHeads[i % TitleHeads.Length]} {TitleTails[i * 7 % TitleTails.Length]}";
        var tuning = Tunings[i * 5 % Tunings.Length];
        var capo = i % 23 == 0 ? 2 + i % 5 : 0;
        var seconds = 95 + i * 53 % 520;
        var difficulty = i % 41 == 0 ? 0 : 1.0 + i * 31 % 90 / 10.0;
        var played = i % 3 != 0;
        var accuracy = played ? 0.6 + i * 13 % 40 / 100.0 : 0;
        var grade = played ? Grades[i * 11 % Grades.Length] : "";

        return new FakeSongRow
        {
            Title = title,
            Artist = artist,
            Album = $"{adjective} {TitleTails[band % TitleTails.Length]}",
            TuningName = tuning.Name,
            TuningLabel = FakeSongLibrary.TuningCell(tuning.Name, capo),
            TuningNotes = tuning.Notes,
            ArrangementNames = Arrangements[i % Arrangements.Length].Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            LengthText = (seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture),
            DifficultyLabel = difficulty > 0 ? difficulty.ToString("0.0", CultureInfo.CurrentCulture) : "—",
            HasBest = played,
            BestGradeLabel = grade,
            BestAccuracyLabel = played ? accuracy.ToString("P1", CultureInfo.CurrentCulture) : "",
            BestScore = played ? 200_000 + i * 7_919 % 1_700_000 : 0,
            BestAccuracy = accuracy,
            LastPlayedLabel = played ? (1 + i % 300).ToString(CultureInfo.InvariantCulture) + " days ago" : "",
            IsFullCombo = played && i % 17 == 0
        };
    }
}
