# Network surfaces — TONE3000 (UI deep reference)

Extracted from `DotCharter.UI/AGENTS.md`.

**There is no `Views/Online/` any more, and nothing LAN is left.** v0.60 removed
the online backend (the Online hub, leaderboards, profiles and room boards), and
on 2026-09-19 the LAN scoreboard, its Network screen, the per-song board, the
shared catalogue and LAN multiplayer went too, with `Views/Multiplayer/` and its
board-grammar style sheet. Restore any of it from tag `pre-cut-2026-09`.

The app's one remaining network surface is **TONE3000**, and it is a Settings
screen like any other: `DotCharter.UI/Views/Settings/Tone3000SettingsView.axaml`
over `DotCharter.Presentation/Screens/Settings/Tone3000SettingsViewModel.cs`
behind `ITone3000SettingsScreen`. Its row grammar is
[settings.md](settings.md)'s; its migration checklist is there too.

- A `Padding`/`Margin` setter fed a `Space*` token throws
  `InvalidCastException` at layout time (`x:Double` → `Thickness`) and builds
  perfectly clean. Thicknesses come from `Padding*`/`Gap*` only.
- **Every input on this screen carries an `AutomationProperties.Name` off the
  row's own label key** — a placeholder is a hint that disappears once the field
  is filled, so an unnamed input reaches a screen reader as `Edit ""`. The
  repo-wide `UI/AccessibleNameTests` covers `TextBox` and nine other input types
  across all of `Views/`, which is what retired the folder-scoped
  `OnlineTextBoxAccessibleNameTests`.
- The TONE3000 OAuth sign-in is the app's ONE inbound listener (a loopback
  `HttpListener` on `Tone3000OAuth.CallbackPort`, 47821). It must stay inside
  the firewall allow-range 47810–47829; `Services/Online/InboundListenerPortTests`
  pins it and fails on any other listener in the service layer.
