using AlphaTab;

namespace DotCharter.Services.Infrastructure;

/// <summary>
///     The one place an AlphaTab <see cref="Settings" /> is constructed.
///     <para>
///         Constructing the first <c>Settings</c> lazily fills AlphaTab's shared
///         STATIC engraving defaults (<c>Settings</c> → <c>DisplaySettings</c> →
///         <c>RenderingResources</c> → <c>EngravingSettings.BravuraDefaults</c> →
///         <c>FillFromSmufl</c>), and that initializer enumerates a list while
///         mutating it. Two threads reaching it at once throw either
///         <c>"Collection was modified; enumeration operation may not execute"</c> or
///         <c>ArgumentOutOfRangeException: capacity was less than the current size</c>
///         out of <c>List.Grow</c> — the second is a torn <c>List.Add</c>, so this is
///         a genuine data race in a shared static, not just an enumeration guard.
///     </para>
///     <para>
///         It is reachable in production: the save companions build DETACHED, so a
///         `.gp` export can overlap a sheet-strip render or a PDF export, and the
///         cold initializer is whichever of them gets there first. The sheet renderer
///         already serialized its own construction; the GP exporter did not, so the
///         two raced each other through separate gates — which is no gate at all.
///         Every construction site must come through here.
///     </para>
/// </summary>
public static class AlphaTabRuntime
{
    private static readonly object Gate = new();

    public static Settings CreateSettings()
    {
        lock (Gate) return new Settings();
    }

    /// <summary>
    ///     Runs a whole alphaTab document lifecycle — <c>Score.ResetIds()</c>,
    ///     model construction, <c>Finish</c> and serialization — with exclusive
    ///     access to alphaTab's process-global state.
    ///     <para>
    ///         <c>Score.ResetIds()</c> rewinds a STATIC id counter that every
    ///         <c>Score</c>/<c>Track</c>/<c>Bar</c>/<c>Beat</c> draws from, and the
    ///         ids land in the emitted <c>score.gpif</c>. So two overlapping
    ///         exports do not merely interleave: whichever one calls
    ///         <c>ResetIds</c> second renumbers the model the FIRST one is still
    ///         building, and the first writes a document whose ids do not match
    ///         the run that produced them. Measured on this repo: with one
    ///         background exporter running, 2 of 40 identical-input export PAIRS
    ///         serialized to different bytes.
    ///     </para>
    ///     <para>
    ///         It is reachable in production for the same reason
    ///         <see cref="CreateSettings" /> is: the save companions build
    ///         DETACHED, so a <c>.gp</c> export can overlap a sheet-strip render or
    ///         a PDF export. The lock is reentrant, so a body may call
    ///         <see cref="CreateSettings" /> freely.
    ///     </para>
    /// </summary>
    public static T RunExclusive<T>(Func<T> body)
    {
        lock (Gate) return body();
    }
}
