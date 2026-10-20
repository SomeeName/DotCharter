using Avalonia.Threading;

namespace DotCharter.Tone.UI.ViewModels.Shell;

/// <summary>
///     Collapses a BURST of filter requests into ONE pass.
///     <para>
///         Both song-library surfaces re-filter, re-sort and REPUBLISH their row
///         collection from the search box's property-changed hook, synchronously,
///         on the UI thread. Measured on the owner's 2,160-song library
///         (Release, headless, real <c>SongBrowserView</c> attached): a pass whose
///         result set CHANGES cost 126–170 ms, of which 104–148 ms was the list
///         re-realizing its ~15 song rows — a many-column row template with
///         style-class bindings and <c>DynamicResource</c> lookups, none of it
///         one hot spot to delete (65–82 ms since the drawn chip strip took the
///         per-chip subtree out). Typing "metallica" cost 307–325 ms of UI-thread work
///         spread across the burst, which is what lands between the keystroke and
///         the frame.
///     </para>
///     <para>
///         The fix is not a cheaper pass, it is FEWER passes: at most one request
///         is in flight, and it runs at <see cref="DispatcherPriority.Background" />
///         — BELOW input — so every queued keystroke is processed first and the
///         pass runs once, when typing pauses. No timer and no guessed delay: the
///         list updates on the first idle frame, so a single keystroke is as
///         prompt as it ever was.
///     </para>
///     <para>
///         This is the UI-side sibling of <c>CoalescedUiChannel{T}</c> (the
///         playhead publish pattern), minus the payload: the "value" here is the
///         view model's own filter state, read when the pass runs. <c>post</c> is
///         injected so the type is unit-testable with no dispatcher — the tests
///         drive a queue by hand and assert the REQUEST-to-RUN ratio.
///     </para>
/// </summary>
public sealed class CoalescedFilterPass : DotCharter.Tone.Presentation.Shared.CoalescedPass
{
    /// <param name="pass">The filter pass. Runs on whatever thread
    /// <paramref name="post" /> schedules onto.</param>
    /// <param name="post">Schedules a callback. Defaults to the UI dispatcher at
    /// <see cref="DispatcherPriority.Background" />, which is the whole point:
    /// input outranks it, so a burst of keystrokes drains before it fires.</param>
    public CoalescedFilterPass(Action pass, Action<Action>? post = null)
        : base(pass, post ?? (action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background)))
    {
    }
}
