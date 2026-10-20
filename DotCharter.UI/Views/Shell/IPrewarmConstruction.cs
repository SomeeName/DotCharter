namespace DotCharter.UI.Views.Shell;

/// <summary>
///     An <see cref="IPrewarmableSurfaces" /> owner whose LISTING has to BUILD
///     something first — deferred <c>ContentTemplate</c> bodies, lazily
///     constructed dialog views — handing that construction to the drain as
///     separate steps.
/// </summary>
/// <remarks>
///     <para>
/// <b>Why.</b> The drain paces one surface per <c>Background</c>
///         turn, but the listing itself ran in ONE turn: Play Mode's three
///         deferred screen bodies and the rig view, 34 dialog views and the three
///         top-level views, measured at 0.7-1.9 s on the Release R2R build. A turn
///         cannot be interrupted, so a click in that window waited for all of it.
///     </para>
///     <para>
///         <c>MainWindowPrewarm</c> runs each step in its own paced turn BEFORE it
///         calls <see cref="IPrewarmableSurfaces.HiddenSurfaces" />, which must
///         stay correct on its own (a step is idempotent, and the listing still
///         builds whatever a step has not). Construction only: style application
///         and layout stay with the drain's realize steps.
///     </para>
/// </remarks>
public interface IPrewarmConstruction
{
    /// <summary>The construction <see cref="IPrewarmableSurfaces.HiddenSurfaces" />
    /// would otherwise do in one go, as idempotent steps, one per dispatcher turn.</summary>
    IEnumerable<Action> ConstructionSteps();
}
