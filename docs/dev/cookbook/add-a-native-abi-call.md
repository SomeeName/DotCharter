# Add a native C ABI call

The audio engine is C++ in the `JuceSharp` submodule. DotCharter talks to it through
a plain C interface (the ABI). Most mistakes here compile clean and fail only when
the engine opens, so go through every step. The long-form rules are in
`JuceSharp/docs/agents/native-engine-abi.md` and `JuceSharp/docs/abi-policy.md`;
the C++ toolchain and rebuild commands are in `JuceSharp/AGENTS.md`.

## Steps

1. **Design the call.**
   - Never change an existing signature or struct. Add a second function (`...2`).
   - A call that can fail returns a status code. Check arguments first, and return
     `DCA_ERR_INVALID_ARG` for a null handle, never `DCA_OK`.
   - A call that returns a value uses `-1` as its failure value; `0` stays a valid
     answer.
   - A per-seat call gets a `_for_player` form. The seat-0 form is a one-line call
     to it, not a copy.
   - Strings are UTF-8. New status codes go at the end of the list; their numbers
     are permanent.
2. **Declare it** in `JuceSharp/src/JuceSharp.Native/include/dotcharter_audio.h`, in
   the literal form `DCA_CALL <name>(`, because tests search for that text. Comment
   its thread, ownership and failure value. Raise `DCA_ABI_VERSION` in the same
   file. Every change raises it, even a purely additive one.
3. **Define it** in `JuceSharp/src/JuceSharp.Native/src/dotcharter_audio.cpp`, next to
   related calls. This is the only file that defines ABI functions; one defined
   elsewhere links but is missing from the DLL. Wrap the body so no exception
   escapes. Never call managed code from the audio callback.
4. **Mirror it** in `JuceSharp/src/JuceSharp/Interop/NativeMethods.cs`. Set
   `ExpectedAbiVersion` to the header's number (it must be equal; a mismatch throws
   `JuceAbiMismatchException` when the engine opens). Add the `[DllImport]` with the
   exact `EntryPoint` string and explicit string and bool marshalling.
5. **Expose it.** Wrap it in `JuceAudioEngine` in the JuceSharp project, then call it
   from DotCharter (for example in `DotCharter.Tone/Audio/JuceAudioPlaybackService.cs`
   or one of its partial files). A call nothing uses is a dead feature.
6. **State that must survive an engine rebuild** (device change, sample-rate
   change, recovery) has to be set again after the rebuild. See "Engine rebuild:
   what survives" in `JuceSharp/docs/agents/native-engine-abi.md`.
7. **Rebuild the DLL** before any managed code runs. The DLL is a build artifact,
   not tracked. `JuceSharp/AGENTS.md` has the exact CMake command. If the call
   touches the audio callback, also run the realtime gate described there.
8. **Land it in order.** Commit and push the submodule first, then commit the
   submodule pointer together with the DotCharter changes. Pushing the parent first
   leaves everyone else with a dangling pointer. Update
   `JuceSharp/docs/agents/native-engine-abi.md` with a section for the new version.

## Tests to add

In `JuceSharp/tests/JuceSharp.Tests/`:

- A new `AbiV<N>Tests.cs` modelled on `AbiV36Tests.cs`: the version matches the
  header, any new status number matches on both sides, the export is present in the
  built DLL (`NativeLibrary.TryGetExport`), and a behaviour test that shows the
  feature happened.
- A row for the new call in the binding theory in `NativeMethodsTests.cs`. It pins
  the `EntryPoint` string and the header line.
- Extend `NullHandleRejectionTests.cs`, `AbiSentinelAndAliasTests.cs` or
  `SeatZeroAliasEquivalenceTests.cs` when the call falls under their rule.

In DotCharter:

- `DotCharter.Tests/Services/Audio/DropTuneNativeMirrorTests.cs` also reads the
  header version and fails on a half-done bump.
- A test of the DotCharter code that calls the new function.

Run the JuceSharp suite through the gate wrapper, then the DotCharter gate:

```powershell
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Solution JuceSharp\JuceSharp.sln -Project JuceSharp\tests\JuceSharp.Tests\JuceSharp.Tests.csproj
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate
```

## Example in the code

The rig footer's CPU readout is a native call carried all the way to the UI: the
export in `JuceSharp/src/JuceSharp.Native/include/dotcharter_audio.h`, its wrapper in
`JuceSharp/src/JuceSharp`, the playback service in
`DotCharter.Tone/Audio/JuceAudioPlaybackService*.cs`, and `RigCpuLoadReadoutTests`.
