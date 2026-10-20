using Avalonia;

namespace DotCharter.UI.Controls.Layers;

// Bridges the render and input layers for clickable sticky (pinned) events.
//
// EventLayer.DrawPinnedLabels populates this registry each frame with the
// exact screen rects it drew badges into. EventInteractionTool reads it
// during pointer-pressed handling to route clicks on those rects to the
// event dialog, without having to reproduce the layer's width measurements.
//
// A single instance is registered in DI — but the rects it holds are in ONE
// viewport's LOCAL pixel space, and there is more than one viewport: the main
// timeline plus a ChartRenderer per stacked track row, each with its own
// EventLayer, each drawing its own pinned badges every frame. With one flat
// list the last renderer to draw wiped the others' entries and its rects were
// then hit-tested against a pointer from a different control. Entries are
// therefore keyed by their OWNING viewport; Clear/Add/TryHit all take it, and
// ChartRenderer calls Forget on detach so a removed stacked row's list does
// not outlive it (those renderers are created and removed frequently).
public sealed class PinnedLabelHitRegistry
{
    public enum PinnedLabelKind
    {
        Section,
        Phrase,
        Tone
    }

    private readonly Dictionary<object, List<Entry>> _byOwner = [];

    public void Clear(object owner)
    {
        if (_byOwner.TryGetValue(owner, out var entries)) entries.Clear();
    }

    public void Add(object owner, Entry e)
    {
        if (!_byOwner.TryGetValue(owner, out var entries))
        {
            entries = [];
            _byOwner[owner] = entries;
        }

        entries.Add(e);
    }

    /// <summary>Drops a viewport's entries entirely — called when the owning
    /// renderer leaves the visual tree.</summary>
    public void Forget(object owner) => _byOwner.Remove(owner);

    /// <summary>How many rects <paramref name="owner" /> has registered. Exists
    /// so a test can prove one viewport's badges survive another's frame.</summary>
    public int CountFor(object owner)
        => _byOwner.TryGetValue(owner, out var entries) ? entries.Count : 0;

    // Iterate most-recently-added first so a later row (e.g. phrase drawn
    // after section at the same X) wins over an earlier one if they overlap.
    public bool TryHit(object owner, Point p, out Entry hit)
    {
        if (_byOwner.TryGetValue(owner, out var entries))
        {
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Rect.Contains(p))
                {
                    hit = entries[i];
                    return true;
                }
            }
        }

        hit = default;
        return false;
    }

    public readonly record struct Entry(Rect Rect, double TimeMs, PinnedLabelKind Kind);
}
