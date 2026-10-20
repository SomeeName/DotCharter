using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Panels;

public partial class StringFingeringInput : ObservableObject
{
    [ObservableProperty] private string? _fingerText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PitchName))]
    private string? _fretText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PitchName))]
    private int _stringIndex;

    [ObservableProperty] private string _stringName = "";

    /// <summary>
    ///     The arrangement this row is measured against — its tuning offsets, its
    ///     capo and, for a bass, its octave. Set by the editor alongside
    ///     <see cref="StringName" />, which comes from the same place.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PitchName))]
    private Arrangement? _arrangement;

    /// <summary>
    ///     What this row's fret actually SOUNDS on this string — <c>A2</c>,
    ///     <c>C#4</c>.
    ///     <para>
    ///         COMPUTED, never pushed. The tuning letter beside a row names the
    ///         OPEN string and stops there; this is the note the fret on it
    ///         produces, which is the question a voicing under a capo or a
    ///         dropped tuning is reasoned about in. Deriving it from
    ///         <see cref="FretText" /> rather than filling it from the edit paths
    ///         is what makes it impossible for the readout to name the previous
    ///         fret — there is no path that rewrites a fret and forgets this.
    ///     </para>
    ///     <para>
    ///         Empty for a string the shape does not play and for a muted
    ///         (<c>x</c>) one: an unplayed string has no pitch, and a plausible
    ///         wrong note is worse than a blank.
    ///     </para>
    /// </summary>
    public string PitchName => SoundingPitchReadout.ForFretText(Arrangement, StringIndex, FretText);

    public StringFingeringInput(int index) => StringIndex = index;
}

public class TemplateListItem(ChordTemplate template)
{
    public ChordTemplate Template { get; } = template;
    public string DisplayName => string.IsNullOrWhiteSpace(Template.Name) ? $"Template {Template.Id}" : Template.Name;
}

/// <summary>
///     One offered containing shape, with a label legible enough to choose from.
///     <para>
///         The label has to answer "which grip is this" at a glance, and a
///         generated shape usually has no name — so it falls back to the frets
///         themselves. Naming what it ADDS ("+ s4:15") rather than restating the
///         whole shape is what makes a row of these comparable.
///     </para>
/// </summary>
public sealed record ShapeSuggestion(ChordTemplate Template, string Label)
{
    public static ShapeSuggestion For(ChordTemplate candidate, ChordTemplate seed)
    {
        var added = candidate.Frets
            .Where(kv => !seed.Frets.ContainsKey(kv.Key))
            .OrderBy(kv => kv.Key)
            .Select(kv => $"s{kv.Key}:{kv.Value}")
            .ToList();

        var name = string.IsNullOrWhiteSpace(candidate.Name)
            ? string.Join(" ", candidate.Frets.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"))
            : candidate.Name;

        return new ShapeSuggestion(candidate,
            added.Count > 0 ? $"{name}  + {string.Join(" ", added)}" : name);
    }
}
