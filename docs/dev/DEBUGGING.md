# Debugging and diagnostics

Where to look when DotCharter misbehaves, and what to attach when you report it.
For setting up the project, see [GETTING-STARTED.md](GETTING-STARTED.md).

## Logs

The app writes a plain-text log on every run.

- **Folder:** `%APPDATA%\DotCharter\logs`. A portable install uses a `logs`
  folder next to the app; if neither can be written, the system temp folder is
  used. The log files end in `.log`; the newest one is the current session.
- **Line format:**

  ```
  2026-08-02 19:16:55.113 +02:00 [WARN] [Audio] [T22] message text
  ```

  That is: timestamp, level, category, managed thread id, message. A stack
  trace follows its message on the lines below it.
- **Levels** are four letters: `VERB`, `DBUG`, `INFO`, `WARN`, `EROR`, `FATL`.
  Note the spelling `EROR`.
- **Settings:** the Logging section of the Settings window turns file logging
  on or off, picks the log level and the categories, and controls rotation (file
  size and how many old files are kept). Raise the verbosity before reproducing
  a hard-to-catch problem.

What to search for:

```powershell
# problems in the newest log
Select-String -Path "$env:APPDATA\DotCharter\logs\*.log" -Pattern '\[(WARN|EROR|FATL)\]' | Select-Object -Last 40
# one subsystem, for example audio
Select-String -Path "$env:APPDATA\DotCharter\logs\*.log" -Pattern '\[Audio\]'
```

Start from the **last** `EROR` or `FATL` line and read upward: the first
`WARN` shortly before it is often the cause. Repeated identical lines are
summarised as `[xN]`.

The same log is shown inside the app: **Help > Session health** lists recent log
entries newest-first with warnings and errors highlighted, and offers
**Export diagnostics bundle...** (see below).

## Diagnostics window and bundle

**Help > Session health** is the quickest way to see whether the current session
logged problems. **Export diagnostics bundle...** writes a zip with the recent
logs and system information. Secrets such as tokens and credentials, and your
user name, are stripped before anything is zipped; credential files are refused
outright. Read the bundle before you share it anyway.

## Performance and rendering harnesses

Timing problems (a slow screen switch, a slow start) are measured by headless
test harnesses rather than by eye. They run in the separate headless test run:

```powershell
powershell -NoProfile -File tools/agent/Invoke-Gate.ps1 -Headless
```

What each harness measures, how to read its numbers and the traps that made
them lie are documented in
[docs/agents/testing/perf-harnesses.md](../agents/testing/perf-harnesses.md). The
UI-sweep capture (a screenshot of every surface) is described in
[docs/agents/testing/ui-sweep.md](../agents/testing/ui-sweep.md).

Two things to remember when you measure startup:

- A normal `dotnet build` JIT-compiles the startup path, so it starts much
  slower than a published build. The `StartupPhases:` line in the log ends with
  `r2r=yes|no|?`; a number from an `r2r=no` run is a JIT measurement.
- Never compare wall-clock times between machines or between a busy and an idle
  machine.

## Crash reports

Crash reports are **off by default**. Turn them on in Settings ("Save a local
crash report when the app crashes").

When an unhandled exception reaches the top-level handlers and you have opted
in, the app assembles a text report and writes it to
`%APPDATA%\DotCharter\crash-reports\crash-<date>-<time>-<id>.txt`. The newest 20
are kept. The report is **never uploaded**; you decide whether to attach it to
an issue. The report layout (the code is
`DotCharter.Services/Infrastructure/CrashReporting/CrashReportBuilder.cs`):

1. a header: `=== DotCharter crash report ===`, the UTC time, the app version
   and where the crash surfaced;
2. the exception: type, message, stack trace, inner exceptions;
3. system information (Windows, runtime, memory);
4. the last lines of the app log.

The whole text goes through `SensitiveTextRedactor` as a final pass, so user
names, file paths, tokens and credentials are scrubbed. Still skim it before you
post it.

## Attaching a debugger

**Managed code.** Open `DotCharter.sln` in Visual Studio or Rider, set
`DotCharter.UI` as the startup project and press F5. Or start the app and attach
to the `DotCharter.exe` process. Break on first-chance exceptions to find the
origin of an error that the app swallows.

**The native engine.** `juce_sharp.dll` is loaded into the same process.

1. In Visual Studio, open Debug > Attach to Process, pick `DotCharter.exe` and
   choose **Managed and Native** code. Or tick "Enable native code debugging" in
   the `DotCharter.UI` project's debug properties and start with F5.
2. The native build must have symbols. The Release build from
   [GETTING-STARTED.md](GETTING-STARTED.md#3-the-native-engine-juce_sharpdll)
   has few; for stepping through C++ configure a Debug build instead
   (`cmake --build build --config Debug`) and deploy that DLL pair.
3. VST plugins run in a separate child process, `juce_vst_host.exe`. To debug
   the host side, attach to that process too.
4. Do not set breakpoints on the audio callback while a device is playing: the
   device stops delivering buffers and drops out. Log instead, or use the
   null-audio-device tests in `JuceSharp/tests/JuceSharp.Tests`.

Rules for the audio thread (no allocation, no locks) are in
`JuceSharp/docs/agents/rt-safety-and-tests.md`. Read them before changing code
that runs in the callback.

## Known hang and teardown traps

- **Stale test host.** A test that boots the real app session or an audio engine
  can hang its test host at exit. The leftover process locks the build output
  (`MSB3027` on the next build) and, if it opened an ASIO device, keeps that
  device busy. Kill `testhost.exe`, `DotCharter.Tests.exe` and
  `JuceSharp.Tests.exe` before rebuilding.
- **Headless tests share a dispatcher.** Running the `*UiPerfSmokeTests` classes
  in the same process as the default gate makes nearly all of them fail. It
  looks like a hang or a flaky suite but is a process boundary; use the two
  invocations from [GETTING-STARTED.md](GETTING-STARTED.md#5-run-the-tests).
- **Engine open fails with a version message.** The DLL and the checkout are out
  of step. Rebuild the native engine; see Troubleshooting in
  [GETTING-STARTED.md](GETTING-STARTED.md#troubleshooting).
- **Logger shutdown blocks.** The file log sink drains on dispose. Do not hold a
  lock that the logging path needs while you dispose or replace the logger.
- **A popup comes back unstyled** after closing and reopening it. Popovers must
  not be re-realised while closed; the UI guides in `DotCharter.UI/AGENTS.md`
  explain why.

## Reporting a bug

Use the **Bug report** form (`.github/ISSUE_TEMPLATE/bug_report.yml`) on the
GitHub issue page. It asks for:

- what you did, step by step, and what you expected;
- the DotCharter version (Help > About), your Windows version and GPU, and for
  audio problems your interface and driver;
- a **log excerpt**: the `WARN` and `EROR` lines from the log folder around the
  time it happened;
- a screenshot or recording for anything visible.

To make the report easy to act on, attach the newest crash report if the app
crashed, or the exported diagnostics bundle if it did not. Remove anything
personal first. Security problems are not reported as issues; see
[SECURITY.md](../../SECURITY.md).
