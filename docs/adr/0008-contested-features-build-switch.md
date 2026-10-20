# 0008. One build property removes the contested features

**Status:** Accepted

## Context

Some features sit in a legal grey zone, for example downloading audio from a
video site. If a notice arrives, the answer should be one release, not a
refactor under time pressure.

## Decision

- The MSBuild property `DotCharterContestedFeatures` (default `true`) defines
  the compile constant `CONTESTED_FEATURES` in every project.
- Each contested feature registers through one DI module, marks its menu rows
  and routes its command-palette ids through one filter. With the property
  off, nothing is registered and nothing is built: the feature is absent, not
  greyed out, and the bundled third-party tool is left out of the release.
- The release script takes `-ContestedFeatures:$false` to publish that variant.
- A test keeps the list in the guide and the registry in the code equal.

## Consequences

- The source of a contested feature stays in the tree; the switch only keeps
  the shipped program from using it. Deleting the source is a separate step.
- Adding a contested feature means touching the registry, the DI module, the
  menu and the guide; the sync test fails until all agree.
- The default gate tests the switch-on build. The switch-off build is checked
  by the release script.

## Links

[docs/agents/services/contested-features.md](../agents/services/contested-features.md),
`Directory.Build.props`, [LEGAL.md](../../LEGAL.md).
