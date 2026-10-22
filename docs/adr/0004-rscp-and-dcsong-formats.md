# 0004. `.rscp` is the project format, `.dcsong` is the song package

**Status:** Accepted

## Context

The editor needs a working format it can save atomically and round-trip
without loss, and players need one file they can share and play. The
community's existing chart tools already use the RSCharter project format, and
a chart should be able to travel to and from them.

## Decision

- **Project: `.dcproj`.** One XML document, version 3, in the RSCharter /
  Log Charter layout. DotCharter adds its own `dc*` elements after the
  inherited ones, which Charter cannot read, so the file carries DotCharter's
  own extension and every save writes a Charter-compatible `.rscp` beside it
  (only what Charter's model knows). A `.rscp` from Charter or from an older
  DotCharter build still opens. Every written file is version 3, names are frozen once
  released, and unknown data is preserved on load and written back.
- **Package: `.dcsong`.** A zip with a manifest, the full chart, Opus audio
  plus a preview, cover art and optional stems. Every member carries a SHA-256
  checksum, and a package is treated as untrusted input: bounded reads, no
  path traversal.
- Saves write `<file>.saving` and replace the original atomically.

## Consequences

- A chart can move between DotCharter and the tools it descends from.
- The format promise is a cost: a change to a frozen name needs a version bump
  and a migration, so such changes are rare and tested.
- Compressed package audio is playback-only; charting from audio and stem
  separation ask for lossless audio.

## Links

[docs/DCPROJ-FORMAT-GUARANTEES.md](../DCPROJ-FORMAT-GUARANTEES.md),
[docs/agents/services/dcsong-format.md](../agents/services/dcsong-format.md), [ACKNOWLEDGEMENTS.md](../../ACKNOWLEDGEMENTS.md).
