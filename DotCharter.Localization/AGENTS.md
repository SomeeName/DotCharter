# DotCharter.Localization Agent Guide

The app's localization resources + lookup service. Three built-in languages:
English (invariant), German and Japanese. Standalone leaf project (no
DotCharter.* references, no external NuGet). Referenced by `DotCharter.Services`
and `DotCharter.UI`.

## Layout

- `Resources.resx` — the **canonical** string table (English / invariant).
- `Resources.de-DE.resx` — German translations.
- `Resources.ja-JP.resx` — Japanese translations (0.46). Keep the keys of BOTH
  translated files in lock-step with the invariant one.
- `LocalizationService.cs` — `namespace DotCharter.Localization`.
  `ILocalizationService` with an indexer `["Key"]` and `Format("Key", args…)`
  (composite `string.Format`); `LocalizationService.Instance` is the one process
  instance, registered in DI and read by the `{localization:Loc Key}` XAML
  markup.

## Community language packs

- `LoadCommunityLanguages(folder)` overlays runtime language packs from
  `<ConfigRoot>/languages/*.json` (called once at startup in `App.axaml.cs`,
  before `SetConfiguredCulture`). Each file is a **flat JSON object** of
  `"Key": "Value"` pairs; the filename (sans `.json`) is the culture code
  (`fr-FR.json`). An optional reserved `"Language_DisplayName"` key names the
  pack in the picker (else the culture's native name).
- Chosen over runtime `.resx`: a flat JSON map is trivial for translators to
  author and parses with the in-box `System.Text.Json` (keeps this project's
  "no external NuGet" rule). Loading is **fully defensive and every branch of
  that is a test** in `DotCharter.Tests/Localization/CommunityLanguageTests.cs`:
  malformed JSON, a non-object root (`null` / a bare string / a number / a
  bool / an empty file), an object with no string values, a nonexistent folder
  and an empty / whitespace / syntactically invalid folder path are each
  skipped, never thrown. Non-string VALUES inside a valid object are dropped
  individually — a key "defined" as a number falls through to the resx rather
  than rendering the number. A pack that omits a key falls back to the English
  resx; packs cannot shadow a built-in (`en-US`, `de-DE`, `ja-JP`) — the
  built-in set is `LocalizationService.SupportedCultures`.
- **A filename that is not a culture code is KEPT, not skipped** (`readme.json`
  becomes a selectable "readme" language). `CultureInfo.GetCultureInfo` throws
  `CultureNotFoundException` for it, which is caught: the pack keeps its
  strings and formats with `en-US`. Note ICU is lenient — `not-a-culture.json`
  RESOLVES, to a culture actually named `not`. What matters is that neither
  path throws and neither disturbs the three built-ins, which is what the
  theory pins.
- **`LoadCommunityLanguages` re-resolves the ACTIVE pack** against the set it
  just rebuilt. It is a replace, not a merge: before v0.58 `_activeCommunity`
  was left pointing at the old `CommunityLanguage`, so a pack DELETED from the
  folder went on supplying every string it defined until the app restarted.
  Startup calls this before `SetConfiguredCulture`, so it only bites a rescan.
- Packs appear in `LanguageOptions` after the built-ins and are selectable like
  any language. `IsRightToLeft` reflects the active culture so the shell can bind
  `FlowDirection`.
- **Portable-install gotcha:** `<ConfigRoot>` is the install dir on a portable
  layout, so `languages/` is on `PayloadReceiptFilter.ExcludedTopDirs` — an
  update payload never carries, and so never overwrites, a user's packs.

## Rules

- **Every user-facing string is a resx key** — never hard-code display text in
  views or view models. Look up via `{localization:Loc Key}` in XAML or an
  `ILocalizationService` taken through the constructor (or as a parameter) in
  code. **New code never reads `LocalizationService.Instance`**: only this
  project and the composition roots (`App.axaml.cs`, RigLab's `App.axaml.cs` and
  `RigLabHost`) may. `Infrastructure/StaticLocalizationRatchetTests` counts the
  remaining static uses per file against a list that may only shrink, and every
  lookup through an alias of the instance counts too (`L => …Instance`,
  `var loc = …Instance`, a `using` alias, `using static`) — when you migrate a
  file, lower or delete its entry. A test hands an injected class
  `DotCharter.Tests/Infrastructure/FakeLocalization.cs`, not the singleton.
- **No file under `DotCharter.UI/ViewModels/` or `DotCharter.Tone.UI/ViewModels/`
  reads the singleton or has an allowance** (#289, #549;
  `StaticLocalizationRatchetTests.NoViewModelReadsTheStaticLocalization`), and
  **neither does any file in `DotCharter.Presentation`, `DotCharter.Services` or
  `DotCharter.Tone`** (`NoServiceOrScreenLayerFileReadsTheStaticLocalization`).
  DI fills a view model's `ILocalizationService`; a view model that BUILDS one
  (a row, a chip, a child) passes its own down, and a static helper takes it as a
  parameter. The rig's root is `PedalboardViewModel` (its third constructor
  parameter, exposed as `Localization`): every rig child, `AmpFxRigViewModel`,
  `RigDockUnit` (through its node) and the design panel (`ToneDesignHostSeams.Localization`)
  take it from there, so DotCharter's DI and `RigLabHost` each hand in ONE instance.
  A model a service hands out gets it from that service (`PlayerProfileService`
  stamps each `PlayerProfile` with its localized "Player 1"). A host that may
  legitimately be built without one (`TransportViewModel`, `StemMixerViewModel`,
  `ChartTonePreviewController`) passes its nullable down and the child prints the
  KEY. Every remaining allowance is `XamlConstructed` (a control, view, converter
  or markup extension XAML builds with no arguments); `NotMigratedYet` is empty.
- **The code-side key sweep (`UI/LocalizationSweepTests`) sees** `_loc["Key"]`,
  `_loc?["Key"]`, `_loc?.Format("Key", …)`, nullable receivers, and calls to a
  one-line forwarding helper (`string Text(string key) => _loc?[key] ?? key;` —
  `Text("Key")` is checked). A local alias (`var loc = _loc;`) is NOT a receiver:
  look keys up on the field or parameter itself.
- **Add a key to ALL THREE resx files** in the same change. A key present only
  in the invariant file falls back to English at runtime for German and
  Japanese users; a key
  missing from BOTH resolves to the key NAME. **Nothing throws** —
  `LocalizationService.GetString` is
  `culture ?? en-US ?? key`, so a typo ships as the literal string
  `Settings_Titel` on screen and never as an exception. (This bullet claimed a
  missing invariant key "throws" for several releases, contradicting the
  fallback rule stated 20 lines below it. It does not; nothing here does.)
  The three must stay in sync, and `LocalizationSweepTests` is what enforces it.
- **A string that prints a COUNT needs two forms, chosen by
  `LocalizationService.Plural(oneKey, manyKey, count, …)`.** The game surfaces
  print small counts constantly and a plural-only string reads as broken the
  moment one of them is 1 — the launch screen shipped "1 Tage Serie", the
  calendar "1 aktive Tage" and "1 Sessions". English hides it (often both forms
  are the same phrase, which is allowed); German does not. Convention: the
  singular key is the plural key with `Fmt` → `One`, and the count is always
  `{0}` — the chooser supplies it, so a caller passes only the EXTRA arguments.
  Pinned by `DotCharter.Tests/UI/PluralStringTests.cs`, whose `PairList` table
  is the list of counted strings: **add your pair there.** Japanese has no
  plural, so its two forms may be the same text; `BothFormsAreTranslatedInJapanese`
  checks each form is present, prints `{0}` and is not the English text. The
  chooser itself is tested through `LocalizationService.FormatPlural` with a
  lookup whose forms differ by construction, never through the singleton (whose
  culture another test may have left on ja-JP). You cannot forget —
  `EveryPluralCallSiteInTheApp_IsRegisteredInThePairsTable` walks the other way
  and fails the gate for any `Plural(one, many, …)` written in `DotCharter.UI`
  or `DotCharter.Services` that is not in the table. (v0.58 swept thirteen
  strings that were still on `Format`; two of them — the phrase group's
  iteration count and the hand shape's enclosed-sound count — reach 1 in the
  ORDINARY case, so the inspector printed "1 Iterationen" / "1 Klänge darin"
  most of the time it was open.)
- Keep keys namespaced by feature area (`PlayMode_*`, `Lan_*`, `Toolbar_*`,
  `Settings_*`, …) so related strings sort together.
- `&` in values must be XML-escaped as `&amp;` inside the `.resx`.
- **A TRANSLATED value must use the SAME composite-format arguments as its
  English counterpart.** A dropped `{0}` prints a message with its data missing
  — the number, the file name, the song title — and `string.Format` never
  complains; an EXTRA index throws `FormatException` at the call site for that
  language's users only.
  `LocalizationSweepTests.TranslatedValues_UseTheSamePlaceholdersAsEnglish` is a
  theory over de-DE and ja-JP that compares the index SETS across every key
  pair (specifier- and alignment-aware, so `{0:N0}` and `{0,3}` count as
  index 0). Japanese reorders placeholders far more than German does —
  `{1} of {0}` is naturally `{0}件中{1}件` — so it is the SET that is pinned,
  never the order or the position.

## A FILE or a WIRE is never localized

Localization is for what a human READS. Anything that is written to disk or sent
to a peer — a `.rscp` attribute, an exported `.xml`, a JSON field, a log line
another build parses — is `CultureInfo.InvariantCulture`, always, in both
directions.

**The part that bites is the SIGN, not the decimal separator.** `int.TryParse`
never accepts a separator, so integer parsing reads as culture-proof and was
signed off as such by an audit in 2026-08. It is not: under **sv-SE, fi-FI,
nb-NO, lt-LT and sv-FI** `NumberFormatInfo.NegativeSign` is **U+2212 MINUS
SIGN**, not U+002D HYPHEN-MINUS. So `"-2"` — the low string of every Drop-D
tuning DotCharter has ever written — does not parse at all on those machines,
and `"−2"` written there does not parse anywhere else.

None of it throws. `RscpParsingUtilities.ParseFraction` answers a rejected
`FractionalPosition` with `Zero`, and `RscpArrangementParser.ParseTuning`
substitutes 0 per string — so a Swedish user's saved project reopened elsewhere
came back **E standard with the note on beat 0**, silently, with nothing logged.
98 call sites across 17 files were missing the provider when this was found
(2026-08-29); all are fixed, and `FractionalPosition.ToString` /
`Fraction.ToString` / both `TryParse`s and `RscpWriter`'s `tuningValues` join
are invariant at the source.

Two guards, and they cover different halves:

- **`Infrastructure/InvariantWireParseLintTests`** — the READ half. Every
  numeric `Parse`/`TryParse` under `DotCharter.Services/Parsing/`,
  `DotCharter.Services/Export/` and `DotCharter.Core/Domain/Primitives/` must
  pass a provider. Comments are stripped; the allow-list is **empty** and should
  stay that way — a format reader that wants the user's culture is reading text
  a HUMAN typed, and that belongs in a view model. **Do not add a row to turn a
  red build green.**
- **`Localization/InvariantWireFormatTests`** — the WRITE half, behaviourally,
  because no regex can tell a numeric `.ToString()` from a string one. It writes
  a whole `.rscp` under sv-SE / fi-FI / nb-NO / de-DE / en-US and asserts the
  five documents are **identical**, then saves in one culture and reloads in
  another. It carries its own non-vacuity pin
  (`TheHostileCultures_ActuallyRenderANonAsciiMinus`) so it fails loudly rather
  than passing emptily if ICU ever normalises those signs.

`FormatWithMs` is the deliberate exception and stays half-ambient: its bar/beat
part is the invariant `FractionalPosition.ToString`, its millisecond part
follows the user's culture, because that string is only ever read by a person.

## A type with a display form keeps TWO of them

`KeyChord` is the worked example (0.61). `ToString()` is INVARIANT — logs,
failure messages, `Services/Configuration/KeyChordTests`, and the shape
`KeyChordJsonConverter` mirrors — and `ToDisplayString(ILocalizationService)`
is the one a user reads: a German keyboard has no key marked "Ctrl", the cap
says **Strg**.

**Do not make `ToString()` itself follow the UI language.** It would put
process-global state inside a method the default gate asserts on, and the
asserting class is not in the `LocalizationSingleton` collection — nor should
it be, since it only READS the culture and `CultureCollectionLintTests`
deliberately does not serialize readers. That is the flake shape this repo
already spent a collection on.

Only the MODIFIERS are language (`Keybind_Mod_Ctrl` / `_Alt` / `_Shift`). The
key half is the character printed on the cap and `KeyChord.FormatKey` already
produces it. `Localization/KeyChordDisplayTests` pins both halves plus a source
lint over the four display sites, because calling the wrong one of two one-line
methods compiles perfectly and simply prints English.

## The UI LANGUAGE and the FORMATTING culture are two different settings

`SetConfiguredCulture` writes **`DefaultThreadCurrentUICulture` only**. It must
never write `DefaultThreadCurrentCulture` or `Thread.CurrentThread.CurrentCulture`
— number separators, date patterns and the first day of the week are a Windows
setting the user already made, and the language they picked for the menus says
nothing about it.

It wrote both until 2026-08-29, and that was wrong in both directions.
`ResolveCulture` falls back to `en-US` for anything outside `en-US` / `de-DE` /
`ja-JP` and the installed packs, so **every French, Spanish, Polish or
Portuguese user got en-US dates and decimal points on their own machine** — and
a supported language is normalised to its base culture, so a **de-AT** user was
moved to de-DE and their calendar started saying "Januar" instead of "Jänner".

Consequences to keep straight:

- `Format` and `Plural` take the STRING from `Culture` (the resource culture)
  and format the ARGUMENTS with `CultureInfo.CurrentCulture`. That split is the
  whole point; do not "tidy" them back to one culture.
- **A number a view model formats itself follows the same rule**:
  `CultureInfo.CurrentCulture`, never `ILocalizationService.Culture`. Eight sites
  (Get Ready's best, the launch chip's and Home's latency, Home's percentages,
  four tuner readouts) passed the resource culture as the `IFormatProvider`, so with
  English menus on a de-AT machine one path's best read 99.9% on Get Ready and
  99,9% in Song select. A percentage goes through
  `Presentation/Shared/PercentText`, whose spacing is the region's too ("99,9 %"
  in de-AT). `Localization/FormattingCultureTests` lints every `.Culture` passed
  as an argument or captured into a variable, over the display projects; a read
  that is neither (the tone narrator's LANGUAGE callback) is not formatting.
- The split is only safe because the file/wire layers stopped reading the
  ambient culture in the same change (section above). **Do not revert one half
  without the other** — the two lints named there are what make the leak
  visible, and `Localization/FormattingCultureTests` is what pins this half.
- A test that wants deterministic FORMATTING must now set it itself. The
  headless suite does: `Perf/HeadlessAppFixture.IsolateConfigRoots` pins
  `DefaultThreadCurrentCulture` to `DOTCHARTER_HEADLESS_CULTURE` alongside the
  language, because a group separator is a pixel or two of width and layout
  findings must not depend on the developer's Windows region. It is the one
  named exemption in `Infrastructure/CultureCollectionLintTests`, which also
  strips comments before looking for `[Collection(...)]` now — a doc comment
  QUOTING `[Collection("HeadlessUI")]` had been exempting that whole file by
  accident.

## Validation

**A key that does not exist prints its own IDENTIFIER on screen.** `GetString`
falls back to the key (`LocalizationService.cs`), so `["Foo_Bar"]` renders
`Foo_Bar` and — worse — `Format("Foo_Bar", 512)` becomes
`string.Format(culture, "Foo_Bar", 512)`, a format string with no placeholders,
which silently DROPS the arguments. Nothing throws and nothing is logged.
`Diagnostics_FramesFmt` shipped that way: Session Health printed the literal key
where the audio buffer size belongs, for every user, whenever audio was running,
while its three siblings on the adjacent lines existed.

Both halves of the reference surface are now linted in the default gate by
`DotCharter.Tests/UI/LocalizationSweepTests.cs`:

- `AllXamlLocReferences_ResolveToADefinedKey` — every `{localization:Loc Key}`.
- `AllCodeLocalizationLookups_ResolveToADefinedKey` — every C# lookup
  (`["Key"]`, `Format`, `Plural`, `PluralWith`, `GetString`) across
  `DotCharter.UI`, `.Services`, `.Tone`, `.Presentation`, `.Tone.UI` and
  `.RigLab` (`LookupProjects`). It resolves RECEIVERS (the literal
  `LocalizationService.Instance`, anything declared `ILocalizationService x`,
  anything assigned from the singleton) rather than matching a list of variable
  names, and a partial type's receivers count in EVERY part — an injected
  `_localization` is declared in one file and read in its siblings. A key built
  by concatenation (`"Prefix_" + name`) cannot be checked statically and is
  skipped. A total site floor **and a per-project floor** (`LookupFloors`)
  assert the walk did not go blind; raise a floor when a project gains lookups.
- `ResxFiles_HaveIdenticalKeySets` (all three tables) +
  `ResxFile_DefinesEachKeyExactlyOnce` (a theory over all three).
- `TranslatedValues_UseTheSamePlaceholdersAsEnglish` — see the placeholder rule
  above.

Two more cover the value a translator LEFT BEHIND, and they are different
instruments on purpose:

- `LocalizationSweepTests.UntranslatedValues_DoNotGrow` — a numeric RATCHET
  (245 de / 25 ja) over every value identical to its English counterpart that
  carries a word. The right instrument for the short shared labels, of which
  there are hundreds and each is a judgement call.
- `Localization/UntranslatedLongValueTests` (2026-09-04) — the complementary
  EXACT list, for values over 12 characters. A ceiling is a budget, not a list:
  translate one short leftover and add one long English sentence in the same
  change and the count does not move while a whole sentence ships in English.
  So the long ones are enumerated — 11 in de-DE, 4 in ja-JP — and each carries a
  NAMED reason (product names; Fender model names; words German shares with
  English, such as `Arrangement` and `Band`; the `Panel_Bend_*` family keeping
  the English bend techniques; placeholder-and-unit formats). A second
  case fails if a row stops describing a real identical value, so an exemption
  cannot outlive its reason.

Add a key to ALL THREE files in the same change and the lints stay quiet.

### The keys no text search can see

**A key whose NAME is computed is spelled out NOWHERE in the repo**, so both
lints above are blind to it and any "unused key" sweep calls the whole family
dead. That is not hypothetical: the sound-queue dropdown is built as
`$"Inspector_Queue_Filter_{filter}"` over a seven-member enum and only THREE
keys existed, so the picker listed the literal identifiers
`Inspector_Queue_Filter_AnySound`, `…_Untemplated`, `…_NoAnchorCoverage`,
`…_HasIssue` — including the default entry — in every language until v0.58.

`LocalizationSweepTests.EveryComputedKeyFamily_IsCompleteInEveryLanguage`
enumerates each family from the TYPE rather than from source text, and asserts
every member is present and non-empty in EVERY resx file. The families:

| Key shape | Enumerated from |
|---|---|
| `Inspector_EqTypeShort_*`, `Inspector_EqTypeName_*` | `EqBandType` |
| `Inspector_EqPreset_*` | `EqPresets.Keys` via `EqPresets.ResourceKeyFor` |
| `Inspector_Queue_Filter_*` | `SoundQueueFilter` |
| amp group headers | `DspModelGrouping.AmpModelGroup` via `AmpGroupResourceKey` |
| `History_<Action>`, `History_Verb_<Verb>`, labelled composites' `History_*` | every concrete `IChartAction`, every merge-verb constant and every `LabelKey = "…"` literal, via `HistoryActionText.LabelKeyFor` (`HistoryLabelKeys` in `Services/Editing/HistoryLabelTests.cs`) |

**Add a row when you add a computed lookup**, and never delete a key merely
because nothing spells it out.

### Unused keys are a lint (`UI/UnusedResxKeyTests`)

`EveryResxKey_IsReferencedBySomething` fails the default gate for a key in
`Resources.resx` whose name appears as a token in no `.cs` / `.axaml` / `.json`
under any `DotCharter.*` project (tests included; comments are not stripped, so
a comment naming the key keeps it). A key whose name is computed is spelled out
nowhere, so its family is listed in `DynamicPrefixes` (`$"Prefix_{x}"`) or
`DynamicSuffixes` (`key + "_Short"`) in that test, and
`EveryDynamicFamily_StillMatchesAKey` fails for a row that matches nothing.
**Add a row when you add a computed lookup** (next to the
`EveryComputedKeyFamily_IsCompleteInEveryLanguage` row above). The 2026-10-14
sweep removed 127 dead keys (5,401 to 5,274) from all three files. Traps a naive
scan falls into: dotted keys (`Export.Validation.*` are `const string`s) and the
computed families. Delete a key from ALL THREE files with a script that removes
only its `<data>` block: the files have mixed line endings, down to a doubled CR before the LF.
