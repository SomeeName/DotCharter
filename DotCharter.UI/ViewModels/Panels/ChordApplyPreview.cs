using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
/// The chord panel's bulk-edit PREVIEW. While the "Apply to" rung
///     reaches more than the selection, every chord and hand shape the next edit
///     would change is published as the editor's <see cref="ChordUseHighlight" />:
///     the 2D timeline outlines them and the song map marks them across the whole
///     song. "Previous / next" step the playhead between them, and the uses on
///     other difficulty levels — which neither surface shows — get their own count.
///     <para>
///         The set is <see cref="ChordApplyScope.Preview" /> of the scope the edit
///         is resolved on: the same resolver, never a second computation, so the
///         preview cannot disagree with what the edit writes.
///     </para>
///     <para>
///         It only shows while a panel host is on screen
///         (<see cref="SetHostShown" />, fed by each view's
///         <c>EffectiveVisibilityWatcher</c>): the rung is sticky for the session,
///         and marks for a panel nobody can see would be noise.
///     </para>
/// </summary>
public sealed partial class ChordApplyPreview : ObservableObject
{
    private readonly ChartEditorViewModel _editor;
    private readonly ILocalizationService _loc;
    private readonly HashSet<object> _shownHosts = new(ReferenceEqualityComparer.Instance);
    private ChordUseHighlight _resolved = ChordUseHighlight.None;

    [ObservableProperty] private bool _isActive;

    /// <summary>"12 changes marked · + 18 on other levels".</summary>
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>The level on screen has a mark to step to.</summary>
    [ObservableProperty] private bool _canStep;

    public ChordApplyPreview(ChartEditorViewModel editor, ILocalizationService loc)
    {
        _editor = editor;
        _loc = loc;
    }

    /// <summary>The set the panel resolved last, shown or not — test seam.</summary>
    internal ChordUseHighlight Resolved => _resolved;

    /// <summary>A ladder is on screen (a host reported itself effectively visible) — what the
    /// keyboard's technique verbs ask before they follow the rung past the selection.</summary>
    internal bool IsHostShown => _shownHosts.Count > 0;

    /// <summary>Re-reads the preview off <paramref name="scope" /> for the rung
    /// <paramref name="chosen" />. Called with every ladder refresh;
    /// <paramref name="selection" /> is the panel's selection, companion hand
    /// shapes folded in — the sounds the charter already sees as "selected".</summary>
    internal void Update(ChordApplyScope? scope, ChordApplyRung chosen, ChordApplySounds selection,
        ChordApplyFamily family = ChordApplyFamily.Shape)
    {
        var sounds = scope?.Preview(chosen, family) ?? ChordApplySounds.None;
        _resolved = ReachesBeyond(sounds, selection)
            ? ChordUseHighlight.From(sounds.Chords, sounds.HandShapes, _editor.ActiveArrangement)
            : ChordUseHighlight.None;
        Publish();
    }

    /// <summary>A panel host reports whether it is effectively visible; the
    /// preview shows while at least one is.</summary>
    internal void SetHostShown(object host, bool shown)
    {
        var changed = shown ? _shownHosts.Add(host) : _shownHosts.Remove(host);
        if (changed) Publish();
    }

    [RelayCommand]
    private void PreviousTarget() => _editor.SeekToUseHighlight(-1);

    [RelayCommand]
    private void NextTarget() => _editor.SeekToUseHighlight(1);

    private void Publish()
    {
        var shown = _shownHosts.Count > 0 ? _resolved : ChordUseHighlight.None;
        _editor.PublishUseHighlight(shown);
        IsActive = !shown.IsEmpty;
        if (!IsActive)
        {
            Summary = string.Empty;
            CanStep = false;
            return;
        }

        var here = shown.On(_editor.PlacedChords).Count;
        var elsewhere = shown.ElsewhereThan(_editor.PlacedChords);
        CanStep = here > 0;
        var marked = _loc.Plural("ChordTemplate_Preview_MarkedOne", "ChordTemplate_Preview_Marked", here);
        Summary = elsewhere > 0
            ? $"{marked} · {_loc.Format("ChordTemplate_Preview_OtherLevels", elsewhere)}"
            : marked;
    }

    /// <summary>True when the set holds a sound the selection does not: only then
    /// is there something off screen, or unselected, to show.</summary>
    private static bool ReachesBeyond(ChordApplySounds sounds, ChordApplySounds selection)
    {
        if (sounds.Count == 0) return false;
        var chords = new HashSet<Chord>(selection.Chords, ReferenceEqualityComparer.Instance);
        var handShapes = new HashSet<HandShape>(selection.HandShapes, ReferenceEqualityComparer.Instance);
        return sounds.Chords.Any(c => !chords.Contains(c)) || sounds.HandShapes.Any(h => !handShapes.Contains(h));
    }
}
