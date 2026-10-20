using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="ICountdownScreen" />: the first and last beats (GO! at its widest),
///     a lockup long enough to wrap, and the shortest and longest count-ins.
/// </summary>
public sealed class FakeCountdownScreen : ObservableObject, ICountdownScreen
{
    private FakeCountdownScreen(string countLabel, bool isGo, int litPips, string songTitle,
        string artist, string arrangementName, string tuningLabel, int beats = 4)
    {
        CountLabel = countLabel;
        IsGo = isGo;
        Pips = Enumerable.Range(0, beats).Select(i => (ICountdownPip)new FakeCountdownPip(i < litPips)).ToList();
        SongTitle = songTitle;
        Artist = artist;
        ArrangementName = arrangementName;
        TuningLabel = tuningLabel;
        CancelCommand = new RelayCommand(() => { });
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<ICountdownScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, ThreeToGo),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("Go", Go)
    ];

    /// <summary>The first beat of a four-beat count-in.</summary>
    public static FakeCountdownScreen ThreeToGo() =>
        new("3", isGo: false, litPips: 1, "Master of Puppets", "Metallica", "Lead 1", "E♭ Standard");

    /// <summary>The last beat, where the numeral becomes a word.</summary>
    public static FakeCountdownScreen Go() =>
        new("Go!", isGo: true, litPips: 4, "Stranded", "Gojira", "Rhythm", "Drop C");

    /// <summary>No count-in beats to light and no artist: the leanest frame the screen shows.</summary>
    public static FakeCountdownScreen Empty() =>
        new("3", isGo: false, litPips: 0, "Untitled", "", "Lead", "E Standard", beats: 0);

    /// <summary>The longest count-in the settings offer, two bars of four, five beats in.</summary>
    public static FakeCountdownScreen Full() =>
        new("3", isGo: false, litPips: 5, "Pull Me Under", "Dream Theater", "Lead", "E Standard", beats: 8);

    /// <summary>The stress case: a lockup long enough to wrap under the figure.</summary>
    public static FakeCountdownScreen LongText() =>
        new("1", isGo: false, litPips: 3, FakeFlowText.JapaneseSongTitle, FakeFlowText.LongArtist,
            FakeFlowText.LongArrangement, FakeFlowText.LongTuning);

    // ── ICountdownScreen ──────────────────────────────────────────────────

    public string CountLabel { get; }

    public bool IsGo { get; }

    public IReadOnlyList<ICountdownPip> Pips { get; }

    public string SongTitle { get; }

    public string Artist { get; }

    public bool HasArtist => Artist.Length > 0;

    public string ArrangementName { get; }

    public string TuningLabel { get; }

    public ICommand CancelCommand { get; }
}

/// <summary>One beat of the count-in.</summary>
public sealed class FakeCountdownPip(bool isLit) : ObservableObject, ICountdownPip
{
    public bool IsLit { get; } = isLit;
}
