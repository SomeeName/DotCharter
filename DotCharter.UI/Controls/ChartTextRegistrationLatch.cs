namespace DotCharter.UI.Controls;

/// <summary>
///     The latch that keeps whole-chart font-atlas pre-registration off the
///     per-frame path: re-register only when the lyric / event collections
///     change, by INSTANCE or by count.
///     <para>
///         Its two references are <b>weak</b>, and that is the whole reason this
///         is a type rather than four fields. <c>ChartEditorViewModel.LyricPhrases</c>
///         is the open vocal track's own <c>Phrases</c> and
///         <c>ProjectState.EventPoints</c> is the active arrangement's own
///         collection, so a strong latch on a render driver — which lives as long
///         as the 3D canvas — kept every lyric word and every section/phrase
///         marker of every song the session ever previewed. Identity without
///         ownership, the same rule <c>RsTonePlaybackCoordinator</c> and
///         <c>RocksmithExportDialogViewModel</c> follow.
///     </para>
///     <para>
///         A collected target can never be the collection being asked about now,
///         so "changed" is the right answer either way — and re-registering is
///         idempotent work that only ever happens on a chart the driver has just
///         been handed.
///     </para>
/// </summary>
internal sealed class ChartTextRegistrationLatch
{
    private WeakReference<object>? _lyrics;
    private int _lyricCount = -1;
    private WeakReference<object>? _events;
    private int _eventCount = -1;
    private WeakReference<object>? _tones;
    private int _toneCount = -1;

    /// <summary>True when the atlas has to be re-registered for this pair.</summary>
    public bool NeedsRegistration(object lyrics, int lyricCount, object events, int eventCount) =>
        !StillTheSame(_lyrics, lyrics) || lyricCount != _lyricCount ||
        !StillTheSame(_events, events) || eventCount != _eventCount;

    /// <summary>The 2D timeline's form: it also draws tone badges, so the tone
    /// changes are a third latched collection.</summary>
    public bool NeedsRegistration(object lyrics, int lyricCount, object events, int eventCount,
        object tones, int toneCount) =>
        NeedsRegistration(lyrics, lyricCount, events, eventCount) ||
        !StillTheSame(_tones, tones) || toneCount != _toneCount;

    /// <summary>Records a registration that SUCCEEDED. A failed one must not be
    /// latched, or the atlas never gets the characters.</summary>
    public void Accept(object lyrics, int lyricCount, object events, int eventCount)
    {
        _lyrics = new WeakReference<object>(lyrics);
        _lyricCount = lyricCount;
        _events = new WeakReference<object>(events);
        _eventCount = eventCount;
    }

    public void Accept(object lyrics, int lyricCount, object events, int eventCount, object tones, int toneCount)
    {
        Accept(lyrics, lyricCount, events, eventCount);
        _tones = new WeakReference<object>(tones);
        _toneCount = toneCount;
    }

    /// <summary>Forgets everything accepted — the project-switch hook, paired
    /// with the atlas's own <c>ResetToSeedCharacters</c>: a new project whose
    /// collections happen to be the same instances at the same counts must
    /// still be registered into the emptied sheet.</summary>
    public void Reset()
    {
        _lyrics = null; _lyricCount = -1;
        _events = null; _eventCount = -1;
        _tones = null; _toneCount = -1;
    }

    private static bool StillTheSame(WeakReference<object>? weak, object current) =>
        weak is not null && weak.TryGetTarget(out var target) && ReferenceEquals(target, current);
}
