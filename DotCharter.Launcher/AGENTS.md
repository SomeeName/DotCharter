# DotCharter.Launcher

NativeAOT stub that becomes the release's top-level `DotCharter.exe`. It starts
`app\DotCharter.exe` with the same command line and exit code (`Program.cs`,
`LaunchCommandLine.cs`).

- References nothing first-party; keep it that way (startup size and AOT).
- The app resolves install-scoped paths (portable config, `tools/`, `voices/`,
  `models/`) through `InstallLocation.InstallRoot`, never `AppContext.BaseDirectory`.
- Layout, publish and update behaviour:
  [split-install.md](../docs/agents/services/infrastructure-update/split-install.md).
