using System.Reflection;
using Avalonia.Threading;
using DotCharter.Tone.UI.Platform;

namespace DotCharter.Tests.UI.Platform;

/// <summary>
///     The app's <see cref="AvaloniaUiDispatcher" />, where the Services-side
///     coordinators' timers and posts land.
/// </summary>
public sealed class AvaloniaUiDispatcherTests
{
    /// <summary>
    ///     A periodic tick must never pre-empt a frame. The crash-recovery snapshot
    ///     rides it and deep-clones the whole project on the UI thread, so it
    ///     belongs BELOW Render. Avalonia's parameterless <c>DispatcherTimer()</c>
    ///     already defaults to <c>Background</c> (measured by reflection, not
    ///     assumed); the adapter says so out loud and this fact holds it there, so
    ///     neither a future Avalonia default nor a hand-edited priority can promote
    ///     the tick silently. Structural, not timed: the priority IS the claim.
    /// </summary>
    [Fact]
    public void PeriodicTimer_RunsBelowRender()
    {
        var timer = AvaloniaUiDispatcher.CreatePeriodicTimer(TimeSpan.FromSeconds(2), () => { });

        Assert.False(timer.IsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(2), timer.Interval);
        Assert.Equal(DispatcherPriority.Background, PriorityOf(timer));
    }

    /// <summary>
    ///     Avalonia's <c>DispatcherTimer</c> keeps its priority private and has
    ///     renamed the member before, so this finds it BY TYPE rather than by
    ///     name and says so loudly if it disappears — a reflection probe that
    ///     silently found nothing would make the test above vacuous.
    /// </summary>
    private static DispatcherPriority PriorityOf(DispatcherTimer timer)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var property in typeof(DispatcherTimer).GetProperties(flags))
        {
            if (property.PropertyType == typeof(DispatcherPriority) && property.CanRead)
                return (DispatcherPriority)property.GetValue(timer)!;
        }

        foreach (var f in typeof(DispatcherTimer).GetFields(flags))
        {
            if (f.FieldType == typeof(DispatcherPriority))
                return (DispatcherPriority)f.GetValue(timer)!;
        }

        throw new InvalidOperationException(
            "Avalonia's DispatcherTimer no longer carries a DispatcherPriority member — repoint this test.");
    }
}
