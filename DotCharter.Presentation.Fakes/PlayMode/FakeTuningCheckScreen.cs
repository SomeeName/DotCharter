using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.PlayMode;

namespace DotCharter.Presentation.Fakes.PlayMode;

/// <summary>
///     A services-free <see cref="ITuningCheckScreen" />.
///     <para>
///         This is the densest screen in the app, so its fixture has to be dense
///         too: a path picker with records on some tiles and not others, six
///         tuner rows in three different states at once, and every run setting
///         filled. <see cref="NeedsTuning" /> is the state the screen exists
///         for — two strings off, no stems, no record — and
///         <see cref="NoInput" /> is the one where the check CANNOT run, which
///         is the case a capability must never look live in.
///     </para>
/// </summary>
public sealed class FakeTuningCheckScreen : ObservableObject, ITuningCheckScreen
{
    private readonly List<FakePathTile> _paths;
    private readonly List<FakeTunerString> _strings;
    private IPathTile? _selectedPath;
    private bool _muteMyInstrument;
    private bool _useInGameTones = true;

    private FakeTuningCheckScreen(string songTitle, string artist, string tuningLabel, string tuningNotes,
        IEnumerable<FakePathTile> paths, IEnumerable<FakeTunerString> strings, bool isInputOpen,
        string inputLabel, double inputLevel, string inputLevelLabel, string speedLabel,
        string scrollSpeedLabel, string hitWindowLabel, string backingLabel, string toneLabel)
    {
        SongTitle = songTitle;
        Artist = artist;
        TuningLabel = tuningLabel;
        TuningNotes = tuningNotes;
        _paths = paths.ToList();
        foreach (var path in _paths) path.Attach(this);
        _strings = strings.ToList();
        IsInputOpen = isInputOpen;
        InputLabel = inputLabel;
        InputLevel = inputLevel;
        InputLevelLabel = inputLevelLabel;
        SpeedLabel = speedLabel;
        ScrollSpeedLabel = scrollSpeedLabel;
        HitWindowLabel = hitWindowLabel;
        BackingLabel = backingLabel;
        ToneLabel = toneLabel;

        RestartTunerCommand = new RelayCommand(() =>
        {
            foreach (var s in _strings) s.Reset();
            Republish();
        });
        SkipTunerCommand = new RelayCommand(() =>
        {
            foreach (var s in _strings) s.Verify();
            Republish();
        });
        StartCommand = new RelayCommand(() => { });
        BackCommand = new RelayCommand(() => { });
        SaveAsDefaultCommand = new RelayCommand(() => { });
        OpenRigCommand = new RelayCommand(() => { });

        SelectedPath = _paths.FirstOrDefault();
    }

    // ── the named variants ────────────────────────────────────────────────

    public static IReadOnlyList<FakeVariant<ITuningCheckScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Ready),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new("NeedsTuning", NeedsTuning),
        new("NoInput", NoInput),
        new("Setlist", InSetlist),
        new("SetlistSkipped", SetlistSkipped)
    ];

    /// <summary>Song 2 of a 5-song setlist.</summary>
    public static FakeTuningCheckScreen InSetlist() => WithSetlist(Ready(), FakeSetlistRun.MidRun());

    /// <summary>Song 3 of a setlist, after one that left the library was skipped.</summary>
    public static FakeTuningCheckScreen SetlistSkipped() => WithSetlist(Ready(), FakeSetlistRun.Skipped());

    private static FakeTuningCheckScreen WithSetlist(FakeTuningCheckScreen screen, FakeSetlistRun setlist)
    {
        screen.Setlist = setlist;
        return screen;
    }

    /// <summary>Everything verified, a record on the picked path — ready to start.</summary>
    public static FakeTuningCheckScreen Ready() =>
        new("Master of Puppets", "Metallica", "E♭ Standard", "D# G# C# F# A# D#",
        [
            new FakePathTile("Lead 1", "LEAD", "1,204 notes", "A · 94.1 %"),
            new FakePathTile("Rhythm", "RHYTHM", "982 notes", "B · 88.4 %"),
            new FakePathTile("Bass", "BASS", "614 notes", "")
        ],
        [
            FakeTunerString.Verified("E♭2"), FakeTunerString.Verified("A♭2"),
            FakeTunerString.Verified("D♭3"), FakeTunerString.Verified("G♭3"),
            FakeTunerString.Verified("B♭3"), FakeTunerString.Verified("E♭4")
        ], isInputOpen: true, "Focusrite Scarlett 2i2 · input 1", 0.62, "-14.2 dB",
            "100 %", "2.4 S ON SCREEN", "Normal", "Full band", "This song's own tone");

    /// <summary>
    ///     Two strings off, one waiting for a pluck, no record, no stems. This
    ///     is the state the screen exists for and the one no capture had.
    /// </summary>
    public static FakeTuningCheckScreen NeedsTuning() =>
        new("Stranded", "Gojira", "Drop C", "C G C F A D",
        [
            new FakePathTile("Lead", "LEAD", "876 notes", ""),
            new FakePathTile("Rhythm", "RHYTHM", "1,102 notes", ""),
            new FakePathTile("Bass", "BASS", "540 notes", "")
        ],
        [
            FakeTunerString.Off("C2", -38, "FLAT"), FakeTunerString.Verified("G2"),
            FakeTunerString.Awaiting("C3"), FakeTunerString.Off("F3", 22, "SHARP"),
            FakeTunerString.Verified("A3"), FakeTunerString.Verified("D4")
        ], isInputOpen: true, "Focusrite Scarlett 2i2 · input 1", 0.18, "-31.7 dB",
            "85 %", "2.0 S ON SCREEN", "Lenient", "No stems — full mix", "Global rig");

    /// <summary>
    ///     No input is open, so the check cannot run at all: the tuner panel
    ///     says so and the run's settings stay usable. A capability the app
    ///     cannot honour must never look live.
    /// </summary>
    public static FakeTuningCheckScreen NoInput() =>
        new("Creep", "Radiohead", "E Standard", "E A D G B E",
        [
            new FakePathTile("Lead", "LEAD", "312 notes", "S · 99.6 %")
        ], [], isInputOpen: false, "No guitar input", 0, "—",
            "100 %", "2.4 S ON SCREEN", "Normal", "Full band", "Global rig");

    /// <summary>
    ///     A chart with no arrangement in it: no path to pick, so no string to check and
    ///     nothing to start, while the run's settings stay on screen.
    /// </summary>
    public static FakeTuningCheckScreen Empty() =>
        new("Untitled", "", "E Standard", "E A D G B E", [], [], isInputOpen: true,
            "Focusrite Scarlett 2i2 · input 1", 0.04, "-48.0 dB",
            "100 %", "2.4 S ON SCREEN", "Normal", "Full band", "Global rig");

    /// <summary>
    ///     The most a chart offers: eight paths with and without records, and a
    ///     seven-string tuning with every string state on screen at once.
    /// </summary>
    public static FakeTuningCheckScreen Full() =>
        new("Pull Me Under", "Dream Theater", "B Standard (7-string)", "B E A D G B E",
        [
            new FakePathTile("Lead", "LEAD", "2,412 notes", "A · 92.8 %"),
            new FakePathTile("Rhythm", "RHYTHM", "1,980 notes", "B · 86.1 %"),
            new FakePathTile("Bass", "BASS", "1,204 notes", ""),
            new FakePathTile("Combo", "COMBO", "2,106 notes", "C · 78.4 %"),
            new FakePathTile("Alt. Lead", "LEAD", "2,388 notes", ""),
            new FakePathTile("Alt. Rhythm", "RHYTHM", "1,902 notes", ""),
            new FakePathTile("Bonus Lead", "LEAD", "2,644 notes", "S · 98.2 %"),
            new FakePathTile("Alt. Bass", "BASS", "1,176 notes", "")
        ],
        [
            FakeTunerString.Verified("B1"), FakeTunerString.Verified("E2"),
            FakeTunerString.Off("A2", -12, "FLAT"), FakeTunerString.Verified("D3"),
            FakeTunerString.Awaiting("G3"), FakeTunerString.Off("B3", 31, "SHARP"),
            FakeTunerString.Verified("E4")
        ], isInputOpen: true, "Focusrite Scarlett 2i2 · input 1", 0.71, "-9.8 dB",
            "100 %", "2.4 S ON SCREEN", "Strict", "Full band", "This song's own tone");

    /// <summary>The stress case: every string at its longest in three scripts.</summary>
    public static FakeTuningCheckScreen LongText()
    {
        var screen = new FakeTuningCheckScreen(FakeFlowText.LongSongTitle, FakeFlowText.LongArtist,
            FakeFlowText.LongTuning, "C# G# C# F# A# D# (capo VII)",
        [
            new FakePathTile(FakeFlowText.LongArrangement, "ALTERNATIVES LEAD-ARRANGEMENT",
                "12,345 notes", "S · 99.94 % · voller Combo"),
            new FakePathTile("非常に長いアレンジメント名のテストケース", "リード", "9,876 notes", ""),
            new FakePathTile("Rhythm (alternate, drop tuning, 12-string overdubs)", "RHYTHM",
                "8,204 notes", "A · 91.2 %")
        ],
        [
            FakeTunerString.Off("C#2", -49, "SEHR TIEF GESTIMMT"),
            FakeTunerString.Awaiting("G#2"),
            FakeTunerString.Verified("C#3"), FakeTunerString.Verified("F#3"),
            FakeTunerString.Off("A#3", 47, "ZU HOCH GESTIMMT"),
            FakeTunerString.Verified("D#4")
        ], isInputOpen: true,
            "Universal Audio Apollo Twin X DUO Heritage Edition · Instrument-Eingang 1", 0.94,
            "-0.4 dB (Übersteuerung droht)",
            "12.5 % — sehr langsames Üben", "18.4 S AUF DEM BILDSCHIRM",
            "Streng (kleinstes Trefferfenster)",
            "Getrennte Stems: Gitarre stumm, Band spielt weiter",
            "Eigenes Rig: Ein sehr langer Preset-Name für den Test")
        {
            MuteMyInstrument = true
        };
        return WithSetlist(screen, FakeSetlistRun.LongText());
    }

    // ── ITuningCheckScreen ────────────────────────────────────────────────

    public IGetReadySetlist Setlist { get; private set; } = FakeSetlistRun.Idle();

    public string SongTitle { get; }

    public string Artist { get; }

    public bool HasArtist => Artist.Length > 0;

    public string TuningLabel { get; }

    public string TuningNotes { get; }

    public bool HasBest => _selectedPath is { HasRecord: true };

    public string BestLabel => _selectedPath?.RecordLabel ?? "";

    public IReadOnlyList<IPathTile> Paths => _paths;

    public IPathTile? SelectedPath
    {
        get => _selectedPath;
        set
        {
            _selectedPath = value;
            foreach (var tile in _paths) tile.SetSelected(ReferenceEquals(tile, value));

            // Republish unconditionally — the tile's own visual has already
            // flipped by the time this runs, so an unchanged value still needs a
            // notification to put it back.
            OnPropertyChanged(nameof(SelectedPath));
            OnPropertyChanged(nameof(HasBest));
            OnPropertyChanged(nameof(BestLabel));
            OnPropertyChanged(nameof(HasTunerCheck));
            OnPropertyChanged(nameof(CanStart));
        }
    }

    public bool HasTunerCheck => IsInputOpen && _selectedPath != null && _strings.Count > 0;

    public bool AllVerified => _strings.Count > 0 && _strings.All(s => s.IsVerified);

    public string VerifiedLabel =>
        _strings.Count(s => s.IsVerified).ToString(CultureInfo.CurrentCulture)
        + " OF " + _strings.Count.ToString(CultureInfo.CurrentCulture) + " VERIFIED";

    public IReadOnlyList<ITunerString> Strings => _strings;

    public ICommand RestartTunerCommand { get; }

    public ICommand SkipTunerCommand { get; }

    public string InputLabel { get; }

    public bool IsInputOpen { get; }

    public double InputLevel { get; }

    public string InputLevelLabel { get; }

    public string SpeedLabel { get; }

    public string ScrollSpeedLabel { get; }

    public string HitWindowLabel { get; }

    public string BackingLabel { get; }

    public string ToneLabel { get; }

    public bool MuteMyInstrument
    {
        get => _muteMyInstrument;
        set => SetProperty(ref _muteMyInstrument, value);
    }

    public bool UseInGameTones
    {
        get => _useInGameTones;
        set => SetProperty(ref _useInGameTones, value);
    }

    public ICommand StartCommand { get; }

    public bool CanStart => _selectedPath != null;

    public ICommand BackCommand { get; }

    public ICommand SaveAsDefaultCommand { get; }

    public ICommand OpenRigCommand { get; }

    // ── the fake's own machinery ──────────────────────────────────────────

    internal void Pick(FakePathTile tile) => SelectedPath = tile;

    private void Republish()
    {
        OnPropertyChanged(nameof(AllVerified));
        OnPropertyChanged(nameof(VerifiedLabel));
    }
}
