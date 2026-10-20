# Add a new language

There are two ways, and the first needs no code change.

## Option A: a language pack (no pull request needed)

1. Create a JSON file named after the culture code, for example `fr-FR.json`, in
   the `languages` folder of DotCharter's config directory.
2. Make it one flat object of `"Key": "Value"` pairs. Copy the key names from
   `DotCharter.Localization/Resources.resx`. Keys you leave out fall back to
   English.
3. Add a `"Language_DisplayName"` entry to name the pack in the language picker.
4. Restart the app and pick the language in Settings.

A pack can never replace English, German or Japanese. The loader's rules are in
`DotCharter.Localization/AGENTS.md` and its tests in
`DotCharter.Tests/Localization/CommunityLanguageTests.cs`.

## Option B: a built-in language

Only for a language the project agrees to ship and maintain. Open an issue first.

1. **Create the table.** Copy `DotCharter.Localization/Resources.resx` to
   `DotCharter.Localization/Resources.<culture>.resx` (for French the
   culture code is `fr-FR`) and translate every value. Keep every key and every
   `{0}` placeholder.
2. **Add a name key** `Language_<Name>` to all existing resx files, next to
   `Language_German` and `Language_Japanese`
   ([add-a-localized-string.md](add-a-localized-string.md)).
3. **Register the culture** in `DotCharter.Localization/LocalizationService.cs`: add
   the code to `SupportedCultures` and a `new("fr-FR", "Language_French")` entry in
   `RebuildLanguageOptions`.
4. **Raise the test lists.** `DotCharter.Tests/UI/LocalizationSweepTests.cs` names
   each file in `UntranslatedValues_DoNotGrow` (add a row with a ceiling that fits
   your table), and the other localization tests in `DotCharter.Tests/Localization/`
   have per-language rows; run the whole folder and fix each failure.
5. **Check the layout.** Longer words break layouts. Capture the main screens in the
   new language and look at them before you ask for review.

## Tests

- Option A: a pack file loaded in a test, as in `CommunityLanguageTests.cs`.
- Option B: `ResxFiles_HaveIdenticalKeySets` and
  `TranslatedValues_UseTheSamePlaceholdersAsEnglish` in
  `DotCharter.Tests/UI/LocalizationSweepTests.cs`; then
  `-FilterClass '*Localization*'` and the full gate.

## Example in the code

Japanese is the built-in route in its smallest form:
`DotCharter.Localization/Resources.ja-JP.resx` plus the language-name key in the
other two resx files. Steps 3 and 4 are what the code requires on top of that.
