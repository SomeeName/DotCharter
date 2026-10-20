# Add a pedal or amp model to the rig

A rig module has a C++ core in the `JuceSharp` submodule, a catalogue row, a
managed mirror, and a look on the pedalboard. The core runs on the audio thread,
so read the realtime rules first: `JuceSharp/docs/agents/dsp-tone-rig.md`,
sections "The rig, the catalogue and the cores" and "`prepare()` is where cores go
wrong". Plan the module's parameters before you write code.

JuceSharp is its own repository. Commit and push there first, then bump the
submodule pointer in DotCharter. See the last step of
[add-a-native-abi-call.md](add-a-native-abi-call.md).

## Steps

1. **Pick a category.** One of the fixed words in
   `JuceSharp/src/JuceSharp.Native/src/dsp/GearEntry.h` (dynamics, drive, amp,
   delay, and so on). The category decides which `gear/<Category>Gear.cpp` the
   module goes in. It is not free text.
2. **Write the core** in `JuceSharp/src/JuceSharp.Native/src/dsp/cores/dcdsp_<name>.h`.
   Header-only, no JUCE: a params struct plus `prepare`, `reset` and `process`.
   No allocation, locks or I/O in `process`. Ramp every gain and mix so a knob move
   never clicks. Copy a small existing core as your starting point.
3. **Add the adapter and catalogue row** in
   `JuceSharp/src/JuceSharp.Native/src/dsp/gear/<Category>Gear.cpp`: one `DspModule`
   adapter and one `out.push_back({...})` row with id, display name, params and
   category. Give every discrete parameter value labels or a step. A brand-new
   `<Category>Gear.cpp` file must also be added to the source list in
   `JuceSharp/src/JuceSharp.Native/CMakeLists.txt`.
4. **Add a realtime scenario** in `JuceSharp/src/JuceSharp.Native/tests/rt/rt_main.cpp`:
   chain the module, move every switch and knob for about 200 blocks, assert it
   did something. Run the native realtime gate (see `JuceSharp/AGENTS.md` for the
   build commands).
5. **Mirror it in managed code.** Add the id constant and parameter ranges to
   `DotCharter.Tone/Audio/Dsp/RsToneConversion/DcRigTargetCatalog.cs`. The ranges
   must match the native ones exactly; `DcRigTargetCatalogTests` compares them with
   the built DLL.
6. **Dress it.** In `DotCharter.Tone.UI/Controls/Dsp/DspModuleVisuals.cs` give the
   id a category and a unique accent colour. In
   `DotCharter.Tone.UI/Controls/Dsp/GearCuration.cs` put the id in the `Order` list
   inside its family, or it sorts last. Faces are drawn in code; there are no
   bitmaps to add.
7. **Strings.** The display name comes from the native row. Only extra UI text needs
   resx keys, in all three files ([add-a-localized-string.md](add-a-localized-string.md)).
8. **Docs.** Update the gear guide for the category under `JuceSharp/docs/guides/`
   and re-count the catalogue size in the guides that quote it; do not just add one
   to the old number.

## Tests to add

- `JuceSharp/tests/JuceSharp.Tests/Dsp/Gear<Category>Tests.cs`: one behaviour you
  saw fail first, a ramp check on every level or mix parameter, NaN input, two
  sample rates, and an oversized block.
- `JuceSharp/tests/JuceSharp.Tests/Dsp/JuceDspChainWrapperTests.cs`: raise the
  catalogue-count floor.
- In DotCharter, run these classes: `PedalboardVisualsTests`,
  `DcRigTargetCatalogTests`, `FactoryPresetsAgainstLiveCatalogueTests`
  (`DotCharter.Tests/UI/PedalboardVisualsTests.cs`,
  `DotCharter.Tests/Audio/Dsp/RsToneConversion/DcRigTargetCatalogTests.cs`,
  `DotCharter.Tests/Audio/Dsp/Presets/FactoryPresetsAgainstLiveCatalogueTests.cs`),
  then the full gate.
- A new module row alone does not change the ABI version. Changing the ABI
  surface does; see the ABI recipe.

## Example in the code

Follow any existing module end to end: its core in
`JuceSharp/src/JuceSharp.Native/src/dsp/cores/dcdsp_*.h` with its catalogue row and
native tests, then its row in `DotCharter.Tone/Audio/Dsp/RsToneConversion/DcRigTargetCatalog.cs`,
its pedalboard look, and the managed tests.
