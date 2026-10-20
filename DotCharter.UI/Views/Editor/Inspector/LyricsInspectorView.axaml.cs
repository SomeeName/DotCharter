using Avalonia.Controls;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Lyrics tab: raw lyrics, the selected syllable and import / export behind the section rail.</summary>
public partial class LyricsInspectorView : UserControl
{
    public LyricsInspectorView()
    {
        InitializeComponent();
        Rail = new InspectorSectionRail(this, this.FindControl<StackPanel>("LyricsRailEntries")!);
    }

    /// <summary>The section rail — the test hook.</summary>
    internal InspectorSectionRail Rail { get; }
}
