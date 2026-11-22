# `State/Checkpoints/` + `Diagnostics/` — snapshots and the diagnostics bundle (deep reference)

One file per subject under `state-diagnostics/`. Read the one you need:

`ProjectState.SongAudioPath` exposes the declared model reference without resolving it;
section ownership uses its presence only for first attachment. `ChartProject.ProjectId`
and `SongAudioRevision` own the logical song independently of save/cache path changes.
`CommitSongAudio` advances the revision for attachment, replacement and detachment;
Add Silence calls it for execute/undo/redo. Playback reload/notification does not.

- [arrangement-switch.md](state-diagnostics/arrangement-switch.md) — the arrangement gesture's timing boundary
- [checkpoints-and-snapshot.md](state-diagnostics/checkpoints-and-snapshot.md) — checkpoint durability; the snapshot source of truth
- [dirty-tracker-and-history.md](state-diagnostics/dirty-tracker-and-history.md) — `State/ProjectDirtyTracker`; `State/HistoryService`
- [usage-statistics.md](state-diagnostics/usage-statistics.md) — `Diagnostics/UsageStatistics`
