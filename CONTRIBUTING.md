# Contributing to DotCharter

Thanks for wanting to help. This page covers how to build, how to test, what
the code has to look like and what a pull request needs before it can merge.
The structure of the code is explained in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md),
the reasons behind the big choices in [docs/adr/](docs/adr/README.md), and
[docs/README.md](docs/README.md) indexes every guide. The detailed rules every
change follows are in [AGENTS.md](AGENTS.md) and the `AGENTS.md` of the project
you touch: that tree is the reference our AI coding assistants load, and it is
readable by humans too, but you can start without it.

By taking part you agree to the [Code of Conduct](CODE_OF_CONDUCT.md). Security
problems are NOT reported as issues — see [SECURITY.md](SECURITY.md).

## Before you start

- **Open an issue first** for anything bigger than a typo: a bug, a feature or a
  refactor. Say what happens, what you expected and, for anything visible, attach
  a screenshot. The issue templates ask for exactly that.
- One branch and one pull request per issue. Branch from the current `main`.

## Building

New to the code? [docs/dev/GETTING-STARTED.md](docs/dev/GETTING-STARTED.md) walks
from clone to running app and tests, with troubleshooting, and
`tools/setup/Bootstrap.ps1` checks your machine. When something misbehaves, see
[docs/dev/DEBUGGING.md](docs/dev/DEBUGGING.md).

You need Windows 10/11 x64, the **.NET 10 SDK** and a GPU with **Vulkan**
drivers. Clone with the submodule:

```powershell
git clone --recurse-submodules https://github.com/SomeeName/DotCharter.git
cd DotCharter
dotnet build DotCharter.sln --nologo -v:m
```

The first build downloads the prebuilt native engine (`juce_sharp.dll` +
`juce_vst_host.exe`, verified by SHA-256 and ABI version), so no C++ toolchain is
needed. The engine (`JuceSharp/`) is only rebuilt when you change C++ — set
`DotCharterSkipNativeFetch=true` and see `JuceSharp/AGENTS.md` for its toolchain.
An ABI change means rebuilding `juce_sharp.dll` before any managed code runs.

## Testing

Use the gate wrapper. It builds first (so a green run is never against stale
binaries), prints counts instead of console noise, and writes the full log to a
file it names:

```powershell
# build + the default gate
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate
# a few classes while you iterate (wildcards only at either end)
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -FilterClass '*MyFeature*'
# the headless-UI suite, after ANY view or XAML change
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless
```

**The test suite runs as TWO separate invocations, and that is deliberate.**
`Avalonia.Headless` owns a process-global dispatcher. Run in the same process as
the default gate, almost every headless case fails and takes unrelated tests
with it. So the default gate excludes `*UiPerfSmokeTests`, and those run in
their own processes (`-Headless` splits them across shards). Never "fix" this by
serialising the gate. The raw commands underneath are listed in
[AGENTS.md § Validation](AGENTS.md#validation) and `DotCharter.Tests/AGENTS.md`.

Some JuceSharp stem-separation and child-process tests are sensitive to the
environment — compare against a clean checkout before blaming your change. If a
test host is left running after an aborted run, kill `testhost.exe` /
`DotCharter.Tests.exe` before rebuilding; it holds the output DLLs.

## Every change ships with its own evidence

A change is done when something would fail if it regressed:

- **New behaviour** gets a test in the default gate. Prefer a pure type with an
  injected clock over anything timed; never assert wall-clock time there.
- **A new or edited view** gets a construction + attach + layout smoke test
  (see `DotCharter.Tests/Perf/*ConstructionUiPerfSmokeTests.cs`) — several XAML
  faults build clean and only fail at runtime.
- **Prove the test is not vacuous**: break the thing on purpose, watch the test
  fail, restore it. Say in the PR that you did.

Adding a menu command, a setting, a string, a note technique, a pedal or a native
call? The [cookbook](docs/dev/cookbook/README.md) lists the files to touch, in
order, and the test each step needs.

## Code style

The build enforces it. Every rule in [.editorconfig](.editorconfig) is a warning
and warnings are errors, so a style violation fails the build. Fix it — never
suppress it (no `#pragma`, no `[SuppressMessage]`). The non-obvious conventions
(`var`, file-scoped namespaces, `_camelCase` fields, braces only around
multi-line bodies) are listed in [AGENTS.md § Code style](AGENTS.md#code-style).

Also:

- Keep the layer rules intact. Which project may reference which is a test
  (`DotCharter.Tests/Infrastructure/AssemblyLayeringTests.cs`), not a convention.
- User-visible text goes into all THREE resource files
  (`DotCharter.Localization/Resources{,.de-DE,.ja-JP}.resx`).
- Source files have a size budget (`SourceFileSizeBudgetTests`). Extract a
  collaborator rather than growing a large file.
- Methods have a complexity budget (`MethodComplexityBudgetTests`: cyclomatic
  complexity 25, 150 lines). Extract a method or a type, or use a table.
- No machine paths, real names or personal e-mail addresses in tracked files
  (`PersonalDataLintTests`).
- Comments say what the code does and WHY it is the way it is (or what would
  break otherwise). History does not belong in them: "used to ...", "before #N",
  dates and version stamps go in the commit message or the PR, and an issue
  number appears only where it is the one pointer to a still-relevant decision.
  `CommentHygieneTests` holds every project at zero.
- Real-time audio lives in the native engine. Do not add managed code to the
  audio hot path.

## Pull requests

Fill in the pull request template. In short:

- **What** changed and **why**, with `Fixes #<issue>` on its own line.
- **Evidence**: the gate's numbers (passed / failed / skipped), the headless
  run's numbers if you touched a view, and how you proved the new test fails
  without the fix.
- **Before/after screenshots** for anything visible.
- **Known limits**: what you did not do, or could not verify. For audio-engine
  changes, name the interface, driver and OS you tested on.
- Do not commit dated reports, plans or audits. Put lasting knowledge into the
  relevant `AGENTS.md` / `docs/agents/` guide in the same change, and let the
  pull request description be the report.

CI does not run on push; a maintainer starts it by hand. Your local gate and
headless numbers are what the review relies on.

## Dependencies

Every NuGet version lives in `Directory.Packages.props` (Central Package
Management); a `.csproj` names the package and never the version. To bump or add
a package, edit that one file, rebuild and run the gate. Adding a package also
means regenerating the licence inventory with
`tools/release/Update-DependencyInventory.ps1` (`-Check` fails when
`licenses/dependencies.json` is stale). Dependabot opens a weekly pull request
for minor and patch updates (`.github/dependabot.yml`). `JuceSharp/` keeps its
own versions.

## Release notes (there is no CHANGELOG file)

Release notes are written per release, in the body of the GitHub release, not in
a file in the repository. `tools/release/New-Release.ps1` builds the assets
locally and prints the `gh release create` command. As part of that it writes a
stub, `notes-v<version>.md`, from `git log` since the last `v*` tag: commit
subjects grouped by `feat:`, `fix:` and `perf:` prefixes, everything else under
Other. The maintainer edits the stub (highlights, tables, before/after images)
and the command publishes it with `--notes-file`. So give commits and pull
request titles a clear subject; the notes start from them.
## AI-assisted contributions

DotCharter itself is built with AI coding assistants (see
[ACKNOWLEDGEMENTS.md](ACKNOWLEDGEMENTS.md#how-dotcharter-is-made)), so
AI-assisted pull requests are welcome. They meet the same bar as any other:
the evidence rules above, the full gate, and you understand and stand behind
every line you submit. Say in the PR description that an assistant was used.
Do not paste code you cannot account for the licence of, and keep Rocksmith
content out of tests and fixtures.

## Developer Certificate of Origin (DCO)

Every commit needs a `Signed-off-by:` line, which certifies the
[Developer Certificate of Origin](https://developercertificate.org). Add it
with `git commit -s`. There is no CLA to sign.

## Licence

DotCharter is GPL-3.0-or-later. By contributing you agree that your
contribution is licensed under the same terms ([LICENSE](LICENSE)).
