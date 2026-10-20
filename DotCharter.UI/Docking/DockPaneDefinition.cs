using DotCharter.Tone.UI;
using DotCharter.Tone.UI.ViewModels.Shell;

namespace DotCharter.UI.Docking;

/// <summary>
/// Describes one dockable tool pane. The registry of these drives layout
/// construction, the View-menu visibility sync, and layout persistence, so
/// adding a pane means adding one definition — not new plumbing.
/// </summary>
public sealed class DockPaneDefinition
{
    /// <summary>Stable id — used in the dock layout and persisted state.</summary>
    public required string Id { get; init; }

    /// <summary>Localized tab/window title.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// The pane's view model. Views are materialized per dock host by the
    /// app-level <see cref="ViewLocator"/>; never store a control here (a
    /// control instance can only have one visual parent, and float/redock
    /// hosts it twice — "already has a visual parent").
    ///
    /// <para><b>The type is the guard.</b> An <c>object</c> here
    /// would make "put a control here" a compile-clean mistake that only ever
    /// surfaced as a crash on the first float. <see cref="ViewModelBase"/> is
    /// also exactly what <see cref="ViewLocator.Match"/> keys on, so a Context
    /// this property accepts is a Context the locator can resolve — narrowing
    /// it costs nothing and removes the whole failure mode. Never widen it back
    /// (pinned by <c>DockPaneContractTests</c>).</para>
    /// </summary>
    public required ViewModelBase Context { get; init; }

    /// <summary>Told whether the pane is in a float window, after every layout change — the shell
    /// shows a "dock it back" card in the spot it left.</summary>
    public Action<bool>? SetFloating { get; init; }

    /// <summary>Reads the owning view-model visibility flag.</summary>
    public required Func<bool> IsVisible { get; init; }

    /// <summary>Writes the owning view-model visibility flag.</summary>
    public required Action<bool> SetVisible { get; init; }
}
