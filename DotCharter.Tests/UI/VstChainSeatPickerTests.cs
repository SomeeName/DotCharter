using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     Per-seat VST ASSIGNMENT (0.58) — the authoring half of
///     <see cref="VstChainSeatTests" />.
///     <para>
///         The service layer has taken a player and persisted it since ABI v28
///         (<c>VstChainEntry.Player</c>, <c>dca_plugin_attach_to_input_for_player</c>,
///         <c>ApplyGuitarVstChainCore</c>'s unlocked reseat), and no surface could
///         SAY which seat a plugin belonged to. So every slot anyone added went on
///         seat 0, and player 2 could not put a plugin on their own rig at all —
///         a capability the engine had and the app could not reach.
///     </para>
///     <para>
///         These assert against what is PUSHED AT THE ENGINE and what is
///         PERSISTED — the two things that decide which input chain the plugin
///         really runs in — never against the picker's own selection. A selector
///         that flips a property nobody reads is the shape this repo has shipped
///         four times.
///     </para>
/// </summary>
public sealed class VstChainSeatPickerTests
{
    /// <summary>
    ///     Runs everything inline. The default dispatcher posts the VST restore
    ///     onto a real Avalonia UI thread that no default-gate test has, so
    ///     <c>_vstRestoreComplete</c> would stay false and every
    ///     <c>PersistGuitarChain</c> — the thing half these tests assert — would
    ///     be a silent no-op that still passed the engine-push half.
    /// </summary>
    private sealed class InlineUi : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private sealed class Harness
    {
        public Harness() =>
            Transport = new TransportViewModel(Audio, State, Config, FakeLocalization.KeysOnly(), new InlineUi(), new FakeAppLogger());

        public FakeAudioPlaybackService Audio { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public TransportViewModel Transport { get; }
        public ProjectState State { get; } = new();
    }

    private static IReadOnlyList<RigSeatOption> TwoSeats() =>
        [new RigSeatOption(0, "Ada"), new RigSeatOption(1, "Bo")];

    private static async Task<Harness> WithTwoSlots()
    {
        var h = new Harness();
        await h.Transport.VstStartupTask;

        h.Audio.VstPluginsOnDisk =
        [
            new VstPluginInfo { Name = "TS-9", PluginPath = @"C:\VST3\TS-9.vst3" },
            new VstPluginInfo { Name = "Room", PluginPath = @"C:\VST3\Room.vst3" }
        ];
        await h.Transport.RescanVstPluginsCommand.ExecuteAsync(null);

        foreach (var plugin in h.Transport.AvailableVstPlugins.Where(p => p.PluginPath.Length > 0).ToList())
        {
            // Assigning the selection IS the gesture — OnAddGuitarVstSelectionChanged
            // appends the slot. The [RelayCommand] wrapper this used to press was
            // removed in 0.58 as dead: both chain surfaces bind SelectedItem, so
            // nothing ever bound the generated command.
            h.Transport.AddGuitarVstSelection = plugin;
        }

        Assert.Equal(2, h.Transport.GuitarVstChain.Count);
        return h;
    }

    // ------------------------------------------------------------- the picker

    /// <summary>Single player is not a choice. No roster ⇒ no picker on any row,
    /// and every slot is the single-player chain it always was.</summary>
    [Fact]
    public async Task WithNoRoster_NoRowOffersASeatPicker()
    {
        var h = await WithTwoSlots();

        Assert.All(h.Transport.GuitarVstChain, s => Assert.False(s.HasMultipleSeats));
        Assert.All(h.Transport.GuitarVstChain, s => Assert.Equal(0, s.Player));
    }

    /// <summary>The rows offer the seats the co-op roster published, by PLAYER
    /// name — and it is the SAME list instance for every row, because two seat
    /// models are two answers to "which seats exist".</summary>
    [Fact]
    public async Task ARosterGivesEveryRowTheSameSeatList()
    {
        var h = await WithTwoSlots();

        h.Transport.SetVstSeats(TwoSeats());

        Assert.All(h.Transport.GuitarVstChain, s => Assert.True(s.HasMultipleSeats));
        Assert.All(h.Transport.GuitarVstChain,
            s => Assert.Same(h.Transport.VstSeats, s.Seats));
        Assert.Equal(["Ada", "Bo"], h.Transport.VstSeats.Select(x => x.Label));
    }

    // -------------------------------------------------------------- THE POINT

    /// <summary>
    ///     Moving a slot to another seat must reach the ENGINE carrying that seat
    ///     — that push is what makes <c>ApplyGuitarVstChainCore</c> detach the
    ///     plugin and re-attach it to the other player's input chain. Nothing else
    ///     moves a plugin.
    /// </summary>
    [Fact]
    public async Task PuttingASlotOnAnotherSeat_PushesThatSeatAtTheEngine()
    {
        var h = await WithTwoSlots();
        h.Transport.SetVstSeats(TwoSeats());

        var slot = h.Transport.GuitarVstChain[0];
        slot.SelectedSeat = slot.Seats[1];

        var pushed = h.Audio.PushedGuitarChains[^1];
        Assert.Equal(1, pushed.Single(e => e.Id == slot.Id).Player);
        // …and ONLY that slot moved.
        Assert.Equal(0, pushed.Single(e => e.Id == h.Transport.GuitarVstChain[1].Id).Player);
    }

    /// <summary>A seat that only lived until the next launch is not an
    /// assignment: the move is persisted too.</summary>
    [Fact]
    public async Task PuttingASlotOnAnotherSeat_PersistsThatSeat()
    {
        var h = await WithTwoSlots();
        h.Transport.SetVstSeats(TwoSeats());

        var slot = h.Transport.GuitarVstChain[1];
        slot.SelectedSeat = slot.Seats[1];

        var saved = h.Config.LoadAppConfig().GuitarVstChain;
        Assert.Equal(1, saved.Single(e => e.Id == slot.Id).Player);
        Assert.Equal(0, saved.Single(e => e.Id == h.Transport.GuitarVstChain[0].Id).Player);
    }

    /// <summary>A reorder after the move must carry it — the same rule
    /// <c>MoveGuitarSlot_carriesEachSlotsSeat</c> pins for seat 0, now with a
    /// seat that is not 0 (which is the only version that can fail).</summary>
    [Fact]
    public async Task AReorderAfterAMove_StillCarriesTheSeat()
    {
        var h = await WithTwoSlots();
        h.Transport.SetVstSeats(TwoSeats());

        var moved = h.Transport.GuitarVstChain[0];
        moved.SelectedSeat = moved.Seats[1];

        Assert.True(h.Transport.MoveGuitarSlot(0, 1));

        Assert.Equal(1, h.Audio.PushedGuitarChains[^1].Single(e => e.Id == moved.Id).Player);
    }

    // ------------------------------------------------------------ the refusals

    /// <summary>
    ///     A roster ARRIVING is not an edit. Echoing one would re-push the whole
    ///     chain at the engine on every co-op start — a plugin unload/reload per
    ///     slot for a change nobody made.
    /// </summary>
    [Fact]
    public async Task PublishingARoster_DoesNotTouchTheEngine()
    {
        var h = await WithTwoSlots();
        var pushes = h.Audio.PushedGuitarChains.Count;

        h.Transport.SetVstSeats(TwoSeats());

        Assert.Equal(pushes, h.Audio.PushedGuitarChains.Count);
    }

    /// <summary>
    ///     Collapsing back to single player at the end of a run must NOT rewrite a
    ///     slot's seat to 0. The persisted chain still says player 2, and silently
    ///     re-seating it here would destroy that on the next save — precisely the
    ///     data loss <see cref="VstChainSeatTests" /> exists to prevent.
    /// </summary>
    [Fact]
    public async Task CollapsingBackToSinglePlayer_KeepsASlotsSeat()
    {
        var h = await WithTwoSlots();
        h.Transport.SetVstSeats(TwoSeats());

        var slot = h.Transport.GuitarVstChain[0];
        slot.SelectedSeat = slot.Seats[1];

        h.Transport.SetVstSeats(null); // the seat publisher collapsed back

        Assert.False(slot.HasMultipleSeats);
        Assert.Equal(1, slot.Player);
        Assert.Equal(1, h.Config.LoadAppConfig().GuitarVstChain.Single(e => e.Id == slot.Id).Player);
    }

    /// <summary>Re-selecting the seat a slot is already on changes nothing — no
    /// push, no save, no plugin reload.</summary>
    [Fact]
    public async Task ReSelectingTheSameSeat_IsANoOp()
    {
        var h = await WithTwoSlots();
        h.Transport.SetVstSeats(TwoSeats());

        var pushes = h.Audio.PushedGuitarChains.Count;
        var slot = h.Transport.GuitarVstChain[0];
        slot.SelectedSeat = slot.Seats[0];

        Assert.Equal(pushes, h.Audio.PushedGuitarChains.Count);
    }
}
