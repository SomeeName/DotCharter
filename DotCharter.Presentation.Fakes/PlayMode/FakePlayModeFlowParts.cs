using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     The long strings every Play Mode flow fake's <c>LongText</c> variant draws from:
///     German because its prose is longer, Japanese because it costs three UTF-8 bytes a
///     character. An empty fixture hides layout bugs; these are the full ones.
/// </summary>
public static class FakeFlowText
{
    public const string LongSongTitle =
        "Symphony No. 4 in D Minor, Op. 120 — IV. Langsam; Lebhaft (arranged for two guitars, bass and a very patient drummer)";

    public const string JapaneseSongTitle =
        "こんにちは世界、これは非常に長い日本語のタイトルであり、行の折り返しを試すためのものです";

    public const string GermanSongTitle =
        "Ein außergewöhnlich langer deutscher Songtitel, der die Spaltenbreite dieses Bildschirms gnadenlos ausreizt";

    public const string LongArtist =
        "The Extraordinarily Long Band Name Appreciation Society Orchestra feat. a Very Long Guest Credit";

    public const string LongArrangement = "Alternate Lead (bonus, drop tuning, 12-string overdubs)";

    public const string LongTuning = "Drop C# / Db standard with a capo at the seventh fret";

    public const string LongProfileName = "Der außergewöhnlich geduldige Gitarrist mit dem langen Namen";
}

/// <summary>One entry in a combo or a picker. Nothing here moves.</summary>
public sealed class FakeChoiceOption(string label) : IChoiceOption
{
    public string Label { get; } = label;

    public override string ToString() => Label;
}

/// <summary>A caption over one already-formatted figure.</summary>
public sealed class FakeFactTile : ObservableObject, IFactTile
{
    public FakeFactTile(string caption, string value, bool isReady = false, bool isWarning = false)
    {
        Caption = caption;
        Value = value;
        IsReady = isReady;
        IsWarning = isWarning;
    }

    public string Caption { get; }

    public string Value { get; }

    public bool IsReady { get; }

    public bool IsWarning { get; }
}
