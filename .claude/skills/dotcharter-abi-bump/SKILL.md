---
name: dotcharter-abi-bump
description: Add, extend or change an export of DotCharter's native C ABI (JuceSharp dotcharter_audio.h / dotcharter_audio.cpp ↔ NativeMethods.cs) and bump DCA_ABI_VERSION correctly — header, implementation, P/Invoke mirror, status codes, null-handle and sentinel conventions, seat-0 aliases, DLL rebuild, export-presence tests, and the submodule commit order. Use this whenever a new dca_* function, callback, struct, enum value or status code is needed, when the managed side needs something the native engine cannot yet do, when an EntryPointNotFoundException or JuceAbiMismatchException appears, or when the user says "expose X from the engine" or "add an ABI call" — even for a "small" one-function addition.
---

# Changing the native C ABI

The ABI is a contract between two repos' worth of code, and most of its
failure modes compile clean. A mistyped `EntryPoint` string, a definition
that never reaches the DLL, or a forgotten managed version bump each surface
only at runtime, or on the next rebuild. So walk every step, and let the
tests named below prove each one.

Reference: `JuceSharp/docs/agents/native-engine-abi.md` (per-version rules;
the ABI v36 section is the model for a well-done addition),
`JuceSharp/docs/abi-policy.md` (versioning policy) and
`.claude/rules/native-interop.md` (the per-change checklist: layout, widths,
bools, strings, callbacks, ownership, threads).

## 1. Design the entry point

- **Never change an existing signature, typedef or struct layout.** Add a
  second entry point instead (`…2`, `…3`): v36 added
  `dca_stems_separate_file2` + `dca_stems_progress_cb2` rather than changing
  the `void` callback, because every compiled caller would have broken
  silently.
- **Status-returning export:** returns `dca_status`. Validate arguments first
  (null out-pointer, bad count, seat ≥ `DCA_MAX_PLAYERS`), and return
  `DCA_ERR_INVALID_ARG` for a NULL handle, **never `DCA_OK`**.
- **Value-returning export (no status channel):** the failure sentinel is
  `-1`, and `0` stays a legal answer. The documented exceptions
  (`dca_engine_dsp_get_metering`, `dca_engine_get_player_count`) are listed
  in the guide; don't add new ones.
- **Per-seat features:** the seat-0 form of a `_for_player` export is a
  **one-line alias**, `return …_for_player(h, 0, …);`, never a copy. The
  `_for_player` twin zeroes its out-params before its first failure path.
- **Callbacks that must be answerable** return `int` with non-zero meaning
  CONTINUE, so a binding that forgets a return fails safe. Native never
  calls managed code from the audio callback.
- **New status codes are appended** and their ordinals are permanent. Mirror
  them in `NativeMethods.DcaStatus` and in any managed enum the wrapper casts
  to, and map them to an exception in the wrapper.
- Strings are UTF-8 both ways. Out-strings go through a caller buffer plus a
  length, truncated without splitting a code point (`Utf8Truncate.h`).
- If the export touches per-device or per-seat state, decide what happens
  on an engine rebuild (device change, 48 kHz request, watchdog). State
  that must survive is replayed. See "Engine rebuild: what survives" in the
  guide.

## 2. Implement

1. **Header**: `JuceSharp/src/JuceSharp.Native/include/dotcharter_audio.h`.
   Declare the function in the literal `DCA_CALL <name>(` form, because tests
   grep for that string. Document its thread, ownership and sentinel in the
   comment.
2. **Bump `#define DCA_ABI_VERSION`** in the same header. Every ABI change
   bumps it, purely additive ones included.
3. **Definition**: in `JuceSharp/src/JuceSharp.Native/src/dotcharter_audio.cpp`,
   the one ABI translation unit, next to its family. The old `src/abi/*.cpp`
   split is gone; a function defined outside the compiled source list links
   clean and is missing from the DLL. Wrap the body so nothing escapes
   (the `guard()` pattern), and marshal calls that touch the device through
   `callSync`.
4. **Managed mirror**: `JuceSharp/src/JuceSharp/Interop/NativeMethods.cs`.
   Set `ExpectedAbiVersion` to exactly the header's number (the check is
   equality, not ≥; a mismatch throws `JuceAbiMismatchException` when the
   engine opens). Add
   `[DllImport(LibName, EntryPoint = "dca_…", CallingConvention = CallingConvention.Cdecl)]`
   with explicit marshalling for strings (`LPUTF8Str`) and bools.
5. **Wrapper and consumers**: expose the call through `JuceAudioEngine`
   (and `IJuceAudioEngine` if the app needs it), pinning the engine handle as
   the neighbours do. Then wire the DotCharter side
   (`DotCharter.Services/Audio/JuceAudioPlaybackService*.cs`). An export that
   nothing calls is a dead feature, so make sure something reaches it.

## 3. Rebuild the DLL before any managed code runs

`cmake` is not on PATH; use the VS 2026 Build Tools copy (see
`JuceSharp/AGENTS.md` § rebuild):

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe" --build JuceSharp/src/JuceSharp.Native/build --config Release
```

POST_BUILD deploys `juce_sharp.dll` (and `juce_vst_host.exe`) to
`JuceSharp/src/JuceSharp/runtimes/win-x64/native/`. The DLL is a build
artifact and is not tracked. If the export touches the callback, run the full
`dotcharter-rt-gate` skill, which builds the whole tree. After adding a
native source file, re-run `tools/agent/New-CompileCommands.ps1` for clangd.

## 4. Prove it

- An `AbiV<N>Tests.cs` next to `JuceSharp/tests/JuceSharp.Tests/AbiV36Tests.cs`,
  modelled on it: `TheExpectedAbiVersion_MatchesTheHeader`, the status
  ordinal on both sides if you added one, an export-presence theory using
  `NativeLibrary.TryGetExport` on the **built** DLL (a declaration compiles
  even when the definition never reaches the binary), and a behavioural test
  that shows the feature actually happened.
- Add an `[InlineData(nameof(NativeMethods.X), "dca_x")]` row to the binding
  theory in `NativeMethodsTests.cs` (`EveryEngineWaveAndV35Export_HasABindingNamedAfterIt`
  is the pattern). It pins the `EntryPoint` string and the `DCA_CALL` line
  in the header.
- Extend `NullHandleRejectionTests`, `AbiSentinelAndAliasTests` or
  `SeatZeroAliasEquivalenceTests` when the export falls under their rule.
- `DotCharter.Tests/Services/Audio/DropTuneNativeMirrorTests.cs` also reads
  the header's version, and goes red on a half-done bump.
- Run the JuceSharp suite through the gate wrapper
  (`tools/agent/Invoke-Gate.ps1 -Solution JuceSharp\JuceSharp.sln -Project JuceSharp\tests\JuceSharp.Tests\JuceSharp.Tests.csproj`),
  then the DotCharter gate.

## 5. Land it in the right order

JuceSharp is a submodule with its own repo. Commit there first, **push the
submodule before the parent**, then commit the gitlink bump plus the
DotCharter-side changes in DotCharter. Otherwise the gitlink dangles for
everyone else. An agent worktree built with the plain `JuceSharp/` copy
cannot commit to the submodule, so ABI work belongs in the main checkout.
Update `native-engine-abi.md` (a section for the new version) and the
hard-rule index (`JuceSharp/docs/agents/hard-rule-index.md`) in the same change.

The gitlink bump also publishes the new DLL pair for contributors without a C++
toolchain: `pwsh -NoProfile -File tools/release/Publish-NativeRuntime.ps1 -Upload`
(from the main checkout, after the native build deployed the pair) zips it,
pins it in `tools/setup/native-runtime.json` and uploads it to JuceSharp's
`native-prebuilt` release. Commit the pin with the bump;
`Infrastructure/NativeRuntimeFetchTests` fails the gate while the gitlink is unpinned.
