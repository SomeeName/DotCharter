using Avalonia.Controls;
using DotCharter.UI.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.UI.Views.Editor.Inspector;

/// <summary>The Song tab: the song's information, sections, tones and phrases as design D's cards.</summary>
public partial class SongInspectorView : UserControl
{
    public SongInspectorView()
    {
        InitializeComponent();
        Cards = this.FindControl<ColumnFlowPanel>("SongCards")!;
        // the tab is prewarmed at startup, usually before a project is open,
        // and the project's tones, sections and phrases then arrive behind its
        // IsVisible=false. Re-realize it in the background when they do, so the
        // click on "Song" does not pay for laying the new rows out. A paged-away
        // card stays visible and is measured with its panel, so it needs no list.
        Rewarmer = new HiddenSurfaceRewarmer(this);
    }

    /// <summary>Re-realizes this tab when its lists change while it is hidden.</summary>
    internal HiddenSurfaceRewarmer Rewarmer { get; }

    /// <summary>The card body — the test hook.</summary>
    internal ColumnFlowPanel Cards { get; }

    /// <summary>Brings the named card on screen, as its page button does — the test hook.</summary>
    internal void ShowCard(string name) => Cards.ShowPage(this.FindControl<Control>(name)!);
}
