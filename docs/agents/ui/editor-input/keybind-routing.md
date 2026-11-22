# Editor keyboard input — keybind ROUTING through `MainWindow`

Part of the editor keyboard-input contract. Router: [../editor-input.md](../editor-input.md).

## Keybind ROUTING (`MainWindow`) — how a chord reaches `ChartKeybindHandler`

The defaults and their semantics are above; this is the delivery mechanism.

## Keybind routing (MainWindow)

- Three routes feed `ChartKeybindHandler` (all in `MainWindow.axaml.cs`):
  `Timeline_KeyDown` (focus inside the timeline grid), the window **bubble
  fallback** `GlobalKeybindFallback` (any key no control handled, editor
  surface only — so shortcuts survive focus landing on a toolbar/inspector
  control), and the window **tunnel** `GlobalKeyDownPreview` (palette keys,
  Esc, F11, Ctrl+Space, and the play/pause chord — Space must beat a focused
  Button's "Space = click"). Both global routes stand down while a `TextBox`
  (incl. the inner editors of NumericUpDown/AutoCompleteBox) or a
  `KeybindCaptureBox` has focus — text inputs keep native typing/undo. The
  bubble route asks `CanRouteGlobalKeybinds()`; **every tunnel branch asks
  `Views/GlobalChordGate`**, a pure predicate set (unit-tested in
  `GlobalChordGateTests` — `MainWindow` needs the whole app booted, so its own
  routing tests are `*UiPerfSmokeTests`: outside the default gate, in their own
  `dotnet test --filter-class "*UiPerfSmokeTests"` run. They DO
  execute and they DO pass — run them after touching a route, because the gate
  will not.
  **A new tunnel branch MUST go through the gate**: four of them used to claim
  their key outright, so `Ctrl+Shift+P`/`Esc`/`F11`/`Ctrl+Space` could never be
  rebound (the tunnel runs root→leaf and marks the event handled before the
  `KeybindCaptureBox`'s own tunnel handler, which bails on `e.Handled`) and
  `Ctrl+Space` fired while the user typed in the Lyrics inspector. Three
  exceptions are deliberate and documented in that file: `Esc` ignores the
  text-input gate (nothing else handles it at all), and the palette's
  navigation keys plus `Ctrl+Shift+P` ignore it while the palette is open (the
  palette focuses its own search `TextBox`). Open dialogs are blocked inside
  `HandleKeyPress` (`HasOpenDialog`). Don't add per-control key handlers for global actions;
  register them in `AppKeybindRegistrar` and let these routes deliver.
- **The tunnel claims the KeyDOWN — but a focused Button/ToggleButton activates
  on the Space KEY-UP** (Avalonia default `ClickMode.Release`), so consuming
  only the KeyDown leaves the paired KeyUp live and a clicked (focused)
  ribbon-row toggle flips on every play/pause. `GlobalKeyUpPreview` (a second
  KeyUp tunnel handler) swallows the release of any key `GlobalKeyDownPreview`
  just consumed (`ActivationKeyUpLatch`). `WireGlobalKeyRoutes(Window)`
  installs **three** things — the KeyDown tunnel, the KeyUp tunnel and
  `Deactivated → latch.Clear()` — on MainWindow and on **every** pop-out
  top-level (the float timeline AND the 3D preview). **A new pop-out window
  must call that helper**: the 3D pop-out shipped without the handlers, so
  clicking into it (the whole point — it is a large preview surface) left Esc,
  F11/Alt+Enter, Ctrl+Shift+P and play/pause dead until the user clicked back
  to the main window. Registering only the KeyDown is equally wrong; the helper
  exists so the set cannot drift. Real pop-out windows are not headlessly
  testable (they need a live TopLevel + compositor re-parent), so the
  shared helper IS the guard — verify manually per the dock checklist below.
  Regression for the routing itself: `PlayPauseSpaceKeyRouting*`.
  - **The latch must never outlive the keypress.** It was a bare `Key?`
    cleared ONLY by the matching KeyUp, so any way of losing that release —
    the play action opening a dialog, the native Vulkan child window taking
    Win32 focus, an alt-tab — left it armed, and the next unrelated `Space`
    release reaching the window (a legitimate focused-button activation on the
    Settings page) was swallowed and the button did not click. Two escape
    hatches now close it: `Deactivated` clears the arm, and a KeyDown for a
    DIFFERENT key drops a stale one. `ActivationKeyUpLatch` is pure so it is
    tested in the DEFAULT gate (`ActivationKeyUpLatchTests`) — the same reason
    `GlobalChordGate` is a separate pure type.
- **Esc yields to an open inline timeline editor** (FHP / section / lyric card,
  `Views/Editor/InlineEditOverlayView.ClaimsEscape`, fed into
  `GlobalChordGate.Context.InlineEditorClaimsEscape`): the card's own tunnel
  handler cancels the draft, which it never got to do while the window tunnel
  cleared the selection first (#588). Same shape as `SearchBoxClaimsEscape`.
  The window still records that first delegated Escape in the common held-key
  filter, so an OS repeat after clear/cancel cannot fall into shell navigation.
- **Auto-repeat never re-runs a TOGGLE verb** (#588). Avalonia's `KeyEventArgs`
  carries no repeat flag, so `ChartKeybindHandler` treats a second press of the
  same chord with no release in between as a repeat: `IsToggleVerb` labels
  (`PlayPause`, every `Toggle…`, `BookmarkBase`, `StringMaskBase`,
  `PenToggleStringBase`, `ClearSelection`, and the three `Open*Popover` verbs) are consumed and NOT run; nudges, fret steps and seeks
  keep repeating. `GlobalKeyUpPreview` reports every release
  (`HandleKeyRelease`) and `ClearActivationLatch` (Deactivated) calls
  `ResetHeldKeys`, so a lost release cannot eat a real press. Pinned by
  `KeybindAutoRepeatTests`. The window's F11/Alt+Enter, Ctrl+Shift+P, palette
  activation/dismissal and Escape chain call `HandleToggleKeyPress`, sharing
  that same hold with registered verbs. This also covers gameplay's Escape
  pause/resume and every pop-out wired through `WireGlobalKeyRoutes`.
- **A cleared digit family binds nothing**: every digit slot is
  `KeyChord.WithDigit`, which returns `KeyChord.None` for an unbound base (it
  used to register the bare digits with the base's empty modifiers).


- **Registrar lookups must read the CURRENT `ProjectState.Keybinds` table.**
  Settings Cancel and config import replace that object. Capturing it in a
  local at registration time makes `RefreshBinds` keep reading the abandoned
  table: reverted shortcuts remain active, and subsequent saved rebindings
  show in Settings and the palette while dispatch still uses the old keys.
  `RegistrarKeybindReplacementTests` exercises the real registrar through
  Cancel, a subsequent Save, dispatch and clearing the binding.
