# Play Mode — the backdrop, the profile chip and the guest fast path

Part of the Play Mode reference. Router: [../play-mode.md](../play-mode.md).

- **The Play Mode backdrop is ONE custom-drawn control** (0.16 5.2; whole
  surface since the 0.16 visual pass): `Controls/MenuBackdropControl` draws
  the whole scene — wash, a field of accent glows with parallax drift +
  breathing, gently-tilted string-shimmer lines (Wave V4 diagonal fan, `MenuBackdropMath.StringSlope`),
  a pooled ~40-particle field and the EQ bars — in a
  single render pass on a ~30 fps `DispatcherTimer` that STOPS whenever the
  control is detached or not effectively visible (the Wave-1 `backdropLive`
  gating semantics live inside the control now; there are no style animations
  left to gate). The single instance renders behind EVERY Play Mode screen
  (`IsBackdropVisible`, hidden only under the pause menu's frozen-highway
  capture); the `Intensity` styled property (bound to
  `PlayModeViewModel.BackdropIntensity`) picks the per-screen profile via the
  pure `MenuBackdropMath.ProfileFor` — `Full` on Start/Menu, `Ambient`
  (softer/slower glows, fewer + slower particles, fainter strings, NO EQ bars
  and thus no input-peak polls, plus a background-coloured readability dim)
  behind all content screens. Palette
  resolves from theme tokens (`Accent*`, `AppBackgroundColor`) +
  `ChartTheme.BaseStringColors` at attach and on `ChartTheme.ThemeChanged` —
  never per frame. Animation math is the pure `MenuBackdropMath`
  (`MenuBackdropMathTests`, incl. the profile curves). The EQ bars are **audio-reactive** (0.16 5.3):
  `PlayModeView` wires `InputPeakProvider` to
  `PlayModeViewModel.ReadBackdropInputPeak` — the same non-destructive
  `ReadInputCaptureWindow` tap as the pre-play signal meter, polled only at
  the control's own tick (no new native calls, nothing polled while parked);
  silence decays back to the idle pattern. Don't re-add style-animated
  elements to this surface (a hidden control's style clocks keep ticking —
  the original ~29 clocks ran under the editor forever); extend the
  control's render pass.
- The profile-switcher flyout content is the shared
  `ProfileSwitcherFlyoutContent` DataTemplate in
  `Views/Shell/ProfileSwitcherFlyout.axaml` (bound to the shell's single
  `StatusBarViewModel`), merged into `StatusBar.axaml` — edit it there, never
  re-inline it. Play Mode's own chip template (`MenuProfileChipTemplate`) had no
  user left and went in #240.
  - **Every avatar goes through `ViewModels/Application/ProfileAvatarImage`, and
    nothing decodes one at full resolution.** The picture is a file the USER
    picked and `PlayerProfileService.SetAvatar` is a plain `File.Copy` (Services
    owns no image dependency), so a 12 MP phone photo used to be stored verbatim
    and then decoded with `new Bitmap(stream)` — 80-250 ms and ~48 MB of RGBA
    RETAINED per profile, for a circle that is at most **20 px** across, held by
    the status bar, the front-door chip and the flyout at once. `Load` decodes at
    `DecodeWidth` (96); `PrepareForImport` re-encodes anything wider than
    `StoredWidth` (256) into a temp PNG BEFORE the service copies it (and never
    upscales — `StoredWidthFor` returns null for an already-small picture, which
    also keeps its original format).
    Pinned by `UI/ProfileAvatarImageTests` (pure policy + a source lint —
    the pixel work needs a Skia platform the default gate does not boot).
- **Guest session fast path** (v0.31): a "Guest session" button next to the
  profile chip reveals an inline name row (`IsVisible` toggle, no overlay);
  "Calibrate & play" creates an ORDINARY profile via pure
  `GuestSessionNaming.Resolve` (trim/cap/dedup), switches to it, lands in Song
  Select and opens the existing `LatencyCalibrationWizard`. The tuner step is
  the existing per-song `TuningCheck` gate — deliberately not duplicated. A
  0.28 roamed-profile import ends in the same state and simply skips this.
