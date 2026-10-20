using Avalonia.Controls;

namespace DotCharter.Tone.UI.Controls;

/// <summary>
///     A panel whose FIRST layout, while it is hidden, can be paid a chunk of
/// children at a time — what the startup prewarm
///     (<c>HiddenSubtreePrewarmer.RealizeNextChunk</c>) drives, one chunk per
///     dispatcher turn, so a long list never lands as one uninterruptible turn.
/// </summary>
public interface IChunkedPanel
{
    /// <summary>Children a hidden measure is still holding back.</summary>
    bool HasBacklog { get; }

    /// <summary>Lets the next hidden measure lay out one more chunk and invalidates
    /// the panel; false when there is nothing left to grant.</summary>
    bool RealizeNextChunk();

    /// <summary>Whether a hidden measure leaves <paramref name="child" /> out — a
    /// layout walk must not count it as stale work.</summary>
    bool Withholds(Control child);
}
