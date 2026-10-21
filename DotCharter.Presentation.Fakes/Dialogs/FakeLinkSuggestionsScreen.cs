using System.Windows.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="ILinkSuggestionsScreen" />; Link all is the default answer.</summary>
public sealed class FakeLinkSuggestionsScreen : FakeDialog<LinkSuggestionsChoice>, ILinkSuggestionsScreen
{
    private FakeLinkSuggestionsScreen(string headline, IReadOnlyList<string> details)
    {
        Headline = headline;
        Details = details;
        LinkAllCommand = Answer(LinkSuggestionsChoice.LinkAll);
        ReviewCommand = Answer(LinkSuggestionsChoice.Review);
        SkipCommand = Answer(LinkSuggestionsChoice.Skip);
    }

    public static IReadOnlyList<FakeVariant<ILinkSuggestionsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    public static FakeLinkSuggestionsScreen Default() => new("6 groups to link, 18 placements.",
        ["Lead and Rhythm share 48 of 60 measures.", "Chorus repeats 3× in each."]);

    /// <summary>One repeat inside one part, nothing shared.</summary>
    public static FakeLinkSuggestionsScreen Empty() => new("1 group to link, 2 placements.", []);

    public static FakeLinkSuggestionsScreen LongText() => new("12 Gruppen zum Verknüpfen, 37 Platzierungen.",
    [
        "Leadgitarre (alternative Stimmung) und Rhythmusgitarre (zweite Stimme) teilen sich 148 von 160 Takten.",
        "リードとリズムは 160 小節のうち 148 小節を共有しています。",
        "Refrain mit Zwischenspiel wiederholt sich 4× in jeder."
    ]);

    /// <summary>Four parts that share: every pair gets its line, then the repeat.</summary>
    public static FakeLinkSuggestionsScreen Full() => new("24 groups to link, 96 placements.",
    [
        "Lead and Rhythm share 112 of 140 measures.",
        "Lead and Lead 2 share 96 of 140 measures.",
        "Rhythm and Lead 2 share 88 of 140 measures.",
        "Chorus repeats 4× in each."
    ]);

    public string Headline { get; }

    public IReadOnlyList<string> Details { get; }

    public ICommand LinkAllCommand { get; }

    public ICommand ReviewCommand { get; }

    public ICommand SkipCommand { get; }
}
