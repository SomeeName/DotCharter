# AI chat history: per-chart, never another chart's (`DotCharter.Services/Ai/Chat/`, #562)

Part of the AI chart chat epic (#558). Running an instruction on the chart is
[ai-chat-instructions.md](ai-chat-instructions.md) (#560). Every conversation of the chat pane is
saved and can be reopened — **only for the chart it belongs to**. This folder is
the storage and the model; the pane, its picker and the localized refusal text
are #559's.

## Identity: `ChartProject.ProjectId`

- 32 lowercase hex digits (`Core/Domain/Project/ProjectIdentity`). Minted by the
  `ChartProject` constructor, so a new, never-saved project already has one.
- Written as `<dcProjectId>` on EVERY `.rscp` save (`RscpWriter`), like
  `dcWriterVersion`: absence means "older file". Read by `RscpHeaderParser` only
  when id-shaped — the id names a directory, so a hand-edited value must not be
  able to carry `..` or a separator.
- A file without one (or with a malformed one) gets
  `RscpChartSerializer.LegacyProjectId`: the first 128 bits of the SHA-256 of the
  FILE'S BYTES. Derived, not random, because the id is only written by the next
  save — a random id would orphan every chat started on a legacy project that is
  closed unsaved. The bytes do not change until that save, and the save writes
  this very id. Pinned by `Services/Parsing/Rscp/RscpProjectIdTests`.
- `ChartProjectRestorer` copies it (a clone is the same project). It is not chart
  content: no edit or undo touches it.

## Storage: a folder beside the project, keyed by the id

`<project folder>/ai-chats/<project id>/<conversation id>.json`, one file per
conversation (`AiChatStore`). Chosen over storing chats INSIDE the `.rscp`:

- chat text is not chart content — inside the project it would ride every
  `ChartProjectCloner` snapshot (undo, save, crash recovery), and a chat turn
  would dirty the project;
- copying or moving the project folder carries its chats (the folder goes with
  it), and a copy's chats then diverge from the original's;
- two projects never see each other's: the folder is per id, AND every read
  checks the file's own `chartId` against the open chart, so a conversation
  copied or edited into another chart's folder is refused (`ChartMismatch`) —
  never listed, loaded, overwritten or deleted there. Two `.rscp` files with
  DIFFERENT ids can share one project folder safely.

## The service: `IAiChatHistory` / `AiChatHistory`

- Works on the OPEN chart only (`IAiChatChartSource`, read on the caller's
  thread at the START of each call; `ProjectStateAiChatChartSource` is the app's
  one, over `ProjectState.ActiveProject` + `CurrentProjectPath`). A listing
  carries the `ChartId` it was made for, because the I/O runs on the pool and
  the user may have switched charts meanwhile. Saving a conversation whose
  `ChartId` is not the open chart's is refused — a reply that finishes after a
  chart switch is not written into the new chart.
- `List` / `Load` / `Save` / `Rename` / `Delete`, each returning an
  `AiChatStatus`; everything but `Ok` is a refusal the pane shows. `NoChart` and
  `ChartNotSaved` (a library song or unsaved import: no project folder) refuse
  up front.
- The three read outcomes stay apart: absent is `NotFound`; `Corrupt` is refused
  and moved aside as `<id>.corrupt-<stamp>.json` only when a save of the SAME id
  replaces it; `Unreadable` is presumed intact and never written over. A newer
  format is `UnsupportedFormat` and never overwritten. Writes are atomic
  (`AtomicFile`); calls are serialized and run off the UI thread.
- Bounds (`AiChatLimits`): 1 MiB per conversation file (a bigger save is
  refused — start a new chat; a bigger file is not read) and 200 conversations
  per chart (a NEW one past it is refused). Titles come from the first user
  message (`AiChatTitle`, ≤ 80 chars, cut at a word); rename keeps
  `UpdatedUtc`, a blank rename goes back to the first message's title.
- **Registered in `App.AiServices.cs`** as the singleton `IAiChatHistory` over
  `ProjectStateAiChatChartSource`, and driven by the chat pane's view model
  (`Presentation/Screens/Editor/AiChatViewModel` + `AiChatHistoryViewModel`):
  Send saves, the title's dropdown lists and searches, a row opens, renames and
  deletes (after asking). Every `AiChatStatus` refusal is shown in words from the
  `AiChat_Status_*` keys (`AiChatText.Status`); a listing made for a chart that is
  not the open one is dropped (`listing.ChartId != OpenChartId`). Pinned by
  `Presentation/Editor/AiChatViewModelTests` over the real store and
  `Perf/AiChatDockUiPerfSmokeTests` in the real shell.

- **Save As to another folder carries the chats** (#633): `ProjectSaver` copies
  `ai-chats/<id>/*.json` into the new folder after the write, next to the
  checkpoint carry (`AiChatSaveAsCarry`). COPIED, so the old project keeps its
  own; a file already at the target is never replaced; each copy lands under a
  temporary name and is renamed in without overwriting. Best effort: a failure is
  logged and never fails the save, and the original stays. Pinned by
  `Services/Session/ProjectSessionSaveOutcomeTests`.
- **Delete is idempotent, and a missing file never strands its row** (#632): the
  pane treats a `NotFound` delete as done (row gone, `Forget`), and a `NotFound`
  open drops the row, re-lists and says the chat is no longer there.
  `AiChatHistoryViewModel` always PUBLISHES a new `Entries` list — re-assigning
  the list it mutated raised no change, which is how a deleted row stayed on
  screen. A successful delete is logged (Info), so a later `NotFound` for the
  same id reads as a second delete.

Tests: `Services/Ai/Chat/AiChatHistoryTests` (round trip through a real `.rscp`
save and reopen, a legacy project's chats across an unsaved close and its first
save, folder copy, isolation, tamper, bounds, damaged files).

## Known limits

- Two copies of one project saved into the SAME folder share an id and so
  share their chats.
- A legacy file re-saved by another tool before DotCharter saves it gets a new
  derived id; chats made before that are no longer found.
