namespace DotCharter.UI.Input;

/// <summary>
///     One item of the timeline's right-click menu, as pure data: a resx key
///     (or a literal for language-free headers like fret numbers), the action,
///     and optional children for a submenu. Built by
///     <see cref="TimelineContextMenuBuilder" />, realized into Avalonia
///     <c>MenuItem</c>s by <c>ChartRenderer.ShowContextMenu</c> — the split is
///     what keeps the menu's CONTENT unit-testable without a visual tree.
/// </summary>
public sealed record TimelineMenuItem(
    string? LocKey,
    Action? Invoke = null,
    string? LiteralHeader = null,
    IReadOnlyList<TimelineMenuItem>? Children = null,
    KeyChord? Chord = null,
    bool IsEnabled = true,
    string? DisabledReason = null,
    string? DisabledReasonLocKey = null)
{
    public static readonly TimelineMenuItem Separator = new((string?)null);

    public bool IsSeparator => LocKey is null && LiteralHeader is null && Invoke is null && Children is null;
}

/// <summary>The verbs the timeline context menu can invoke — every one an
/// EXISTING command/service call supplied by the caller; the menu invents no
/// edit verbs of its own and therefore rides the same undo actions the
/// keybinds use. <c>Run</c> executes a command-palette id (the id the menu bar's
/// command is registered under), <c>ChordOf</c> reads that verb's CURRENT chord,
/// <c>CanRun</c> is the command's own availability, <c>DisabledReason</c> says why,
/// and the two counts say what the selection holds. Unavailable rows stay visible.</summary>
public sealed record TimelineContextMenuActions(
    Action DeleteSelection,
    Action DuplicateSelection,
    Action OpenInInspector,
    Action<int> SetFret,
    Action AddHandShape,
    Action AddFhpAnchor,
    Action AddNoteHere,
    Action Paste,
    Action SelectAllInView,
    Action AddEventAtTime,
    Action<string> Run,
    Func<string, KeyChord> ChordOf,
    Func<string, bool> CanRun,
    int SelectedNotes,
    int SelectedChords,
    Func<string, string?> DisabledReason);

/// <summary>
///     Builds the timeline context menu for hand shapes and the two highway contexts:
///     a note/chord under the pointer (near-miss tolerant, the same
///     <c>ChartHitTester</c> pass every click uses — the menu targets what the
///     user visibly clicked) and empty highway. Shift+right-click opens the menu;
///     plain right-click keeps the instant charting verbs.
/// </summary>
public static class TimelineContextMenuBuilder
{
    /// <summary>Frets 0..24 — the same clamp every fret verb uses.</summary>
    public const int MaxFret = FretLimits.MaxFret;

    public static IReadOnlyList<TimelineMenuItem> ForHandShape(Action gripsFromHeldNotes, Action handShapesFromLetRing,
        string? disabledReason) =>
    [
        new("Menu_GripsFromHeldNotes", gripsFromHeldNotes),
        new("Menu_HandShapesFromLetRing", disabledReason == null ? handShapesFromLetRing : null,
            IsEnabled: disabledReason == null, DisabledReason: disabledReason)
    ];

    /// <summary>Technique toggles and pick-slide shaping, each as (loc key, palette id).</summary>
    public static readonly IReadOnlyList<(string LocKey, string Id)> TechniqueRows =
    [
        ("Menu_Tech_Hopo", "note.toggleHopo"), ("Menu_Tech_Harmonic", "note.toggleHarmonic"),
        ("Menu_Tech_Mute", "note.toggleMute"), ("Menu_Tech_LinkNext", "note.toggleLinkNext"),
        ("Menu_Tech_Vibrato", "note.toggleVibrato"), ("Menu_Tech_Tremolo", "note.toggleTremolo"),
        ("Menu_PickSlide", "note.pickSlide"),
        ("Menu_Tech_Picking", "note.togglePicking"), ("Menu_Tech_Accent", "note.toggleAccent"),
        ("Menu_Tech_Ignore", "note.toggleIgnore"), ("Menu_Tech_PassOtherNotes", "note.togglePassOtherNotes")
    ];

    public static IReadOnlyList<TimelineMenuItem> ForSound(TimelineContextMenuActions a)
    {
        var frets = new TimelineMenuItem[MaxFret + 1];
        for (var f = 0; f <= MaxFret; f++)
        {
            var fret = f;
            frets[f] = new TimelineMenuItem(null, () => a.SetFret(fret),
                LiteralHeader: fret.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var technique = TechniqueRows.Select(r => Verb(a, r.LocKey, r.Id)).OfType<TimelineMenuItem>().ToList();
        var chord = ChordRows(a);
        var items = new List<TimelineMenuItem?>
        {
            Verb(a, "Menu_Cut", "edit.cut"),
            Verb(a, "Menu_Copy", "edit.copy"),
            Verb(a, "Timeline_Ctx_Duplicate", "edit.duplicate", a.DuplicateSelection),
            Verb(a, "Timeline_Ctx_Delete", "edit.delete", a.DeleteSelection),
            TimelineMenuItem.Separator,
            new("Timeline_Ctx_SetFret", Children: frets),
            technique.Count > 0 ? new TimelineMenuItem("Menu_GroupTechnique", Children: technique) : null,
            chord.Count > 0 ? new TimelineMenuItem("Menu_GroupChords", Children: chord) : null,
            new("Menu_GroupMoveResize", Children:
            [
                Verb(a, "Menu_SnapSelected", "edit.snapSelected"),
                Verb(a, "Menu_IncreaseLength", "edit.increaseLength"),
                Verb(a, "Menu_DecreaseLength", "edit.decreaseLength"),
                TimelineMenuItem.Separator,
                Verb(a, "Menu_NudgeEarlier", "note.nudgeEarlier"),
                Verb(a, "Menu_NudgeLater", "note.nudgeLater"),
                Verb(a, "Menu_NudgeEarlier10Ms", "note.nudgeEarlier10Ms"),
                Verb(a, "Menu_NudgeLater10Ms", "note.nudgeLater10Ms"),
                Verb(a, "Menu_NudgeEarlier1Ms", "note.nudgeEarlier1Ms"),
                Verb(a, "Menu_NudgeLater1Ms", "note.nudgeLater1Ms")
            ]),
            TimelineMenuItem.Separator,
            Verb(a, "Timeline_Ctx_AddHandShape", "tools.generateHandShape"),
            Verb(a, "Menu_HandShapesFromLetRing", "tools.handShapesFromLetRing"),
            Verb(a, "Menu_Generate_Arpeggio", "tools.generateArpeggio"),
            new("Timeline_Ctx_AddFhpAnchor", a.AddFhpAnchor),
            TimelineMenuItem.Separator,
            Verb(a, "Menu_Refret", "tools.refret"),
            Verb(a, "LinkIdentical_Title", "edit.linkIdentical"),
            new("Timeline_Ctx_OpenInspector", a.OpenInInspector)
        };
        return Tidy(items);
    }

    /// <summary>Edit &gt; Chord, with selection requirements explained on disabled rows.</summary>
    private static List<TimelineMenuItem> ChordRows(TimelineContextMenuActions a)
    {
        var notes = a.SelectedNotes > 0;
        var chords = a.SelectedChords > 0;
        return new List<TimelineMenuItem?>
        {
            Verb(a, "Menu_ConvertToPowerChordSmall", "tools.powerChordSmall", disabledKey: notes ? null : "Timeline_Ctx_SelectNotes"),
            Verb(a, "Menu_ConvertToPowerChordBig", "tools.powerChordBig", disabledKey: notes ? null : "Timeline_Ctx_SelectNotes"),
            Verb(a, "Menu_MergeNotesIntoChord", "tools.mergeNotesIntoChord", disabledKey: a.SelectedNotes >= 2 ? null : "Status_MergeChord_NoCandidates"),
            Verb(a, "Menu_SplitChordsIntoNotes", "tools.splitChordsIntoNotes", disabledKey: chords ? null : "Status_NoChordsSelected"),
            Verb(a, "Menu_ToggleOctaveFingering", "tools.toggleOctaveFingering", disabledKey: chords ? null : "Status_NoChordsSelected"),
            Verb(a, "Menu_TogglePowerChordFingering", "tools.togglePowerChordFingering", disabledKey: chords ? null : "Status_NoChordsSelected"),
            Verb(a, "Menu_MutedStrumsToChord", "tools.mutedStrumsToChord", disabledKey: notes ? null : "Timeline_Ctx_SelectNotes"),
            Verb(a, "Menu_ToggleGhostGems", "tools.toggleGhostGems"),
            Verb(a, "Menu_TappedHarmonic", "tools.tappedHarmonic", disabledKey: notes ? null : "Timeline_Ctx_SelectNotes")
        }.OfType<TimelineMenuItem>().ToList();
    }

    /// <summary>One row: the existing command by palette id (or an explicit existing action),
    /// its current chord, and its availability with a reason.</summary>
    private static TimelineMenuItem Verb(TimelineContextMenuActions a, string locKey, string id, Action? action = null,
        string? disabledKey = null)
    {
        var enabled = disabledKey == null && a.CanRun(id);
        var reason = enabled || disabledKey != null ? null : a.DisabledReason(id);
        if (!enabled && string.IsNullOrWhiteSpace(reason) && disabledKey == null)
        {
            reason = null;
            disabledKey = "Timeline_Ctx_Unavailable";
        }
        var chord = a.ChordOf(id);
        void Invoke()
        {
            if (!a.CanRun(id)) return;
            if (action != null) action();
            else a.Run(id);
        }

        return new TimelineMenuItem(locKey, enabled ? Invoke : null, Chord: chord.IsBound ? chord : null, IsEnabled: enabled,
            DisabledReason: reason, DisabledReasonLocKey: disabledKey);
    }

    /// <summary>Drops absent rows, then separators that lead, trail or repeat.</summary>
    private static IReadOnlyList<TimelineMenuItem> Tidy(IEnumerable<TimelineMenuItem?> items)
    {
        var result = new List<TimelineMenuItem>();
        foreach (var item in items.OfType<TimelineMenuItem>())
        {
            if (item.IsSeparator && (result.Count == 0 || result[^1].IsSeparator)) continue;
            result.Add(item);
        }

        while (result.Count > 0 && result[^1].IsSeparator) result.RemoveAt(result.Count - 1);
        return result;
    }

    public static IReadOnlyList<TimelineMenuItem> ForEmptyHighway(TimelineContextMenuActions a) => Tidy(
    [
        new TimelineMenuItem("Timeline_Ctx_AddNoteHere", a.AddNoteHere),
        // No chord on these two: Ctrl+V pastes at the playhead and Ctrl+A selects the whole chart,
        // so showing them would promise something this row (at the click, in view) does not do.
        new TimelineMenuItem("Timeline_Ctx_Paste", a.Paste),
        new TimelineMenuItem("Timeline_Ctx_SelectAllInView", a.SelectAllInView),
        TimelineMenuItem.Separator,
        new TimelineMenuItem("Timeline_Ctx_AddEventAtTime", a.AddEventAtTime)
    ]);
}
