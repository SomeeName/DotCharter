# DotCharter.Presentation.Fakes Agent Guide

Services-free fakes of the `DotCharter.Presentation` contracts, for the gallery
(`DotCharter.UI.Gallery`) and the tests. **`DotCharter.UI` may never reference
this project** — a fake in the product assembly is one `DataContext` typo away
from shipping (`Infrastructure/AssemblyLayeringTests` checks the emitted
references and the csproj).

## Shape

- `FakeScreens.VariantsOf(contract)` finds a contract's one fake and reads its
  `Variants` — the gallery and the golden harness enumerate the screen catalog
  through it.
- `Library/FakeLibraryScreen.cs` (+ `FakeLibraryData.cs`: search, sort with unknowns last, and column filters that really narrow the rows, so a chip and its lit funnel always agree; `ByTitle` lights the song column's title sort; `ManyTunings` (+ `FakeLibraryTunings.cs`) is a large library's Tuning popover, ~40 options labelled as the facet engine labels them, one wider than the popover, with a `ManyTunings(Func<string, string>)` overload a layout test feeds the shipped strings. Its `Full` is the 16 hand-written rows, not a library at its realistic maximum: the layout tests read those rows, and the owner-sized check runs on the real adapter, `LibraryConstructionUiPerfSmokeTests.AnOwnerSizedLibrary_RealizesOnlyTheRowsOnScreen`), `Dialogs/FakeMessagePromptScreen.cs` and the wave-2 dialog fakes (`Fake<Name>Screen` over the shared `FakeDialog<TResult>`, whose `Answer(result)` builds a verb that completes the dialog — the default button must answer a NON-default result, which the headless Enter test relies on), `Onboarding/FakeSystemCheckScreen.cs` (all ready, checking, and each capability off), `Editor/FakeChordShapesScreen.cs` (+ `FakeChordShapeData.cs`: a song's grips with duplicates, missing and suggested fingerings, an arpeggio and an unused shape, 90 templates for `Full`, German/Japanese names for `LongText`, each filter and the rename box), `Editor/FakeAiChatScreen.cs` (+ `FakeAiChatParts.cs` — option, turn, and a history picker whose rename and delete really work — and `FakeAiChatData.cs`: a chat about a verse riff, German/Japanese turns with a long local model name for `LongText`, 30 exchanges and 40 earlier chats for `Full`, a reply being worked on, a failed one, no provider, the picker open and refusing another chart's chat, a three-line draft, the empty pane on Ask / explain with that task's line and hint (each task has its own, `FakeAiChatData.JobText`), and an unanswered reply whose Retry really sends again), and `PlayMode/Fake<Screen>Screen.cs` per contract (Get Ready's path tile and tuner string in `FakeTuningCheckParts.cs`; the setlist parts — `FakeSetlistPanel`, whose New / Add / remove / Delete really edit it, and `FakeSetlistRun`, whose Next and End move it — in `FakeSetlistParts.cs`, with Song Select's setlist variants in `FakeSongSelectScreen.Setlists.cs`), plus shared pieces
  (`FakePlayModeFlowParts.cs`: `FakeFlowText`'s long strings, `FakeChoiceOption`,
  `FakeFactTile`).
- Every fake exposes `public static IReadOnlyList<FakeVariant<IContract>> Variants`
  (`FakeVariant.cs`) naming its public factories. Required names are
  `FakeVariantNames.Default`, `Empty` and `LongText`; `Full` with a list,
  `Busy` / `Error` with a progress or failure state. Other states keep their own
  names (`NoMatch`, `NeedsTuning`, `Weak`, …).
- `Presentation/FakeVariantsTests` fails when a factory is missing from
  `Variants`, a name repeats, a variant returns a shared instance, or a name the
  fake's CONTRACT calls for is missing (`ScreenCatalogTests.RequiredVariants`:
  `Full` with any list property, `Busy` / `Error` with the flags). There is no
  pin: add the variant.
- `Full` is the list at its REALISTIC maximum, not a copy of `Default`:
  Song Select's is 2,160 distinct songs (`PlayMode/FakeSongLibraryFull.cs`, built
  by index arithmetic so it is the same on every run; the hand-written rows
  stay `Default`), Results has 32 sections and 12 leaderboard rows, Start 12
  profiles, Get Ready 8 paths and a 7-string tuning, Countdown 8 beats.
  `Perf/SongSelectRedesignUiPerfSmokeTests.Full_RealizesOnlyTheRowsInView_Of2160`
  fails if the song list stops virtualizing (unvirtualized, one layout took ~32 s).
- `LongText` carries German and Japanese strings. An empty fixture hides layout
  bugs — seed real-sized data.
- Commands are plain `RelayCommand` fields, never `[RelayCommand]` methods: a
  generated `…Command` reached only through an interface `x:DataType` reads as
  ambiguous to `UI/RelayCommandReachabilityTests`.

The Presentation source rules (banned APIs, file/type size, no history comments,
≤ 20 % comments) apply here too.
