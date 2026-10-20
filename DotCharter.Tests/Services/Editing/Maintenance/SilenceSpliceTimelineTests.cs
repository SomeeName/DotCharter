using System.Reflection;
using DotCharter.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     #313: silence spliced in front of the song moves every ABSOLUTE time in the project, and only
///     those (anything placed by beat follows the beats). A new time field on the domain fails here
///     until <see cref="SilenceSpliceTimeline" /> moves it or the list below says why it need not.
/// </summary>
public sealed class SilenceSpliceTimelineTests
{
    /// <summary>The time-like domain properties the shift moves, as Type.Property.</summary>
    private static readonly string[] Shifted =
    [
        "BeatMarker.Time",
        "Bookmark.Time",
        "ChartAnnotation.TimeMs",
        "ChartData.Time"
    ];

    [Fact]
    public void Every_absolute_time_field_on_the_domain_is_one_the_shift_moves()
    {
        var timeLike = typeof(ChartProject).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("DotCharter.Core.Domain", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanWrite && IsNumber(p.PropertyType) && IsTimeName(p.Name))
                .Select(p => $"{t.Name}.{p.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Shifted.Order(StringComparer.Ordinal), timeLike);
    }

    private static bool IsNumber(Type type) => type == typeof(double) || type == typeof(float) || type == typeof(int) || type == typeof(long);

    private static bool IsTimeName(string name) =>
        name == "Time" || name.EndsWith("TimeMs", StringComparison.Ordinal) || name.EndsWith("Ms", StringComparison.Ordinal)
        || name.StartsWith("Time", StringComparison.Ordinal);

    [Fact]
    public void The_shift_moves_each_absolute_time_and_records_the_added_offset()
    {
        var project = new ChartProject();
        project.Beats.Add(new BeatMarker { Time = 100 });
        project.Bookmarks.Add(new Bookmark { Time = 200 });
        project.Annotations.Add(new ChartAnnotation { TimeMs = 300 });
        project.Chart.Time = 400;
        project.Chart.MainAudioOffsetAdded = 50;

        SilenceSpliceTimeline.ShiftForLeadingSilence(project, 1000);

        Assert.Equal(1100, project.Beats[0].Time);
        Assert.Equal(1200, project.Bookmarks[0].Time);
        Assert.Equal(1300, project.Annotations[0].TimeMs);
        Assert.Equal(1400, project.Chart.Time);
        Assert.Equal(1050, project.Chart.MainAudioOffsetAdded);
    }
}
