# `Infrastructure/` + `Update/` — config, backup, crash reports, library doctor, auto-update (deep reference) — small updates: content-addressed partial downloads

## Small updates — content-addressed PARTIAL downloads, no patch assets

A release is ~414 MB and most of it (the .NET runtime, `juce_sharp.dll`, the
bundled ffmpeg/yt-dlp) is byte-identical from version to version. Since 0.45 the
updater downloads **only the bytes this install is missing, out of the ONE full
zip every release already publishes**. There is no second asset.

**Why the previous design was replaced.** 0.41–0.42.x published a second, small
"covering patch" asset diffed against the last few releases
(`PayloadDelta.Covering`, selected by asset NAME). It only ever helped installs
inside that window — an older one still downloaded 414 MB — and widening the
window meant publishing more assets on every release page, forever. The premise
under it ("a GitHub release asset has no addressable interior") turned out to be
FALSE: `GET` the **API asset url** (`.apiUrl`, not `browser_download_url`) with
`Accept: application/octet-stream` and a `Range:` header and GitHub answers
`206 Partial Content` with `Accept-Ranges: bytes` — after a 302 to
`objects.githubusercontent.com`, so the client must follow redirects. Verified
against a live release asset before any of this was written.

**Measured against the live v0.42.4 asset** (396 MB), 2026-08-01, by
`GitHubUpdateServiceTests.LivePartialFetchAgainstRealRelease_EnvGated` — an
env-gated harness kept runnable so this can be re-checked rather than believed
(`DOTCHARTER_LIVE_PARTIAL_TEST=1`, `DOTCHARTER_LIVE_UPDATE_TOKEN=$(gh auth
token)`, `DOTCHARTER_LIVE_PARTIAL_FROM=<x.y.z>`):

| Installed | Fetched | Files | Wall |
|---|---|---|---|
| v0.42.3 (one release back) | **15.9 MB** of 396 MB | 19 | ~5 s |
| v0.41.0 (four releases back) | **33.3 MB** of 396 MB | 61 | ~4 s |

The second row is the whole point: the covering-patch scheme needed an asset
explicitly naming v0.41.0 as its baseline to serve that install at all, and the
staged payload passed `AppPayload.ValidateDirectory` in both cases.

**The path.** `GitHubUpdateService.DownloadUpdateAsync` → `TryDownloadPartialAsync`:

1. Fetch the release's `payload-manifest.json` asset using headers-first streaming:
   reject an oversized declared length before reading, and stop an undeclared
   body one byte past the limit (`PartialManifestChecksumTests`). The asset is
   66 KB, bounded by
   `MaxPayloadManifestBytes` = 4 MB — deliberately NOT the 256 KB checksum
   ceiling, since the file grows a line per payload file) — its presence in
   `UpdateInfo.ManifestAssetApiUrl` is the entire condition for trying — and
   **cross-check it against the digest `SHA256SUMS.txt` publishes for it**
   (`PayloadManifestMatchesPublishedDigestAsync`). Read as BYTES, because the
   published digest is over the file, not over its decoded text.
2. `InstalledPayloadReader.FromDirectory` — what the install has. **`Reconcile`
   hashes, it does not merely `stat` (2026-09-04).** The size probe left a
   PERMANENT gap its own XML doc admitted to ("closes the size-CHANGING half"):
   a file modified in place at the same length — a targeted swap, an AV
   "repair", bit rot — was reported as already-present by that update, by every
   later partial update, and forever, with no UI anywhere to ask for it back.
   The stat still runs FIRST, so a wrong-length file is dropped without being
   read and only files that still look intact are hashed; an entry whose hash
   cannot be read is dropped too, because re-fetching costs bandwidth and
   trusting it costs the install.
3. Range-fetch the asset's tail (192 KB), read the end-of-central-directory
   record and the central directory (`ZipCentralDirectory`, pure).
4. `UpdateFetchPlanner.Plan` — diff, resolve each needed file to its member,
   bound each member's extent by the NEXT member's local header, coalesce
   nearby extents into as few requests as possible (pure).
5. `PartialUpdateDownloader` fetches the ranges, inflates each member into a
   staging dir and **verifies every file against the manifest's SHA-256**.
6. The staged payload is zipped and handed to the UNCHANGED
   `ApplyDownloadedUpdate`, so the bounds check, `BoundedZipExtractor`,
   `AppPayload.ValidateDirectory`, `PayloadDeletions` and the robocopy script
   all run byte-identically to the full-zip case.

Rules that are load-bearing:

- **Every refusal falls back to the full download; none of them fails the
  update.** A 200 instead of a 206, a zip64 archive, an unsupported compression
  method, a short response, a hash mismatch, a dropped connection, an
  `AppPayload.ValidateDirectory` complaint — all return an outcome and the
  ordinary full-zip path runs. That fallback is what makes attempting any of it
  safe, and it is what `PartialUpdateDownloaderTests` mostly asserts.
  **The ONE exception is the plugin guard (#893):** an install carrying a plugin
  (a declared plugin's file in its app folder) whose release manifest does not list
  it is a plugin build looking at a public release, and the full zip would only be
  refused on apply.
 `PluginUpdateGuard` throws `PluginUpdateRefusedException`, which
  the partial path's catch-all rethrows; `UpdateInstaller` refuses the same pairing
  on apply (`PluginUpdateGuardTests`).
- **`RangeFetchResponse.IsPartial` must be checked before the body is read.** A
  server that ignores `Range` answers 200 with the WHOLE asset; a client that
  reads it has done the 414 MB download it was avoiding and then mis-parses it
  as the slice it asked for. `HttpAssetRangeFetcher` returns an empty body on a
  non-206 on purpose.
- **Zip64 is REFUSED, not handled.** The 32-bit records carry `0xFFFF` /
  `0xFFFFFFFF` sentinels when the real values live in a zip64 record, and
  reading those as real numbers is how a parser silently addresses the wrong
  bytes. Same decision, same reason, as `BoundedZipExtractor`.
- **A member's extent is bounded by the NEXT member's local header**, not by a
  guessed slack. The LOCAL header's extra field is not the central record's, so
  the data offset is only knowable from the bytes fetched — and using the next
  offset doubles as an overlap check (crossing extents means `MalformedLayout`,
  i.e. a full download).
- **The manifest is written BEFORE the zip** so the payload carries its own
  copy; that is what lets an install describe itself to the NEXT update without
  re-hashing 414 MB. It EXCLUDES ITSELF (its own hash changes when it is
  written). Built by `tools/release/BuildPayloadManifest.cs`, which calls these
  very types so packaging and the updater cannot drift.
- **`PayloadManifest.AlwaysInclude` is read off `AppPayload.Validate`'s
  requirements, and it includes the HOST PAIR.** `hostfxr.dll` and
  `coreclr.dll` do not change in a managed-only release, so a purely
  content-diffed plan would omit them — and `AppPayload.ValidateDirectory`,
  which runs against the STAGED payload on the user's machine before the
  irreversible updater script launches, refuses a self-contained payload without
  them. That payload would be rejected after the download, on every machine.
  ~7 MB against 414 MB is what makes a partial payload applicable at all. The
  planner gets this for free by sharing `PayloadDelta.Between`.
- **The install inventory is `InstalledPayloadReader`, and its hashed fallback
  is built from the RELEASE's path list — never by walking the install dir.** On
  a portable install `WritablePaths.ConfigRoot` IS the install directory, so a
  directory walk would inventory `appconfig.json`, profiles, keybinds and crash
  reports, find none of them in the release, and hand the updater a
  `payload-deletions.txt` that erases them. `InstalledPayload.CanDeriveDeletions`
  is the explicit half of the same guarantee: only a manifest the RELEASE wrote
  may produce deletions. (Same footgun, same directory, as
  `PayloadReceiptFilter.IsLocalState`.)
- **A shipped manifest is RECONCILED by a size probe before it is trusted.** An
  install is a directory a user can touch; a manifest entry whose file is now
  missing or a different size is dropped so the file gets re-fetched. The verify
  step cannot cover this — it only ever sees the files that WERE fetched.
- **`SelectWinX64Asset` still excludes `-from-v` names.** Nothing builds patch
  assets any more, but v0.41.0–v0.42.4 published them and those release pages
  are permanent; a patch is named `…-win-x64.zip` too, so without the exclusion
  the FULL selector could hand a partial payload to an install it was never
  diffed against. `-partial-` is excluded for the same reason: that is the name
  of the locally ASSEMBLED payload, which lands in the same downloads folder.
- `PayloadDelta.Between(from, to)` — changed / added / deleted / unchanged. It
  is the ONE diff; the planner uses it rather than restating the rules. Its
  must-ship list is `PayloadManifest.AlwaysIncludeFor(to)`: the flat five, or for
  a SPLIT release (#413) the launcher plus the app's five under `app/` — and a
  split install carries its manifest at `app/payload-manifest.json`. See
  [split-install.md](split-install.md).

- **Deletions are load-bearing, because of the no-purge rule.** A file the new
  release dropped survives forever otherwise. The assembled payload carries
  `payload-deletions.txt`; `PayloadDeletions.TakeFrom` reads it out of the
  staged payload (and REMOVES it, so it is never copied into the install) and
  `UpdaterScriptBuilder.Build` emits one `del` per entry, **after the robocopy
  failure check** — a failed copy leaves the old version installed and that
  version still needs its files.
- **`PayloadDeletions.Parse` treats the list as untrusted input** even though we
  produce it: each line becomes a `del` argument in a script running over the
  install directory. It drops anything rooted or `..`-walking, anything cmd.exe
  would re-interpret (`" % & | < > ^ * ? :`), anything non-ASCII (the script
  must stay plain ASCII for cmd's codepage) and — above all — anything in
  `AlwaysInclude`. A rooted path is REFUSED rather than trimmed to a relative
  one: silently re-rooting `/etc/passwd` into the install dir and deleting it is
  worse than ignoring the line. `UpdaterScriptBuilder` re-validates rather than
  trusting its caller, since it is the last step before the shell.
- **An update archive is an UNTRUSTED archive, on BOTH paths, and goes through
  `BoundedZipExtractor` like every other one (0.41).** It did not, twice:
  `AppPayload.ValidateArchive` enumerated `archive.Entries` — one
  `ZipArchiveEntry` materialised per member — with no `CheckDeclaredEntryCount`
  pre-check, on a zip a remote host had just streamed at us; and
  `ApplyDownloadedUpdate` (`UpdateInstaller` since #652), which every apply
  path funnels through, expanded it with a bare `ZipFile.ExtractToDirectory` — no member
  count, no decompressed-byte budget.
  Now: `AppPayload.CheckArchiveBounds` (declared count off the
  end-of-central-directory record, before anything is opened, capped at
  `MaxArchiveEntries` = 16,384 ≈ 20× a real payload) runs at every entry point —
  `GitHubUpdateService.DownloadUpdateAsync` in place of its old
  `Entries.Count == 0` probe, and `ApplyDownloadedUpdate` before extraction —
  and the extraction itself is `BoundedZipExtractor.ExtractBounded` under
  `AppPayload.InflationCeiling` (`max(512 MiB, 8× the archive)`). A refusal
  throws `InvalidDataException` and the staging tree is deleted, so nothing is
  ever half-applied. **A zip64 archive is refused** (its EOCD stores the `0xFFFF`
  sentinel) — deliberate; no release has 65,535 members. `ValidateArchive`'s
  runtimeconfig read is capped too, at 1 MiB: it used to `ReadToEnd` whatever a
  member under that name inflated to, just to classify the layout.
- **Every HTTP read in `Update/` is BOUNDED, and the bounds are not the
  client's `Timeout` (0.45).** All four were missing and each one let a host we
  do not control decide how much of the machine to take.
  - `GitHubUpdateService.DownloadFullAsync` used `Content-Length` only for the
    progress fraction and copied `while (read > 0)` to EOF. It now refuses a
    declared size over `GitHubUpdateService.MaxAssetBytes` (4 GiB) up front,
    reads `while (written < ceiling)`, and fails a short body.
  - `HttpAssetRangeFetcher` read a 206 with a bare `ReadAsByteArrayAsync`. The
    body is now bounded by the range that was REQUESTED (declared length
    refused before buffering, read stops one byte past, `MaxRangeBytes` = 32 MiB
    over the planner's 16 MiB). A SHORT body is still handed back short, because
    `PartialUpdateDownloader` owns that comparison. **One ENTRY can be far bigger
    than the cap** (v0.60.0's `DotCharter.Tone.UI.dll` and `tools/ffmpeg.exe`
    are 160 MB each), so `PartialUpdateDownloader` fetches a range over its
    `maxRequestBytes` (the fetcher's cap) as consecutive capped requests and
    joins them, refusing any short piece (#220). Before that, every update that
    changed such a file fell back to the full asset.
  - `HttpClient.Timeout` is `Timeout.InfiniteTimeSpan` on both clients' owner
    and every call carries a linked-CTS budget: `DefaultApiTimeout` (30 s) for
    metadata, `DefaultDownloadTimeout` (30 min) armed on the SAME cts once the
    headers are in, `DefaultRangeTimeout` (2 min) per range. The old 30 s
    bounded the header exchange only — with `ResponseHeadersRead` it always
    does — and its own comment said so while offering no replacement.
  - Both `Update/` clients are built by `Online/OnlineHttp.CreateClient`, so
    they carry `MaxResponseContentBufferSize`. They pass their OWN handler
    (redirects; one instance per process), so the shared pool is deliberately
    not used — the cap is the half that was missing.
  `OnlineHttpPolicyTests` scans FOLDERS, and `Update/` is now one of them:
  a bare `new HttpClient(` under `Online/` or `Update/` fails the gate.
  `UpdateNetworkBoundsTests` drives all of it off stub handlers with no network.
- **The FULL asset is verified against `SHA256SUMS.txt` too (0.48), and it was
  the only download that never checked its content.** The partial path hashes
  every file it fetches against `payload-manifest.json`; the full path — the one
  every pre-0.41 install takes, every range refusal falls back to, and the one
  that actually moves the 414 MB — had only `AppPayload.CheckArchiveBounds`,
  which asks whether the bytes are a readable zip of a plausible member count and
  nothing about whose bytes they are. They arrive over a redirect chain to
  `objects.githubusercontent.com` and are then robocopied over the install.
  **It fails CLOSED, version-gated (2026-09-04).** The three outcomes used to be
  "mismatch throws, unreachable proceeds, unlisted proceeds" — and the last two
  are states an attacker who controls the release document can ARRANGE, by
  renaming the asset out of the manifest or making the manifest unreachable, so
  the one content check on the path was optional at the attacker's discretion.
  `GitHubUpdateService.FirstDigestPublishingVersion` (0.41, the release the
  script began emitting digests) is the gate: at or above it every one of the
  three is a refusal; below it the escape hatch stays, because those releases
  published nothing to check against and refusing them would strand every old
  install. `ResolveReleaseDigestsAsync` fetches the digests ONCE for both
  download paths and is where the version gate lives.
  `Services/Update/FullAssetChecksumTests` covers all four cases (match, the
  three refusals) plus the pre-0.41 escape hatch.
- **The download url is HOST-RESTRICTED, not merely un-signed (2026-09-04).**
  Every asset url comes out of the release JSON, so it is data. The allow-list
  (`AllowedHosts`) gated only whether the user's GitHub token was ATTACHED —
  never whether the app would download from that host at all, which is the
  larger half. `IsAllowedReleaseHost` (was `MayCarryCredentials`) lives with the
  other pure release rules in `Services/Update/GitHubReleaseRules.cs` and is
  enforced in `Update/GitHubApiRequest.Create` — the one request factory both
  `GitHubUpdateService.CreateRequest` and the private model download
  (`GitHubReleaseAssetDownloader`, #267) build through — so it is a choke point rather than a rule at one call site,
  and `DownloadUpdateAsync` refuses the asset and manifest urls up front with a
  message naming the host. `Services/Update/UpdateCredentialScopeTests
  .AnAssetUrlOutsideTheAllowedHostsIsRefusedOutright` asserts the app never
  talks to the foreign host AT ALL — it used to assert only that the header was
  withheld.
- **…and since 2026-09-04 the PARTIAL path checks its manifest the same way.**
  The per-file hashing at step 5 was circular on its own: `payload-manifest.json`
  is the document that says which files this install is missing and what each one
  must hash to, so an attacker who could substitute it simply declared the digests
  of whatever it wanted written. `New-Release.ps1` has digested it into
  `SHA256SUMS.txt` since 0.41 (the header comment at :13-14 says "digests of
  assets 1-2") and nothing read that. `PayloadManifestMatchesPublishedDigestAsync`
  applies the SAME three outcomes as the full path — unreachable ⇒ warn and
  proceed, unlisted ⇒ warn and proceed, listed-and-mismatched ⇒ **fall back to the
  full download, which digests its own bytes and refuses a mismatch outright**. A
  fallback is a refusal here, not a "trust it": `Services/Update/PartialManifestChecksumTests
  .AMismatchingManifestOnAReleaseWhoseAssetAlsoMismatches_RefusesTheUpdate` is the
  case that proves the second half.
- **The partial fetch gives up when it would exceed HALF the asset** — that
  usually means the runtime moved, and the full zip is the honest answer
  (`FetchPlanLimits.MaxTotalBytes`, defaulted from the asset length). It also
  gives up when the release publishes no manifest at all, which is the normal
  case for **every release before v0.41.0**.

---

