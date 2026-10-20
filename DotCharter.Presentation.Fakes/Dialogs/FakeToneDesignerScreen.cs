using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IToneDesignerScreen" />; Done answers true.</summary>
public sealed class FakeToneDesignerScreen : FakeDialog<bool>, IToneDesignerScreen
{
    private IProjectTone? _selectedTone;
    private bool _isPreviewOn;

    private FakeToneDesignerScreen(IReadOnlyList<(string Key, string Name)> tones, IReadOnlyList<(string Name, string Type)> arrangements,
        IReadOnlyList<(string Key, string Name)> imported, string status = "", bool canPreview = true, string undefinedSlot = "")
    {
        ProjectTones = [.. tones.Select(t => new Tone(t.Key, t.Name))];
        _selectedTone = ProjectTones.FirstOrDefault();
        var defined = tones.Select(t => t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Arrangements =
        [
            .. arrangements.Select((a, i) =>
                new Row(a.Name, a.Type, i == 0 && tones.Count > 0 ? tones[0].Key : "", i == 0 ? undefinedSlot : "", defined))
        ];
        ImportedTones = [.. imported.Select(t => new Imported(t.Key, t.Name))];
        ImportStatus = status;
        CanPreview = canPreview;
        Editor = new FakeToneEditor(_selectedTone?.Key ?? "", _selectedTone?.Name ?? "");
        static ICommand Idle() => new RelayCommand(() => { });
        AddToneCommand = Idle();
        DuplicateToneCommand = HasSelectedTone ? Idle() : Disabled();
        DeleteToneCommand = HasSelectedTone ? Idle() : Disabled();
        GenerateTonesFromAudioCommand = Idle();
        ImportTonesFromPsarcCommand = Idle();
        ImportToneFileCommand = Idle();
        CreateWithAiCommand = Idle();
        ImportFromToneRigCommand = Idle();
        AddImportedToProjectCommand = new RelayCommand<IImportedTone>(_ => { });
        CloseCommand = Answer(true);
    }

    public static IReadOnlyList<FakeVariant<IToneDesignerScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("PackageReaderOff", PackageReaderOff)
    ];

    public static FakeToneDesignerScreen Default() => new(
        [("clean_verse", "Clean verse"), ("crunch_chorus", "Crunch chorus"), ("lead_solo", "Lead solo")],
        [("Lead", "Lead"), ("Rhythm", "Rhythm")], []);

    /// <summary>A project with no tones and no instrumental arrangement: the rail says so, the editor asks for a tone.</summary>
    public static FakeToneDesignerScreen Empty() => new([], [], []);

    public static FakeToneDesignerScreen LongText() => new(
        [("verzerrter_leadsound_mit_hall", "Verzerrter Leadsound mit langem Hall und Verzögerung"), ("クリーン", "クリーンなアルペジオ（コーラス付き）")],
        [("Leadgitarre (alternative Stimmung)", "Lead"), ("リズムギター", "Rhythm")],
        [("importierter_ton", "Importierter Ton aus einem anderen Paket")],
        status: "3 Töne erzeugt. 1 übersprungen, weil der Name schon existiert. Der Abschnitt klingt verzerrt und mittenbetont.",
        canPreview: false);

    public static FakeToneDesignerScreen Full() => new(
        [.. Enumerable.Range(1, 12).Select(i => ($"tone_{i}", $"Tone {i}"))],
        [("Lead", "Lead"), ("Rhythm", "Rhythm"), ("Bass", "Bass"), ("Alt. lead", "Lead")],
        [("imported_clean", "Imported clean"), ("imported_drive", "Imported drive")],
        status: "Imported 2 tone definitions.",
        undefinedSlot: "old_lead_dist");

    /// <summary>A build that cannot read game packages: no "Import from PSARC" button.</summary>
    public static FakeToneDesignerScreen PackageReaderOff()
    {
        var screen = Default();
        screen.CanImportFromPackages = false;
        return screen;
    }

    /// <summary>
    ///     This screen with the "from tone rig" review open over it, a stand-in and a left-out pedal.
    ///     Not a <see cref="Variants" /> entry: the review is modal, so it covers the footer every
    ///     variant is graded on; <c>RigToneImportReviewUiPerfSmokeTests</c> lays it out instead.
    /// </summary>
    public FakeToneDesignerScreen WithRigImportOpen()
    {
        RigImport = FakeRigToneImportReview.WithLosses();
        return this;
    }

    public IReadOnlyList<IProjectTone> ProjectTones { get; }
    public bool HasProjectTones => ProjectTones.Count > 0;

    public IProjectTone? SelectedTone
    {
        get => _selectedTone;
        set => SetProperty(ref _selectedTone, value);
    }

    public bool HasSelectedTone => SelectedTone is not null;
    public IToneEditor Editor { get; }
    private bool CanPreview { get; }
    public string ToneActionUnavailableReason => !CanPreview ? "Preview needs the audio engine's tone rig." : HasSelectedTone ? "" : "Pick a tone first.";
    public bool HasToneActionUnavailableReason => ToneActionUnavailableReason.Length > 0;
    public bool CanPreviewNow => CanPreview && HasSelectedTone;
    public bool IsPreviewOn { get => _isPreviewOn; set => SetProperty(ref _isPreviewOn, value); }
    public IToneDesignerPreviewTargets PreviewTargets { get; } = new Targets();
    public IReadOnlyList<IToneArrangementRow> Arrangements { get; }
    public bool HasArrangements => Arrangements.Count > 0;
    public IReadOnlyList<string> AuthoredToneKeys => [.. ProjectTones.Select(t => t.Key)];
    public IReadOnlyList<IImportedTone> ImportedTones { get; }
    public bool HasImportedTones => ImportedTones.Count > 0;
    public string ImportStatus { get; }
    public ICommand AddToneCommand { get; }
    public ICommand DuplicateToneCommand { get; }
    public ICommand DeleteToneCommand { get; }
    public ICommand GenerateTonesFromAudioCommand { get; }
    public ICommand ImportTonesFromPsarcCommand { get; }
    public ICommand ImportToneFileCommand { get; }
    public ICommand CreateWithAiCommand { get; }
    public bool CanImportFromPackages { get; private set; } = true;
    public ICommand AddImportedToProjectCommand { get; }
    public ICommand ImportFromToneRigCommand { get; }
    public IRigToneImportReview? RigImport { get; private set; }
    public ICommand CloseCommand { get; }

    private sealed record Choice(string DisplayName) : IToneChoice;

    /// <summary>No tonelab session: the DotCharter rig is the target, Rocksmith is disabled with its reason.</summary>
    private sealed class Targets : ObservableObject, IToneDesignerPreviewTargets
    {
        public bool IsClone { get => true; set { } }
        public bool IsInGame { get => false; set { } }
        public bool CanUseInGame => false;
        public string InGameUnavailableReason => "In-game unavailable: start Rocksmith with tonelab, then Reconnect.";
        public bool HasInGameUnavailableReason => true;
        public ICommand ReconnectCommand { get; } = new RelayCommand(() => { });
        public bool CanLaunch => false;
        public ICommand LaunchCommand { get; } = new RelayCommand(() => { });
        public string AsioNote => "";
        public bool HasAsioNote => false;
        public string Status => "";
        public bool HasStatus => false;
    }

    private sealed class Tone(string key, string name) : ObservableObject, IProjectTone
    {
        public string Key { get; } = key;
        public string Name { get; } = name;
    }

    private sealed record Imported(string Key, string Name) : IImportedTone;

    /// <summary>A slot is undefined when it names a key <paramref name="defined" /> lacks.</summary>
    private sealed class Row(string name, string type, string toneBase, string toneB, IReadOnlySet<string> defined)
        : ObservableObject, IToneArrangementRow
    {
        public string Name { get; } = name;
        public string TypeLabel { get; } = ArrangementTypeLabel.For(name, type);
        public string ToneBase { get; set; } = toneBase;
        public string ToneA { get; set; } = toneBase;
        public string ToneB { get; set; } = toneB;
        public string ToneC { get; set; } = "";
        public string ToneD { get; set; } = "";
        public bool IsToneBaseUndefined => IsUndefined(ToneBase);
        public bool IsToneAUndefined => IsUndefined(ToneA);
        public bool IsToneBUndefined => IsUndefined(ToneB);
        public bool IsToneCUndefined => IsUndefined(ToneC);
        public bool IsToneDUndefined => IsUndefined(ToneD);

        private bool IsUndefined(string key) => key.Length > 0 && !defined.Contains(key);
    }

    private sealed class Knob(string name, double value, string text, IReadOnlyList<string>? options = null) : ObservableObject, IToneKnob
    {
        public string DisplayName { get; } = name;
        public double Minimum => 0;
        public double Maximum => 100;
        public double Step => 1;
        public bool IsEnum => EnumOptions.Count > 0;
        public IReadOnlyList<string> EnumOptions { get; } = options ?? [];
        public double Value { get; set; } = value;
        public int EnumIndex { get; set; }
        public string ValueText { get; } = text;
    }

    private sealed class GearSlot(string label, IReadOnlyList<IToneChoice> options, int picked, IReadOnlyList<IToneKnob> knobs)
        : ObservableObject, IToneGearSlot
    {
        public string Label { get; } = label;
        public IReadOnlyList<IToneChoice> Options { get; } = options;
        public IToneChoice SelectedOption { get; set; } = options[picked];
        public bool HasGear => SelectedOption.DisplayName != "(empty)";
        public string GearDisplayName => SelectedOption.DisplayName;
        public IReadOnlyList<IToneKnob> Knobs { get; } = knobs;
        public bool HasKnobs => Knobs.Count > 0;
        public bool ShowNoKnobsHint => HasGear && Knobs.Count == 0;
    }

    private sealed class FakeToneEditor : ObservableObject, IToneEditor
    {
        public FakeToneEditor(string key, string name)
        {
            EditKey = key;
            EditName = name;
            DescriptorOptions = [new Choice("None"), new Choice("Clean"), new Choice("Distortion"), new Choice("Chorus"), new Choice("Delay")];
            Descriptor1 = DescriptorOptions[1];
            Descriptor2 = DescriptorOptions[3];
            Descriptor3 = DescriptorOptions[0];
            IReadOnlyList<IToneChoice> amps = [new Choice("Amp_TW40"), new Choice("Amp_Marshall Plexi 1959")];
            IReadOnlyList<IToneChoice> pedals = [new Choice("(empty)"), new Choice("Pedal_Chorus"), new Choice("Pedal_Tube Screamer")];
            GearSlots =
            [
                new GearSlot("Amp", amps, 0,
                [
                    new Knob("Gain", 35, "35%"), new Knob("Bass", 50, "50%"), new Knob("Mid", 60, "60%"),
                    new Knob("Treble", 55, "55%"), new Knob("Channel", 0, "Clean", ["Clean", "Drive"])
                ]),
                new GearSlot("Cabinet", [new Choice("Cab_2x12 Open")], 0, []),
                .. Enumerable.Range(1, 4).Select(i => new GearSlot($"Pre {i}", pedals, i == 1 ? 1 : 0, [])),
                .. Enumerable.Range(1, 4).Select(i => new GearSlot($"Loop {i}", pedals, 0, [])),
                .. Enumerable.Range(1, 4).Select(i => new GearSlot($"Rack {i}", [new Choice("(empty)")], 0, []))
            ];
            SelectedGearSlot = GearSlots[0];
        }

        public string EditKey { get; set; }
        public string EditName { get; set; }
        public string KeyError => "";
        public bool HasKeyError => false;
        public double VolumeAttenuation { get; set; } = 12;
        public double VolumeAttenuationMin => 0.1;
        public double VolumeAttenuationMax => 30;
        public IReadOnlyList<IToneChoice> DescriptorOptions { get; }
        public IToneChoice Descriptor1 { get; set; }
        public IToneChoice Descriptor2 { get; set; }
        public IToneChoice Descriptor3 { get; set; }
        public IReadOnlyList<IToneGearSlot> GearSlots { get; }
        public IToneGearSlot? SelectedGearSlot { get; set; }
    }
}
