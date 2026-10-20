# Rendering / perf — startup work and hidden-surface prewarm

One file per subject under `startup-and-prewarm/`. Read the one you need:

- [startup.md](startup-and-prewarm/startup.md) — startup: MainWindowViewModel's constructor runs before the window exists; what `new MainWindow()` and the bind no longer build (#194)
- [hidden-surface-prewarm.md](startup-and-prewarm/hidden-surface-prewarm.md) — hidden-surface prewarm: the four cases and the drain order
- [deferred-screen-bodies.md](startup-and-prewarm/deferred-screen-bodies.md) — deferred screen bodies: the case prewarm cannot help with
