# Add a setting

A setting is one saved value, one control on a Settings tab, and one place in the
code that reads it. This recipe adds a row to an existing tab. For a whole new
tab, follow "How to add a Settings tab" in `docs/agents/ui/settings.md`.

## Steps

1. **Store it.** Add a property with a safe default to
   `DotCharter.Services/Infrastructure/Configuration/AppConfig.cs`, with a comment
   saying what it does. Unknown keys in the config file are kept, so old and new
   builds can share one file.
2. **Describe it for the view.** Add a member to the tab's contract in
   `DotCharter.Presentation/Contracts/Settings/` (the example uses
   `IFilesSettingsScreen.cs`). Every contract member must be bound by the view.
3. **Fake it.** Add the member to the tab's fake in
   `DotCharter.Presentation.Fakes/Settings/` so the gallery and tests still build.
4. **Wire the view model.** In
   `DotCharter.Presentation/Screens/Settings/<Tab>SettingsViewModel.cs`, load the
   value, save it, revert it, reset it, and add it to the change tracking so the
   unsaved counter is right. Read and write through the configuration service,
   never a file. Add the row's resx keys to the tab's search rows so search finds it.
5. **Show it.** Add the row in `DotCharter.UI/Views/Settings/<Tab>SettingsView.axaml`,
   using the row grammar already in that file.
6. **Use it.** Read the value where the behaviour changes. A setting nothing reads
   is a dead feature.
7. **Strings.** The label and description go into all three resx files
   ([add-a-localized-string.md](add-a-localized-string.md)).
8. **Play Mode has its own list.** A setting shown in Play Mode's settings window
   is also registered in `DotCharter.UI/ViewModels/PlayMode/GameplaySettingsCatalog.cs`.

## Tests to add

- A view model test that loads, edits, saves, reverts and resets the new value:
  `DotCharter.Tests/Presentation/Settings/FilesSettingsViewModelTests.cs` is the
  model.
- A test that the consumer behaves differently for each value (in the example,
  `DotCharter.Tests/Services/Library/LibraryLevelImportTests.cs`).
- `DotCharter.Tests/UI/SettingsRoundTripTests.cs` and the Settings construction
  smoke test (`DotCharter.Tests/Perf/SettingsConstructionUiPerfSmokeTests.cs`) cover
  the saved file and the view. Run both after a view change.

## Example in the code

Any row on the Files settings page shows the full set: its `AppConfig` property,
the contract under `DotCharter.Presentation/Contracts/Settings/`, the fake
`FakeFilesSettingsScreen`, the view model under
`DotCharter.Presentation/Screens/Settings/`, the view, the service that reads it,
and the tests.
