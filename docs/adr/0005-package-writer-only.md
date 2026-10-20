# 0005. The repository holds the package writer; reading game packages is not part of it

**Status:** Accepted

## Context

DotCharter ships no game content. Exporting a playable package for a game the
user owns needs a writer; opening the game's own installed packages needs a
reader. A build switch can hide a reader, but its source would still sit in
the repository.

## Decision

- The repository contains charting, the neutral import formats, `.dcsong`
  playing and the package writer used for export. It contains no code that
  reads or decrypts a game package or reads the game install.
- The app has a neutral extension point for optional plugins
  (`IPluginModule`, `IPackageFeatures` and the seams in
  `DotCharter.Services/Plugins/`). Every entry point that reads a game package
  or the game install asks `IPackageFeatures`; when nothing supplies the
  feature, those entry points are absent, not greyed out.
- Lints keep it that way: one scans every tracked text file for reader names
  and file paths, one inspects the compiled assemblies, a third keeps the
  writer from referencing reader code, and a fourth keeps the product from
  naming any particular plugin. The `plugins/` folder is git-ignored and never
  scanned.

## Consequences

- Users of this repository's build get charting, export and `.dcsong`.
- Docs and tests here describe the seams, never what a plugin does with them.
  Write neutrally, and run the lints before pushing.
- Every seam (library source, export, tone import) has to work with and
  without a plugin.

## Links

[contested-features.md](../agents/services/contested-features.md),
[LEGAL.md](../../LEGAL.md).
