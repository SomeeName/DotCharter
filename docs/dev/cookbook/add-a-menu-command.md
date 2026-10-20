# Add a menu command

A command has up to five homes. Miss one and it exists but cannot be reached.

## Steps

1. **Put the item in its menu file.** Each top-level menu is one file in
   `DotCharter.UI/Views/Shell/Menus/` (`FileMenu.axaml`, `EditMenu.axaml`,
   `TempoMapMenu.axaml`, `ArrangementsMenu.axaml`, `VocalsMenu.axaml`,
   `ToolsMenu.axaml`, `ViewMenu.axaml`, `HelpMenu.axaml`). Pick the right group:
   a long menu is one level of submenus, at most 14 direct entries, and no submenu
   with fewer than three rows. A new group header needs its own resx key. A
   brand-new top-level menu is a new `EditorTopMenu` subclass plus its element in
   `DotCharter.UI/Views/Shell/MainMenuView.axaml`.
2. **Write the command.** Add a `[RelayCommand]` method to
   `DotCharter.UI/ViewModels/Shell/MainWindowViewModel.cs`, or to the partial file
   next to it that owns your area (for example `MainWindowViewModel.Editing.cs`).
   The source generator turns `DoThing()` into `DoThingCommand`. Put the real work
   in a service, not in the view model.
3. **Add a command-palette row** in
   `DotCharter.UI/ViewModels/CommandPalette/CommandCatalog.cs`. Ids look like
   `file.saveProject` or `tools.*`. This is not optional for a command with a
   shortcut.
4. **Add a shortcut, only if it needs one.**
   - Add a `KeyChord` property to
     `DotCharter.Services/Infrastructure/Configuration/KeybindConfig.cs`, a row in
     `DotCharter.Presentation/Screens/Settings/KeybindRowCatalog.cs`, and register
     the command in `DotCharter.UI/Input/AppKeybindRegistrar.cs`. Register the
     shell's `[RelayCommand]`; never write the action inline, or the key and the
     palette end up with two implementations.
   - In the menu file, show the chord with
     `InputGesture="{Binding ProjectState.Keybinds.X, Converter={x:Static converters:KeyChordToGestureConverter.Instance}}"`,
     never a literal, so a rebind shows up.
5. **Add the label** as a `Menu_*` key in all three resx files (see
   [add-a-localized-string.md](add-a-localized-string.md)). Add history and status
   keys too if the command edits the chart or reports a result.
6. **A command that opens a dialog** also needs the dialog itself. See
   [add-a-view.md](add-a-view.md).

## Tests to add

- A test of the behaviour behind the command, in the same folder as the service
  it calls (the example has `MutedStrumChordsTests` and
  `MutedStrumsToChordCommandTests`).
- Nothing extra for the menu wiring: these tests already fail when you miss a
  step.
  - `DotCharter.Tests/UI/MainMenuLocalizationTests.cs`: group size, resx keys.
  - `DotCharter.Tests/UI/CommandPaletteCoverageTests.cs`: a shortcut with no
    palette row.
  - `DotCharter.Tests/UI/MenuGestureCoverageTests.cs`: a menu row that shows no
    chord, or the wrong one.
  - `DotCharter.Tests/UI/PowerToolReachabilityTests.cs`: a service nothing calls.

## Example in the code

"Muted strums into a muted chord" is one menu item, one command, one palette row,
one shortcut (`KeybindRowCatalog`), three resx keys, and the service and tests
behind it: `DotCharter.Services/Editing/ChordShapes/MutedStrumChords.cs`,
`MutedStrumChordsTests` and `MutedStrumsToChordCommandTests`.
