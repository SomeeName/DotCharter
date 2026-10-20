# 0006. Separated stems are cached per song and never regenerated

**Status:** Accepted

## Context

Stem separation takes minutes on a GPU and much longer on a CPU. Models
improve and settings change, so it is tempting to redo old work. Doing so
silently wastes the user's time and can replace stems they have already used
in a chart.

## Decision

- Stems are stored as one compressed `.dcstems` archive per song, keyed by the
  song's identity folded with a signature of its audio content.
- Code checks the cache before it separates and saves to it afterwards. A
  second separation asks before replacing existing stems.
- A run belongs to the song, not the screen: it survives closing the dialog
  that started it.
- Opus stems are playback-only. Live note detection never reads stems; its
  only input is the live capture ring, and a lint checks that.

## Consequences

- Reopening a song is instant after the first separation.
- A better model does not improve old songs by itself; the user separates again
  on purpose.
- Archives hold work the user cannot cheaply redo, so tests and tools use an
  isolated data root and never delete from a real one.

## Links

[docs/agents/services/stem-separation.md](../agents/services/stem-separation.md).
