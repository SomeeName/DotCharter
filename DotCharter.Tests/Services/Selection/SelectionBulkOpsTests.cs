using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Selection;

/// <summary>
///     Pins <see cref="SelectionBulkOps" /> — the bulk path every multi-item
///     selection gesture (marquee release, shift-range, select-alike, pattern
///     select, "select by query", FHP generation) goes through.
/// </summary>
/// <remarks>
///     <para>
///         <b>Structural, never timed.</b> The fault was O(N²): each
///         <c>Selected*.Add</c> raises <c>CollectionChanged</c>, and every
///         subscriber re-reads the WHOLE selection —
///         <c>SelectionService</c> re-derives its scalar and raises
///         <c>SelectionChanged</c>, and <c>InspectorViewModel</c> (a DI singleton,
///         alive from boot) answers with a <c>ToList()</c> plus seven full scans.
///         So what is asserted here is the two things that can regress silently:
///         the number of whole-selection rebuilds a gesture costs, and that the
///         resulting selection is item-for-item what the loop produced.
///     </para>
///     <para>
///         The equivalence test keeps the REPLACED loop verbatim as the spec, the
///         same shape the server query rewrite used.
///     </para>
/// </remarks>
public sealed class SelectionBulkOpsTests
{
    private static GuitarNote Note(int beat, int s = 0) =>
        new() { Position = new FractionalPosition(beat), StringIndex = s };

    /// <summary>The loop this helper replaced, kept verbatim as the specification
    /// the bulk path has to agree with.</summary>
    private static void TheLoopItReplaced<T>(ObservableCollection<T> target, IEnumerable<T> additions)
    {
        foreach (var item in additions)
        {
            if (!target.Contains(item))
                target.Add(item);
        }
    }

    [Fact]
    public void AddRange_agreesWithTheOneAtATimeLoop_overOverlappingAndRepeatedSources()
    {
        var rng = new Random(20260803);
        var pool = Enumerable.Range(0, 40).Select(i => Note(i)).ToArray();

        for (var trial = 0; trial < 200; trial++)
        {
            var seed = pool.Where(_ => rng.Next(3) == 0).ToList();
            // Deliberately allowed to repeat itself and to re-offer seeded items:
            // a marquee over an already-selected run does exactly that.
            var additions = Enumerable.Range(0, rng.Next(0, 25))
                .Select(_ => pool[rng.Next(pool.Length)])
                .ToList();

            var expected = new ObservableCollection<GuitarNote>(seed);
            TheLoopItReplaced(expected, additions);

            var actual = new BulkObservableCollection<GuitarNote>();
            foreach (var n in seed) actual.Add(n);
            var added = SelectionBulkOps.AddRange(actual, additions);

            Assert.Equal(expected, actual);
            Assert.Equal(expected.Count - seed.Count, added);
        }
    }

    [Fact]
    public void AddRange_rebuildsTheSelectionOnce_whereTheLoopRebuiltItPerItem()
    {
        // A subscriber that re-reads the whole selection is not a contrivance:
        // it is exactly what InspectorViewModel.SyncNoteProperties does, and it
        // is subscribed from boot whether or not the pane is shown.
        var notes = Enumerable.Range(0, 500).Select(i => Note(i)).ToList();

        var loopService = new SelectionService();
        var loopRebuilds = 0;
        loopService.SelectionChanged += (_, _) =>
        {
            loopRebuilds++;
            _ = loopService.SelectedNotes.ToList();
        };
        TheLoopItReplaced(loopService.SelectedNotes, notes);

        var bulkService = new SelectionService();
        var bulkRebuilds = 0;
        bulkService.SelectionChanged += (_, _) =>
        {
            bulkRebuilds++;
            _ = bulkService.SelectedNotes.ToList();
        };
        SelectionBulkOps.AddRange(bulkService.SelectedNotes, notes);

        Assert.Equal(notes.Count, loopRebuilds);
        Assert.Equal(1, bulkRebuilds);
        Assert.Equal(notes, bulkService.SelectedNotes);
    }

    /// <summary>
    ///     The allocation half of the same claim, metered on THIS thread so the
    ///     rest of the suite cannot leak into it. Not a wall-clock assertion — the
    ///     shape it pins is that the transient cost stops growing with the square
    ///     of the selection.
    /// </summary>
    [Fact]
    public void AddRange_doesNotAllocateASnapshotPerItem()
    {
        var notes = Enumerable.Range(0, 1_000).Select(i => Note(i)).ToList();

        // Through AllocationProbe, so BOTH sides are the cheapest of several
        // warmed batches rather than one window each. A single window is decided
        // by whatever one-off lands in it — tier-0 code does not get the escape
        // analysis tier-1 does, and promotion arrives later on a loaded box — so
        // two single windows made this a comparison of two coin flips. A fresh
        // SelectionService per batch (built outside the window) is what makes the
        // batch repeatable: re-adding notes a service already holds is a no-op.
        var loopBytes = AllocationProbe.MeasureCheapest(() =>
        {
            var service = new SelectionService();
            service.SelectionChanged += (_, _) => _ = service.SelectedNotes.ToList();
            return () => TheLoopItReplaced(service.SelectedNotes, notes);
        });

        var bulkBytes = AllocationProbe.MeasureCheapest(() =>
        {
            var service = new SelectionService();
            service.SelectionChanged += (_, _) => _ = service.SelectedNotes.ToList();
            return () => SelectionBulkOps.AddRange(service.SelectedNotes, notes);
        });

        Assert.True(bulkBytes * 20 < loopBytes,
            $"bulk selection allocated {bulkBytes} bytes against the loop's {loopBytes} — " +
            "expected at least a 20x reduction over 1,000 items.");
    }

    [Fact]
    public void AddRange_withNothingNew_changesNothingAndRaisesNothing()
    {
        var service = new SelectionService();
        var notes = Enumerable.Range(0, 5).Select(i => Note(i)).ToList();
        SelectionBulkOps.AddRange(service.SelectedNotes, notes);

        var raises = 0;
        service.SelectionChanged += (_, _) => raises++;

        var added = SelectionBulkOps.AddRange(service.SelectedNotes, notes);

        Assert.Equal(0, added);
        Assert.Equal(0, raises);
        Assert.Equal(notes, service.SelectedNotes);
    }

    [Fact]
    public void ReplaceWith_holdsExactlyWhatItWasGiven_inOneRaise()
    {
        var service = new SelectionService();
        SelectionBulkOps.AddRange(service.SelectedNotes, Enumerable.Range(0, 50).Select(i => Note(i)));

        var raises = 0;
        service.SelectionChanged += (_, _) => raises++;

        var replacement = Enumerable.Range(100, 3).Select(i => Note(i)).ToList();
        SelectionBulkOps.ReplaceWith(service.SelectedNotes, replacement);

        Assert.Equal(replacement, service.SelectedNotes);
        Assert.Equal(1, raises);
    }

    /// <summary>
    ///     The rule, made mechanical: no gesture in the editor's tools or
    ///     coordinators may fill a selection collection one item at a time inside
    ///     a loop. It has been reintroduced three times, in three separate files.
    /// </summary>
    [Fact]
    public void NoToolOrCoordinator_FillsASelectionInsideALoop()
    {
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var file in GestureSourceFiles())
        {
            var lines = UiSourceTree.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(StripComment(lines[i]), @"Selected\w*\.Add\(")) continue;
                scanned++;
                if (LoopEncloses(lines, i))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        // Vacuity keeper: these files DO still add to selections (single-item
        // click targets), so a scan that finds nothing at all means the pattern
        // was renamed and this guard stopped guarding.
        Assert.True(scanned >= 4, $"only {scanned} selection adds found — the scan no longer matches the code.");
        Assert.True(offenders.Count == 0,
            "A selection collection is filled one item at a time inside a loop. Every Add raises " +
            "CollectionChanged, and the inspector answers each one by rebuilding its whole view of " +
            "the selection — use SelectionBulkOps.AddRange/ReplaceWith instead:\n  " +
            string.Join("\n  ", offenders));
    }

    private static IEnumerable<string> GestureSourceFiles()
    {
        var root = TestPaths.RepoRoot();
        // The tools, and the coordinators that left DotCharter.UI/Coordinators for
        // Services (the editing verbs at the top of Editing/, chart maintenance,
        // the project session, the audio coordinators, crash recovery).
        foreach (var (dir, depth) in new[]
                 {
                     (UiSourceTree.Resolve(Path.Combine(root, "DotCharter.UI", "Input", "Tools")), SearchOption.AllDirectories),
                     (Path.Combine(root, "DotCharter.Services", "Editing"), SearchOption.TopDirectoryOnly),
                     (Path.Combine(root, "DotCharter.Services", "Editing", "Maintenance"), SearchOption.AllDirectories),
                     (Path.Combine(root, "DotCharter.Services", "Session"), SearchOption.AllDirectories),
                     (Path.Combine(root, "DotCharter.Services", "Audio", "Coordination"), SearchOption.AllDirectories),
                     (Path.Combine(root, "DotCharter.Services", "Infrastructure", "Recovery"), SearchOption.TopDirectoryOnly)
                 })
        {
            Assert.True(Directory.Exists(dir), $"missing source directory {dir}");
            foreach (var file in UiSourceTree.EnumerateFiles(dir, "*.cs", depth))
                yield return file;
        }
    }

    /// <summary>
    ///     Walks the statement chain above <paramref name="index" /> — each step
    ///     out is the first preceding line indented LESS than the one before it —
    ///     and reports whether any link in it is a loop header. A bare <c>{</c> is
    ///     transparent, so a braced loop body is seen through to its header.
    /// </summary>
    private static bool LoopEncloses(IReadOnlyList<string> lines, int index)
    {
        if (IsLoopHeader(StripComment(lines[index]).TrimStart())) return true;

        var indent = IndentOf(lines[index]);
        for (var i = index - 1; i >= 0; i--)
        {
            var code = StripComment(lines[i]);
            var trimmed = code.Trim();
            if (trimmed.Length == 0) continue;

            var lineIndent = IndentOf(code);
            if (lineIndent >= indent) return false;

            if (trimmed == "{")
            {
                // The header sits at the brace's own indentation.
                indent = lineIndent + 1;
                continue;
            }

            indent = lineIndent;
            if (IsLoopHeader(trimmed)) return true;
            if (!IsGuard(trimmed)) return false;
        }

        return false;
    }

    private static bool IsLoopHeader(string trimmed) =>
        Regex.IsMatch(trimmed, @"^(foreach|for|while)\s*\(") || trimmed.StartsWith("do", StringComparison.Ordinal);

    private static bool IsGuard(string trimmed) =>
        Regex.IsMatch(trimmed, @"^(if|else|try|catch|finally|using|lock|switch|case|default:)\b") ||
        trimmed.StartsWith('}');

    private static string StripComment(string line)
    {
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    private static int IndentOf(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return i;
    }
}
