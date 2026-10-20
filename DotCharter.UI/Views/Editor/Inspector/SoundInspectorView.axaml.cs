using Avalonia.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>
/// The Sound tab: an icon-only rail of sections beside ONE section. Everything it shows
///     is bound to <c>InspectorViewModel</c> and its <c>Sections</c>; the code-behind only closes
///     the filter list on a pick and names the hidden section bodies for the prewarm.
/// </summary>
public partial class SoundInspectorView : UserControl, IPrewarmableSurfaces
{
    /// <summary>
    ///     The section bodies, biggest first. Bends is listed as its two bend hosts (one per
    ///     selection type), because the section itself only wraps them: realizing the wrapper would
    ///     measure two hidden children, i.e. nothing. The lookup is BY NAME: a rename in the markup
    ///     silently drops a body from the prewarm, so <c>Perf/InspectorPrewarmUiPerfSmokeTests</c>
    ///     asserts each resolves.
    /// </summary>
    internal static readonly string[] SectionBodyNames =
    [
        "SoundChordTechniqueCard", "SoundNoteTechniqueCard", "SoundShapeCard", "SoundPlacementCard", "SoundTaskBody",
        "NoteBendsBody", "ChordBendsBody", "SoundBulkCard", "SoundFindingsBody", "SoundLibraryBody"
    ];

    public SoundInspectorView() => InitializeComponent();

    /// <summary>
    ///     The Sound tab's own <c>IsVisible</c>-gated section bodies. The tab itself is realized by
    ///     <c>InspectorView.HiddenSurfaces()</c>, and the prewarmer deliberately does NOT recurse
    ///     into a nested hidden root, so these are listed here. They gate on the SELECTION and on a
    ///     rail click, the gestures a user makes within seconds of opening a project, so they are
    ///     worth paying for up front. Only what is hidden RIGHT NOW is returned.
    /// </summary>
    public IReadOnlyList<Control> HiddenSurfaces() =>
    [
        .. SectionBodyNames.Select(name => this.FindControl<Control>(name)).OfType<Control>().Where(c => !c.IsVisible)
    ];

    /// <summary>The filter list closes on a pick, like the dropdown it replaced.</summary>
    private void OnFilterPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && this.FindControl<Button>("SoundNavFilter")?.Flyout is { } flyout) flyout.Hide();
    }
}
