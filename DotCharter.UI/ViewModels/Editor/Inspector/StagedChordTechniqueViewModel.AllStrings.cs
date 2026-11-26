using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     The chord Technique page's "Chord techniques" rows: the note's rows (mute, HOPO, harmonic, picking as
///     none-first segmented choices, and tremolo) applied to EVERY string of every chord the "Apply to" ladder reaches,
///     inside the string-edit mask, in one stage (one Apply, one undo step). A row reads -1 (mixed: no option lit) when
///     those strings disagree; the per-string rows above stay for the odd string.
/// </summary>
public sealed partial class StagedChordTechniqueViewModel
{
    public int AllMute => AllChoice(TechniqueField.Mute);

    public int AllHopo => AllChoice(TechniqueField.Hopo);

    public int AllHarmonic => AllChoice(TechniqueField.Harmonic);

    public int AllBassPicking => AllChoice(TechniqueField.BassPicking);

    /// <summary>Tremolo on every string; null when the strings disagree.</summary>
    public bool? AllTremolo => AllChoice(TechniqueField.Tremolo) switch
    {
        -1 => null,
        var v => v == 1
    };

    /// <summary>The rows take an edit: a chord with at least one string inside the mask.</summary>
    public bool CanEditAllStrings => AllStrings().Any();

    [RelayCommand]
    private void SetAllMute(string choice) => StageAll(TechniqueField.Mute, Parse(choice));

    [RelayCommand]
    private void SetAllHopo(string choice) => StageAll(TechniqueField.Hopo, Parse(choice));

    [RelayCommand]
    private void SetAllHarmonic(string choice) => StageAll(TechniqueField.Harmonic, Parse(choice));

    [RelayCommand]
    private void SetAllBassPicking(string choice) => StageAll(TechniqueField.BassPicking, Parse(choice));

    /// <summary>Tremolo on every string, or off on every string when every string has it.</summary>
    [RelayCommand]
    private void ToggleAllTremolo() => StageAll(TechniqueField.Tremolo, AllTremolo == true ? 0 : 1);

    /// <summary>Stages <paramref name="value" /> on every string the rows reach, then re-reads every row (a click on
    /// the option already lit puts its highlight back).</summary>
    private void StageAll(TechniqueField field, int value)
    {
        var strings = AllStrings().ToList();
        if (strings.Count == 0 || value < 0) return;
        foreach (var stringIndex in strings) Engine.StageTechnique(new StagedKey(field, stringIndex), value);
        Staged();
        Reread();
    }

    /// <summary>The value every reached string holds (its staged value, else its own), or -1 when they disagree.</summary>
    private int AllChoice(TechniqueField field)
    {
        var values = Reached().Select(p => Engine.Technique.TryGetValue(new StagedKey(field, p.StringIndex), out var staged)
            ? staged ?? 0
            : StagedTechnique.Read(p.Chord, new StagedKey(field, p.StringIndex), out _) ?? 0).Distinct().Take(2).ToList();
        return values.Count == 1 ? values[0] : values.Count == 0 ? 0 : -1;
    }

    /// <summary>The played string indices of every reached chord inside the mask.</summary>
    private IEnumerable<int> AllStrings() => Reached().Select(p => p.StringIndex).Distinct().Order();

    private IEnumerable<(Chord Chord, int StringIndex)> Reached()
    {
        if (Chord is null) return [];
        var mask = Owner.ProjectState.StringEditMask;
        return Engine.Targets(StagedPage.Technique).OfType<Chord>()
            .SelectMany(c => c.ChordNotes.Where(n => mask.Includes(n.StringIndex)).Select(n => (c, n.StringIndex)));
    }

    private static int Parse(string choice) => int.Parse(choice, System.Globalization.CultureInfo.InvariantCulture);
}
