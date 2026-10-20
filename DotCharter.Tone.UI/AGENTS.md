# DotCharter.Tone.UI Agent Guide

The rig's UI and the design system it is drawn in, split out of `DotCharter.UI`
(2026-09-26) so DotCharter and the standalone rig (`DotCharter.RigLab`) host the
SAME rig. **Namespaces are `DotCharter.Tone.UI.*` and the folders mirror
`DotCharter.UI`'s** (`Views/PlayMode/AmpFxRigView.axaml` is at the same relative
path it had; its type is `DotCharter.Tone.UI.Views.PlayMode.AmpFxRigView`).
`Infrastructure/NamespaceMatchesAssemblyTests` fails a type declared outside
`DotCharter.Tone.UI`. `ViewLocator` maps a view model to the view of the same
name in the VIEW MODEL'S assembly, so a rig view model and its view both live
here. Source lints that map a view's namespace to its folder fold the two roots
through `UiSourceTree.UiNamespace`.

## What lives here

- **The rig**: `Views/PlayMode/AmpFxRigView` and the `Rig*` views (the Tones mode is
  `RigTonesView` since 2026-09-27 — its own name scope, hosted once as `RigTonesMode`),
  `Views/PlayMode/RigEditors/`,
  `ViewModels/PlayMode/` (`AmpFxRigViewModel`, `PedalboardViewModel` partials,
  `Rig*`, `PresetBrowserViewModel`, `GearRailViewModel`, `NamCaptureBrowserViewModel`,
  `PedalModelSelectorViewModel`, `DeferredRigScreen`, `ToneDesignHostSeams`),
  `ViewModels/Settings/LiveInputChannelViewModel`, `ViewModels/Editor/VstChainSlotViewModel`.
- **The plugin rack** (2026-09-27): `Views/PlayMode/VstRackView` + its
  `VstChainDragController`, over `ViewModels/Editor/IVstRack` — the guitar VST3
  chain's rows (grip, index, name, loading, ON/BYPASS, seat, GUI, remove), the
  empty well and the "+ Add VST3…" picker, with NO heading or rescan (each host
  has its own). ONE view for every chain surface: DotCharter's Tools popover and
  Amp/FX panel (`TransportVstRack`, an adapter over the transport's chain) and
  RigLab's Plugins tab (`VstRackViewModel`). Do not give a host its own chain
  template — `PopoverFeatureTests.EveryChainSurface_HostsTheOneSharedRack`.
- **The design system**: `Styles/` (`ColorTokens`, `Resources/`, `FireTokens/`,
  `Fire/`, `Fire.axaml`, `Icons`, `Typography`, `Buttons`, `Inputs`,
  `Containers`, `ControlThemes`, `FireMenuChrome`, `FireControls`,
  `FireHardwareTokens`), `Controls/Fire/`, `Controls/Dsp/`, `Controls/FirePopover`,
  `Controls/MasonryPanel`, `Controls/EffectiveVisibilityWatcher`, the chunked
  panels (`Controls/IChunkedPanel`, `HiddenChunkGate`, `ChunkedWrapPanel`,
  `ChunkedStackPanel` — a hidden first layout paid a chunk per prewarm turn,
  #253; see [hidden-surface-prewarm.md](../docs/agents/ui/rendering-perf/startup-and-prewarm/hidden-surface-prewarm.md)), the Fire
  converters, `Localization/LocExtension` (`{localization:Loc}`), `Assets/Fonts`,
  `Assets/Rig`.
- **`Assets/Rig/` ships inside this DLL** (`AvaloniaResource Include="Assets\**"`), so
  every byte there is install size. Its WebP art is LOSSY: a new or re-imported face
  goes through `tools/rig-art/compress-faces.py` (pinned cwebp, q90 + sharp YUV, full
  size); `UI/RigArtEncodingTests` fails on a lossless or oversized face. Details in
  [widgets-and-faces.md](../docs/agents/ui/pedalboard/widgets-and-faces.md).
- **Shared plumbing**: `ViewModels/Shell/ViewModelBase`, `CoalescedFilterPass`,
  `ViewLocator`, and `Platform/` (`AvaloniaUiDispatcher`,
  `DesktopFileStorageService`, `DesktopShellLauncher`, `AvaloniaSystemClipboard`,
  `TextFallbackFonts`, `WindowsTimerResolution`).
- **`Styles/RigTheme.axaml`**: the design system WITHOUT DotCharter's chrome, for
  RigLab. `RigThemeOrderTests` keeps its include order equal to
  `DotCharter.UI/Styles/Theme.axaml`'s.
- **The rig's styles (#242)**: its Border / TextBlock / ToggleButton families are
  ControlThemes in `Styles/Fire/FireRig*.axaml` (resources: they cost a control
  nothing until it wears one); everything else the rig styles is VIEW-SCOPED —
  `Views/PlayMode/AmpFxRigStyles.axaml` (first in `AmpFxRigView`'s Styles),
  `Views/PlayMode/RigModuleEditorStyles.axaml` (in `RigModuleEditorView`'s) and
  `ToneDesignCorePanel`'s own Styles. RigLab hosts the same `AmpFxRigView`, so its
  scoped rules travel with it and `RigTheme.axaml` needs nothing. Detail and the
  traps: [fire-design-system.md](../docs/agents/ui/fire-design-system.md) § "The
  rig's styles are ControlThemes or VIEW-SCOPED".

## Rules

- **XAML outside this assembly reaches its types with `using:DotCharter.Tone.UI.…`**
  or `clr-namespace:DotCharter.Tone.UI.…;assembly=DotCharter.Tone.UI` — a bare
  `clr-namespace:` resolves in its OWN assembly only. A DotCharter view that
  hosts both an app type and a rig type from the mirrored folder declares two
  prefixes (`PlayModeView.axaml`: `vm` and `vmRig`).
- **Resource URIs are `avares://DotCharter.Tone.UI/…`** for anything here
  (styles, fonts, rig art). `DotCharter.UI/Styles/Theme.axaml` lists them one by
  one to keep the app's merge order.
- **The AI tone designer's CORE lives here** (2026-09-26):
  `ViewModels/PlayMode/ToneDesignCoreViewModel` (prompt, conversation, result,
  the four actions) and `Views/PlayMode/ToneDesignCorePanel`. DotCharter's
  `ToneDesignViewModel` DERIVES from it and adds Match / Sections (ONNX, the
  charting stack) through the protected members (`Loc`, `Logger`, `Post`,
  `SaveUserSeam`, `ChainSummary`, `InlineProgress`, `OnResultChangedCore`);
  DotCharter's `ToneDesignPanel` wraps the core panel and passes those blocks in
  its `ExtrasTemplate` (a template, so a collapsed panel realizes none of it).
  The preset browser still takes a `Func<ToneDesignHostSeams, object?>` factory
  (typed back as `PresetBrowserViewModel.Designer`) and `RigTonesView` hosts the
  result in `RigToneDesignPanel`, a column shown while it is expanded; each host maps its
  view model: DotCharter's `App.axaml` (`ToneDesignViewModel` → `ToneDesignPanel`),
  RigLab's `RigLabWindow` (`ToneDesignCoreViewModel` → `ToneDesignCorePanel`).
- **The designer's "ChatGPT (copy & paste)" route needs NO provider** (2026-09-28):
  the composer's "Use ChatGPT" toggle (and, with no provider configured, the setup
  banner's door) opens a block under the send row — `CopyPromptCommand` puts the
  exact prompt a design would send on the clipboard, `ImportReplyCommand` runs the
  pasted answer through `IToneDesignService.ImportReplyAsync` and lands it in the
  same card and thread. Both hosts pass the clipboard (`ISystemClipboard`, the ctor's
  last parameter: DotCharter from its container, RigLab from `RigLabHost.Clipboard`);
  without one the copy step is disabled. The block sits in the PINNED composer, so
  it is compact and its paste box is capped (`FireToneReplyMaxHeight`) — every
  pixel it takes comes out of the thread (`Perf/ToneDesignManualRouteUiPerfSmokeTests`
  pins a readable thread at 1280x800). A failed import writes `ManualErrorText`
  under the paste box and nothing else. Detail:
  [../docs/agents/ui/pedalboard/ai-tone-design.md](../docs/agents/ui/pedalboard/ai-tone-design.md).
- **A custom-drawn Fire control derives from `Controls/Fire/FireDrawnControl`**
  (counted `Repaint()`, the `Paint` cache, and the theme-variant subscription
  when constructed with `followsThemeVariant: true`) instead of copying that
  plumbing. Detail: [../docs/agents/ui/fire/custom-controls.md](../docs/agents/ui/fire/custom-controls.md).
- **Profiles are a callback** (`PedalboardViewModel`'s `subscribeProfileSwitch`),
  not a Services type.
- Banned APIs are on (`DotCharterBannedApis`); `Platform/**` is exempt in
  `.editorconfig` exactly like `DotCharter.UI/Platform/**`.
- **Tests read this tree through `UiSourceTree`** (`DotCharter.Tests/Infrastructure`):
  a lint that walks `DotCharter.UI/<dir>` also walks the mirror here, and a file
  that moved is read from here. A new source lint must do the same, or it goes
  blind to the rig.
- Everything the UI guides say about the rig still applies:
  [../docs/agents/ui/pedalboard.md](../docs/agents/ui/pedalboard.md),
  [../docs/agents/ui/fire-design-system.md](../docs/agents/ui/fire-design-system.md).
