using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DotCharter.UI.ViewModels.Panels;

// Guitar-Pro-style bend editor view model. The UI draws a graph where X is
// "time along the note's sustain" (TimeSlotCount equally-spaced slots,
// 0..TimeSlotCount inclusive) and Y is "semitones pitched up"
// (0..MaxStep, quarter-step grid).
//
// We decouple the editor's control-point representation from the domain
// BendValue list:
//
//   * Internally the editor works with (TimeSlot, Step) pairs where
//     TimeSlot is an integer 0..TimeSlotCount. This keeps all graph
//     interaction trivial — click, drag, snap — without having to
//     manufacture FractionalPosition values mid-gesture.
//
//   * When the user commits we map TimeSlot to a real FractionalPosition
//     using FractionalPosition arithmetic:
//         position = start + (end - start) * Fraction(slot, TimeSlotCount)
//     giving precise, reversible placement along the note's sustain. The
//     scheduler and renderer both interpolate Step between consecutive
//     BendValues by time, so preserving the slot as a real position is
//     essential to preserving the bend's shape on round-trip.
//
// Loading projects existing BendValues back onto the slot grid by the same
// rule: the relative offset of each value's position between start and end
// is mapped to the nearest slot. Duplicate slots (multiple values landing
// on the same slot after rounding) keep the latest Step seen.
//
// The preset buttons seed the same control-point set a Guitar Pro user would
// get by clicking those presets.
public sealed partial class BendEditorViewModel : ObservableObject
{
    public enum BendPreset
    {
        None,
        Bend,
        Release,
        BendRelease,
        Hold,
        Prebend,
        PrebendBend,
        PrebendRelease
    }

    public const int TimeSlotCount = 12; // matches GP's 12-slot grid
    public const double MaxStep = 6.0; // 3 whole steps (6 semitones)
    public const double StepQuantum = 0.25; // quarter-step snapping on Y
    public const double FineStepQuantum = 0.125; // Ctrl/Shift fine snap (eighth)

    private readonly Action _commit;
    private readonly Func<FractionalPosition> _getEndPosition;
    private readonly Func<FractionalPosition> _getStartPosition;
    private readonly Func<IReadOnlyList<BendValue>> _read;
    private readonly Action<IReadOnlyList<BendValue>> _write;

    // Which preset (if any) matches the current set of control points.
    // Lets the UI highlight the active preset button.
    [ObservableProperty] private BendPreset _activePreset;

    [ObservableProperty] private ObservableCollection<ControlPoint> _points = [];

    // Last point the user interacted with. Persists across point moves so
    // keyboard nudging + the "which point am I editing" highlight are
    // always in sync. Nulled when the selection is explicitly cleared or
    // the Points collection is rebuilt wholesale.
    [ObservableProperty] private ControlPoint? _selectedPoint;

    private bool _suppressCommit;
    private int _transactionDepth;
    private bool _transactionDirty;

    public BendEditorViewModel(
        Func<IReadOnlyList<BendValue>> read,
        Action<IReadOnlyList<BendValue>> write,
        Func<FractionalPosition> getStartPosition,
        Func<FractionalPosition> getEndPosition,
        Action commit)
    {
        _read = read;
        _write = write;
        _getStartPosition = getStartPosition;
        _getEndPosition = getEndPosition;
        _commit = commit;

        ReloadFromDomain();
    }

    // Signals to the view that the underlying data changed and the canvas
    // should be invalidated. Raised any time Points is replaced wholesale
    // or a preset is applied.
    public event Action? GraphChanged;

    // Called externally when the selection changes (different note / chord
    // string). Wipes the current control points and re-reads the domain.
    public void ReloadFromDomain()
    {
        _suppressCommit = true;
        try
        {
            Points = new ObservableCollection<ControlPoint>(ProjectBendValuesToPoints(_read()));
            SelectedPoint = null;
        }
        finally
        {
            _suppressCommit = false;
        }

        RefreshActivePreset();
        GraphChanged?.Invoke();
    }

    // --- Drag/edit transaction ---
    //
    // Each pointer-drag gesture wraps its mutations between BeginTransaction
    // / EndTransaction so the history service only sees one coalesced
    // change. Without this, every pointer-move commits, HistoryChanged
    // fires, ReloadFromDomain replaces Points, and the drag reference
    // becomes an orphan — the user sees the point "step" at most once per
    // gesture instead of tracking the cursor.

    public void BeginTransaction() => _transactionDepth++;

    public void EndTransaction()
    {
        if (_transactionDepth <= 0) return;
        _transactionDepth--;
        if (_transactionDepth == 0 && _transactionDirty)
        {
            _transactionDirty = false;
            CommitNow();
        }
    }

    // Project existing BendValues onto our slot grid using the relative
    // offset of each value's position between start and end. Duplicate
    // slots (after rounding) collapse to the latest value seen, which
    // preserves the bend's shape when it was authored in-editor.
    private IEnumerable<ControlPoint> ProjectBendValuesToPoints(IReadOnlyList<BendValue> values)
    {
        if (values.Count == 0) yield break;

        var start = _getStartPosition();
        var end = _getEndPosition();
        var span = end.DoubleValue - start.DoubleValue;

        var seenSlots = new HashSet<int>();
        var ordered = values
            .Select(v =>
            {
                double t;
                if (span <= 0) t = 0;
                else t = (v.Position.DoubleValue - start.DoubleValue) / span;

                var slot = (int)Math.Round(Math.Clamp(t, 0.0, 1.0) * TimeSlotCount);
                return (slot, step: Math.Clamp(v.Step, 0.0, MaxStep));
            })
            .ToList();

        // Walk in reverse so "latest wins" when two source values collapse
        // into the same slot, then reverse the result to keep left-to-right
        // order.
        var result = new List<ControlPoint>(ordered.Count);
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var (slot, step) = ordered[i];
            if (seenSlots.Add(slot)) result.Add(new ControlPoint(slot, step));
        }

        result.Reverse();
        result.Sort((a, b) => a.TimeSlot.CompareTo(b.TimeSlot));

        foreach (var p in result) yield return p;
    }

    // --- Editing operations used by the view ---

    public ControlPoint AddOrMovePoint(int slot, double step, bool freeStep = false)
    {
        slot = Math.Clamp(slot, 0, TimeSlotCount);
        step = QuantizeStep(step, freeStep);

        var existing = Points.FirstOrDefault(p => p.TimeSlot == slot);
        if (existing != null)
        {
            existing.Step = step;
            SelectedPoint = existing;
            Commit();
            return existing;
        }

        var created = new ControlPoint(slot, step);
        Points.Add(created);
        ResortInPlace();
        SelectedPoint = created;
        Commit();
        return created;
    }

    public void MovePoint(ControlPoint point, int slot, double step, bool freeStep = false)
    {
        slot = Math.Clamp(slot, 0, TimeSlotCount);
        step = QuantizeStep(step, freeStep);

        // If another point occupies the target slot, keep the X fixed and
        // only update the step. This lets the user refine vertical
        // position without the old "snap to the nearest free slot" jump
        // that made dragging through a neighbour feel broken.
        var blocked = Points.Any(p => p != point && p.TimeSlot == slot);
        if (blocked) slot = point.TimeSlot;

        if (point.TimeSlot != slot) point.TimeSlot = slot;
        if (Math.Abs(point.Step - step) > 1e-9) point.Step = step;

        ResortInPlace();
        SelectedPoint = point;
        Commit();
    }

    public void RemovePoint(ControlPoint point)
    {
        if (!Points.Remove(point)) return;
        if (ReferenceEquals(SelectedPoint, point)) SelectedPoint = null;
        Commit();
    }

    // Keyboard nudge helpers. dSlot moves left/right along the grid,
    // dStep in semitones. freeStep bypasses quarter-step snap for
    // fine-grain adjustments.
    public void NudgeSelected(int dSlot, double dStep, bool freeStep = false)
    {
        var sel = SelectedPoint;
        if (sel == null) return;
        MovePoint(sel, sel.TimeSlot + dSlot, sel.Step + dStep, freeStep);
    }

    public void DeleteSelected()
    {
        var sel = SelectedPoint;
        if (sel != null) RemovePoint(sel);
    }

    public void ClearSelection() => SelectedPoint = null;

    // Human-readable label for a step value. Domain Step is in semitones
    // (1.0 = half-step = ½ tone, 2.0 = full tone, 3.0 = 1½ tones) so we
    // display it as tones to match the timeline's bend annotations.
    public static string StepLabel(double step)
    {
        // Quantize to eighth-steps in semitones (= sixteenth-tones, finest grid).
        var q = Math.Round(step * 8) / 8.0;
        if (Math.Abs(q - 0.0) < 1e-6) return "0";
        if (Math.Abs(q - 0.25) < 1e-6) return "⅛";
        if (Math.Abs(q - 0.5) < 1e-6) return "¼";
        if (Math.Abs(q - 0.75) < 1e-6) return "⅜";
        if (Math.Abs(q - 1.0) < 1e-6) return "½";
        if (Math.Abs(q - 1.25) < 1e-6) return "⅝";
        if (Math.Abs(q - 1.5) < 1e-6) return "¾";
        if (Math.Abs(q - 1.75) < 1e-6) return "⅞";
        if (Math.Abs(q - 2.0) < 1e-6) return "Full";
        if (Math.Abs(q - 2.25) < 1e-6) return "1⅛";
        if (Math.Abs(q - 2.5) < 1e-6) return "1¼";
        if (Math.Abs(q - 2.75) < 1e-6) return "1⅜";
        if (Math.Abs(q - 3.0) < 1e-6) return "1½";
        if (Math.Abs(q - 3.25) < 1e-6) return "1⅝";
        if (Math.Abs(q - 3.5) < 1e-6) return "1¾";
        if (Math.Abs(q - 3.75) < 1e-6) return "1⅞";
        if (Math.Abs(q - 4.0) < 1e-6) return "2";
        if (Math.Abs(q - 4.5) < 1e-6) return "2¼";
        if (Math.Abs(q - 5.0) < 1e-6) return "2½";
        if (Math.Abs(q - 5.5) < 1e-6) return "2¾";
        if (Math.Abs(q - 6.0) < 1e-6) return "3";
        // Fractional fallback in tones.
        return (step / 2.0).ToString("0.##");
    }

    [RelayCommand]
    private void Reset()
    {
        Points.Clear();
        SelectedPoint = null;
        Commit();
        RefreshActivePreset();
        GraphChanged?.Invoke();
    }

    // --- Preset shapes (seven standard Guitar Pro bend shapes) ---
    //
    // Each preset fully replaces the current point set. Step values (0, 1.0
    // "full", 1.5, etc.) match GP's defaults.

    private void ApplyPreset(IEnumerable<ControlPoint> points, BendPreset preset)
    {
        Points = new ObservableCollection<ControlPoint>(points);
        SelectedPoint = null;
        Commit();
        ActivePreset = preset;
        GraphChanged?.Invoke();
    }

    [RelayCommand]
    private void PresetBend()
    {
        ApplyPreset(new[]
        {
            new ControlPoint(0, 0),
            new ControlPoint(3, 2.0),
            new ControlPoint(12, 2.0)
        }, BendPreset.Bend);
    }

    [RelayCommand]
    private void PresetRelease()
    {
        ApplyPreset(new[]
        {
            new ControlPoint(0, 2.0),
            new ControlPoint(9, 0),
            new ControlPoint(12, 0)
        }, BendPreset.Release);
    }

    [RelayCommand]
    private void PresetBendRelease()
    {
        ApplyPreset(new[]
        {
            new ControlPoint(0, 0),
            new ControlPoint(4, 2.0),
            new ControlPoint(8, 2.0),
            new ControlPoint(12, 0)
        }, BendPreset.BendRelease);
    }

    [RelayCommand]
    private void PresetHold()
    {
        ApplyPreset(new[]
        {
            new ControlPoint(0, 2.0),
            new ControlPoint(12, 2.0)
        }, BendPreset.Hold);
    }

    [RelayCommand]
    private void PresetPrebend()
    {
        ApplyPreset(new[]
        {
            new ControlPoint(0, 2.0),
            new ControlPoint(12, 2.0)
        }, BendPreset.Prebend);
    }

    [RelayCommand]
    private void PresetPrebendBend()
    {
        ApplyPreset(new[]
        {
            new ControlPoint(0, 2.0),
            new ControlPoint(5, 3.0),
            new ControlPoint(12, 3.0)
        }, BendPreset.PrebendBend);
    }

    [RelayCommand]
    private void PresetPrebendRelease()
    {
        ApplyPreset(new[]
        {
            new ControlPoint(0, 2.0),
            new ControlPoint(10, 0),
            new ControlPoint(12, 0)
        }, BendPreset.PrebendRelease);
    }

    // --- Commit / persistence plumbing ---

    // Turn the current control-point set into domain BendValues and push
    // through the supplied writer (which in turn calls the history service
    // via the inspector's ApplyNoteMutation / ApplyChordMutation path).
    //
    // Each slot maps to a precise FractionalPosition via
    //     start + (end - start) * Fraction(slot, TimeSlotCount)
    // so the round-trip through the domain preserves the bend's shape.
    private void Commit()
    {
        if (_suppressCommit) return;

        if (_transactionDepth > 0)
        {
            _transactionDirty = true;
            RefreshActivePreset();
            return;
        }

        CommitNow();
    }

    private void CommitNow()
    {
        if (_suppressCommit) return;

        var start = _getStartPosition();
        var end = _getEndPosition();
        var delta = end - start;

        var list = new List<BendValue>(Points.Count);
        foreach (var p in Points.OrderBy(pp => pp.TimeSlot))
        {
            FractionalPosition position;
            if (p.TimeSlot <= 0) position = start;
            else if (p.TimeSlot >= TimeSlotCount) position = end;
            else position = start + delta * new Fraction(p.TimeSlot, TimeSlotCount);

            list.Add(new BendValue(position, Math.Clamp(p.Step, 0.0, MaxStep)));
        }

        _write(list);
        _commit();
        RefreshActivePreset();
    }

    private void RefreshActivePreset() => ActivePreset = DetectPreset(Points);

    private static BendPreset DetectPreset(IReadOnlyList<ControlPoint> pts)
    {
        static bool Match(ControlPoint p, int slot, double step) => p.TimeSlot == slot && Math.Abs(p.Step - step) < 1e-6;

        switch (pts.Count)
        {
            case 2 when Match(pts[0], 0, 2.0) && Match(pts[1], 12, 2.0):
                return BendPreset.Hold; // Also matches Prebend visually
            case 3 when Match(pts[0], 0, 0) && Match(pts[1], 3, 2.0) && Match(pts[2], 12, 2.0):
                return BendPreset.Bend;
            case 3 when Match(pts[0], 0, 2.0) && Match(pts[1], 9, 0) && Match(pts[2], 12, 0):
                return BendPreset.Release;
            case 3 when Match(pts[0], 0, 2.0) && Match(pts[1], 5, 3.0) && Match(pts[2], 12, 3.0):
                return BendPreset.PrebendBend;
            case 3 when Match(pts[0], 0, 2.0) && Match(pts[1], 10, 0) && Match(pts[2], 12, 0):
                return BendPreset.PrebendRelease;
            case 4 when Match(pts[0], 0, 0) && Match(pts[1], 4, 2.0) && Match(pts[2], 8, 2.0) && Match(pts[3], 12, 0):
                return BendPreset.BendRelease;
            default:
                return BendPreset.None;
        }
    }

    private void ResortInPlace()
    {
        // Keep Points ordered by TimeSlot so the graph draws left to right.
        // Simple selection-style reorder via ObservableCollection.Move —
        // point counts are small (≤ TimeSlotCount + 1) so an O(n²) pass is
        // fine and keeps existing item references stable for the UI.
        for (var i = 0; i < Points.Count - 1; i++)
        {
            var minIdx = i;
            for (var j = i + 1; j < Points.Count; j++)
            {
                if (Points[j].TimeSlot < Points[minIdx].TimeSlot)
                    minIdx = j;
            }

            if (minIdx != i) Points.Move(minIdx, i);
        }
    }

    private static double QuantizeStep(double v, bool freeStep = false)
    {
        var clamped = Math.Clamp(v, 0.0, MaxStep);
        if (freeStep) return clamped;
        return Math.Round(clamped / StepQuantum) * StepQuantum;
    }

    public sealed partial class ControlPoint : ObservableObject
    {
        [ObservableProperty] private double _step;
        [ObservableProperty] private int _timeSlot;

        public ControlPoint(int slot, double step)
        {
            _timeSlot = slot;
            _step = step;
        }
    }
}
