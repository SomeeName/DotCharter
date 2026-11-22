# Arrangement switch diagnostics

`ProjectState.ActiveArrangement` raises `PropertyChanging` before assigning the new
arrangement or notifying any change subscribers. Reassigning the same instance and
`NotifyActiveArrangementChanged` (metadata republishing) do not raise that start signal.

**Only a USER switch is timed** (#856): `Views/Shell/ArrangementSwitchTiming.IsUserSwitch` requires an
outgoing arrangement that belongs to the open project and no open in progress
(`ProjectState.BeginProjectOpen`, taken by `ProjectSessionService`'s file open and the song browser's
library open around `ApplyProject` and the view-memory restore). Before that, an open's own
arrangement moves were logged as `Arrangement switch #1/#2`, and their window ran to the first idle
tick after the deserialize, the audio decode and the stem load: 26 of the 29 over-budget owner samples
(7,527 / 5,142 / 2,442 ms) were opens, not switches. A new path that replaces the project and then
moves the arrangement takes the scope too.

`ArrangementSwitchTiming.Observe` (called by the composition root) starts `SurfaceShowTiming` from that signal. This
uses the same application log sink as inspector tabs and dialogs: `Surface 'Arrangement switch #N'
shown in N ms (gesture → first idle tick).` Unlike those, the switch completes at
`DispatcherPriority.ApplicationIdle`, not Background: its subscribers queue their own refresh at
Background (the Shapes catalogue, `PrewarmPacer` re-warms), and a Background completion posted
BEFORE them would run first and log the switch as done too early. It includes synchronous
subscribers and every queued pass down to Background, including work those passes queue in turn.
The sequence number keeps consecutive switches distinct in the logger's duplicate filter.
It does not wait for delayed validation, sheet rendering, tone workers or GPU presentation.

`SurfaceShowTiming.DetailSink` records named synchronous work separately. Hidden surface
re-realization uses it; the app emits debug detail for passes taking at least 3 ms.
The diagnostic cannot replace an exception thrown by the measured work.

The hidden Shapes catalogue marks arrangement, level and history changes stale;
`ShapesPanelView` activates its current data through `IScreenLifecycle` on show.
See [inspector-pane.md](../../ui/misc-surfaces/inspector-pane.md) for its refresh policy.

`UI/ArrangementSwitchTimingTests` pins the start boundary, which changes count as switches, the open
scope on both open paths and the composition wiring (the idle priority included); `Perf/ArrangementSwitchOrderUiPerfSmokeTests` pins that the line is recorded
after a subscriber's queued refresh and the work that refresh queues; `UI/SurfaceShowTimingTests`
pins the diagnostic sink contract. None asserts a duration. `Perf/ArrangementSwitchColdUiPerfSmokeTests`
logs cold and warm switches on the densest multi-arrangement corpus chart, with each synchronous
`ProjectState` subscriber's cost, to `perf-reports/arrangement-switch-cold.txt` (skips without
`TestData/`).

Measured on a 3-arrangement chart with dynamic difficulty (Debug, headless, 60 switches; median /
worst): Shapes tab hidden 849 / 4,824 ms before, single-digit / under 70 ms after. The cost was the
hidden Shapes catalogue: every switch replaced its rows, the list built a container per card inside
the `Rows` binding's change handler, and `HiddenSurfaceRewarmer` then laid all of them out.
