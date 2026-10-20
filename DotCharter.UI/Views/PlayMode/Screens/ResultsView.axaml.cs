using System.Diagnostics;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.UI.Views.PlayMode.Screens;

/// <summary>
///     Play Mode's Results screen. Its code-behind is the results REVEAL (0.15.5 B3):
///     staged score count-up → grade stamp → FC badge → record flash, on the four
///     controls it animates. <see cref="PlayModeView" /> decides WHEN — it starts the
///     reveal as <see cref="PlayModeViewModel.IsResultsScreen" /> turns true, cancels
///     it as the screen is left and skips it on any key or click (the reduced-motion
///     escape) — and this view does the HOW.
/// </summary>
public partial class ResultsView : UserControl
{
    private static readonly TransformOperations ScaleUp = TransformOperations.Parse("scale(1.6)");
    private static readonly TransformOperations ScaleNormal = TransformOperations.Parse("scale(1)");

    private CancellationTokenSource? _revealCts;

    public ResultsView() => InitializeComponent();

    internal void StartReveal(PlayModeViewModel vm)
    {
        CancelReveal();
        var cts = new CancellationTokenSource();
        _revealCts = cts;
        _ = RunRevealAsync(vm, cts.Token);
    }

    internal void CancelReveal()
    {
        _revealCts?.Cancel();
        _revealCts = null;
    }

    /// <summary>Skips a running reveal straight to the final state; no-op when
    /// none is running (so ordinary input on the results screen is free).</summary>
    internal void SkipReveal(PlayModeViewModel? vm)
    {
        if (_revealCts == null) return;
        CancelReveal();
        ApplyFinalRevealState(vm);
    }

    private async Task RunRevealAsync(PlayModeViewModel vm, CancellationToken ct)
    {
        try
        {
            // Stage 0 — hide the late-stage elements instantly (transitions
            // detached), so only transform/opacity ever animate below.
            GradeStamp.Transitions = null;
            NewRecordFlash.Transitions = null;
            FcStamp.Transitions = null;
            GradeStamp.Opacity = 0;
            GradeStamp.RenderTransform = ScaleUp;
            FcStamp.Opacity = 0;
            FcStamp.RenderTransform = ScaleUp;
            NewRecordFlash.Opacity = 0;

            // Stage 1 — score counts up (eased cubic-out, ~60 Hz text ticks;
            // plain UI-thread text updates, no layout animation).
            var target = vm.RunResults.ResultScoreValue;
            const int countMs = 700;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < countMs)
            {
                ct.ThrowIfCancellationRequested();
                var t = Math.Min(1.0, stopwatch.ElapsedMilliseconds / (double)countMs);
                var eased = 1 - Math.Pow(1 - t, 3);
                ResultScoreText.Text = ((long)(target * eased)).ToString("N0");
                await Task.Delay(16, ct);
            }

            ResultScoreText.Text = vm.RunResults.ResultScore;

            // Stage 2 — grade stamps in: attach transitions, then move
            // opacity/transform to their final values (render-cheap).
            GradeStamp.Transitions =
            [
                new DoubleTransition
                {
                    Property = OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(260),
                    Easing = new CubicEaseOut()
                },
                new TransformOperationsTransition
                {
                    Property = RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(260),
                    Easing = new CubicEaseOut()
                }
            ];
            GradeStamp.Opacity = 1;
            GradeStamp.RenderTransform = ScaleNormal;
            await Task.Delay(320, ct);

            // Stage 2.5 — FC badge stamps in a beat after the grade, only for a
            // full combo (IsVisible is bound to ResultIsFullCombo, so a non-FC run
            // never shows it). Same transform/opacity treatment as the grade.
            if (vm.RunResults.ResultIsFullCombo)
            {
                FcStamp.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(240),
                        Easing = new CubicEaseOut()
                    },
                    new TransformOperationsTransition
                    {
                        Property = RenderTransformProperty,
                        Duration = TimeSpan.FromMilliseconds(240),
                        Easing = new CubicEaseOut()
                    }
                ];
                FcStamp.Opacity = 1;
                FcStamp.RenderTransform = ScaleNormal;
                await Task.Delay(300, ct);
            }
            else
            {
                FcStamp.Opacity = 1; // final state for the next run
                FcStamp.RenderTransform = ScaleNormal;
            }

            // Stage 3 — new-record flash (opacity pulse; IsVisible is bound
            // to IsNewBestScore, so this only shows when the VM says so).
            if (vm.RunResults.IsNewBestScore)
            {
                NewRecordFlash.Transitions =
                [
                    new DoubleTransition
                    {
                        Property = OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(150)
                    }
                ];
                NewRecordFlash.Opacity = 1;
                await Task.Delay(220, ct);
                NewRecordFlash.Opacity = 0.35;
                await Task.Delay(170, ct);
                NewRecordFlash.Opacity = 1;
            }
            else
            {
                NewRecordFlash.Opacity = 1; // final state for the next run
            }

            // Natural completion: release the CTS only if it is still ours
            // (a restart may have swapped in a fresh one meanwhile).
            if (_revealCts?.Token == ct) _revealCts = null;
        }
        catch (OperationCanceledException)
        {
            // Skipped (input) or screen left — SkipReveal/ApplyFinalRevealState
            // already put things right for the skip path; leaving the screen
            // hides the subtree, so its values are refreshed on next entry.
        }
    }

    /// <summary>Final (post-reveal) visual state — also the reduced-motion
    /// target when the user skips.</summary>
    private void ApplyFinalRevealState(PlayModeViewModel? vm)
    {
        if (vm == null) return;
        GradeStamp.Transitions = null;
        NewRecordFlash.Transitions = null;
        FcStamp.Transitions = null;
        ResultScoreText.Text = vm.RunResults.ResultScore;
        GradeStamp.Opacity = 1;
        GradeStamp.RenderTransform = ScaleNormal;
        FcStamp.Opacity = 1;
        FcStamp.RenderTransform = ScaleNormal;
        NewRecordFlash.Opacity = 1;
    }
}
