using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The chord bend / per-string technique target survived exactly one
///     committed edit.
///     <para>
///         <c>SyncChordProperties</c> runs on <c>HistoryChanged</c>, and
///         <c>Chord.CopyFrom</c> — the transfer <c>MutateChordsAction</c> uses —
///         replaces every <c>ChordNote</c> with a clone, so the panel's target is
///         an orphan and the string list has to be rebuilt. It was rebuilt with
///         <c>BendTargetChordNote = ChordBendStrings.FirstOrDefault()</c>, which
///         is the LOWEST string: pick string 4, drop one bend point, and the
///         commit snapped the target back to string 1. The graph blanked and the
///         next point was authored onto the wrong string, so a chord bend on any
///         string but the lowest could not be built at all.
///     </para>
/// </summary>
public sealed class InspectorChordBendTargetTests
{
    private static IReadOnlyList<ChordNote> Strings(params int[] stringIndices) =>
        stringIndices.Select(i => new ChordNote { StringIndex = i, Fret = 5 }).ToArray();

    [Fact]
    public void TheTargetSurvivesARebuild_OnTheSameString()
    {
        var strings = Strings(1, 3, 4);

        var kept = InspectorViewModel.PreserveBendTarget(strings, previousStringIndex: 4);

        Assert.Same(strings[2], kept);
    }

    [Fact]
    public void AFreshChord_OpensOnItsLowestString()
    {
        var strings = Strings(2, 5);

        // No previous target — a different chord was selected, which is fresh
        // intent, not a rebuild of the same one.
        Assert.Same(strings[0], InspectorViewModel.PreserveBendTarget(strings, previousStringIndex: null));
    }

    [Fact]
    public void AStringTheChordNoLongerPlays_FallsBackToTheLowest()
    {
        var strings = Strings(0, 1);

        Assert.Same(strings[0], InspectorViewModel.PreserveBendTarget(strings, previousStringIndex: 4));
    }

    [Fact]
    public void AChordWithNoStrings_HasNoTarget()
    {
        Assert.Null(InspectorViewModel.PreserveBendTarget([], previousStringIndex: 2));
        Assert.Null(InspectorViewModel.PreserveBendTarget([], previousStringIndex: null));
    }

    /// <summary>
    ///     The pure rule above is only worth anything while the sync actually
    ///     asks it — and while the "same chord?" test is REFERENCE identity, not
    ///     value equality: a mutation keeps the <c>Chord</c> instance and swaps
    ///     its notes, and two field-identical chords at one position on two
    ///     difficulty levels are ordinary chart state.
    /// </summary>
    [Fact]
    public void TheSync_AsksThatRule_AndKeysTheChordByReference()
    {
        var sync = BodyOf(InspectorSource(), "private void SyncChordProperties()");

        Assert.Contains("PreserveBendTarget(ChordBendStrings, previousTargetString)", sync);
        Assert.Contains("ReferenceEquals(_bendTargetChord, SelectedChord)", sync);
        Assert.Contains("_bendTargetChord = SelectedChord;", sync);
        Assert.DoesNotContain("BendTargetChordNote = ChordBendStrings.FirstOrDefault();", sync);
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
