# `Online/` — TONE3000 + NAM captures, MusicBrainz (deep reference)

Extracted verbatim from `DotCharter.Services/AGENTS.md`. **Read this before touching the TONE3000 OAuth flow, the NAM capture library or the lookup clients.** The NAM **file extension is load-bearing** (a capture stored as `.bin`/`.NAM` silently loads as nothing) and the DPAPI token store must stay on all three exclusion lists.

**v0.60 removed the online backend.** `DotCharterServerScoreService`,
`SupabaseScoreService`, `OnlineBackendRouter`, `RoomLeaderboardClient`,
`OnlineScoreSubmitter`, `LiveStatsPublisher` and the DPAPI online token store
are gone; what is left under `Online/` is TONE3000, MusicBrainz and the
two shared helpers (`OnlineHttp`, `OnlineJson`) plus the typed `OnlineResult` /
`OnlineError` pair in `OnlineModels.cs`.

**The capture library has a SECOND consumer since W8 (2026-09-16): the AI tone
designer.** `Ai/NamCaptureCatalog.FromLibrary(INamCaptureLibrary)` turns
`GetAll()` into the CLOSED list of captures a designed `namamp` module may name,
which is printed into the model's prompt. Two consequences for anything touched
here:

- **`NamCaptureManifest.FileName` and `Title` are now prompt-visible**, and the
  file name minus its extension IS the id the model answers with. A change to
  `NamCaptureIndex.BuildFileName` renames every downloaded capture's id.
- **`ModelId < 0` means BUNDLED** (the negative synthetic id
  `BundledNamCapture.SyntheticId` mints) and the catalogue orders on it — bundled
  first, then the player's own — because that block sits in a cacheable prompt
  prefix. Do not make a bundled import take a positive id.

The designer never sees a PATH: the validator resolves an id to the file name and
`DcRigPresetPreparation.Prepare` makes it absolute. See
[ai.md](ai.md) § "The capture list".

---

## Rules that apply to EVERY client in this folder

- **Read a response body through `Online/OnlineJson`, never a private copy of
  it.** The tolerant `GetString`/`GetBool`/`GetInt`/`GetLong`/`GetDouble`/
  `GetUtc` bank existed twice VERBATIM (~60 lines each) and
  `ExtractErrorMessage` four times, until 2026-08-28. The whole contract is
  *degrade, do not fault*: a missing, null or wrong-typed property falls back, a
  naive timestamp is ASSUMED UTC (reading it as local shifts every row by the
  viewer's offset), and a proxy's HTML error page yields an empty message rather
  than a parse exception. Error-key preference differs per server, so it is a
  parameter. `Services/Online/OnlineJsonTests` pins that contract and fails the
  gate on a re-introduced private reader; `Tone3000Json` is allow-listed with a
  reason (its readers try SEVERAL names per field — a different API, not a
  copy).

- **`OnlineHttp.CreateClient` is the only place an `HttpClient` is built here**,
  and `OnlineHttpPolicyTests.OnlineHttpIsTheOnlyPlaceAnHttpClientIsConstructed`
  fails the default gate on a bare `new HttpClient(` anywhere under `Online/`.
  It carries two things nobody remembers to add by hand:
  - **A response-size cap** (`MaxResponseContentBufferSize`, 8 MB). Every client
    reads its body with `ReadAsStringAsync`, which is UNBOUNDED — a hostile or
    broken server answering a leaderboard fetch with a multi-gigabyte body was
    materialized in full before any of our code ran. Over-cap now raises
    `HttpRequestException` with `HttpRequestError.ConfigurationLimitExceeded`,
    which each client's existing catch-all turns into a typed
    `OnlineError.Network`. **Detect it with that enum, never with the message —
    the message is localized**, and this repo's own build output is German.
  - **One shared, lifetime-bounded `SocketsHttpHandler`.** The DI clients are
    singletons, but `MusicBrainzClient` can be constructed **per lookup**, so a
    private handler each was a fresh connection pool per lookup. `PooledConnectionLifetime` is the other half — a singleton
    `HttpClient` over a process-lifetime pool never re-resolves DNS.
- **`HttpClient.Timeout` is a deadline for the WHOLE exchange, including reading
  the response stream — so it is the wrong tool for a download.** `Tone3000Client`
  therefore sets `Timeout.InfiniteTimeSpan` and applies a linked-CTS budget per
  call: `DefaultApiTimeout` (30 s) for search/tone/models, `DefaultDownloadTimeout`
  (10 min) for a model file capped at 128 MB. A timeout is reported as "timed
  out", never as "Cancelled." — the caller cancelled nothing and saying they did
  sends them hunting for a click they never made. Pinned by a SOURCE lint
  (`TheDownloadAndTheMetadataCallsUseSeparateBudgets`), because with a scripted
  handler that answers instantly no timeout of any size ever fires and the
  regression is invisible to behaviour.
- **The server's error text is untrusted content that we log verbatim.**
  `Tone3000Client` quotes it into a WARNING on purpose (it is the one line the
  owner can act on), so it runs through `Tone3000Client.Scrub` first: a server
  that echoes the bearer we just sent must not put it in the log file *or* in
  the typed message the UI shows. `OnlineSecretLoggingTests` drives every client
  through its real failure paths with distinctive credentials and searches the
  WHOLE log — the line that leaks is never the one that prints a token on
  purpose, it is a catch-all interpolating `ex.Message` that someone added while
  fixing something else.
- **Only `Network` and `Server` are worth retrying.** A 400 stays a 400 and a
  rejected credential stays rejected, so retrying those spends the delay and a
  round trip to be told the same thing.

---

- `Online/` — the lookup and capture clients. Everything account-, scoreboard-
  and server-shaped went with the online backend in v0.60.
  **TONE3000 NAM-capture browser (0.16):** `Online/Tone3000/` — the
  tone3000.com community-capture integration behind the pedalboard's "NAM
  Captures" section. `Tone3000Client` (`ITone3000Service`): search/tone/models
  metadata + size-capped model download — **all Bearer-authenticated** (the live
  API rejects anonymous requests with 401; the client attaches the user's token
  when a session exists and NEVER sends an empty/malformed header when it
  doesn't, and maps 401 → typed `OnlineError.NotSignedIn`); pure
  schema-tolerant mapping in `Tone3000Json`; client-side
  `Tone3000RateLimiter` (80/min sliding window, injected time) under the
  server's 100 req/min.
  **An ANONYMOUS search that comes back 200-with-zero-results is reported as
  `NotSignedIn`, not as a successful empty page.** Against the live API that
  cannot happen (it is 401), so the only things that produce it are a proxy, a
  captive portal or a schema change — none of which mean "no captures matched",
  which is exactly what the browser would otherwise print to a user whose real
  problem is being signed out. A SIGNED-IN empty page is still the genuine empty
  result, and the request still goes out either way, so a future
  anonymous-capable API keeps working.
  **The search wire contract is `Tone3000Wire`, and it is NOT guessable
  (0.36 — the whole of "search doesn't work" was this).** Source of truth:
  the reference at <https://www.tone3000.com/api> plus the official SDK
  (`github.com/tone-3000/api`, `src/tone3000-client.ts` → `searchTones`),
  which agree exactly. `GET /api/v1/tones/search` takes **`query`** (not `q`),
  `page`, **`page_size`** (not `per_page`; default 10, **max 25**), `sort`,
  **`gears`** (not `gear_type` — a LIST, `_`-joined because the values
  themselves contain hyphens: `amp_amp-cab_pedal`), **`sizes`**, `format`
  (singular) and `architecture`. Sort vocabulary:
  `best-match`/`newest`/`oldest`/`trending`/**`downloads-all-time`**; omitting
  `sort` is the documented default (best-match with a query, trending
  without), which is what `Tone3000Sort.ServerDefault` sends.
  **Unknown parameters are IGNORED, not rejected**, so every wrong spelling
  failed silently as an empty unfiltered query — never assume a bad parameter
  name will surface as an error. `BuildSearchQueryString` is pure so the whole
  URL is pinned without a socket (`Tone3000ClientTests`).
  **`architecture` has no "everything" value and the absent value is not one:**
  omitting it returns A1 + Custom and EXCLUDES A2-only tones (documented legacy
  default, "so API users that pre-date A2 don't receive models they can't
  load"). `Tone3000Architecture` (`Default`/`A1`/`A2`/`Custom` → omit/`1`/`2`/
  `custom`) is surfaced as the browser's architecture dropdown, defaulting to
  `Default` (A1 is most of the catalogue and there is no both-option) with A2
  one click away. `NamCaptureBrowserViewModel.PickModel` takes the same
  selection, because `GET /models?tone_id=` lists EVERY architecture of a tone
  and downloading the A1 file after an A2 search would make the filter look
  inert. The rule itself is `Tone3000ModelPicker.Pick` (DotCharter.Tone) since
  2026-09-26, shared with the AI designer's capture search
  (`Ai/NamCaptureSource.cs`, see [ai.md](ai.md)).
  **Response shape** (`PaginatedResponse<Tone[]>`): the list is under `data`;
  a tone carries `gear` (not `gear_type`), `format` (a scalar, not a list),
  `sizes`, `downloads_count`, and its architecture ONLY as the per-architecture
  counts `a1/a2/custom_models_count` (mapped to `Tone3000Tone.A1/A2/
  CustomModelCount` + `HasA2`); a model carries `architecture_version`
  (`"1"`/`"2"`/`"custom"`) and **no `format` at all** — an empty
  `Tone3000Model.Format` means UNKNOWN, never "not a NAM", and
  `NamCaptureLibrary.DownloadAsync` falls back to the TONE's format when
  resolving the load-bearing file extension. `Tone3000Json` stays tolerant
  (older spellings still probed) but tolerance only helps if one probe is the
  live name — keep the documented spelling FIRST.
  **Search leaves a log line either way** (it used to leave none at any level,
  which is why a totally broken search went unnoticed): INFO with the effective
  query string + result count on success, WARNING with the HTTP status + the
  server's own message + the URL on a rejection, WARNING (not Debug — Debug is
  off by default) on a transport failure. The VM mirrors it: a search that RAN
  always sets a status (count / "no captures matched" / "the search failed:
  …") and clears the "search TONE3000 for captures" invitation, so "matched
  nothing" is distinguishable from "the click did nothing".
  `Tone3000AuthService` (`ITone3000AuthService`) runs
  OAuth 2.0 + PKCE: system browser → loopback `HttpListener` on **port 47821**
  (`Tone3000OAuth.CallbackPort`, in the firewall-allowed range; redirect URI
  `http://localhost:47821/tone3000/callback/` must be registered with the
  client id) → code exchange → DPAPI session in `tone3000-token.dat`
  (`DpapiTone3000TokenStore` — on `ConfigBackupRules`' credential list AND
  `PayloadReceiptFilter.IsLocalState`; keep all three in sync).
  **Three rules the flow shipped without (2026-09-04), all pinned:**
  (1) **A failed refresh is not automatically a sign-out.** `ExchangeAsync`
  returns an `ExchangeOutcome` whose `Rejected` is true only for 400/401 (RFC
  6749 §5.2) or a 200 carrying an `error`; every other failure — a 503, a 429,
  an unparseable body — keeps the session and retries on the next call. It used
  to `SignOut()` on ANY null, so one bad minute on the far side DELETED the
  DPAPI file and cost the user a full browser sign-in. Same doctrine as the
  degraded-read rule for JSON stores: failing to read an answer is never
  authority to delete.
  (2) **`Tone3000OAuth.TryParseCallback` validates `state` FIRST.** It read the
  provider's `error` ahead of the CSRF check, so any page the user had open
  could `GET` the loopback prefix with `?error=access_denied` and end a sign-in
  that was going fine. The listener itself only ends its wait on a request for the
  callback path whose query carries THIS flow's `state` (`Tone3000OAuth.CarriesState`,
  passed to `IOAuthCallbackListener.WaitForCallbackAsync` as its gate); every other
  request gets a 404 and the wait runs on to the timeout, so a stray page cannot end
  the sign-in either.
  (3) **A sign-in is cancellable and single-flight.** The listener owns port
  47821 for the whole five-minute `CallbackTimeout`;
  `UI/ViewModels/Online/Tone3000SettingsViewModel` owns the flow's CTS, refuses
  a second concurrent `SignInAsync` (a second `HttpListener` on the same prefix
  cannot bind), and cancels from both `CancelSignIn` and `SignOut`. `CanSignIn`
  = configured AND not busy; `IsBusy` was bound by nothing before. Config:
  `AppConfig.Tone3000ClientId` (config-file-only; empty ⇒ NOTHING online works
  — the UI shows the tone3000.com/settings configure hint. Configured but
  signed out ⇒ search AND downloads need a sign-in: the browser shows the
  sign-in CTA, not the configure hint). `NamCaptureLibrary`
  (`INamCaptureLibrary`) is the local store under
  `%APPDATA%/DotCharter/models/nam/` (env override `DOTCHARTER_NAM_DIR`): one JSON manifest sidecar per capture
  (tone/author/gear/format/size/**license/attribution URL**/sha-256 — the
  attribution policy applies), downloads are slug-named
  (`NamCaptureIndex.BuildFileName`, untrusted remote titles never reach the
  filesystem raw), `.partial` + size-sanity + atomic rename like
  `StemModelManager`. Pure library filtering in `NamCaptureIndex`.
  **File EXTENSION is load-bearing:** NeuralAudio dispatches its loader by the
  file's LOWERCASE, case-sensitive extension, so a NAM stored as `.bin`/`.NAM`
  loads silently as nothing (the owner's `.bin` marshall bug). `BuildFileName`
  resolves the ext via `NamCaptureIndex.ExtensionFor`
  (`NormalizeFormat(nam/aidax/ir)` → else the `modelUrl`'s own ext → else `.nam`;
  **never `.bin`**), and `NamCaptureLibrary.MigrateFileExtensions()` (called once
  at startup in `App.axaml.cs`) repairs pre-fix files: rename to the correct ext
  (manifest format, else a content sniff — `"architecture"`⇒nam, `"in_shape"`⇒
  aidax) + rewrite the sidecar, idempotent + throw-free, case-only renames via a
  temp step. `Tone3000Json` probes several license key spellings so the chip/
  manifest license isn't blank. The native
  `namamp` playback module is Wave 3 (ABI v22) — until then the UI's
  "Load into rig" stays a disabled affordance keyed off
  `IDspChainService.Catalog` containing a `namamp` module type.
  **Bundled captures (0.16 starter set; grown into the catalogue's backbone in
  0.37):** the app ships a curated set of freely-licensed `.nam` files in
  `DotCharter.UI/NamCaptures/` (csproj `Content` copy, NOT AvaloniaResource —
  the module needs real files on disk), described by a sibling `manifest.json`
  (array of `BundledNamCapture`) + `ATTRIBUTION.md`. On startup
  `BundledNamCaptureInstaller.InstallFromDirectory` reads that manifest and calls
  `INamCaptureLibrary.ImportBundled` for each (copy + hash + manifest sidecar,
  synthetic NEGATIVE `SyntheticId` so they never alias TONE3000's positive ids),
  so a fresh install lists them in the Library view with NO sign-in. Idempotent +
  throw-free; `installed` counts only NEW imports (ImportBundled returns true on
  an idempotent no-op too). **Provenance is gated** by
  `BundledNamCaptureAttributionTests` (every shipped file ↔ manifest entry ↔
  `ATTRIBUTION.md`, license restricted to redistribution-permitting kinds, total
  under a 32 MB cap). The capture-backed factory presets (`nam-*` in
  `DspTonePresetLibrary`) reference these by bare file name.
  **Two things changed in 0.37 and both are deliberate.** (1) The set is no
  longer a demo — 63 files, and every one of the 70 capture-backed presets
  resolves its `modelPath` into it, so a missing capture is a SILENT preset
  rather than a missing example (`CaptureBackedPresetTests` pins the mapping in
  both directions). (2) **GPL-3.0 joined the license allowlist.** 58 of the
  files come from `pelennor2170/NAM_models`, which is copyleft. The reason is
  narrow: the shipped binary already statically links JUCE 9, Rubber Band 4 and
  the VST 3 SDK, all GPLv3-or-commercial, so the DISTRIBUTED app is already
  effectively GPLv3 and copyleft model DATA adds no obligation it did not carry.
  It does constrain the REPO — the source stays BSD-3, these data files do not —
  which is stated in full in `ATTRIBUTION.md` along with the one-step reversal.
  Unlicensed model-zoo files are still refused.
  **No bundled capture is NAM A2.** All are A1 (`WaveNet`/`LSTM`, file format
  0.5.0), verified at fetch time. The engine loads A2 fine (NeuralAudio 0.1.3
  dispatches on the file), but no public direct-download A2 pack existed as of
  2026-07-26 — TONE3000 requires a bearer token even to SEARCH
  (`GET /api/v1/tones/search` is 401 anonymously), so the in-app browser with
  its A2 architecture filter is the only supported route to one.

---

## `Online/MusicBrainz/` (moved here from `DotCharter.Services/AGENTS.md`)

  `Online/MusicBrainz/` backs ONE feature: New project's Latin-name suggestion
  (`MusicBrainzClient.FindLatinNamesAsync`, reached through
  `Session/NewProjectSources`). It follows the `Tone3000Client` shape — an
  optional `HttpMessageHandler?` ctor arg is the test seam, a pure
  `MusicBrainzJson` parses tolerantly, and every failure is an `OnlineResult<T>`
  rather than a throw. **MusicBrainz needs a descriptive User-Agent with contact
  info AND ≤1 req/s and blocks you for skipping either**; the gate uses an
  injected clock so the rule is tested without sleeping. The recording search
  (`FindRecordingAsync`) is private: it only supplies the title and album half of
  the suggestion. There is no Spotify lookup, no Cover Art Archive lookup and no
  CustomsForge upload plan any more (#866): the site's form changed too often to
  keep a filler, so the app never touches CustomsForge.

## Rejected sessions and capture names

A server rejection invalidates the stored TONE3000 session and raises
`AuthStateChanged`; the NAM browser displays the localized sign-in reason.
`Tone3000AuthServiceTests.InvalidatingSessionClearsStoredCredentialsAndNotifiesOnce`
covers invalidation. ID-less captures use a deterministic hash prefix to separate
downloads with different model URLs (`NamCaptureIndexTests`). Positive numeric
IDs keep the established filename format, including short IDs; the faceplate
recognizes both formats (`NamCaptureNamingTests`). A numeric amp name cannot be
distinguished from an ID by length alone, so manifest titles take precedence.
