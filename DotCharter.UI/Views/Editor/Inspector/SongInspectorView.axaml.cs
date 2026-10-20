using Avalonia.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Song tab: key signature, tones, sections and phrases behind the section rail.</summary>
public partial class SongInspectorView : UserControl
{
    public SongInspectorView()
    {
        InitializeComponent();
        Rail = new InspectorSectionRail(this, this.FindControl<StackPanel>("SongRailEntries")!);
        // the tab is prewarmed at startup, usually before a project is open,
        // and the project's tones, sections and phrases then arrive behind its
        // IsVisible=false. Re-realize it in the background when they do, so the
        // click on "Song" does not pay for laying the new rows out — and each
        // hidden section with it, or the click on its rail entry would.
        Rewarmer = new HiddenSurfaceRewarmer(this, () => Rail.Bodies);
    }

    /// <summary>Re-realizes this tab when its lists change while it is hidden.</summary>
    internal HiddenSurfaceRewarmer Rewarmer { get; }

    /// <summary>The section rail — the test hook.</summary>
    internal InspectorSectionRail Rail { get; }
}
