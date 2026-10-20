# Editor keyboard input (UI router)

One file per subject under `editor-input/`. Read the one you need:

- [string-edit-and-chord-verbs.md](editor-input/string-edit-and-chord-verbs.md) — the string-edit mask; splitting a hand shape; the chord-template trap
- [chord-verbs.md](editor-input/chord-verbs.md) — what a keyboard verb means on a CHORD, and how far it reaches: a technique key follows the Inspector's pinned "Apply to" ladder while it is on screen (#604); the chord-template panel's ladder
- [pointer-wheel-and-clicks.md](editor-input/pointer-wheel-and-clicks.md) — the pointer wheel; right-click; double-click, pan aliases and the cursor; the time ruler
- [tools-and-pointer-drag.md](editor-input/tools-and-pointer-drag.md) — the editor tools are transient; what a pointer drag owes the chart; where the detail lives

## MIDI controller binds (#310)

A MIDI press is a second door into the SAME verbs: `UI/Input/MidiBindingDispatch`
hears `IMidiTriggerInput.Pressed` (UI thread), looks the trigger up in
`ProjectState.Keybinds.FindMidiVerb` and runs `ChartKeybindHandler.HandleVerb(verb)`
— the action registered under the verb's `KeybindConfig` property name, with the
same open-dialog block and the same usage id as the chord. It routes only where a
key would (`IsChartEditorVisible`, palette closed), so a press while Settings is
learning runs nothing; `MainWindow.RequestChartFrame` redraws after it. A verb whose
chord was cleared still runs from its pad (`HandleVerb` indexes every registration,
bound or not). A new keybind verb is MIDI-bindable for free IF it is registered as
`Register(() => …Keybinds.<Verb>, …)`; a computed-chord family must join
`KeybindConfig.MidiUnbindableVerbs` — `UI/MidiBindingDispatchTests` fails otherwise.
Binds and rebinding UX: [settings.md](settings.md) (the Keybinds tab).

