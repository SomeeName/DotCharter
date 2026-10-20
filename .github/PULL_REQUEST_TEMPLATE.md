## What

<!-- What changed and why, in a few sentences. -->

Fixes #

## How

<!-- The approach, and anything a reviewer should look at first. -->

## Evidence

<!-- See CONTRIBUTING.md. A change is done when something would fail if it regressed. -->

- [ ] Default gate (`tools/agent/Invoke-Gate.ps1 -Gate`): passed / failed / skipped = …
- [ ] Headless-UI suite (`-Headless`, required after any view or XAML change): …
- [ ] New or changed behaviour has a test, and I watched it fail without the fix
- [ ] Before/after screenshots for anything visible
- [ ] User-visible text is in all three resource files (en, de-DE, ja-JP)
- [ ] Guides (`AGENTS.md`, `docs/agents/`) updated where this change makes them wrong
- [ ] No game data, no copyrighted tabs or audio, and I have the right to submit this.
- [ ] Every commit is signed off (DCO).

## Known limits

<!-- What this does not do, or what you could not verify (for audio: interface, driver and OS tested). -->
