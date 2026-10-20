using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;

namespace DotCharter.Presentation.Fakes.Dialogs;

/// <summary>A services-free <see cref="IToneCreatorScreen" />; Accept answers a key, Cancel null.</summary>
public sealed class FakeToneCreatorScreen : FakeDialog<string?>, IToneCreatorScreen
{
    private bool _isSaveAsNew;

    private FakeToneCreatorScreen(string description, IReadOnlyList<(string Role, string Gear)> rows, string status = "",
        string slotText = "Declared in Tone B of Lead", string acceptReason = "", bool canEvidence = true, bool busy = false,
        string judge = "", IReadOnlyList<string>? rounds = null)
    {
        CheckJudgeText = judge;
        CheckRounds = rounds ?? [];
        CheckUnavailableReason = canEvidence ? "" : "Needs the song's audio and beat map at the playhead to compare against";
        IsBusy = busy;
        Description = description;
        ProposalRows = [.. rows.Select(r => new Row(r.Role, r.Gear))];
        Status = status;
        NewToneSlotText = slotText;
        AcceptUnavailableReason = rows.Count == 0 ? "Create a proposal first." : acceptReason;
        CanUseSongEvidence = canEvidence;
        SongEvidenceText = canEvidence
            ? "Also measure the guitar stem at the playhead (1:02.4–1:12.4)"
            : "No song audio or beat map to measure: the tone comes from your words.";
        static ICommand Idle() => new RelayCommand(() => { });
        CreateCommand = Idle();
        StopCommand = Disabled();
        RefineCommand = rows.Count == 0 ? Disabled() : Idle();
        CheckCommand = canEvidence && !busy ? Idle() : Disabled();
        AcceptCommand = AcceptUnavailableReason.Length == 0 ? Answer("ai_tone") : Disabled();
        CancelCommand = Answer(null);
    }

    public static IReadOnlyList<FakeVariant<IToneCreatorScreen>> Variants { get; } =
    [
        new(FakeVariantNames.Default, Default),
        new(FakeVariantNames.Empty, Empty),
        new(FakeVariantNames.LongText, LongText),
        new(FakeVariantNames.Full, Full),
        new(FakeVariantNames.Busy, Busy)
    ];

    public static FakeToneCreatorScreen Default() => new("Master of Puppets rhythm, tight, scooped, no reverb",
    [
        ("Pre-pedal 1", "Overdrive (Gain 20, Tone 60, Level 80)"),
        ("Amplifier", "Mesa Rectifier (Gain 70, Bass 60, Mid 30, Treble 65)"),
        ("Cabinet", "4x12 Closed · 57 Cone"),
        ("Volume", "-14.0 dB")
    ], judge: "Judged by DotCharter's clone only, not by the game (Rocksmith with tonelab is not ready): 41 %",
    rounds:
    [
        "Round 1: Overdrive → Mesa Rectifier → 4x12 Closed (clone 33 %; the game did not judge it)",
        "Round 2: Mesa Rectifier → 4x12 Closed (clone 41 %; the game did not judge it)",
        "Round 3: every proposal was refused (unknown gear 'Pedal_Invented')"
    ]);

    public static FakeToneCreatorScreen Empty() => new("", [], canEvidence: false);

    /// <summary>German and Japanese text, a refusal naming invented gear, and an arrangement with no free slot.</summary>
    public static FakeToneCreatorScreen LongText() => new(
        "Verzerrter Rhythmus wie auf dem Album, eng und ohne Hall; 歪んだリズムギター、リバーブなし",
        [("Vorverstärkerpedal 1", "Übersteuerungspedal mit sehr langem Namen (Verstärkung 20, Klangfarbe 60, Pegel 80)"), ("アンプ", "クリーンアンプ（ゲイン 35）")],
        "Abgelehnt: das vorgeschlagene Effektgerät 'Pedal_ErfundenerVerzerrer' gibt es im Spiel nicht.",
        "Leadgitarre (alternative Stimmung) hat keinen freien Platz: Basis und A–D sind belegt.",
        "Leadgitarre (alternative Stimmung) hat keinen freien Platz: Basis und A–D sind belegt.",
        judge: "Nur von DotCharters Nachbildung bewertet, nicht vom Spiel (eine Live-Vorschau in Rocksmith spielt über das Spiel; schalte sie aus, um dort zu prüfen): 27 %",
        rounds: ["Runde 1: Übersteuerungspedal mit sehr langem Namen → クリーンアンプ → 4x12 (Nachbildung 27 %; vom Spiel nicht bewertet)"]);

    /// <summary>A call in flight: Stop shows, Create hides.</summary>
    public static FakeToneCreatorScreen Busy() => new(Default().Description, [], "Asking the AI…", busy: true);

    /// <summary>A full chain: four pre-pedals, racks and post-pedals.</summary>
    public static FakeToneCreatorScreen Full() => new("Lead solo with a wah, delay and a big hall",
    [
        .. Enumerable.Range(1, 4).Select(i => ($"Pre-pedal {i}", $"Pedal {i} (Level {i * 10})")),
        ("Amplifier", "Plexi (Gain 80)"),
        ("Cabinet", "4x12 Vintage · 121 Edge"),
        .. Enumerable.Range(1, 4).Select(i => ($"Rack {i}", $"Studio rack {i} (Mix {i * 5})")),
        .. Enumerable.Range(1, 4).Select(i => ($"Loop pedal {i}", $"Delay {i} (Time {i * 100})")),
        ("Volume", "-11.5 dB")
    ], judge: "Judged by Rocksmith itself: 58 %",
    rounds:
    [
        "Round 1: Wah → Plexi → 4x12 Vintage → Studio verb (clone 44 %, Rocksmith 51 %)",
        "Round 2: Wah → Plexi → 4x12 Vintage → Studio verb → Delay (clone 49 %, Rocksmith 58 %)",
        "Round 3: Wah → Plexi → 4x12 Vintage → Delay (clone 50 %, Rocksmith 55 %)"
    ]);

    public string Description { get; set; }
    public int MaxLength => 500;
    public bool IsRocksmithGearOnly { get => true; set { } }
    public bool IsDotCharterRig { get => false; set { } }
    public bool CanUseDotCharterRig => false;
    public string DotCharterRigReason => "A DotCharter rig cannot be saved as a Rocksmith tone.";
    public bool UseSongEvidence { get; set; }
    public bool CanUseSongEvidence { get; }
    public string SongEvidenceText { get; }
    public ICommand CreateCommand { get; }
    public ICommand StopCommand { get; }
    public bool IsBusy { get; }
    public string Status { get; }
    public bool HasStatus => Status.Length > 0;
    public bool HasProposal => ProposalRows.Count > 0;
    public IReadOnlyList<IToneProposalRow> ProposalRows { get; }
    public string Rationale => HasProposal ? "A high-gain amp, scooped mids and a closed cabinet keep the riff tight." : "";
    public string EvidenceNote => HasProposal ? "From your words only" : "";
    public bool IsAuditioning { get; set; }
    public bool CanAudition => HasProposal;
    public string AuditionUnavailableReason => HasProposal ? "" : "Create a proposal first.";
    public string RefineText { get; set; } = "";
    public ICommand RefineCommand { get; }
    public ICommand CheckCommand { get; }
    public bool CanCheck => CheckUnavailableReason.Length == 0;
    public string CheckUnavailableReason { get; }
    public bool HasCheckResult => CheckRounds.Count > 0;
    public string CheckJudgeText { get; }
    public IReadOnlyList<string> CheckRounds { get; }

    public bool IsReplaceEdited
    {
        get => !_isSaveAsNew;
        set => SetProperty(ref _isSaveAsNew, !value);
    }

    public bool IsSaveAsNew
    {
        get => _isSaveAsNew;
        set => IsReplaceEdited = !value;
    }

    public bool CanReplaceEdited => true;
    public string ReplaceEditedText => "Replace “Clean verse”";
    public string NewToneName { get; set; } = "Rhythm";
    public string NewToneSlotText { get; }
    public bool PlaceAtPlayhead { get; set; }
    public string PlaceAtPlayheadText => "Place a tone change at the playhead (1:02.4)";
    public string AcceptUnavailableReason { get; }
    public bool HasAcceptUnavailableReason => AcceptUnavailableReason.Length > 0;
    public ICommand AcceptCommand { get; }
    public ICommand CancelCommand { get; }

    private sealed record Row(string Role, string Gear) : IToneProposalRow;
}
