# `Infrastructure/` + `Update/` — config, backup, crash reports, library doctor, auto-update (deep reference)

One file per subject under `infrastructure-update/`. Read the one you need:

- [config-and-json-stores.md](infrastructure-update/config-and-json-stores.md) — config saves are coalesced; a degraded read is never authoritative
- [backup-restore.md](infrastructure-update/backup-restore.md) — a restore is atomic per file and per set
- [small-updates.md](infrastructure-update/small-updates.md) — small updates: content-addressed partial downloads
- [update-feed.md](infrastructure-update/update-feed.md) — which repo the update check reads: the public one, or the feed an optional plugin registers (#925); a private feed needs the GitHub sign-in
- [split-install.md](infrastructure-update/split-install.md) — the launcher on top, the app in `app/` (#413): which paths follow the install root, how a v0.62 install migrates in one update, `LegacyUpdaterShim`, `SplitInstallCleanup`
- [logging.md](infrastructure-update/logging.md) — `Logging/`: severity and category are independent
- [storage.md](infrastructure-update/storage.md) — `Infrastructure/Storage/`: three footguns with their own helper

## Release identity

The UI project's `Version` is the release identity. Screens and diagnostics use
`Infrastructure/AppVersionInfo` to read `InformationalVersion`, retain prerelease
identifiers and trim build metadata. Binary assembly/file versions remain SDK-derived.
The updater compares SemVer text; see [update-feed.md](infrastructure-update/update-feed.md).
