---
name: dotcharter-rt-gate
description: Build and run DotCharter's native real-time safety gate (juce_sharp_rtcheck — allocation hook, lock-site allow-list, bounded waits), read its failures, and add or update scenarios. Use this whenever native code reachable from the audio callback changes (AudioEngine, DspChain/DspModule, dcdsp cores, sources, limiter, mirror, MIDI), when adding a lock/mutex/wait on the audio path, when rt_lock_sites.txt or rt_baseline.txt would change, when a "RtSomething" scenario fails, or before calling any realtime/DSP change done — even if the user only says "check it's RT-safe" or "does this allocate on the audio thread".
---

# DotCharter native RT gate

`juce_sharp_rtcheck` compiles the engine sources directly, drives the null
audio device by hand (`DCA_ENABLE_NULL_DEVICE=1`, `DCA_NULL_DEVICE_MANUAL=1`)
and fails on any heap allocation, unexpected lock or unbounded wait inside
the callback. It is the evidence behind "this is realtime-safe". The deep
reference is `JuceSharp/docs/agents/rt-safety-and-tests.md` § "Native RT gate".
Read that section when a failure is not self-explanatory.

## Run it

`cmake` and `ctest` are not on PATH on this machine. The script uses the
VS 2026 Build Tools' own copies:

```powershell
# while iterating: build only the gate, run matching scenarios
powershell -NoProfile -File .claude/skills/dotcharter-rt-gate/scripts/Invoke-RtGate.ps1 -Only <NameSubstring>
# sign-off: build the whole native tree (redeploys juce_sharp.dll) and run every native ctest
powershell -NoProfile -File .claude/skills/dotcharter-rt-gate/scripts/Invoke-RtGate.ps1
```

`-Only` matches scenario names by substring, and exit 2 means nothing matched.
A partial run **skips the two meta-assertions** (`RtLockSiteSetIsUnchanged`,
`RtBaselineGate`), so only a full run counts as a pass. Report which one you
ran.

## The two allow-lists, and when you may edit them

Both live in `JuceSharp/src/JuceSharp.Native/tests/rt/`.

- **`rt_lock_sites.txt`** lists the mutexes the audio thread may take, each
  through `DCA_RT_LOCK_GUARD(m, "site")`. A new lock on the callback path fails
  with a diff. Adding a line is a design decision: say in the change why a
  lock-free handoff (`LatestSnapshot`, atomics) is not possible, and prefer
  a try-lock. A site that disappears is removed from the file in the same
  change. The list cannot see locks taken inside the standard library, or a
  writer that forgot to take the lock at all, so a green gate does not
  replace reading the code.
- **`rt_baseline.txt`** holds architectural exceptions that cannot be fixed,
  currently one line (the sandbox round-trip wait). **Heap allocations are
  never baselined.** Fix the allocation by preallocating in `prepare` or on
  the control thread.

## Reading a failure

- **Allocation count > 0** in a scenario: something on the callback path
  allocated. The hook is `_CrtSetAllocHook`, which is why the target is forced
  onto the debug CRT; it catches `malloc`-based JUCE allocations such as
  `HeapBlock` and `AudioBuffer::setSize`. Typical causes: container growth,
  `juce::String`, a `std::function` capture, `make_shared`, or a
  `setSize` without `avoidReallocating`.
- **Lock-site diff:** a new or removed `DCA_RT_LOCK_GUARD` site (see above).
- **`RtRunListOrderIsPinned`:** a scenario was inserted instead of appended.
  New scenarios go at the END of the `run("Rt…")` list in `rt_main.cpp`.
- **Known flakes that are not your change:** `RtBridgeWaitIsBounded` phase A
  (re-run it first) and phase C. Before blaming your diff, A/B it: put the
  old sources back with `git show HEAD:<path> > <path>`, rebuild, and confirm
  the failure persists. Never use `git stash`, because other agents share it.

## Adding a scenario

Every new callback-reachable feature gets a scenario. Register it with
`run("Rt<Name>", &scenarioX)`, **appended** at the end of the list in
`tests/rt/rt_main.cpp`. A scenario that takes arguments is wrapped as
`if (wanted("Rt<Name>")) { scenarioX(arg); harvest(); dcrt::reset(); }`, the
way `RtEveryCatalogueModuleIsAllocationFree` is. The scenario
must also assert that the **feature actually happened** (the meter moved,
the param reached the core, the event was rendered). A zero-allocation run
of code that did nothing passes vacuously.
`RtNodeMeteringIsAllocationFreeAndNotVacuous` is the worked example. Then
update the scenario count wherever the guide states it.

## Managed side

No managed code runs on the callback, so there is no managed RT gate.
Managed tests that open a real device, spawn `juce_vst_host.exe` or park
threads on bounded waits carry `[Collection("NativeEngine")]` (see
`JuceSharp/tests/JuceSharp.Tests/TestCollections.cs` for the others:
`NullDevice`, `AllocationProbes`, …). `TestCollectionLintTests` enforces this
per class. Afterwards, kill any stray `testhost.exe` or `JuceSharp.Tests.exe`:
a survivor can hold the ASIO device exclusively.
