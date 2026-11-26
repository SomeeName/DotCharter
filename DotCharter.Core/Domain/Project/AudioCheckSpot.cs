using DotCharter.Core.Domain.Primitives;

namespace DotCharter.Core.Domain.Project;

/// <summary>What an import guessed at an <see cref="AudioCheckSpot" />: the tab does not say it.</summary>
public enum AudioCheckReason
{
    /// <summary>A dead-note slide-out's landing fret: the tab marks the direction, never where it ends.</summary>
    DeadNoteSlideLanding,

    /// <summary>The tab's own tempo map replaced the beat map: nothing synced it to a recording.</summary>
    TempoFromTab,

    /// <summary>A grace ornament whose placement (before or on the beat) the tab does not state: the importer assumed it.</summary>
    GraceTiming
}

/// <summary>One spot of an arrangement that still needs a listen against the audio (D110).</summary>
public readonly record struct AudioCheckSpot(FractionalPosition Position, AudioCheckReason Reason);
