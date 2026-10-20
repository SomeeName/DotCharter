using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IArrangementSettingsScreen" />; Save answers true.</summary>
public sealed class FakeArrangementSettingsScreen : FakeDialog<bool>, IArrangementSettingsScreen
{
    private static readonly string[] Derived =
    [
        "Bends", "Slides", "Unpitched slides", "Hammer-ons / pull-offs", "Tapping", "Vibrato", "Tremolo",
        "Sustain", "Palm mutes", "Fret-hand mutes", "Harmonics", "Pinch harmonics", "Slap / pop",
        "Power chords", "Drop D power chords", "Barre chords", "Open chords", "Non-standard chords",
        "Double stops", "Fifths and octaves"
    ];

    private static readonly string[] Authored = ["Finger picking", "Two-finger picking", "Pick direction", "Syncopation"];

    private FakeArrangementSettingsScreen(string name, ArrangementType type, int capo, int[] steps, string preset,
        bool hasScrollSpeed = false, bool overrideDerived = false, int setFlags = 3,
        string[]? derivedLabels = null, string[]? authoredLabels = null)
    {
        Name = name;
        Type = type;
        Capo = capo;
        Strings = steps.Length;
        TuningPreset = preset;
        var labels = TuningUtilities.GetStringLabels(steps.Length);
        TuningOffsets = [.. steps.Select((s, i) => new TuningRow(s, labels[i],
            TuningUtilities.GetResultingNote(steps.Length, i, s), $"String {steps.Length - i} ({labels[i]}), half steps"))];
        PathOptions = [new Path("Automatic (from the type)"), new Path("Lead"), new Path("Rhythm"), new Path("Any"), new Path("Bass")];
        SelectedPath = PathOptions[0];
        HasScrollSpeed = hasScrollSpeed;
        OverrideDerivedFlags = overrideDerived;
        DerivedFlags = [.. (derivedLabels ?? Derived).Select((l, i) => new Flag(l, i < setFlags))];
        AuthoredFlags = [.. (authoredLabels ?? Authored).Select((l, i) => new Flag(l, i == 0))];
        SaveCommand = Answer(true);
        CancelCommand = Answer(false);
    }

    public static IReadOnlyList<FakeVariant<IArrangementSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full)
    ];

    /// <summary>A lead in Drop D with a capo.</summary>
    public static FakeArrangementSettingsScreen Default() =>
        new("Lead", ArrangementType.Lead, 2, [-2, 0, 0, 0, 0, 0], "Drop D");

    /// <summary>A new arrangement: E standard, nothing derived yet.</summary>
    public static FakeArrangementSettingsScreen Empty() =>
        new("Arrangement 2", ArrangementType.Lead, 0, [0, 0, 0, 0, 0, 0], "E Standard", setFlags: 0);

    /// <summary>German name and flag labels, the longest copy the dialog carries.</summary>
    public static FakeArrangementSettingsScreen LongText() =>
        new("Leadgitarre (alternative Stimmung, zweite Fassung)", ArrangementType.Rhythm, 0, [-2, -2, -2, -2, -2, -2],
            "D Standard", derivedLabels:
            [
                "Saitenziehen", "Slides", "Tonlose Slides", "Hammer-ons / Pull-offs", "Tapping", "Vibrato",
                "Tremolo", "Ausklingen", "Handballendämpfung", "Greifhanddämpfung", "Flageoletts",
                "Gezupfte Flageoletts", "Slap / Pop", "Powerchords", "Drop-D-Powerchords", "Barrégriffe",
                "Offene Akkorde", "Nicht-Standard-Akkorde", "Doppelgriffe", "Quinten und Oktaven"
            ], authoredLabels: ["Fingerpicking", "Zwei-Finger-Anschlag", "Anschlagsrichtung", "Synkopierung"]);

    /// <summary>A seven-string custom tuning, a scroll speed set, the derived flags pinned.</summary>
    public static FakeArrangementSettingsScreen Full() =>
        new("Rhythm", ArrangementType.Rhythm, 5, [-5, -2, -2, -2, -2, -2, -2], "Custom",
            hasScrollSpeed: true, overrideDerived: true, setFlags: 14);

    public string Name { get; set; }

    public ArrangementType Type { get; set; }

    public IReadOnlyList<ArrangementType> ArrangementTypes { get; } =
        [ArrangementType.Lead, ArrangementType.Rhythm, ArrangementType.Bass];

    public ArrangementSubtype Subtype { get; set; } = ArrangementSubtype.Main;

    public IReadOnlyList<ArrangementSubtype> ArrangementSubtypes { get; } = Enum.GetValues<ArrangementSubtype>();

    public int Capo { get; set; }

    public double CentOffset { get; set; }

    public int Strings { get; set; }

    public string TuningPreset { get; set; }

    public IReadOnlyList<string> TuningPresets { get; } =
        ["E Standard", "Drop D", "Eb Standard", "D Standard", "Drop C", "Custom"];

    public IReadOnlyList<ITuningStep> TuningOffsets { get; }

    public IReadOnlyList<IArrangementPathOption> PathOptions { get; }

    public IArrangementPathOption? SelectedPath { get; set; }

    public bool HasScrollSpeed { get; set; }

    public double ScrollSpeed { get; set; } = 1.3;

    public bool IsBass => Type == ArrangementType.Bass;

    public bool PickedBass { get; set; }

    public IReadOnlyList<IArrangementFlag> AuthoredFlags { get; }

    public bool OverrideDerivedFlags { get; set; }

    public IReadOnlyList<IArrangementFlag> DerivedFlags { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    private sealed class TuningRow(int step, string label, string note, string accessibleName) : ObservableObject, ITuningStep
    {
        public int Step { get; set; } = step;

        public string StringLabel { get; } = label;

        public string ResultingNote { get; } = note;

        public string AccessibleName { get; } = accessibleName;
    }

    private sealed record Path(string Label) : IArrangementPathOption;

    private sealed class Flag(string label, bool isSet) : ObservableObject, IArrangementFlag
    {
        public string Label { get; } = label;

        public bool IsSet { get; set; } = isSet;
    }
}
