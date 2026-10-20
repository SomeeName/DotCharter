# Add a view with its smoke test

New screens and dialogs follow one pattern: a contract, a fake of it, a view model,
a view, and one line in a catalogue. The catalogue then gives you a gallery page,
screenshot goldens and several lint tests for free. Background:
`DotCharter.UI/AGENTS.md`, section "New screens: `ScreenCatalog`, `ScreenHost`,
`DialogHost`", and `docs/agents/ui/dialogs/hosted-dialogs.md` for dialogs.

## Steps

1. **Contract.** `DotCharter.Presentation/Contracts/<Area>/I<Name>Screen.cs`:
   formatted strings, booleans, commands and lists. No Avalonia types; the
   Presentation projects cannot reference them.
2. **Fake.** `DotCharter.Presentation.Fakes/<Area>/Fake<Name>Screen.cs` with a static
   `Variants` list: at least a default, an empty, and a long-text case in German
   and Japanese. The gallery and the tests use these.
3. **View model.** `DotCharter.Presentation/Screens/<Area>/<Name>ViewModel.cs`,
   implementing the contract. Take services through the constructor (at most seven
   parameters). Register it in the area's module, for example
   `DotCharter.Presentation/Dialogs/DialogsModule.cs`.
4. **View.** `DotCharter.UI/Views/<Area>/<Name>View.axaml` plus a code-behind that
   only calls `InitializeComponent()`. Bind to the contract with
   `x:CompileBindings`. Use `Padding*` and `Gap*` tokens for padding and margin,
   never `Space*` (those are numbers and throw when attached). Give every control
   you want to test an `x:Name`. The file name must be unique under `Views/`.
5. **Catalogue line.** Add `Screen<I<Name>Screen, <Name>View>(Area, "<Name>")` or
   `Dialog<...>(Dialogs, "<Name>")` to `DotCharter.UI/Views/Shell/ScreenCatalog.cs`.
   Every contract member must be bound by the view.
6. **Open it.** A dialog is shown from a command, for example in
   `DotCharter.UI/ViewModels/Shell/ShellDialogsViewModel.cs` with
   `_hostedDialogs.ShowAsync<I<Name>Screen, bool>()`. A screen is reached through the
   navigator. Hook the command up as in [add-a-menu-command.md](add-a-menu-command.md).
7. **Strings** in all three resx files ([add-a-localized-string.md](add-a-localized-string.md)).

## Tests to add

- **The smoke test.** A view can build clean and still fail at run time (a missing
  resource, a bad binding, a wrong token type). Add a test in
  `DotCharter.Tests/Perf/<Name>UiPerfSmokeTests.cs`:
  - put the view in a real `Window`, `Show()` it, lay it out;
  - assert the parts you named measured **non-zero** (a view whose styles failed
    still arranges, it just has no size);
  - for a list, assert each row laid out.
  Model it on `DotCharter.Tests/Perf/AlignToRecordingDialogUiPerfSmokeTests.cs`, or
  on `DialogConstructionUiPerfSmokeTests.*.cs` and `SettingsConstructionUiPerfSmokeTests.cs`
  in the same folder if your view joins a family. The class carries
  `[Collection("HeadlessUI")]`.
- **A view model test** with the real service fakes, in
  `DotCharter.Tests/Presentation/<Area>/` or `DotCharter.Tests/UI/ViewModels/`.
- **Run the headless suite**, because it runs apart from the normal gate:

  ```powershell
  powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless
  ```

- These tests already cover the catalogue: `DotCharter.Tests/Presentation/ScreenCatalogTests.cs`,
  `ContractBindingLintTests.cs` and `ContractConformanceTests.cs` in the same folder.
- Break the view on purpose (rename a bound property, or remove a style resource)
  and confirm the smoke test fails.

## Example in the code

The Tempo map "Align to recording" dialog: the contract `IAlignToRecordingScreen`,
its fake `FakeAlignToRecordingScreen`, `AlignToRecordingViewModel`,
`AlignToRecordingDialogView.axaml`, its line in `DialogsModule`, the command that
opens it, and `AlignToRecordingDialogUiPerfSmokeTests`.
