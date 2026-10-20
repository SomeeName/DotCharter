# `Infrastructure/` + `Update/` — config, backup, crash reports, library doctor, auto-update (deep reference) — `Logging/`: severity and category are independent

## `Logging/` — severity and category are independent (moved here from `DotCharter.Services/AGENTS.md`)

- **Log SEVERITY and log CATEGORY are independent, and both are configurable
  (0.40).** `AppConfig.MinimumLogLevel` (default **Info**) + `LogLevelOverrides`
  keyed by category NAME. Before this the only filter was the category bit and the
  noise sat at the same category as the signal — silencing ~470 minimap-seek lines
  meant turning off `Input`, which also silenced every click and selection change.
  An override wins in BOTH directions (a one-way "stricter only" rule makes
  debugging one subsystem impossible). A multi-flag category matches no override
  key and falls through to the global floor; an `Error` ORs in
  `AppLogCategory.Errors` and therefore still escapes a disabled category, which
  is deliberate. The check is on the existing lock-free, allocation-free
  pre-filter — keep it there.

- **A `[xN] … suppressed over …` line is a FLUSH SUMMARY, and its own timestamp
  is when the summary was written, not when anything happened.** Read a log
  before you reason from one. `AppLogger` coalesces identical
  level+category+message+exception events (`CoalesceRepeatedLogs`, window
  `LogRepeatCoalesceWindowMs` = 2000 ms) and a background `Timer` flushes the
  runs every 1000 ms once `LogRepeatSummaryIntervalMs` (1000 ms) has elapsed
  since the FIRST occurrence — so the summary can be stamped **up to ~2 s after
  the last real event**, and the whole batch flushed on one tick shares one
  millisecond. The summary also carries the ORIGINAL occurrence's thread id, so
  even `[Tn]` points back at the earlier event. The first occurrence of any
  message still prints as a normal line at its own time; **the `[xN]` form only
  ever appears for events that already printed**.
  **This is not theoretical — it cost a full misdiagnosis in 0.45.** v0.44's
  tone-restore failures all occurred at 12:40:48.44x (`ToneSessionService`
  firing the saved rig before the engine existed — see
  [dsp-tone-rig.md](../dsp-tone-rig.md)); their three summaries flushed together at
  12:40:50.124 and were read as a SECOND, separate premature tone-apply site on
  a different code path ~1.7 s later. The tell was in the lines themselves:
  three different module names sharing one millisecond while reporting internal
  spans of 0 ms and 2.8 ms is impossible for real events and inevitable for a
  flush batch. The summary now prints `first at HH:mm:ss.fff` beside the span so
  the window is on the line; `FormatRepeatSummary` is pure and pinned by
  `Services/Logging/AppLoggerTests`.
- **Every number and timestamp in a log line is `InvariantCulture`.** The
  duration in the summary above was an interpolated `$"{ms:0.#}ms"` with no
  provider, so the owner's German machine wrote `2,8ms` where every doc, test
  and reader expects `2.8ms`. A log is read by whoever is diagnosing, not by
  whoever produced it.

