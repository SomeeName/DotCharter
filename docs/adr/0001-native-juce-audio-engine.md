# 0001. All real-time audio runs in a native JUCE engine behind a C ABI

**Status:** Accepted

## Context

A guitar game needs low-latency device I/O (ASIO, WASAPI), time-stretching,
VST3 hosting and an amp-and-pedal rig, all inside an audio callback that may
never allocate, lock or wait on the garbage collector. A managed audio pump
cannot promise that. The project started on a managed audio library and moved
off it.

## Decision

- The engine is C++ on JUCE, built as the shared library `juce_sharp.dll` in
  the `JuceSharp` submodule. It owns the device, the mixer, the transports, the
  tone rig and the VST3 chain.
- C# reaches it through a small, versioned C ABI (`dotcharter_audio.h`,
  `DCA_ABI_VERSION`) and a thin P/Invoke wrapper that does marshalling and
  lifetime only.
- `JuceAudioPlaybackService` implements the app's audio interface on that
  wrapper. No audio sample passes through C#; C# sends commands and receives
  positions.
- The DLL is a build artifact. The first build downloads a prebuilt copy that
  matches the submodule commit, so contributors need no C++ toolchain unless
  they change the engine.

## Consequences

- Real-time safety is checked in native code by the RT gate, not by convention.
- Every new engine capability is an ABI change: header, implementation,
  P/Invoke mirror and a version bump, with the submodule committed first.
- Engine state must be replayed when the engine is rebuilt (device change,
  watchdog recovery), or it is lost.
- Windows only for now; the native code has not been built elsewhere.

## Links

[docs/agents/services/audio-engine.md](../agents/services/audio-engine.md),
`JuceSharp/README.md`, [ARCHITECTURE.md](../ARCHITECTURE.md), the
[add-a-native-ABI-call recipe](../dev/cookbook/add-a-native-abi-call.md).
