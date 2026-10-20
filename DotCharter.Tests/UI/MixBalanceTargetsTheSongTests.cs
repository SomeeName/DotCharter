using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Mix popover moves the SONG, not the master bus.
///     <para>
///         Owner report, 2026-08-23: <i>"the mix top says song but it actually
///         moves the master in the levels, that shouldn't happen, it should only
///         move the song"</i>. Both names existed and were correct on the service —
///         <c>Volume</c> calls <c>SetMasterGain</c>, <c>SongVolume</c> gains the
///         song source and its stems — the popover simply bound the wrong one.
///     </para>
///     <para>
///         The second half was worse and nobody had reported it, because its
///         symptom is silence rather than a wrong number: master gain is applied
///         LAST on the device bus (<c>AudioEngine.cpp</c> multiplies every output
///         channel by it), so <b>"guitar only" set master to 0 and muted the
///         guitar too</b> — and lit its chip while doing it, since the indicator
///         read the same field it had just zeroed.
///     </para>
/// </summary>
public sealed class MixBalanceTargetsTheSongTests
{
    private static TransportViewModel NewTransport() =>
        new(new Infrastructure.FakeAudioPlaybackService(), new ProjectState(),
            new Infrastructure.FakeConfigurationService(), FakeLocalization.KeysOnly());

    /// <summary>
    ///     The bug as the owner met it: dragging the row labelled "Song" must not
    ///     touch the master bus.
    /// </summary>
    [Fact]
    public void SettingTheSongLevelLeavesMasterAlone()
    {
        var transport = NewTransport();

        transport.SongVolume = 0.4;

        Assert.Equal(0.4, transport.SongVolume, 3);
        Assert.Equal(1.0, transport.Volume, 3);
    }

    /// <summary>
    ///     The silent-output bug. "Guitar only" isolates the guitar by taking the
    ///     SONG to zero; taking MASTER to zero silences the guitar with it.
    /// </summary>
    [Fact]
    public void GuitarOnlyDoesNotZeroTheMasterBus()
    {
        var transport = NewTransport();

        transport.SetMixBalanceCommand.Execute("guitar");

        Assert.Equal(0.0, transport.SongVolume, 3);
        Assert.Equal(1.0, transport.InputVolume, 3);
        Assert.Equal(1.0, transport.Volume, 3);
        Assert.True(transport.IsMixGuitarOnly, "the chip must light for the mix it just set");
    }

    [Fact]
    public void SongOnlySilencesTheInputAndNotTheBus()
    {
        var transport = NewTransport();

        transport.SetMixBalanceCommand.Execute("song");

        Assert.Equal(1.0, transport.SongVolume, 3);
        Assert.Equal(0.0, transport.InputVolume, 3);
        Assert.Equal(1.0, transport.Volume, 3);
        Assert.True(transport.IsMixSongOnly);
    }

    [Fact]
    public void EvenBringsBothUpWithoutTouchingMaster()
    {
        var transport = NewTransport();
        transport.Volume = 0.5;

        transport.SetMixBalanceCommand.Execute("even");

        Assert.Equal(1.0, transport.SongVolume, 3);
        Assert.Equal(1.0, transport.InputVolume, 3);
        Assert.Equal(0.5, transport.Volume, 3);
        Assert.True(transport.IsMixEven);
    }

    /// <summary>
    ///     The indicators must read the SAME field the chips write, or a chip
    ///     lights for a mix that is not playing. This is what made the silent
    ///     "guitar only" look like it had worked.
    /// </summary>
    [Fact]
    public void TheIndicatorsFollowTheSongLevelNotTheMaster()
    {
        var transport = NewTransport();
        transport.SongVolume = 1.0;
        transport.InputVolume = 1.0;

        Assert.True(transport.IsMixEven);

        // Pulling MASTER down changes how loud everything is, but not which of
        // the three balances is set — the chip must stay lit.
        transport.Volume = 0.25;
        Assert.True(transport.IsMixEven, "master gain is not a balance between sources");

        // Pulling the SONG down does change the balance.
        transport.SongVolume = 0.0;
        Assert.False(transport.IsMixEven);
        Assert.True(transport.IsMixGuitarOnly);
    }

    /// <summary>
    ///     Reset restores the song too. It used to leave <c>SongVolume</c> exactly
    ///     where it was, so "reset every level" could leave the song silent.
    /// </summary>
    [Fact]
    public void ResetRestoresEveryLevelIncludingTheSong()
    {
        var transport = NewTransport();
        transport.Volume = 0.2;
        transport.SongVolume = 0.0;
        transport.InputVolume = 0.3;
        transport.MidiVolume = 0.4;

        transport.ResetMixLevelsCommand.Execute(null);

        Assert.Equal(1.0, transport.Volume, 3);
        Assert.Equal(1.0, transport.SongVolume, 3);
        Assert.Equal(1.0, transport.InputVolume, 3);
        Assert.Equal(1.0, transport.MidiVolume, 3);
    }

    /// <summary>
    ///     …and the markup, because the report was about a binding. The row
    ///     labelled Song must bind the song level; a correct view model bound to
    ///     the wrong property is exactly the bug that was shipped.
    /// </summary>
    [Fact]
    public void TheSongRowInTheMixPopoverBindsTheSongLevel()
    {
        var markup = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Shell/Popovers/MixPopoverView.axaml"));

        Assert.Contains("Transport.SongVolume", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Transport.Volume", markup, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The three levels read as a percentage of their normal level. They read "1,00" each while
    ///     their thumbs sat in three different places — the sliders' ranges differ on purpose (the
    ///     song cannot be boosted, the guitar can double) — and a bare multiplier said nothing to a
    ///     player (2026-09-23). "100 %" beside a thumb at half travel says the guitar can go higher.
    /// </summary>
    [Fact]
    public void EveryLevelInTheMixPopover_ReadsAsAPercentage()
    {
        var markup = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Shell/Popovers/MixPopoverView.axaml"));

        foreach (var level in new[] { "SongVolume", "InputVolume", "MidiVolume" })
            Assert.Contains("Text=\"{Binding Transport." + level + @", StringFormat=\{0:P0\}}""", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("0:0.00", markup, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The Mixer tab shows the same levels, and read "1,00" four times while its sliders sat at
    ///     four different places (each has its own range). Same rule as the popover (2026-09-24).
    /// </summary>
    [Fact]
    public void EveryLevelInTheMixerTab_ReadsAsAPercentage()
    {
        var markup = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Views/Editor/Inspector/MixerInspectorView.axaml"));

        foreach (var level in new[] { "Volume", "SongVolume", "InputVolume", "MidiVolume", "PlaybackSpeed" })
            Assert.Contains("Text=\"{Binding Transport." + level + @", StringFormat=\{0:P0\}}""", markup, StringComparison.Ordinal);
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
