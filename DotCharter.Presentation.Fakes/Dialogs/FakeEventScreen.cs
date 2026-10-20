using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IEventScreen" />; Save answers true.</summary>
public sealed class FakeEventScreen : FakeDialog<bool>, IEventScreen
{
    private static readonly string[] Sections =
        ["intro", "verse", "prechorus", "chorus", "bridge", "solo", "breakdown", "outro", "noguitar"];

    private static readonly string[] Codes =
    [
        "High pitch tick (B0)", "Low pitch tick (B1)", "Crowd speed slow (e0)", "Crowd speed default (e1)",
        "Crowd speed fast (e2)", "Crowd applause (E3)", "Crowd critique applause (D3)", "End crowd applause (E13)"
    ];

    private FakeEventScreen(string section, string phrase, string tone, int status, bool solo,
        IReadOnlyList<string> arrangements, string[]? selectedCodes = null, string? unknownSection = null,
        bool toneHasNoBody = false)
    {
        var options = new List<IEventSectionOption> { new Option("None") };
        options.AddRange(Sections.Select(s => new Option(char.ToUpperInvariant(s[0]) + s[1..])));
        if (unknownSection != null) options.Add(new Option(unknownSection));
        SectionOptions = options;
        SectionName = unknownSection ?? section;
        SelectedSection = unknownSection != null
            ? options[^1]
            : options.FirstOrDefault(o => string.Equals(o.Display, section, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        SectionNameIsUnknown = unknownSection != null;
        SectionStatusIndex = status;
        PhraseName = phrase;
        SoloPhrase = solo;
        ToneName = tone;
        ToneHasNoBody = toneHasNoBody;
        EventToggles = [.. Codes.Select(c => new Toggle(c, selectedCodes?.Contains(c) == true))];
        Arrangements = [.. arrangements.Select((a, i) => new Target(a, i == 0, i < 2))];
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IEventScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>A chorus marker with its phrase following it, on a lead and a rhythm arrangement.</summary>
    public static FakeEventScreen Default() =>
        new("chorus", "chorus", "clean_chorus", 2, false, ["Lead", "Rhythm"]);

    /// <summary>Empty ground on a one-arrangement chart: the dependent rows print why they are unavailable.</summary>
    public static FakeEventScreen Empty() => new("", "", "", 0, false, ["Lead"]);

    /// <summary>German phrase and tone names, a Japanese off-list section, a tone with no body.</summary>
    public static FakeEventScreen LongText() => new("", "Übergang zur zweiten Strophe mit Wechselschlag", "Verzerrter Leadsound mit Hall",
        1, true, ["Leadgitarre (Alternative Stimmung)", "Rhythmusgitarre", "Bass"],
        unknownSection: "サビ前のブリッジ（認識されない名前）", toneHasNoBody: true);

    /// <summary>A solo marker with several event codes, written to four arrangements.</summary>
    public static FakeEventScreen Full() => new("solo", "solo", "lead_solo", 4, true,
        ["Lead", "Rhythm", "Bass", "Alt. lead"], ["High pitch tick (B0)", "Crowd applause (E3)", "Crowd speed fast (e2)"]);

    public IReadOnlyList<IEventSectionOption> SectionOptions { get; }

    public IEventSectionOption? SelectedSection { get; set; }

    public string SectionName { get; }

    public bool SectionNameIsUnknown { get; }

    public IReadOnlyList<string> SectionStatusOptions { get; } = ["None", "To do", "Draft", "Review", "Done"];

    public int SectionStatusIndex { get; set; }

    public string PhraseName { get; set; }

    public bool SoloPhrase { get; set; }

    public IReadOnlyList<string> ToneOptions { get; } = ["clean_chorus", "lead_solo", "crunch_verse", "base"];

    public string ToneName { get; set; }

    public bool ToneHasNoBody { get; }

    public IReadOnlyList<IEventCodeToggle> EventToggles { get; }

    public IReadOnlyList<IEventArrangementTarget> Arrangements { get; }

    public bool HasMultipleArrangements => Arrangements.Count > 1;

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed record Option(string Display) : IEventSectionOption;

    private sealed class Toggle(string display, bool isSelected) : ObservableObject, IEventCodeToggle
    {
        public string Display { get; } = display;

        public bool IsSelected { get; set; } = isSelected;
    }

    private sealed class Target(string name, bool isActive, bool isSelected) : ObservableObject, IEventArrangementTarget
    {
        public string DisplayName { get; } = name;

        public bool IsActive { get; } = isActive;

        public bool IsSelected { get; set; } = isSelected;
    }
}
