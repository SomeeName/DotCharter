# 0009. GPL-3.0-or-later, with a section 7 permission for DirectML

**Status:** Accepted

## Context

The audio engine links JUCE (AGPLv3 or commercial), Rubber Band (GPLv2+ or
commercial) and the Steinberg ASIO SDK headers (GPLv3 or proprietary). A
non-commercial project can use their copyleft licences, but then the whole
combined work must be copyleft too. Stem separation also ships Microsoft's
DirectML runtime, a proprietary redistributable that the GPL's system-library
exception may not cover.

## Decision

- DotCharter is licensed `GPL-3.0-or-later`. The `JuceSharp` wrapper and the
  built `juce_sharp.dll` are a combined work under the GPLv3.
- `THIRD-PARTY-NOTICES.md` carries an additional permission under GPLv3
  section 7 that allows combining DotCharter with `DirectML.dll`, shipped
  unmodified beside the app. It covers DotCharter's own code only.
- Every shipped dependency, model and font is listed there with its licence.
  A test checks the inventory, and the notices open in the app under
  **Help > License & notices**.
- Voice packs are a separate download under their own licence.

## Consequences

- Anyone who distributes a build must offer the matching source, including
  the `JuceSharp` commit the release tag records.
- Non-commercial-only assets are acceptable while the project stays
  non-commercial; going commercial would reopen this record.
- A new dependency needs a licence entry before it ships.

## Links

[LICENSE](../../LICENSE), [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md),
`DependencyLicenceTests`.
