using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
/// The Note tab (inspector design D): a strip of section names over ONE section's cards, and a pinned
///     footer. Everything it shows is bound to <c>InspectorViewModel</c>, its <c>Sections</c> and <c>Staged</c>; the code-behind
///     only wires the strip's "More" menu, closes the filter list on a pick and names the hidden cards for the prewarm.
/// </summary>
public partial class SoundInspectorView : UserControl, IPrewarmableSurfaces
{
    /// <summary>
    ///     The section cards, biggest first, then the bodies a card gates a second time by the selection (the two
    ///     bend hosts), then the two rows of the strip pinned over the footer (the ladder's preview and the reshape
    ///     hint) and the footer's ask bar: realizing a card measures only its VISIBLE children, so the body the
    ///     selection hides is listed on its own, after its card (parent before child). The lookup is BY NAME: a
    ///     rename in the markup silently drops a card from the prewarm, so
    ///     <c>Perf/InspectorPrewarmUiPerfSmokeTests</c> asserts each resolves.
    /// </summary>
    internal static readonly string[] SectionBodyNames =
    [
        "NoteChordStringsCard", "NoteChordWholeCard", "NoteTechniqueCard", "NoteDetailCard", "NoteShapeCard",
        "NotePlacementBoardCard", "NotePlacementCard", "NotePlacementImportCard", "NoteTaskCard", "NoteBendsCard",
        "NoteTaskReadinessCard", "NoteTaskPlayheadCard", "NoteBendsPresetsCard", "NoteBendsScopeCard",
        "NoteSelectionCard", "NoteSelectionValuesCard", "NoteSelectionSummaryCard", "NoteFindingsCard", "NoteFindingsDetailCard",
        "NoteFindingsReviewCard", "NoteLibraryCard", "NoteLibraryGripCard", "NoteLibraryUsesCard", "NoteLibraryTemplateCard",
        "NoteApplyPreviewStrip", "NoteReshapeHint", "NoteFooterAsk", "NoteFooter", "NoteFooterShapeSave"
    ];

    public SoundInspectorView()
    {
        InitializeComponent();
        if (this.FindControl<OverflowTabStrip>("NoteSectionEntries") is { } strip
            && this.FindControl<ToggleButton>("NoteSectionMore") is { } more)
        {
            InspectorTabMoreMenu.Attach(strip, more, index =>
            {
                if (DataContext is InspectorViewModel vm) vm.Staged.Footer.ChooseSectionCommand.Execute((SoundSection)index);
            });
        }
    }

    /// <summary>
    ///     The Note tab's own <c>IsVisible</c>-gated cards. The tab itself is realized by
    ///     <c>InspectorView.HiddenSurfaces()</c>, and the prewarmer deliberately does NOT recurse
    ///     into a nested hidden root, so these are listed here. They gate on the SELECTION and on a
    ///     strip click, the gestures a user makes within seconds of opening a project, so they are
    ///     worth paying for up front. Only what is hidden RIGHT NOW is returned.
    /// </summary>
    public IReadOnlyList<Control> HiddenSurfaces() =>
    [
        .. SectionBodyNames.Select(name => this.FindControl<Control>(name)).OfType<Control>().Where(c => !c.IsVisible)
    ];

    /// <summary>The filter list closes on a pick, like the dropdown it replaced.</summary>
    private void OnFilterPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && this.FindControl<Button>("NoteFooterFilter")?.Flyout is { } flyout) flyout.Hide();
    }
}
