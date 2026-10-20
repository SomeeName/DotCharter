# Legal

## For rights holders

If you believe something in DotCharter infringes your rights, tell us.

- **Open an issue** on the project's GitHub page and say what is affected and
  why. Please do not paste private details into a public issue.
- **Or write privately** through the repository's **Security** tab, using
  *Report a vulnerability*, as described in [SECURITY.md](SECURITY.md). Say it is
  a rights notice, not a security problem.

Include who you are, what you hold the rights to, where in the app or the code
the problem is, and how to reach you.

Some downloadable stem-separation model weights have no known author and no stated licence. The BS-RoFormer SW 6-stem weights are one example (see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)). If you hold rights in them, contact us as described above and we remove the file from our releases.

### What happens with a notice

We read it and answer on the same thread. If it holds up, the feature in
question is switched off in the next release and stays off until it is sorted
out. Releases that were already published are not changed, but affected assets
can be removed from the release page.

## What DotCharter is and is not

- DotCharter ships **no game content**: no songs, no charts, no audio, no game
  files and no encryption keys. The keys needed to read some game files are not
  included and must come from your own copy of the game.
- DotCharter ships **no API keys** for any online service. AI providers use
  credentials you supply yourself.
- The content that does ship (amp captures, voice packs, fonts) is listed with
  its licence in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Rocksmith interoperability

DotCharter reads and writes Rocksmith 2014 packages so that people can chart
for a game they own. The line we hold is interoperability: **no Ubisoft code,
keys, content, art, text or extracted assets in the repository.** The player's
own install is the source for anything the game owns. What is in the tree:

- **Format code.** Writing PSARC and SNG files is our own
  implementation of reverse-engineered formats (the same kind of work that
  community CDLC tools have published for years). The encryption keys are not
  in the tree; they come from a key file the player supplies. Amp chains and
  knob curves are not stored here; an optional plugin can read them from the
  player's own install when needed.
- **Format documentation.** `docs/` and code comments describe how the formats
  are laid out. They carry no dumps and no tables copied from the game.
- **Game identifiers.** Gear keys, tone names, manifest field names and knob
  ranges are used to map tones. The gear catalogue holds those identifiers
  only: no descriptions, no UI strings, no lore.
- **Pictures, fonts and sounds.** The app icon and the tone rig's artwork are
  AI-generated for DotCharter; the fonts and the highway's three textures are
  vendored under their own licences. Nothing was taken from the game. Every
  item is listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and
  `DotCharter.Tests/Fixtures/PROVENANCE.md`.
- **Test packages.** The only game-format package in the tree is built by
  DotCharter itself from a generated chart with placeholder audio.
- **Screenshots.** The README and the docs contain no in-game screenshots.
- **External tools.** The CDLC enabler and the 7-string mod are named in the
  docs only; nothing of them is copied into the repository.

Guards in the test suite keep this true: `RocksmithKeyLeakLintTests` (no keys),
`RsGameDataLintTests` and `RsGameDocsLintTests` (no game tables or figures),
`RsGearDataProseLintTests` (no game text in the gear catalogue) and
`FixtureProvenanceLintTests` (every binary has a recorded origin), and
`PublicTreeHasNoPackageReaderTests` with `PublicAssembliesHaveNoPackageReaderTests`
(no package reader in the source tree or in a shipped assembly, and no game
package among the tracked files).

## Your responsibility

DotCharter is a tool. You are responsible for having the rights to the audio,
tabs and charts you import, convert, download or share with it, and for
following the terms of any service you connect it to. The tools that fetch
material from other sites (YouTube) act on your
request, on your connection, with your accounts.

## Trademarks

The trademark disclaimer is in the [README](README.md#disclaimer).

## What we send

What leaves your computer, and when, is in [PRIVACY.md](PRIVACY.md).

## Licence

DotCharter is free software under the GPL; see [LICENSE](LICENSE). [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) §1 adds a GPLv3 section 7 permission for combining it with Microsoft DirectML.
