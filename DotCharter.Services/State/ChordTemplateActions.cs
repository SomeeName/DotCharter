using System.Collections.ObjectModel;
using DotCharter.Services.Editing;

namespace DotCharter.Services.State;

/// <summary>
/// Undoable edit that mutates an existing <see cref="ChordTemplate"/> in place
/// (name / arpeggio flag / fret + finger pattern) AND propagates the new chord
/// notes to every <see cref="Chord"/> currently bound to that template.
/// </summary>
public sealed class EditTemplateGlobalAction : IMergeableAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    /// <summary>The verb a chord-NAME keystroke opts into, so a run of them
    /// within <c>HistoryService.MergeWindow</c> is ONE undo node — the way a
    /// sustain drag or a wheel run already is. Without merging, an
    /// 8-character rename would be eight nodes.</summary>
    public const string NameMergeVerb = "chord-template-name";

    private readonly IChartEditorSurface _editor;
    private List<Chord> _newChords;
    private ChordTemplate _newTemplate;
    private readonly List<Chord> _oldChords;
    private readonly ChordTemplate _oldTemplate;
    private readonly List<Chord> _targetChords;
    private readonly ChordTemplate _template;

    public EditTemplateGlobalAction(
        ChordTemplate template, ChordTemplate oldTemplate, ChordTemplate newTemplate,
        List<Chord> targetChords, List<Chord> newChords, IChartEditorSurface editor,
        string mergeVerb = "")
    {
        _template = template;
        _oldTemplate = oldTemplate;
        _newTemplate = newTemplate;
        _targetChords = targetChords;
        _oldChords = targetChords.Select(c => c.Clone()).ToList();
        _newChords = newChords;
        _editor = editor;
        MergeVerb = mergeVerb;
    }

    public string MergeVerb { get; }

    /// <summary>The same template, the same target chords — by reference, in
    /// order — so a merged tick can only ever rewrite the record of the edit
    /// that is already on the chart.</summary>
    public bool CanAbsorb(IChartAction next)
    {
        if (next is not EditTemplateGlobalAction other) return false;
        if (!ReferenceEquals(other._template, _template)) return false;
        if (other._targetChords.Count != _targetChords.Count) return false;
        for (var i = 0; i < _targetChords.Count; i++)
        {
            if (!ReferenceEquals(_targetChords[i], other._targetChords[i]))
                return false;
        }

        return true;
    }

    /// <summary>Keeps our origin, takes over the tick's destination — undo of
    /// the merged node lands on the name the user started typing from.</summary>
    public void Absorb(IChartAction next)
    {
        var other = (EditTemplateGlobalAction)next;
        _newTemplate = other._newTemplate;
        _newChords = other._newChords;
    }

    public void Execute()
    {
        _template.Name = _newTemplate.Name;
        // BOTH arpeggio flags. Carrying only the SNG one meant a state the
        // caller had built (clearing an RS-XML `-arp` template) never reached
        // the chart, and the undo could not put a changed one back.
        _template.Arpeggio = _newTemplate.Arpeggio;
        _template.ForceArpeggioInRs = _newTemplate.ForceArpeggioInRs;
        _template.NopInRs = _newTemplate.NopInRs;
        _template.FollowedFingering = _newTemplate.FollowedFingering;
        _template.Fingerless = _newTemplate.Fingerless;
        _template.GhostMask = _newTemplate.GhostMask;
        _template.Pinned = _newTemplate.Pinned;
        _template.Frets = new Dictionary<int, int>(_newTemplate.Frets);
        _template.Fingers = new Dictionary<int, int>(_newTemplate.Fingers);
        for (var i = 0; i < _targetChords.Count; i++)
        {
            _targetChords[i].ChordNotes =
                new ObservableCollection<ChordNote>(_newChords[i].ChordNotes.Select(n => n.Clone()));
        }

        _editor.RequestRedraw();
    }

    public void Undo()
    {
        _template.Name = _oldTemplate.Name;
        _template.Arpeggio = _oldTemplate.Arpeggio;
        _template.ForceArpeggioInRs = _oldTemplate.ForceArpeggioInRs;
        _template.NopInRs = _oldTemplate.NopInRs;
        _template.FollowedFingering = _oldTemplate.FollowedFingering;
        _template.Fingerless = _oldTemplate.Fingerless;
        _template.GhostMask = _oldTemplate.GhostMask;
        _template.Pinned = _oldTemplate.Pinned;
        _template.Frets = new Dictionary<int, int>(_oldTemplate.Frets);
        _template.Fingers = new Dictionary<int, int>(_oldTemplate.Fingers);
        for (var i = 0; i < _targetChords.Count; i++)
        {
            _targetChords[i].ChordNotes =
                new ObservableCollection<ChordNote>(_oldChords[i].ChordNotes.Select(n => n.Clone()));
        }

        _editor.RequestRedraw();
    }
}

/// <summary>
/// Reassigns <see cref="Chord.TemplateId"/> + <see cref="HandShape.TemplateId"/>
/// on a batch of selected items to point at a different existing template,
/// and refreshes their <see cref="Chord.ChordNotes"/> to match.
/// </summary>
public sealed class SwitchTemplateAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly IChartEditorSurface _editor;
    private readonly List<Chord> _newChords;
    private readonly List<HandShape> _newHandShapes;
    private readonly List<Chord> _oldChords;
    private readonly List<HandShape> _oldHandShapes;
    private readonly List<Chord> _targetChords;
    private readonly List<HandShape> _targetHandShapes;

    public SwitchTemplateAction(
        List<Chord> targetChords, List<Chord> newChords,
        List<HandShape> targetHandShapes, List<HandShape> newHandShapes,
        IChartEditorSurface editor)
    {
        _targetChords = targetChords;
        _oldChords = targetChords.Select(c => c.Clone()).ToList();
        _newChords = newChords;
        _targetHandShapes = targetHandShapes;
        _oldHandShapes = targetHandShapes.Select(h => h.Clone()).ToList();
        _newHandShapes = newHandShapes;
        _editor = editor;
    }

    public void Execute()
    {
        for (var i = 0; i < _targetChords.Count; i++)
        {
            _targetChords[i].TemplateId = _newChords[i].TemplateId;
            _targetChords[i].ChordNotes =
                new ObservableCollection<ChordNote>(_newChords[i].ChordNotes.Select(n => n.Clone()));
        }

        for (var i = 0; i < _targetHandShapes.Count; i++)
            _targetHandShapes[i].TemplateId = _newHandShapes[i].TemplateId;
        _editor.RequestRedraw();
    }

    public void Undo()
    {
        for (var i = 0; i < _targetChords.Count; i++)
        {
            _targetChords[i].TemplateId = _oldChords[i].TemplateId;
            _targetChords[i].ChordNotes =
                new ObservableCollection<ChordNote>(_oldChords[i].ChordNotes.Select(n => n.Clone()));
        }

        for (var i = 0; i < _targetHandShapes.Count; i++)
            _targetHandShapes[i].TemplateId = _oldHandShapes[i].TemplateId;
        _editor.RequestRedraw();
    }
}

/// <summary>
/// Adds a brand-new <see cref="ChordTemplate"/> to the arrangement and
/// re-points the selected chords + handshapes at it. Undo removes the new
/// template and restores the original template ids.
/// </summary>
/// <remarks>
///     <para>
///         <b>The template COLLECTION is captured in the constructor, exactly as
///         <c>AddChordTemplatesAction</c> captures it</b> — never re-read from
///         <c>ActiveArrangement</c> inside Execute/Undo. An undo node describes an
///         edit to ONE arrangement, and nothing puts that arrangement back before
///         it runs: <c>HistoryService</c>'s <c>FocusRequested</c> reaches
///         <c>TrackContextRegistry.Focus</c>, which sets <c>FocusedTrack</c> and
///         has no subscriber, so the arrangement at undo time is whichever one the
///         user is looking at.
///     </para>
///     <para>
///         Resolving it late is not a missed refresh, it is data loss in both
///         directions. Undo on a different arrangement removes nothing, so the
///         template stays and is saved into the <c>.rscp</c> as an orphan. Redo
///         then ADDS it to that other arrangement while re-pointing the ORIGINAL
///         arrangement's chords at its id — and the id was minted as
///         <c>max(Id) + 1</c> of the original, so in the other arrangement it
///         either does not exist, in which case <c>SngFromDomainMapper</c> filters
///         on <c>TemplateIndexById.ContainsKey</c> and silently drops those chords
///         from the exported psarc, or it collides with an unrelated template and
///         they adopt its shape.
///     </para>
/// </remarks>
public sealed class CreateAndSwitchTemplateAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly ObservableCollection<ChordTemplate> _templates;
    private readonly IChartEditorSurface _editor;
    private readonly List<Chord> _newChords;
    private readonly List<HandShape> _newHandShapes;
    private readonly ChordTemplate _newTemplate;
    private readonly List<Chord> _oldChords;
    private readonly List<HandShape> _oldHandShapes;
    private readonly List<Chord> _targetChords;
    private readonly List<HandShape> _targetHandShapes;

    public CreateAndSwitchTemplateAction(
        ObservableCollection<ChordTemplate> templates,
        ChordTemplate newTemplate,
        List<Chord> targetChords, List<Chord> newChords,
        List<HandShape> targetHandShapes, List<HandShape> newHandShapes,
        IChartEditorSurface editor)
    {
        _templates = templates;
        _newTemplate = newTemplate;
        _targetChords = targetChords;
        _oldChords = targetChords.Select(c => c.Clone()).ToList();
        _newChords = newChords;
        _targetHandShapes = targetHandShapes;
        _oldHandShapes = targetHandShapes.Select(h => h.Clone()).ToList();
        _newHandShapes = newHandShapes;
        _editor = editor;
    }

    public void Execute()
    {
        // Identity again, and for the same reason Undo needs it: a redo after an
        // undo must not add a SECOND instance, and a value-equal twin already in
        // the collection is not this action's template.
        if (!ChordTemplateCollectionOps.ContainsInstance(_templates, _newTemplate))
            _templates.Add(_newTemplate);

        for (var i = 0; i < _targetChords.Count; i++)
        {
            _targetChords[i].TemplateId = _newChords[i].TemplateId;
            _targetChords[i].ChordNotes =
                new ObservableCollection<ChordNote>(_newChords[i].ChordNotes.Select(n => n.Clone()));
        }

        for (var i = 0; i < _targetHandShapes.Count; i++)
            _targetHandShapes[i].TemplateId = _newHandShapes[i].TemplateId;
        _editor.RequestRedraw();
    }

    public void Undo()
    {
        // BY REFERENCE. ChordTemplate has value equality that ignores Id, so a
        // plain Remove deletes the first template that merely LOOKS like the one
        // this action created — leaving every chord that referenced the real one
        // with a dangling TemplateId, which the SNG export drops the chord for.
        ChordTemplateCollectionOps.RemoveInstance(_templates, _newTemplate);

        for (var i = 0; i < _targetChords.Count; i++)
        {
            _targetChords[i].TemplateId = _oldChords[i].TemplateId;
            _targetChords[i].ChordNotes =
                new ObservableCollection<ChordNote>(_oldChords[i].ChordNotes.Select(n => n.Clone()));
        }

        for (var i = 0; i < _targetHandShapes.Count; i++)
            _targetHandShapes[i].TemplateId = _oldHandShapes[i].TemplateId;
        _editor.RequestRedraw();
    }
}

/// <summary>
/// Lightweight undoable swap of just a template's <see cref="ChordTemplate.Fingers" />
/// dict — used by the auto-fingering generator and by DRY propagation, where
/// the fret pattern and chord notes never change.
/// <para>
/// Every caller sets a fingering someone CHOSE (the panel's "same grip" rung,
/// the 1·3 / 1·4 switches, Generate fingerings), never the FHP generator's
/// shifted grip, so it also clears <see cref="ChordTemplate.FollowedFingering" />
/// and undo puts the old flag back. A fingering that sets any finger
/// also clears <see cref="ChordTemplate.Fingerless" />: the grip is no
/// longer fingerless once someone fingers it.
/// </para>
/// </summary>
public sealed class SetTemplateFingersAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly IChartEditorSurface _editor;
    private readonly Dictionary<int, int> _newFingers;
    private readonly Dictionary<int, int> _oldFingers;
    private readonly bool _oldFollowed;
    private readonly bool _oldFingerless;
    private readonly ChordTemplate _template;

    public SetTemplateFingersAction(ChordTemplate template,
        Dictionary<int, int> oldFingers, Dictionary<int, int> newFingers, IChartEditorSurface editor)
    {
        _template = template;
        _oldFingers = oldFingers;
        _newFingers = newFingers;
        _oldFollowed = template.FollowedFingering;
        _oldFingerless = template.Fingerless;
        _editor = editor;
    }

    /// <summary>Keeps <paramref name="template" />'s fingers and marks them
    /// AUTHORED: a hand edit resolved onto a template the FHP generator minted.</summary>
    public static SetTemplateFingersAction MarkAuthored(ChordTemplate template, IChartEditorSurface editor) =>
        new(template, new Dictionary<int, int>(template.Fingers), new Dictionary<int, int>(template.Fingers), editor);

    public bool HasEffect => _oldFollowed || ClearsFingerless || !FingersDictHelper.Equal(_oldFingers, _newFingers);

    private bool ClearsFingerless => _oldFingerless && _newFingers.Count > 0;

    public void Execute()
    {
        _template.Fingers = new Dictionary<int, int>(_newFingers);
        _template.FollowedFingering = false;
        if (ClearsFingerless) _template.Fingerless = false;
        _editor.RequestRedraw();
    }

    public void Undo()
    {
        _template.Fingers = new Dictionary<int, int>(_oldFingers);
        _template.FollowedFingering = _oldFollowed;
        _template.Fingerless = _oldFingerless;
        _editor.RequestRedraw();
    }
}

/// <summary>
/// Lightweight undoable swap of just a template's <see cref="ChordTemplate.Name" />
/// — used by the auto-fill generator to fill EMPTY names from the pitch-based
/// suggester (a user-authored name is never overwritten there).
/// </summary>
public sealed class SetTemplateNameAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly IChartEditorSurface _editor;
    private readonly string _newName;
    private readonly string _oldName;
    private readonly ChordTemplate _template;

    public SetTemplateNameAction(ChordTemplate template, string oldName, string newName,
        IChartEditorSurface editor)
    {
        _template = template;
        _oldName = oldName;
        _newName = newName;
        _editor = editor;
    }

    public bool HasEffect => !string.Equals(_oldName, _newName, StringComparison.Ordinal);

    public void Execute()
    {
        _template.Name = _newName;
        _editor.RequestRedraw();
    }

    public void Undo()
    {
        _template.Name = _oldName;
        _editor.RequestRedraw();
    }
}

/// <summary>
/// Reassigns <see cref="Chord.TemplateId"/> on a batch of chords. Used by
/// the auto-fill generator when an untemplated chord gets a freshly-built
/// template attached.
/// </summary>
public sealed class SetTemplateIdsAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly IChartEditorSurface _editor;
    private readonly List<(Chord chord, int oldId, int newId)> _entries;

    public SetTemplateIdsAction(IEnumerable<(Chord, int, int)> entries, IChartEditorSurface editor)
    {
        _entries = entries.ToList();
        _editor = editor;
    }

    public bool HasEffect => _entries.Any(e => e.oldId != e.newId);

    public void Execute()
    {
        foreach (var (chord, _, newId) in _entries) chord.TemplateId = newId;
        _editor.RequestRedraw();
    }

    public void Undo()
    {
        foreach (var (chord, oldId, _) in _entries) chord.TemplateId = oldId;
        _editor.RequestRedraw();
    }
}

/// <summary>
/// Reassigns <see cref="HandShape.TemplateId"/> on a batch of handshapes.
/// </summary>
public sealed class SetHandShapeTemplateIdsAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly IChartEditorSurface _editor;
    private readonly List<(HandShape hs, int oldId, int newId)> _entries;

    public SetHandShapeTemplateIdsAction(IEnumerable<(HandShape, int, int)> entries, IChartEditorSurface editor)
    {
        _entries = entries.ToList();
        _editor = editor;
    }

    public bool HasEffect => _entries.Any(e => e.oldId != e.newId);

    public void Execute()
    {
        foreach (var (hs, _, newId) in _entries) hs.TemplateId = newId;
        _editor.RequestRedraw();
    }

    public void Undo()
    {
        foreach (var (hs, oldId, _) in _entries) hs.TemplateId = oldId;
        _editor.RequestRedraw();
    }
}

/// <summary>
/// Pure helpers shared by the chord-template undoable actions.
/// </summary>
/// <summary>
///     Says what a chord-shape edit actually CHANGED, for the log.
///     <para>
///         <b>This is the single most valuable line missing from the charting
///         log.</b> A 32-minute session logged 63 template actions — 26 creates,
///         15 global edits, 22 switches — and not one of them recorded WHAT was
///         edited. You cannot tell from the log whether the charter fixed a
///         fingering, renamed a chord or reshaped it, so the record of the busiest
///         activity in the app answers no question about it.
///     </para>
///     <para>
///         Diffs only; an unchanged facet is omitted entirely, so the line stays
///         short and every token in it is a real change.
///     </para>
/// </summary>
public static class ChordTemplateDiff
{
    public static string Describe(ChordTemplate before, ChordTemplate after)
    {
        var parts = new List<string>(4);

        if (!string.Equals(before.Name, after.Name, StringComparison.Ordinal))
            parts.Add($"name '{Show(before.Name)}'→'{Show(after.Name)}'");

        if (before.Arpeggio != after.Arpeggio)
            parts.Add($"arpeggio {before.Arpeggio}→{after.Arpeggio}");

        var frets = DescribeMap(before.Frets, after.Frets);
        if (frets.Length > 0) parts.Add($"frets {frets}");

        var fingers = DescribeMap(before.Fingers, after.Fingers);
        if (fingers.Length > 0) parts.Add($"fingers {fingers}");

        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    private static string Show(string value) => string.IsNullOrEmpty(value) ? "" : value;

    /// <summary>
    ///     Per-string changes as <c>s3:5→7</c>. A string ADDED reads <c>s3:-→7</c>
    ///     and one REMOVED <c>s3:5→-</c>, because "the shape lost a string" and
    ///     "the shape moved a fret" are different edits and a diff that spelled
    ///     them the same would hide the one that changes what is played.
    /// </summary>
    private static string DescribeMap(Dictionary<int, int> before, Dictionary<int, int> after)
    {
        var strings = new SortedSet<int>(before.Keys);
        strings.UnionWith(after.Keys);

        var parts = new List<string>();
        foreach (var stringIndex in strings)
        {
            var had = before.TryGetValue(stringIndex, out var oldValue);
            var has = after.TryGetValue(stringIndex, out var newValue);
            if (had && has && oldValue == newValue) continue;

            parts.Add($"s{stringIndex}:{(had ? oldValue.ToString() : "-")}→{(has ? newValue.ToString() : "-")}");
        }

        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }
}

public static class FingersDictHelper
{
    public static bool Equal(Dictionary<int, int> a, Dictionary<int, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kvp in a)
        {
            if (!b.TryGetValue(kvp.Key, out var bv)) return false;
            if (bv != kvp.Value) return false;
        }
        return true;
    }
}

