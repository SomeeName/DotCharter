# Add a localized string

Every piece of user-facing text is a key in three resx files: English
(`Resources.resx`), German and Japanese. All three must carry the same key names.
The files are about 600 KB each, so search them; never open them whole.

## Steps

1. **Check the key does not exist.** From the repository root:

   ```powershell
   Select-String -Path DotCharter.Localization/Resources.resx -Pattern 'name="Menu_Export'
   ```

2. **Pick a name** in the same family as its neighbours: `Menu_*`, `Status_*`,
   `History_*`, `PlayMode_*`, and so on. A value that needs data takes
   placeholders, `{0}` and `{1}`, and all three languages must use the same ones.
3. **Add the key to all three files**, next to related keys, in this form:

   ```xml
   <data name="Status_HandShapesGenerated" xml:space="preserve"><value>Added {0} hand shapes over {1} chords.</value></data>
   ```

   - `DotCharter.Localization/Resources.resx` (English)
   - `DotCharter.Localization/Resources.de-DE.resx`
   - `DotCharter.Localization/Resources.ja-JP.resx`

   Translate it. An untranslated copy of the English text is counted, and the
   count may not grow (see the tests below).
4. **Use the key.**
   - In XAML: `{localization:Loc Status_HandShapesGenerated}`.
   - In code: take `ILocalizationService` through the constructor and call
     `_loc["Key"]` or `_loc.Format("Key", args)`. Do not read
     `LocalizationService.Instance` in new code.
5. **A count that can be one or many** gets two keys (one and many), the way
   `Status_HandShapesGeneratedOne` and `Status_HandShapesGenerated` do.

## Tests that guard this

All in `DotCharter.Tests/UI/LocalizationSweepTests.cs` unless noted:

- `ResxFiles_HaveIdenticalKeySets`: the three files carry the same keys.
- `AllXamlLocReferences_ResolveToADefinedKey` and
  `AllCodeLocalizationLookups_ResolveToADefinedKey`: no key is used but undefined.
- `ResxFile_DefinesEachKeyExactlyOnce`.
- `TranslatedValues_UseTheSamePlaceholdersAsEnglish`.
- `UntranslatedValues_DoNotGrow`: German and Japanese copies of English text have
  a ceiling that only goes down.
- `Views_HaveNoHardcodedUserFacingStrings`: no literal text in a view.
- Copy style: `DotCharter.Tests/Localization/CopyCaseLintTests.cs` and
  `CopyGrammarLintTests.cs`.

Run them with `-FilterClass '*LocalizationSweepTests'`.

## Example in the code

Take any `{localization:Loc ...}` key used by an inspector view under
`DotCharter.UI/Views/` and search for it: it sits in all three `Resources*.resx`
files, which is the whole pattern.
