---
paths:
  - "DotCharter.Tests/**"
  - "JuceSharp/tests/**"
  - "JuceSharp/src/JuceSharp.Native/tests/**"
---

# Tests

Conventions, the headless split and the timing rules: `DotCharter.Tests/AGENTS.md`.

- Run the gate through `tools/agent/Invoke-Gate.ps1` — a local hook blocks raw
  `dotnet build` / `dotnet test` and the wrapper prints counts, not console
  noise. It builds first, so a green run is never against stale binaries.
- The runner is Microsoft.Testing.Platform:
  `dotnet test --project <csproj> --filter-class "*Name*"` — wildcards only at
  the ends. The headless-UI suite (`*UiPerfSmokeTests`) always runs in its own
  processes: `Invoke-Gate.ps1 -Headless` splits it across 4 shards (`-Shards N`;
  `-UpdateShardWeights` re-weighs `tools/ci/headless-shard-weights.json`).
- New behaviour: write the failing test first when it can express the
  behaviour (Superpowers TDD), then prove it is not vacuous — break the code,
  watch it fail, restore.
- No wall-clock assertions in the default gate; assert structure and log time.
- Kill stray `testhost.exe`, `DotCharter.Tests.exe` and `JuceSharp.Tests.exe`
  before rebuilding — a survivor holds the DLLs and can hold the ASIO device.
- JuceSharp: `Invoke-Gate.ps1 -Solution JuceSharp\JuceSharp.sln -Project
  JuceSharp\tests\JuceSharp.Tests\JuceSharp.Tests.csproj -FilterClass "*Name*"`
  (StemSeparator/ChildProcess tests are environment-flaky — compare against a
  clean tree). The native RT gate is `juce_sharp_rtcheck`
  (`cmake --build build --config Release --target juce_sharp_rtcheck`).
