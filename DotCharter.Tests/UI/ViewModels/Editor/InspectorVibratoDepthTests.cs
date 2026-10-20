using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Clearing vibrato from the inspector's BULK verbs left the depth behind,
///     and the export reads the depth.
///     <para>
///         <c>SngFromDomainMapper</c> sets the RS vibrato bit from
///         <c>note.Vibrato || note.VibratoStrength &gt; 0</c> — imports arrive
///         at strength 80 — so a sound left at <c>Vibrato=false,
///         VibratoStrength=80</c> still plays vibrato in the psarc. The two
///         single-chip handlers zeroed the depth with the flag;
///         <c>ResetNoteFlags</c> ("Reset") and <c>ApplyNoteValuesToAll</c> wrote
///         <c>n.Vibrato = false</c> alone, so the button said the technique was
///         gone and the exported chart disagreed.
///     </para>
/// </summary>
public sealed class InspectorVibratoDepthTests
{
    [Fact]
    public void ClearingVibrato_ClearsTheDepthWithIt()
    {
        var note = new GuitarNote { Vibrato = true, VibratoStrength = 80 };

        InspectorViewModel.SetVibrato(note, false);

        Assert.False(note.Vibrato);
        Assert.Equal(0, note.VibratoStrength);
    }

    [Fact]
    public void ClearingVibrato_OnAChordNote_Too()
    {
        var chordNote = new ChordNote { StringIndex = 2, Vibrato = true, VibratoStrength = 120 };

        InspectorViewModel.SetVibrato(chordNote, false);

        Assert.False(chordNote.Vibrato);
        Assert.Equal(0, chordNote.VibratoStrength);
    }

    /// <summary>Setting it never INVENTS a depth: RS writes max(strength, 80) on
    /// export, and an imported depth belongs to the charter.</summary>
    [Fact]
    public void SettingVibrato_LeavesAnAuthoredDepthAlone()
    {
        var note = new GuitarNote { Vibrato = false, VibratoStrength = 120 };

        InspectorViewModel.SetVibrato(note, true);

        Assert.True(note.Vibrato);
        Assert.Equal(120, note.VibratoStrength);

        var fresh = new GuitarNote();
        InspectorViewModel.SetVibrato(fresh, true);
        Assert.Equal(0, fresh.VibratoStrength);
    }

    /// <summary>
    ///     The mechanical half: NOTHING in the inspector may write the vibrato
    ///     flag except that one writer. This is the shape the bug had — four
    ///     write sites, two of which folded the depth in — so the guard is that
    ///     there is exactly one.
    /// </summary>
    [Fact]
    public void NoInspectorPartial_WritesTheVibratoFlagOutsideThatWriter()
    {
        var src = InspectorSource();
        var writer = BodyOf(src, "internal static void SetVibrato(IGuitarSound sound, bool on)");
        Assert.Contains("sound.Vibrato = on;", writer);
        Assert.Contains("if (!on) sound.VibratoStrength = 0;", writer);

        var offenders = Regex.Matches(src.Replace(writer, ""), @"\.Vibrato\s*=")
            .Select(m => m.Value)
            .ToList();
        Assert.True(offenders.Count == 0,
            $"The inspector writes .Vibrato outside SetVibrato ({offenders.Count} site(s)) — " +
            "route it through the writer so the depth cannot survive the flag.");
    }

    private static string InspectorSource()
    {
        var dir = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor"));
        var files = UiSourceTree.GetFiles(dir, "InspectorViewModel*.cs");
        Assert.NotEmpty(files);
        return Regex.Replace(string.Join("\n", files.Select(UiSourceTree.ReadAllText)), @"^\s*//.*$", "",
            RegexOptions.Multiline);
    }

    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"InspectorViewModel no longer declares '{signature}' — repoint this lint.");

        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
