using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Controls;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>One on/off value of the Selection page's Values card: Keep (leave each sound), On or Off. Its caption
/// names the flag and either what the selection holds now (Keep) or what Apply writes.</summary>
public sealed partial class StagedBulkFlag(BulkFlag flag) : ObservableObject
{
    public BulkFlag Flag { get; } = flag;

    [ObservableProperty] private string _caption = string.Empty;

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private string _tip = string.Empty;

    /// <summary>A value is staged (On or Off), not Keep.</summary>
    [ObservableProperty] private bool _isStaged;
}

/// <summary>
///     The Selection page of several sounds (design D's "Move N sounds now", "Values for Apply" and summary
///     cards). Move steps the selection at once (one history node each; the footer asks first when a step reaches
///     several sounds or leaves one where it is). Values are STAGED (<see cref="InspectorStagedEdits.StageBulk" />)
///     and written by the footer's Apply: Keep leaves each sound, a value writes every target; a chord whose strings
///     disagree reads mixed.
/// </summary>
public sealed partial class StagedSelectionViewModel : StagedPageViewModel
{
    private static readonly int?[] SlideSteps = [null, 0, 1, 2, -1, -2];

    // Each flag's short caption and its full name.
    private static readonly Dictionary<BulkFlag, (string Short, string Name)> FlagKeys = new()
    {
        [BulkFlag.LinkNext] = ("Inspector_Staged_FlagLinkNext", "Inspector_LinkNext"),
        [BulkFlag.Ignore] = ("Inspector_Staged_FlagIgnore", "Inspector_Ignore"),
        [BulkFlag.Accent] = ("Inspector_Staged_FlagAccent", "Inspector_Accent"),
        [BulkFlag.Vibrato] = ("Inspector_Staged_FlagVibrato", "Inspector_Vibrato"),
        [BulkFlag.Tremolo] = ("Inspector_Staged_FlagTremolo", "Inspector_Tremolo"),
        [BulkFlag.PassOtherNotes] = ("Inspector_Staged_FlagPassOther", "Inspector_PassOther")
    };

    internal StagedSelectionViewModel(InspectorStagedPages owner) : base(owner) =>
        Flags = [.. Enum.GetValues<BulkFlag>().Select(f => new StagedBulkFlag(f))];

    /// <summary>The six on/off values, in the card's order.</summary>
    public IReadOnlyList<StagedBulkFlag> Flags { get; }

    private BulkDraft Draft => Engine.Bulk;

    private IReadOnlyList<ITimedElement> Targets => Engine.Targets(StagedPage.Selection);

    private ISelectionService Selection => Owner.Editor.Selection;

    // ---- Move ---------------------------------------------------------------

    public string MoveTitle => Loc.Format("Inspector_Staged_MoveTitle", Selection.SelectedNotes.Count + Selection.SelectedChords.Count);

    public string MoveSummary => Loc.Format("Inspector_Staged_MoveSummary", Selection.SelectedNotes.Count, Selection.SelectedChords.Count);

    /// <summary>Where the first selected sound is: "7f / str 3 / 1/8".</summary>
    public string FirstSound
    {
        get
        {
            var first = Selection.SelectedNotes.Cast<ITimedElement>().Concat(Selection.SelectedChords)
                .MinBy(s => s.GetTimeMs(Owner.Editor.Beats));
            return first switch
            {
                GuitarNote n => Loc.Format("Inspector_Staged_FirstSound", n.Fret, GuitarStringLabel.Number(n.StringIndex, Owner.StringCount),
                    FractionalLengthFormat.Format(n.Sustain.DoubleValue) ?? "0"),
                Chord c when c.ChordNotes.Count > 0 => Loc.Format("Inspector_Staged_FirstSound", c.ChordNotes.Min(n => n.Fret),
                    GuitarStringLabel.Number(c.ChordNotes.Max(n => n.StringIndex), Owner.StringCount),
                    FractionalLengthFormat.Format(c.GetMaxSustain().DoubleValue) ?? "0"),
                _ => Loc["Inspector_None"]
            };
        }
    }

    [RelayCommand]
    private void MoveFret(string? delta) => Move(BulkMove.Fret, delta);

    [RelayCommand]
    private void MoveString(string? delta) => Move(BulkMove.String, delta);

    [RelayCommand]
    private void MoveSustain(string? delta) => Move(BulkMove.Sustain, delta);

    private void Move(BulkMove move, string? delta)
    {
        if (int.TryParse(delta, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steps)) Owner.Footer.Move(move, steps);
    }

    // ---- Values -------------------------------------------------------------

    /// <summary>Which selected sounds the values reach: notes and chords, notes, chords.</summary>
    public int SourceIndex
    {
        get => (int)Engine.BulkSource;
        set
        {
            if (value < 0 || value == SourceIndex) return;
            Engine.BulkSource = (BulkSource)value;
            Staged();
        }
    }

    /// <summary>The staged sustain, else the one every target agrees on; empty when they differ.</summary>
    public string SustainText
    {
        get => (Draft.Sustain ?? AgreedSustain) is { } sustain ? FractionalLengthFormat.Format(sustain.DoubleValue) ?? "0" : string.Empty;
        set
        {
            if (value == SustainText) return;
            if (string.IsNullOrWhiteSpace(value))
            {
                StageBulk(Draft with { Sustain = null });
                return;
            }

            if (!FractionalLengthFormat.TryParseBeats(value, out var numerator, out var denominator))
            {
                Reread();
                return;
            }

            StageBulk(Draft with { Sustain = new FractionalPosition(new Fraction(numerator, denominator)) });
        }
    }

    /// <summary>The sustain box's hint while the targets differ: "Mixed 1/16–1".</summary>
    public string SustainHint
    {
        get
        {
            var sustains = Targets.Select(t => Sustain(t).DoubleValue).ToList();
            return sustains.Count == 0
                ? Loc["Inspector_NoteLength"]
                : Loc.Format("Inspector_Staged_SustainMixed", FractionalLengthFormat.Format(sustains.Min()) ?? "0",
                    FractionalLengthFormat.Format(sustains.Max()) ?? "0");
        }
    }

    public int MuteIndex { get => Index(Draft.Mute); set => StageChoice(value, Draft.Mute, v => Draft with { Mute = v is null ? null : (MuteType)v }); }

    public int StrumIndex { get => Index(Draft.Strum); set => StageChoice(value, Draft.Strum, v => Draft with { Strum = v is null ? null : (PickDirectionType)v }); }

    public int LegatoIndex { get => Index(Draft.Legato); set => StageChoice(value, Draft.Legato, v => Draft with { Legato = v is null ? null : (HopoType)v }); }

    public int HarmonicIndex { get => Index(Draft.Harmonic); set => StageChoice(value, Draft.Harmonic, v => Draft with { Harmonic = v is null ? null : (HarmonicType)v }); }

    public int SlideIndex
    {
        get => Array.IndexOf(SlideSteps, Draft.Slide) is var i and >= 0 ? i : 0;
        set
        {
            if (value < 0 || value >= SlideSteps.Length || value == SlideIndex) return;
            StageBulk(Draft with { Slide = SlideSteps[value] });
        }
    }

    /// <summary>Which of the Keep choices the targets disagree on now ("Mixed now: legato, harmonic"); empty when none.</summary>
    public string MixedSummary
    {
        get
        {
            var targets = Targets;
            var notes = targets.OfType<GuitarNote>().Cast<ITimedElement>().ToList();
            var mixed = new List<string>();
            if (Draft.Mute is null && notes.Count > 1 && StagedBulk.ReadChoice(notes, s => (int)s.Mute) is null) mixed.Add(Loc["Inspector_Mute"]);
            var chords = targets.OfType<Chord>().ToList();
            if (Draft.Strum is null && chords.Count > 1 && StagedBulk.Agreed(chords.Select(c => (int?)c.Strum)) is null) mixed.Add(Loc["Inspector_Strum"]);
            if (Draft.Legato is null && targets.Count > 0 && StagedBulk.ReadChoice(targets, s => (int)s.Hopo) is null) mixed.Add(Loc["Inspector_Hopo"]);
            if (Draft.Harmonic is null && targets.Count > 0 && StagedBulk.ReadChoice(targets, s => (int)s.Harmonic) is null) mixed.Add(Loc["Inspector_Harmonic"]);
            return mixed.Count == 0 ? string.Empty : Loc.Format("Inspector_Staged_MixedNow", string.Join(", ", mixed));
        }
    }

    public bool HasMixed => MixedSummary.Length > 0;

    /// <summary>A flag button: Keep → On → Off → Keep.</summary>
    [RelayCommand]
    private void CycleFlag(BulkFlag flag)
    {
        var flags = new Dictionary<BulkFlag, bool>(Draft.Flags);
        if (!flags.TryGetValue(flag, out var on)) flags[flag] = true;
        else if (on) flags[flag] = false;
        else flags.Remove(flag);
        StageBulk(Draft with { Flags = flags });
    }

    internal override void Reread()
    {
        var targets = Targets;
        foreach (var item in Flags)
        {
            var label = Loc[FlagKeys[item.Flag].Short];
            var name = Loc[FlagKeys[item.Flag].Name];
            var now = StagedBulk.Read(targets, item.Flag);
            var nowWord = Loc[now switch { true => "Inspector_Staged_StateOn", false => "Inspector_Staged_StateOff", _ => "Inspector_Staged_StateMixed" }];
            var staged = Draft.Flags.TryGetValue(item.Flag, out var set) ? set : (bool?)null;
            item.IsStaged = staged is not null;
            item.Caption = staged is { } value
                ? Loc.Format("Inspector_Staged_FlagSet", label, Loc[value ? "Inspector_Staged_StateOn" : "Inspector_Staged_StateOff"])
                : Loc.Format("Inspector_Staged_FlagNow", label, nowWord);
            item.Name = name;
            item.Tip = Loc.Format("Inspector_Staged_FlagTip", name, nowWord);
        }

        base.Reread();
    }

    private FractionalPosition? AgreedSustain
    {
        get
        {
            var sustains = Targets.Select(Sustain).Distinct().ToList();
            return sustains.Count == 1 ? sustains[0] : null;
        }
    }

    private static FractionalPosition Sustain(ITimedElement sound) => sound switch
    {
        GuitarNote n => n.Sustain,
        Chord c => c.GetMaxSustain(),
        _ => default
    };

    private static int Index<T>(T? value) where T : struct, Enum => value is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) + 1 : 0;

    private void StageChoice<T>(int index, T? current, Func<int?, BulkDraft> change) where T : struct, Enum
    {
        if (index < 0 || index == Index(current)) return;
        StageBulk(change(index == 0 ? null : index - 1));
    }

    private void StageBulk(BulkDraft draft)
    {
        Engine.StageBulk(draft);
        Staged();
    }
}
