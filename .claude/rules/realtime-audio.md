---
paths:
  - "JuceSharp/src/JuceSharp.Native/src/**"
  - "JuceSharp/src/JuceSharp.Native/tests/rt/**"
  - "JuceSharp/src/JuceSharp.Dsp/**"
  - "DotCharter.Services/Audio/**"
---

# Realtime audio

Full rules, the allocation hook and the lock allow-list:
`JuceSharp/docs/agents/rt-safety-and-tests.md` — read it before changing
anything reachable from the device callback. This file is the checklist.

**Code reachable from the audio callback** (`AudioEngine` device callback,
`DspChain`/`DspModule::process`, the `dcdsp` cores, sources' `getNextAudioBlock`,
the limiter, mirror output) must not introduce, unless the change says why it
is safe:

- heap allocation (`new`, `std::vector` growth, `std::string`, `juce::String`,
  `std::function` capture, `make_shared`) — preallocate in `prepareToPlay` or
  on the control thread
- blocking locks (`std::mutex`, `CriticalSection`, waits, condition variables)
  — only the sanctioned try-lock sites in the allow-list
- file, network or registry I/O; blocking or formatting logging (`RtDiag` is
  the RT-safe path)
- calls into managed code or any caller-supplied callback
- unbounded loops, O(n) work in something the UI can grow, or anything
  whose cost depends on user data size
- exceptions escaping: nothing may unwind out of the driver callback (see the
  hard-rule index (`JuceSharp/docs/agents/hard-rule-index.md`))

Prefer preallocation, bounded work, lock-free or `LatestSnapshot`-style
handoff, and one clear owner per piece of state (audio thread vs control
thread). Parameter changes cross threads by atomics/snapshots, never by
sharing a container.

**Managed side** (`DotCharter.Services/Audio`, `JuceSharp.Dsp`): no managed
code runs on the audio callback — keep it that way; never add a managed audio
pump on the hot path. `PositionChanged` arrives at up to 250 Hz on a timer
thread: coalesce before touching UI state, and don't allocate per tick.
Master-bus and per-seat state must be replayed on engine rebuild.

**Never "simplify" realtime or lock-free code because it looks verbose.** A
change here needs evidence: the RT gate (`juce_sharp_rtcheck`, built with
`--target juce_sharp_rtcheck`) for the native side, or a stated reason why it
cannot apply.
