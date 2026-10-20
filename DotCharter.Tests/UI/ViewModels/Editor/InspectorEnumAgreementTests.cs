using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     "Apply current values to all selected" documents itself as treating an
///     INDETERMINATE slot as "leave alone", and it honoured that for the seven
///     binary techniques and for none of the four enum pickers.
///     <para>
///         Mute / HOPO / Harmonic / Picking render as <c>Seg</c>, which has no
///         indeterminate state, so <c>SyncNoteProperties</c> shows the FIRST
///         selected sound's value. The verb then wrote those four back
///         unconditionally, so on a selection of eight where only the first is
///         palm-muted, one click palm-muted all eight — beside seven lines that
///         carefully skipped every mixed chip.
///     </para>
/// </summary>
public sealed class InspectorEnumAgreementTests
{
    private static GuitarNote Note(MuteType mute = MuteType.None, HopoType hopo = HopoType.None,
        HarmonicType harmonic = HarmonicType.None, BassPickingType picking = BassPickingType.None) =>
        new() { Mute = mute, Hopo = hopo, Harmonic = harmonic, BassPicking = picking };

    [Fact]
    public void AUnanimousSelection_AgreesOnEveryPicker()
    {
        var agreed = InspectorViewModel.NoteEnumAgreement.Over(
        [
            Note(MuteType.Palm, HopoType.HammerOn, HarmonicType.Normal, BassPickingType.Slap),
            Note(MuteType.Palm, HopoType.HammerOn, HarmonicType.Normal, BassPickingType.Slap)
        ]);

        Assert.Equal((int)MuteType.Palm, agreed.Mute);
        Assert.Equal((int)HopoType.HammerOn, agreed.Hopo);
        Assert.Equal((int)HarmonicType.Normal, agreed.Harmonic);
        Assert.Equal((int)BassPickingType.Slap, agreed.BassPicking);
    }

    /// <summary>
    ///     The bug, stated: the FIRST sound's value is what the picker shows, and
    ///     it must not become the answer for the rest.
    /// </summary>
    [Fact]
    public void OneDisagreeingSound_LatchesThatPickerToMixed()
    {
        var agreed = InspectorViewModel.NoteEnumAgreement.Over(
        [
            Note(MuteType.Palm, HopoType.HammerOn),
            Note(hopo: HopoType.HammerOn),
            Note(MuteType.Palm, HopoType.HammerOn)
        ]);

        Assert.Null(agreed.Mute);
        // The pickers latch INDEPENDENTLY — a mixed mute must not suppress a
        // HOPO the whole selection does agree on.
        Assert.Equal((int)HopoType.HammerOn, agreed.Hopo);
    }

    [Fact]
    public void ASingleSound_AgreesWithItself()
    {
        var agreed = InspectorViewModel.NoteEnumAgreement.Over([Note(MuteType.Full)]);
        Assert.Equal((int)MuteType.Full, agreed.Mute);
    }

    /// <summary>Nothing selected leaves every picker mixed, so the verb writes
    /// none of them — the same answer <c>NoteFlagAgreement</c> gives.</summary>
    [Fact]
    public void AnEmptySelection_AgreesOnNothing()
    {
        var agreed = InspectorViewModel.NoteEnumAgreement.Over([]);
        Assert.Null(agreed.Mute);
        Assert.Null(agreed.Hopo);
        Assert.Null(agreed.Harmonic);
        Assert.Null(agreed.BassPicking);
    }

    /// <summary>
    ///     The mechanical half: the verb may not write an enum without asking.
    ///     This is the shape the bug had — four unguarded assignments in a method
    ///     whose other seven lines were all guarded.
    /// </summary>
    [Fact]
    public void ApplyNoteValuesToAll_WritesNoPickerItDidNotAgreeOn()
    {
        var body = MethodBody(
            Read("DotCharter.UI/ViewModels/Editor/InspectorViewModel.BulkEdits.cs"),
            "private void ApplyNoteValuesToAll()");

        foreach (var (field, cast) in new[]
                 {
                     ("Mute", "MuteType"), ("Hopo", "HopoType"),
                     ("Harmonic", "HarmonicType"), ("BassPicking", "BassPickingType")
                 })
        {
            Assert.Matches(new Regex($@"if\s*\(enums\.{field}\.HasValue\)\s*n\.{field}\s*=\s*\({cast}\)"), body);

            // …and no unguarded write of the same field survives beside it.
            var unguarded = Regex.Matches(body, $@"n\.{field}\s*=\s*\({cast}\)Note{field};");
            Assert.True(unguarded.Count == 0,
                $"ApplyNoteValuesToAll still broadcasts the first sound's {field} over the selection");
        }
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' has moved or been renamed — repoint this lint.");
        var next = source.IndexOf("\n    [RelayCommand]", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static string Read(string relative) =>
        UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
