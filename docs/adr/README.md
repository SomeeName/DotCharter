# Architecture decision records

Short records of the decisions a newcomer is most likely to question. Each one
says what was decided, why, and what it costs. They describe the project as it
is now; when a decision changes, the record is edited (or replaced and marked
**Superseded**), so this folder never holds a stale answer.

Use [0000-template.md](0000-template.md) for a new one. One page at most. Link
the issue or pull request that carried the discussion instead of repeating it.

| # | Decision |
|---|---|
| [0001](0001-native-juce-audio-engine.md) | All real-time audio runs in a native JUCE engine behind a C ABI |
| [0002](0002-vulkan-only-rendering.md) | The highway and the editor timeline are drawn with Vulkan only |
| [0003](0003-avalonia-and-presentation-layer.md) | Avalonia UI, with screen contracts in a Presentation layer that never references Avalonia |
| [0004](0004-rscp-and-dcsong-formats.md) | `.rscp` is the project format, `.dcsong` is the song package |
| [0005](0005-package-writer-only.md) | The repository holds the package writer; reading game packages is not part of it |
| [0006](0006-stems-never-regenerated.md) | Separated stems are cached per song and never regenerated |
| [0007](0007-ci-on-manual-trigger.md) | CI runs only when started by hand |
| [0008](0008-contested-features-build-switch.md) | One build property removes the contested features |
| [0009](0009-gpl-3-and-directml-permission.md) | GPL-3.0-or-later, with a section 7 permission for DirectML |
