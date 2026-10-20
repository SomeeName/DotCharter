# DotCharter.Tests: a guide for people

This project holds every automated test for DotCharter: unit tests, behaviour
tests on the real services, the headless UI suite and a few hundred *source
lints*. This page covers how to run them, how to read a failure, and how to add a
lint. The conventions every test follows are in [AGENTS.md](AGENTS.md). Deeper
harness notes are in [docs/agents/testing/](../docs/agents/testing/).

## Running the tests

The suite is split into **two runs, in two separate processes**:

| Run | What it is | Command |
|---|---|---|
| **Default gate** | everything except `*UiPerfSmokeTests` | `tools/agent/Invoke-Gate.ps1 -Gate`, or a plain `dotnet test` (below) |
| **Headless UI suite** | every `*UiPerfSmokeTests` class except the golden screenshots | `tools/agent/Invoke-Gate.ps1 -Headless` |
| Golden screenshots | `ScreenGoldenUiPerfSmokeTests` (needs approved baselines) | `tools/agent/Invoke-Gate.ps1 -Goldens` |
| A few classes | while you iterate | `tools/agent/Invoke-Gate.ps1 -FilterClass '*MyFeature*'` |

"Everything" means the gate run followed by the headless run. They are never one
process.

### Why two processes

`Avalonia.Headless` keeps a single dispatcher per process. If the headless tests
run in the same test host as the rest, almost all of them fail with errors like
`The calling thread cannot access this object`, and they take unrelated tests
down with them. This is not a hang, and running the tests one at a time does not
fix it. Keep the two runs separate.

### What a plain `dotnet test` does

The root `global.json` opts `dotnet test` into Microsoft.Testing.Platform. When
`dotnet test` starts the test executable **without any filter**,
[`TestEntryPoint.cs`](TestEntryPoint.cs) adds the gate's own exclusion,
`--filter-not-class "*UiPerfSmokeTests"`. So a newcomer who types `dotnet test`
gets the default gate and cannot mix the two runs by accident.

**Any explicit filter turns that default off.** Every script passes its own
filter, so each one runs exactly what it ran before: `Invoke-Gate.ps1`, CI
(`tools/ci/Invoke-HeadlessUiCi.ps1`), the release preflight
(`tools/release/New-Release.ps1`) and the headless sharding
(`tools/ci/HeadlessShards.psm1`). An IDE's test explorer is not affected either.
It talks JSON-RPC to the test host rather than `dotnettestcli`, and filters over
that protocol. `Infrastructure/DefaultRunFilterTests` pins all of this.

From the repository root, `dotnet test` runs both test projects in
`DotCharter.sln`: this one, with the default above, and `JuceSharp.Tests`, the
audio submodule's own suite, which this default does not touch. Use `--project`
to run only this suite:

```powershell
dotnet build DotCharter.sln
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build   # the default gate
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build `
    --filter-class "*UiPerfSmokeTests" `
    --filter-not-class "DotCharter.Tests.Perf.ScreenGoldenUiPerfSmokeTests"   # the headless suite, one process
dotnet test --project DotCharter.Tests\DotCharter.Tests.csproj --no-build `
    --filter-class "*CopyCaseLintTests"                          # one class
```

Filters: `--filter-class`, `--filter-not-class`, `--filter-method`,
`--filter-namespace`. `*` is allowed only at either end. Repeat a flag to OR its
values; different flags AND together.

Prefer `tools/agent/Invoke-Gate.ps1`. It builds first, so a green run never
tests stale binaries. It prints one line of counts and, for each failure, the
test name and the first line of its message, and it writes the full log to a
file it names. `-Headless` also splits the headless suite across four processes
to save time.

### What to expect

Measured on 2026-10-14 on a 16-thread Windows machine, with the test hosts
pinned to 8 threads at low priority. The time is the test run only, after the
build. An idle machine with every core free is faster. Counts move every week,
so treat these as rough sizes.

| Run | Cases | Time |
|---|---|---|
| Default gate (plain `dotnet test`, `Invoke-Gate.ps1 -Gate`) | 20,914 | 516 s |
| Headless UI suite (`Invoke-Gate.ps1 -Headless`, 4 processes) | 1,545 | 276 s |
| Golden screenshots (`Invoke-Gate.ps1 -Goldens`) | 367 | not part of either run |

Before this default existed, a plain `dotnet test` discovered all 22,784 cases
in one process, headless included, and most of the headless cases fail when they
run that way.

Some tests skip on purpose. They need a GPU, the owner's local corpus
(`TestData/`) or an environment variable (see [AGENTS.md](AGENTS.md)). A skip is
not a failure, and your counts can differ from someone else's for that reason.

## Source lints: tests that read the code

Dozens of classes here never run product code. They read the
repository's **text**: the C#, the XAML, the `.resx` files, the scripts and the
guides. Then they assert a rule about it. Most are named `*LintTests`; some
`*RatchetTests`, `*BudgetTests` and `AssemblyLayeringTests` work the same way.

### Why they exist

Some rules cannot be checked by running the code:

- **Which expression may appear.** For example, every import dialog must ask
  `GpBeatMapBuilder.InitialBpm` for the opening tempo
  (`GpInitialBpmCallSiteLintTests`). A unit test of the helper cannot prove that
  the callers still use it.
- **Faults that build clean.** A XAML literal that depends on the culture, a
  status message that hides a failure, or a meter that draws past its slot all
  compile and only go wrong on someone's screen.
- **Repository hygiene.** No personal data, no game data, a provenance row for
  every fixture, no dead links in the guides, and no source file past its size
  budget.
- **Rules that rotted before.** Most lints exist because the rule was broken
  once and nobody noticed. The class summary usually says when and how.

The compiler cannot enforce any of these. A lint is cheap to run, and it fails
with the file and the line.

### Reading a lint failure

A lint failure message has up to four parts:

```text
A product file keeps its own copy of the standard open-string table: ...   <- the RULE
  DotCharter.Core/Utilities/Example.cs:5: '0, 5, 10, 15, 19'                      <- each OFFENDER, file:line
Fix: read the row from StandardOpenStrings (...) instead of spelling it out.        <- what to DO
Guide: docs/agents/core/domain-model.md (StandardOpenStrings)                       <- where the rule is explained
```

If the message is not enough:

1. Open the class. Its `/// <summary>` says what rule it enforces and why.
   `Infrastructure/LintClassSummaryLintTests` makes sure every `*LintTests`
   class has one.
2. Open the guide it names.
3. **Do not add your file to an allow-list to make the gate green.** An
   allow-list entry needs a reason that is true. Most lists also fail when an
   entry goes stale.

There is a second kind of failure: **"the scan is broken" or "the lint lost its
subject"** (for example, "only scanned 12 files"). It is not about your code. A
file the lint reads has moved or been renamed, so the lint would otherwise pass
without checking anything. Point the lint at the new location. After you split
a file, search this project for the old file name.

## Adding a lint

1. **Write the rule as one sentence first.** If you cannot, it is not a lint yet.
2. **Name the class `<Rule>LintTests`** and give it a `/// <summary>` of at
   least eight words. Say what must hold, what breaks without it, and which
   guide explains it.
3. **Read the text the way the existing lints do.** Find the repository root
   with `TestPaths.RepoRoot()`. Strip comments with `SourceText.StripComments`
   or `StripCommentsKeepingLines` (the second keeps line numbers). For XAML,
   load the file with `LoadOptions.SetLineInfo`. Skip `bin/` and `obj/`. Use
   Roslyn (`Microsoft.CodeAnalysis.CSharp`) when a regex cannot see the shape,
   for example a type split across partial files.
4. **Collect every offender, then assert once.** Assert with a message built by
   `LintReport.Of(rule, offenders, fix, guide)`, and give each offender as
   `path:line: what is wrong`. Never call a bare `Assert.Empty(offenders)` or
   `Assert.True(ok)`. Their messages name no rule and no fix.
5. **Add a vacuity floor.** Assert that the scan found enough to judge, for
   example `scanned > 500` or "the canonical file still has the pattern". Its
   message should say that the scan is broken. A lint that reads nothing passes
   forever.
6. **Add a probe fact.** Run the matcher on a small inline string that breaks
   the rule and on one that does not, and assert the verdict. It documents what
   the matcher catches, and it fails if the matcher stops matching.
7. **Prove the lint is not vacuous.** Break the rule in real product code (add
   the forbidden line to a real file), run the lint and watch it fail with your
   message. Then restore the file. Paste that message into your pull request.
   A guard nobody has seen fail is a guard nobody should trust.
8. **Allow-lists** take a reason per entry and should fail when an entry goes
   stale. A ratchet is a list that may only shrink: when your change shrinks a
   count, lower its entry in the same change.

Run your lint alone while you write it:

```powershell
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -FilterClass '*MyRuleLintTests'
```
