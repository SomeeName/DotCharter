using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Services.Infrastructure.Configuration;

namespace DotCharter.Presentation.Fakes.Settings;

/// <summary>A services-free Keybinds tab; the chords are real <see cref="KeyChord" />s so the capture pill draws them.</summary>
public sealed class FakeKeybindsSettingsScreen : ObservableObject, IKeybindsSettingsScreen
{
    private bool _isMidiMode;

    private FakeKeybindsSettingsScreen(IReadOnlyList<IKeybindGroup> groups, bool midi = false, bool live = false,
        string? status = null)
    {
        Groups = groups;
        _isMidiMode = midi;
        IsMidiInputLive = live;
        MidiInputStatus = status ?? "Listening to Akai MPK mini 3. Click a binding, then press a pad or button on the controller.";
        ResetToDefaultsCommand = new RelayCommand(() => { });
        ClearMidiBindingsCommand = new RelayCommand(() => { });
        foreach (var row in groups.SelectMany(g => g.Rows).OfType<FakeRow>()) row.Owner = this;
    }

    public static IReadOnlyList<FakeVariant<IKeybindsSettingsScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("MidiLive", MidiLive),
        new("MidiNoDevice", MidiNoDevice)
    ];

    public static FakeKeybindsSettingsScreen Default() => new(DefaultGroups());

    public static FakeKeybindsSettingsScreen Empty() => new([]);

    /// <summary>MIDI mode with a controller listening: several binds, one row learning, a digit family.</summary>
    public static FakeKeybindsSettingsScreen MidiLive() => new(DefaultGroups(), midi: true, live: true);

    /// <summary>MIDI mode with no device open: the warning, and the binds still editable under it.</summary>
    public static FakeKeybindsSettingsScreen MidiNoDevice() => new(DefaultGroups(), midi: true, live: false,
        status: "No MIDI input is open, so no binding can fire. Pick one under Rig ▸ MIDI controller. You can still edit the bindings here.");

    private static List<IKeybindGroup> DefaultGroups() =>
    [
        new FakeGroup("Transport", "Play, tempo and the loop region", "IconPlay",
        [
            new FakeRow("Play / Pause", "Space", midi: "C2 · ch 10"),
            new FakeRow("Toggle metronome", "M", midi: "CC 20 · ch 1"),
            new FakeRow("Toggle claps", "C"),
            new FakeRow("Speed up (fast)", "Ctrl+OemPlus"),
            new FakeRow("Set loop start", "Alt+Home", midi: "C#2 · ch 10"),
            new FakeRow("Toggle loop", "F6", learning: true)
        ]),
        new FakeGroup("Navigation", "Moving the playhead and the selection", "IconFollowPlayhead",
        [
            new FakeRow("Seek left", "Left", midi: "CC 116 · ch 16"),
            new FakeRow("Seek right", "Right", midi: "CC 117 · ch 16"),
            new FakeRow("Seek to previous beat", "Ctrl+Left"),
            new FakeRow("Seek to next beat", "Ctrl+Right"),
            new FakeRow("Bookmark (modifier + digit)", "Shift+D0", bindable: false),
            new FakeRow("Move the selection to the next grid line and extend it", "Ctrl+Home", midi: "G9 · ch 16")
        ]),
        new FakeGroup("Files", "New, open, save", "IconContentSave",
        [
            new FakeRow("New project", "Ctrl+N"),
            new FakeRow("Open project", "Ctrl+O"),
            new FakeRow("Save project", "Ctrl+S", midi: "CC 64 · ch 1"),
            new FakeRow("Save project as…", "Ctrl+Shift+S")
        ])
    ];

    public static FakeKeybindsSettingsScreen LongText() => new(
    [
        new FakeGroup("Notenmodifikatoren und Techniken der Bearbeitung", "Hammer-on, Pull-off, Harmonics, Palm Mute und die Gruppenvarianten", "IconTune",
        [
            new FakeRow("Saite nach oben verschieben (gleiche Tonhöhe beibehalten)", "Ctrl+Shift+Alt+PageUp",
                "Verschiebt die ausgewählten Noten auf die nächsthöhere Saite und passt den Bund an, damit die Tonhöhe gleich bleibt"),
            new FakeRow("Donaudampfschifffahrtsgesellschaftskapitänsmütze umschalten", "Ctrl+Shift+Alt+OemQuestion"),
            new FakeRow("非常に長いキーバインド名のテストケースです。ハーモニクス切り替え", "Ctrl+Shift+H"),
            new FakeRow("選択したノートをすべて次のビートにスナップする", "Ctrl+Alt+Shift+Right")
        ]),
        new FakeGroup("ファイル操作", "新規作成、開く、保存", "IconContentSave",
        [
            new FakeRow("プロジェクトを名前を付けて保存", "Ctrl+Shift+S"),
            new FakeRow("Projekt unter neuem Namen speichern", "Ctrl+Shift+Alt+S")
        ])
    ]);

    public static FakeKeybindsSettingsScreen Full()
    {
        string[] verbs = ["Seek", "Toggle", "Select", "Snap", "Paste", "Move", "Zoom", "Insert"];
        string[] objects = ["the next beat", "the previous anchor", "all notes", "the selection", "a hand shape", "the loop", "the grid", "a vocal"];
        var groups = new List<IKeybindGroup>();
        foreach (var (title, icon, count) in new[]
                 {
                     ("Transport", "IconPlay", 16), ("Navigation", "IconFollowPlayhead", 39), ("Editing", "IconPencil", 20),
                     ("Note modifiers", "IconTune", 28), ("View & tools", "IconMonitor", 33), ("Files", "IconContentSave", 4)
                 })
        {
            var rows = new List<IKeybindRow>();
            for (var i = 0; i < count; i++)
            {
                rows.Add(new FakeRow($"{verbs[i % verbs.Length]} {objects[(i / verbs.Length + i) % objects.Length]} ({i + 1})",
                    i % 3 == 0 ? "Ctrl+Shift+Alt+PageDown" : i % 3 == 1 ? "Alt+OemOpenBrackets" : "F" + (i % 12 + 1)));
            }

            groups.Add(new FakeGroup(title, $"{count} verbs of the {title.ToLowerInvariant()} family", icon, rows));
        }

        return new FakeKeybindsSettingsScreen(groups);
    }

    public IReadOnlyList<IKeybindGroup> Groups { get; }

    public ICommand ResetToDefaultsCommand { get; }

    public ICommand ClearMidiBindingsCommand { get; }

    public bool IsMidiMode
    {
        get => _isMidiMode;
        set
        {
            if (!SetProperty(ref _isMidiMode, value)) return;
            OnPropertyChanged(nameof(IsKeyboardMode));
            foreach (var row in Groups.SelectMany(g => g.Rows).OfType<FakeRow>()) row.AnnounceMode();
        }
    }

    public bool IsKeyboardMode
    {
        get => !_isMidiMode;
        set => IsMidiMode = !value;
    }

    public string MidiInputStatus { get; }

    public bool IsMidiInputLive { get; }

    private sealed class FakeGroup(string title, string subtitle, string iconKey, IReadOnlyList<IKeybindRow> rows)
        : ObservableObject, IKeybindGroup
    {
        public string Title { get; } = title;

        public string Subtitle { get; } = subtitle;

        public string IconKey { get; } = iconKey;

        public IReadOnlyList<IKeybindRow> Rows { get; } = rows;

        public bool IsVisible => true;
    }

    private sealed class FakeRow : ObservableObject, IKeybindRow
    {
        private KeyChord _chord;
        private string? _midi;

        public FakeRow(string label, string chord, string? tooltip = null, string? midi = null, bool learning = false,
            bool bindable = true)
        {
            Label = label;
            Tooltip = tooltip ?? label;
            _chord = KeyChord.Parse(chord);
            _midi = midi;
            IsMidiLearning = learning;
            IsMidiBindable = bindable;
            LearnMidiCommand = new RelayCommand(() => { }, () => bindable);
            ClearMidiCommand = new RelayCommand(() =>
            {
                _midi = null;
                OnPropertyChanged(nameof(MidiText));
                OnPropertyChanged(nameof(IsMidiBound));
            });
        }

        public FakeKeybindsSettingsScreen? Owner { get; set; }

        public string Label { get; }

        public string Tooltip { get; }

        public KeyChord Chord
        {
            get => _chord;
            set => SetProperty(ref _chord, value);
        }

        public bool IsVisible => true;

        public bool ShowsMidi => Owner?.IsMidiMode == true;

        public string MidiText => IsMidiLearning ? "Press a pad…" : !IsMidiBindable ? "Keyboard only" : _midi ?? "—";

        public bool IsMidiBindable { get; }

        public bool IsMidiBound => _midi is not null;

        public bool IsMidiLearning { get; }

        public ICommand LearnMidiCommand { get; }

        public ICommand ClearMidiCommand { get; }

        public void AnnounceMode() => OnPropertyChanged(nameof(ShowsMidi));
    }
}
