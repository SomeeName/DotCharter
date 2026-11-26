using DotCharter.Localization;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.State;

namespace DotCharter.Presentation.Screens.Editor;

/// <summary>The Chord shapes tab's pins: a card's Pin grip and "Pin all grips" (<see cref="GripPins.PinAll" />), one undo step each.</summary>
internal sealed class ShapePins(IChartEditorSurface editor, IHistoryService history, ILocalizationService localization)
{
    /// <summary>Pins or unpins the card's shape; a ghost grip, or a shape outside the arrangement, is left alone.</summary>
    public void SetPinned(ShapePanelRowViewModel row, bool value)
    {
        if (!row.CanTogglePin || row.Template.Pinned == value) return;
        if (editor.ActiveArrangement?.ChordTemplates.Any(t => ReferenceEquals(t, row.Template)) != true) return;
        history.Do(new SetTemplatePinnedAction(row.Template, value));
    }

    /// <summary>Pins every unpinned fingered shape in one undo step and says how many; the history change refreshes the tab.</summary>
    public void PinAll(Action<string> say)
    {
        if (editor.ActiveArrangement is not { } arrangement || GripPins.PinAll(arrangement) is not { } action) return;
        var count = GripPins.Unpinned(arrangement).Count;
        if (history.Do(action)) say(localization.Plural("Shapes_Status_PinnedAllOne", "Shapes_Status_PinnedAll", count));
    }

    /// <summary>Whether Pin all grips has work, its label ("Pin all grips (24)") and its tip (what it does, or why it is off).</summary>
    public (bool Has, string Label, string Tip) Read()
    {
        var count = editor.ActiveArrangement is { } arrangement ? GripPins.Unpinned(arrangement).Count : 0;
        return count > 0
            ? (true, localization.Format("Shapes_PinAll", count), localization["Shapes_PinAll_Tip"])
            : (false, localization["Shapes_PinAllNone"], localization["Shapes_PinAll_NoneTip"]);
    }
}
