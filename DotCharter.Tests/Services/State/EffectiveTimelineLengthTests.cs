namespace DotCharter.Tests.Services.State;

/// <summary>
///     <c>ProjectState.EffectiveTimelineLengthMs</c> — THE navigable timeline
///     length. A chart-only project (GP import into the no-audio flow, an empty
///     project) has beats but <c>TotalAudioLengthMs == 0</c>, and every
///     seek/pan/zoom gate or clamp on the audio length left such a project with
///     no time navigation at all: <c>ExecuteSeek</c> clamped every target to 0
///     (which also kept selection navigation from ever scrolling the viewport —
///     the inspector queue seeks through the same funnel), the minimap ignored
///     clicks, and wheel/middle-drag pans clamped to nothing.
/// </summary>
public sealed class EffectiveTimelineLengthTests
{
    [Fact]
    public void AttachedAudio_Wins()
    {
        var ps = new ProjectState();
        ps.Beats.Add(new BeatMarker { Time = 0 });
        ps.Beats.Add(new BeatMarker { Time = 250_000 });
        ps.TotalAudioLengthMs = 180_000;

        Assert.Equal(180_000, ps.EffectiveTimelineLengthMs);
    }

    [Fact]
    public void NoAudio_FallsBackToTheTempoGridEnd()
    {
        var ps = new ProjectState();
        ps.Beats.Add(new BeatMarker { Time = 0 });
        ps.Beats.Add(new BeatMarker { Time = 500 });
        ps.Beats.Add(new BeatMarker { Time = 130_000 });

        Assert.Equal(0, ps.TotalAudioLengthMs);
        Assert.Equal(130_000, ps.EffectiveTimelineLengthMs);
    }

    [Fact]
    public void NoAudioAndNoBeats_IsZero_NothingToNavigate() => Assert.Equal(0, new ProjectState().EffectiveTimelineLengthMs);

    /// <summary>Bindings on the effective length must learn when the audio
    /// length changes — the setter raises the dependent notification.</summary>
    [Fact]
    public void SettingTheAudioLength_AlsoAnnouncesTheEffectiveLength()
    {
        var ps = new ProjectState();
        var announced = new List<string?>();
        ps.PropertyChanged += (_, e) => announced.Add(e.PropertyName);

        ps.TotalAudioLengthMs = 60_000;

        Assert.Contains(nameof(ProjectState.TotalAudioLengthMs), announced);
        Assert.Contains(nameof(ProjectState.EffectiveTimelineLengthMs), announced);
    }

    /// <summary>
    ///     Source lint: the navigation layer must consume the EFFECTIVE length.
    ///     Each of these sites took <c>TotalAudioLengthMs</c> until 0.59, and
    ///     any one of them regressing re-deadens some navigation surface on an
    ///     audio-less chart. The wheel adjuster's SUSTAIN clamp deliberately
    ///     still uses the audio length and is not listed.
    ///     <para>
    ///         <c>MinimapOverviewControl</c> joined the list in 0.61 and is the
    ///         one entry that DRAWS rather than navigates — which is exactly why
    ///         it belongs here. The song-map strip's scale IS the mapping a
    ///         click on it is inverted through, so a strip drawn on any other
    ///         length is a strip whose clicks land somewhere else. It derived
    ///         its own chart-only fallback (a max over the beat grid, every note
    ///         tail, every chord tail and every event — strictly LARGER than the
    ///         effective length), while <c>MinimapDragController</c> seeked
    ///         against the effective length and <c>ExecuteSeek</c> clamped to
    ///         it: every click on a chart-only project landed proportionally
    ///         early and the right end of the strip was unreachable.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("DotCharter.UI/Input/TimelineNavigation.cs")]
    [InlineData("DotCharter.UI/Input/TimelineInteractionManager.cs")]
    [InlineData("DotCharter.UI/Input/MinimapDragController.cs")]
    [InlineData("DotCharter.UI/Controls/MinimapOverviewControl.cs")]
    public void TheNavigationLayer_UsesTheEffectiveLength_NotTheAudioLength(string relativePath)
    {
        var source = UiSourceTree.ReadAllText(RepoPath(relativePath));
        Assert.Contains("EffectiveTimelineLengthMs", source);
        Assert.DoesNotContain(".TotalAudioLengthMs", source);
    }

    /// <summary>
    ///     …and the strip may not grow a SECOND answer beside it. The private
    ///     chart-end scan it used to keep is gone; a re-added one would not trip
    ///     the lint above, because the file would still name the effective
    ///     length somewhere.
    /// </summary>
    [Fact]
    public void TheSongMapStrip_KeepsNoChartEndOfItsOwn()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Controls/MinimapOverviewControl.cs"));

        Assert.DoesNotContain("GetChartEndMs", source);
        Assert.Contains("_projectState?.EffectiveTimelineLengthMs", source);
    }

    [Fact]
    public void ExecuteSeek_ClampsToTheEffectiveLength()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/ViewModels/Editor/ChartEditorViewModel.cs"));
        var start = source.IndexOf("public void ExecuteSeek", StringComparison.Ordinal);
        Assert.True(start >= 0, "ExecuteSeek has moved — repoint this lint.");
        var end = source.IndexOf("public void ", start + 1, StringComparison.Ordinal);
        var body = end < 0 ? source[start..] : source[start..end];

        Assert.Contains("Math.Clamp(timeMs, 0, _projectState.EffectiveTimelineLengthMs)", body);
        Assert.DoesNotContain("_projectState.TotalAudioLengthMs", body);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
