# Getting started

From a fresh clone to a running DotCharter, and to a green test run. Plan on
about 15 minutes; the first build also downloads the prebuilt native engine (see below).
When something goes wrong, jump to [Troubleshooting](#troubleshooting). For
finding out why the app misbehaves once it runs, see [DEBUGGING.md](DEBUGGING.md).

## What you need

- **Windows 10 or 11, x64.** The app and the tests are Windows-only.
- **The .NET 10 SDK.** `global.json` in the repository root pins the test
  runner (Microsoft.Testing.Platform); the projects target .NET 10.
- **A GPU with Vulkan 1.2 drivers.** The 3D highway and the editor timeline are
  drawn with Vulkan. Current NVIDIA, AMD and Intel drivers all qualify.
- **Git with submodule support.** The audio engine lives in the `JuceSharp`
  submodule.
- **Only if you change the native engine (C++):** Visual Studio Build Tools
  2026 with the C++ workload. It bundles the CMake you need. Everyone else gets
  a prebuilt engine (section 3).

## 1. Clone with the submodule

```powershell
git clone --recurse-submodules https://github.com/SomeeName/DotCharter.git
cd DotCharter
```

If you cloned without `--recurse-submodules`, or `JuceSharp/` is empty:

```powershell
git submodule update --init --recursive
```

`JuceSharp` is a separate private repository. If the fetch is refused, you need
access to it; ask the maintainers.

## 2. Run the bootstrap check

```powershell
pwsh -NoProfile -File tools/setup/Bootstrap.ps1
```

It only looks and reports; it never installs anything or changes a setting. It
checks Windows, the .NET SDK, Git, the submodule, the Vulkan loader, and whether
`juce_sharp.dll` exists and reports the same `DCA_ABI_VERSION` as the header in
the checkout. At the end it prints the commands to run next. Run it again after
each fix until nothing is marked `[FAIL]`.

## 3. The native engine (`juce_sharp.dll`)

All real-time audio runs in a native C++ library built from `JuceSharp/`. The
.NET code cannot open an audio device without it, and the DLL is not stored in
git.

**You do not need to compile it.** When `juce_sharp.dll` is missing, the first
`dotnet build` runs `tools/setup/Get-NativeRuntime.ps1`, which downloads
`juce-sharp-native-win-x64-<first 12 characters of the JuceSharp commit>.zip`
from the JuceSharp release `native-prebuilt`, checks the SHA-256 of
`juce_sharp.dll` and `juce_vst_host.exe` against `tools/setup/native-runtime.json`
and the DLL's `dca_abi_version()` against `DCA_ABI_VERSION` in the header, and
only then installs the pair. Any mismatch fails the build and installs nothing.
The manifest may only name `SomeeName/JuceSharp` or `SomeeName/JuceSharp`,
and the DLL is loaded for its ABI check only after both hashes matched, in a
child PowerShell.
You can run the script by hand too:

```powershell
pwsh -NoProfile -File tools/setup/Get-NativeRuntime.ps1          # -Force replaces a present DLL
```

- A present `juce_sharp.dll` is never replaced and nothing is downloaded; after
  the submodule moves, delete the pair (or pass `-Force`) to fetch the new one.
- While `JuceSharp` is private, the download goes through the GitHub CLI: install
  `gh` and run `gh auth login` with an account that can read it. Once it is
  public, a plain HTTPS download works without `gh`.
- CI never downloads unless `DotCharterFetchNative=true` is set.
- `DOTCHARTER_NATIVE_ASSET_DIR=<folder>` installs a zip from a local folder.

### Building the engine yourself

Set `DotCharterSkipNativeFetch=true` (an MSBuild property, or an environment
variable) so the build never downloads. Then, from `JuceSharp/src/JuceSharp.Native`:

```powershell
cmake -S . -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release
```

Notes that save time:

- The first configure downloads JUCE and other dependencies with CMake's
  `FetchContent`; the compile itself takes on the order of ten minutes.
- `cmake` is usually not on `PATH`. Use the copy that Visual Studio Build Tools
  2026 installs, typically under
  `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`.
- The generator name is `"Visual Studio 18 2026"`; `"Visual Studio 17 2022"`
  fails to configure.
- A successful build copies `juce_sharp.dll` **and** `juce_vst_host.exe` to
  `JuceSharp/src/JuceSharp/runtimes/win-x64/native/`. They are a matched pair;
  never mix files from two builds.
- Rebuild the DLL whenever the submodule moves to a newer commit that changes
  `DCA_ABI_VERSION`. `Bootstrap.ps1` tells you when it is out of date.
- When you bump the `JuceSharp` gitlink, publish the new pair so others need no
  compiler: `pwsh -NoProfile -File tools/release/Publish-NativeRuntime.ps1 -Upload`
  zips it, pins it in `tools/setup/native-runtime.json` (commit that with the
  bump) and uploads it. `NativeRuntimeFetchTests` fails the gate while the
  checked-in gitlink has no pin.

The full toolchain notes are in `JuceSharp/AGENTS.md` (section "Native rebuild").

## 4. Build and run

```powershell
dotnet build DotCharter.sln --nologo -v:m
dotnet run --project DotCharter.UI\DotCharter.UI.csproj
```

The build treats style warnings as errors, so a first build on your own change
may fail on formatting; the message names the rule and the line. The rules are
in `.editorconfig` and summarised in [CONTRIBUTING.md](../../CONTRIBUTING.md).

## 5. Run the tests

There are **two** test runs, and that is on purpose.

```powershell
# the default gate: builds first, prints counts, writes the full log to a file it names
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Gate

# a few classes while you iterate (wildcards only at either end)
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -FilterClass '*MyFeature*'

# the headless UI suite, as its OWN run, after any view or XAML change
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless
```

Why the headless suite is separate: `Avalonia.Headless` keeps one dispatcher per
process. If those tests share a process with the rest, most of them fail and
they take unrelated tests down with them. The default gate therefore excludes
every `*UiPerfSmokeTests` class, and the headless run starts several processes of
its own. This is not a hang, and the gate must not be serialised to "fix" it. The
headless run takes about three minutes on an idle machine; the default gate three
to four.

A plain `dotnet test` is safe too. With no filter, the DotCharter suite runs the
default gate and leaves the headless suite out, so the two cannot mix by
accident. Pass any `--filter-*` option and you get exactly what you asked for.
From the repository root it also runs the JuceSharp tests; add
`--project DotCharter.Tests\DotCharter.Tests.csproj` to run only DotCharter's.

The raw commands the wrapper runs are in [AGENTS.md](../../AGENTS.md#validation).
Use the wrapper anyway: it avoids stale binaries and long console output.

**[DotCharter.Tests/README.md](../../DotCharter.Tests/README.md)** is the guide
to the suite for people: the fast, full and headless runs, why so many tests
read source text, how to add one of those lints, and how to read a failure.

JuceSharp has its own tests:

```powershell
dotnet test --project JuceSharp\tests\JuceSharp.Tests\JuceSharp.Tests.csproj
```

Some stem-separation and child-process tests there depend on the environment;
compare against a clean checkout before blaming your change.

## Making your first change

The [cookbook](cookbook/README.md) has a short recipe for each common change: a menu
command, a setting, a string, a note technique, a pedal, a native call and more.

## Where things are written

| What | Where |
|---|---|
| Logs | `%APPDATA%\DotCharter\logs` (a `logs` folder beside the app in a portable install) |
| Crash reports (opt-in) | `%APPDATA%\DotCharter\crash-reports` |
| Settings | `%APPDATA%\DotCharter` |

The log format and how to read it are in [DEBUGGING.md](DEBUGGING.md).

## Troubleshooting

**`MSB3027` / "file is locked by testhost" on the next build.**
A previous test run left a test host alive and it holds the output DLLs. Close
it, then build again:

```powershell
Get-Process testhost, DotCharter.Tests, JuceSharp.Tests -ErrorAction SilentlyContinue | Stop-Process
```

**An ABI mismatch when the audio engine opens** (`JuceAbiMismatchException`, or
a message about the engine version).
`juce_sharp.dll` was built from a different `JuceSharp` commit than the one your
checkout has. Run `tools/setup/Bootstrap.ps1` to compare the two numbers, update
the submodule (`git submodule update --init`), delete the old pair and build
again (the prebuilt one is fetched), or rebuild the DLL yourself. If you
rebuild only the DLL and not `juce_vst_host.exe`, the pair no longer matches
either; the build deploys both.

**Almost every UI test fails when run together with the others.**
You ran the headless tests in the same process as the default gate. Use the two
separate invocations in [Run the tests](#5-run-the-tests).

**The audio interface is busy or "in use" after a crashed run.**
A test host or the app that opened an ASIO device died without releasing it, and
ASIO devices are exclusive. Kill the leftover `testhost.exe`,
`DotCharter.Tests.exe`, `JuceSharp.Tests.exe` or `DotCharter.exe`, then reopen
the device. If it stays busy, unplug and replug the interface or restart its
control panel.

**`TestData/` is missing.**
That is fine. It is an optional, git-ignored folder of song material for local
experiments. Tests that need it skip themselves, so the gate counts differ
between machines. Compare counts only on the same checkout.

**The build fails with `error DCNATIVE`.**
The prebuilt engine could not be installed; the message says why. "No prebuilt
native runtime is pinned" means the `JuceSharp` commit has none yet: build it
yourself (above). A download failure while the repository is private means `gh`
is missing or not logged in. A SHA-256 or ABI mismatch means the file is not the
published one: nothing was installed, report it.

**The build cannot find `cmake` or the generator.**
See the notes under [The native engine](#3-the-native-engine-juce_sharpdll):
use the CMake bundled with Visual Studio Build Tools 2026 and the
`"Visual Studio 18 2026"` generator.

**The window opens but the highway is blank.**
Check that a current GPU driver is installed and that
`C:\Windows\System32\vulkan-1.dll` exists. `Bootstrap.ps1` reports both.

Still stuck? Look at the newest file in the logs folder
([DEBUGGING.md](DEBUGGING.md)) and open an issue with the relevant lines.
