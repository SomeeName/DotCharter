namespace DotCharter.Core.Domain.Project;

/// <summary>
///     Where a chart's content came from, as far as writing it back out is
/// concerned. Carried by <see cref="ChartProject.Provenance" /> AND
///     <see cref="Arrangement.Provenance" />: the project flag covers what is not
///     an arrangement (beats, lyrics, showlights), the arrangement flag follows a
///     part that is moved or pasted elsewhere.
///     <para>
///         <b>Play-only is not "read-only".</b> A play-only chart can be played,
///         practised, viewed and even edited in place; what it can never be is
///         EXPORTED. The guard is <c>DotCharter.Services.Export.PlayOnlyExportGuard</c>.
///     </para>
/// </summary>
public enum ChartProvenance
{
    /// <summary>The user's own chart, or one imported from a
    /// GP / EOF / XML / MIDI file or a <c>.dcsong</c>. Exports freely.</summary>
    Unrestricted = 0,

    /// <summary>Content this app may show and play but never write out again: the
    /// source that supplied it stamped it play-only on import (an optional plugin
    /// can do that). Persisted as <c>playOnly</c>; the spelling an earlier build
    /// wrote still reads as this value.</summary>
    PlayOnly = 1
}
