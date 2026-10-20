using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     Couch co-op gives every seat its own tone rig on its own input channel
///     (ABI v28), and until 0.57 the one Amp &amp; FX surface could only ever
///     address seat 0 — so the extra rigs existed natively, were applied at run
///     start by <c>CoopRigApplier</c>, and could not be looked at or edited.
///
///     <para>
///         These assert against the CHAINS, never against the picker's own state:
///         a selector that changes a property nobody reads is the shape this repo
///         has shipped four times.
///     </para>
/// </summary>
public sealed class RigSeatSelectionTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
        new[] { "amp", "cab", "gain", "reverb", "delay" }
            .Select(id => new DspModuleType(id, id,
                new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
            .ToList();

    /// <summary>Two independent in-memory chains behind a per-player surface.</summary>
    private sealed class FakeChains : IPlayerDspChains
    {
        private readonly List<IDspChainService> _all;

        public FakeChains(int seats, IReadOnlyList<DspModuleType>? catalog = null)
        {
            _all = Enumerable.Range(0, seats)
                .Select(_ => (IDspChainService)new FakeDspChainService(catalog ?? Catalog()))
                .ToList();
        }

        public int MaxPlayers => _all.Count;
        public IReadOnlyList<IDspChainService> All => _all;
        public IDspChainService For(int player) => _all[Math.Clamp(player, 0, _all.Count - 1)];
        public void AttachEngineToAll(nint engine) { }
    }

    private static PedalboardViewModel NewRig(FakeChains chains) =>
        TestRigBuilders.Pedalboard(chains.For(0), new FakeDspTonePresetService(), FakeLocalization.English(), chains: chains);

    private static string[] Order(IDspChainService chain) =>
        chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();

    private static IReadOnlyList<RigSeatOption> TwoSeats() =>
        [new RigSeatOption(0, "Ada"), new RigSeatOption(1, "Bo")];

    // ---------------------------------------------------------------- the picker

    /// <summary>One player is not a choice — a dropdown over it is a live-looking
    /// control over nothing, which is this repo's oldest UI rule.</summary>
    [Fact]
    public void WithNoRoster_ThereIsOneSeatAndNoPicker()
    {
        var rig = NewRig(new FakeChains(4));

        Assert.False(rig.HasMultipleSeats);
        Assert.Equal(0, rig.SelectedPlayer);
    }

    [Fact]
    public void ARosterOffersItsSeatsByPLAYERNameNotBySlotNumber()
    {
        var rig = NewRig(new FakeChains(4));

        rig.SetSeats(TwoSeats());

        Assert.True(rig.HasMultipleSeats);
        Assert.Equal(["Ada", "Bo"], rig.Seats.Select(s => s.Label));
    }

    /// <summary>
    ///     THE POINT. Switching seats must move every edit onto that seat's own
    ///     chain — and must not carry the previous seat's rig with it.
    /// </summary>
    [Fact]
    public void PickingASeat_PointsEveryEditAtThatSeatsOwnChain()
    {
        var chains = new FakeChains(2);
        var rig = NewRig(chains);
        rig.SetSeats(TwoSeats());

        // Seat 0's board.
        rig.AddGear(Catalog().First(m => m.Id == "amp"));
        Assert.Equal(["amp"], Order(chains.For(0)));
        Assert.Empty(Order(chains.For(1)));

        rig.SelectedSeat = rig.Seats[1];

        // Seat 1 starts with the rig IT had — nothing is copied across.
        Assert.Equal(1, rig.SelectedPlayer);
        Assert.Empty(rig.Nodes);

        rig.AddGear(Catalog().First(m => m.Id == "reverb"));
        Assert.Equal(["reverb"], Order(chains.For(1)));
        // …and seat 0 is untouched by anything done while seat 1 was selected.
        Assert.Equal(["amp"], Order(chains.For(0)));
    }

    /// <summary>Coming back shows the rig that seat still has, rebuilt from the
    /// chain rather than from anything the view model cached.</summary>
    [Fact]
    public void ComingBackToASeat_ShowsTheRigItStillHas()
    {
        var chains = new FakeChains(2);
        var rig = NewRig(chains);
        rig.SetSeats(TwoSeats());

        rig.AddGear(Catalog().First(m => m.Id == "amp"));
        rig.SelectedSeat = rig.Seats[1];
        rig.AddGear(Catalog().First(m => m.Id == "delay"));
        rig.SelectedSeat = rig.Seats[0];

        Assert.Equal(0, rig.SelectedPlayer);
        Assert.Equal(["amp"], rig.Nodes.Select(n => n.ModuleTypeId));
    }

    /// <summary>
    ///     A seat the ENGINE cannot host must never be offered.
    ///     <c>IPlayerDspChains.For</c> CLAMPS an out-of-range index into range, so
    ///     a fourth seat on a two-rig engine would quietly edit seat 1's tone while
    ///     the picker said "Player 4".
    /// </summary>
    [Fact]
    public void ASeatPastTheEnginesCeiling_IsNotOffered()
    {
        var rig = NewRig(new FakeChains(2));

        rig.SetSeats([
            new RigSeatOption(0, "Ada"),
            new RigSeatOption(1, "Bo"),
            new RigSeatOption(2, "Cy"),
            new RigSeatOption(3, "Di")
        ]);

        Assert.Equal(["Ada", "Bo"], rig.Seats.Select(s => s.Label));
    }

    /// <summary>The run ended: the extra chains are gone, so offering them would
    /// point the rig at a seat nobody owns.</summary>
    [Fact]
    public void ClearingTheRoster_CollapsesBackToSinglePlayer()
    {
        var chains = new FakeChains(2);
        var rig = NewRig(chains);
        rig.SetSeats(TwoSeats());
        rig.SelectedSeat = rig.Seats[1];

        rig.SetSeats(null);

        Assert.False(rig.HasMultipleSeats);
        Assert.Equal(0, rig.SelectedPlayer);
        Assert.Single(rig.Seats);
    }

    /// <summary>A roster edit (someone renames themselves) must not yank the rig
    /// out from under whoever is dialling seat 1 in.</summary>
    [Fact]
    public void RenamingASeat_KeepsTheOneBeingEditedSelected()
    {
        var rig = NewRig(new FakeChains(2));
        rig.SetSeats(TwoSeats());
        rig.SelectedSeat = rig.Seats[1];

        rig.SetSeats([new RigSeatOption(0, "Ada"), new RigSeatOption(1, "Bo the Younger")]);

        Assert.Equal(1, rig.SelectedPlayer);
        Assert.Equal("Bo the Younger", rig.SelectedSeat!.Label);
    }

    /// <summary>Without a per-player surface (headless, the null service) the rig
    /// keeps the single seat it always had rather than throwing.</summary>
    [Fact]
    public void WithNoPerPlayerSurface_TheRigStaysOnItsOneChain()
    {
        var chain = new FakeDspChainService(Catalog());
        var rig = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English());

        rig.SetSeats(TwoSeats());
        rig.AddGear(Catalog().First(m => m.Id == "amp"));

        Assert.False(rig.HasMultipleSeats);
        Assert.Equal(["amp"], Order(chain));
    }
    /// <summary>
    ///     The tone-preset BROWSER under the board is an edit on this surface too.
    ///     Until 0.61 a card click went through the seat-less <c>Apply</c>, so with
    ///     player 2's board on screen the tone landed on player 1's chain — the
    ///     SCENE strip's compare had the same bug until 0.59 and this is its
    ///     sibling. Asserted against WHICH SEAT the service was told to dress,
    ///     never the browser's own active-name property.
    /// </summary>
    [Fact]
    public void ApplyingAPresetFromTheBrowser_LandsOnTheSelectedSeat()
    {
        var chains = new FakeChains(2);
        var presets = new FakeDspTonePresetService();
        var rig = TestRigBuilders.Pedalboard(chains.For(0), presets, FakeLocalization.English(), chains: chains);
        rig.SetSeats(TwoSeats());
        rig.SelectedSeat = rig.Seats[1];

        var card = rig.Browser.Cards[0];
        rig.Browser.ApplyCard(card);

        Assert.Equal([(1, card.Preset.Id)], presets.AppliedPerPlayer);

        // …and back on seat 0 the same click dresses seat 0.
        rig.SelectedSeat = rig.Seats[0];
        rig.Browser.ApplyCard(card);
        Assert.Equal((0, card.Preset.Id), presets.AppliedPerPlayer[^1]);
    }

    // ------------------------------------------- state that must NOT follow the seat

    /// <summary>
    ///     The command bar's master BYPASS is an armed gesture over ONE board: it
    ///     remembers which pedals were already bypassed, keyed by NATIVE NODE ID,
    ///     so disarming restores exactly those. Node ids are only unique per
    ///     chain — both seats start at 1 — so carrying the armed state across a
    ///     seat repoint makes the lamp lie about the board on screen AND makes the
    ///     next press write player 1's bypass pattern onto player 2's chain.
    /// </summary>
    [Fact]
    public void TheMasterBypass_DoesNotFollowTheRigToAnotherSeat()
    {
        var chains = new FakeChains(2);
        var rig = NewRig(chains);
        rig.SetSeats(TwoSeats());

        // Seat 1 has a board of its own, entirely live.
        rig.SelectedSeat = rig.Seats[1];
        rig.AddGear(Catalog().First(m => m.Id == "amp"));
        rig.AddGear(Catalog().First(m => m.Id == "cab"));

        // Seat 0: two pedals, the second deliberately switched out, then the
        // whole board bypassed from the command bar.
        rig.SelectedSeat = rig.Seats[0];
        rig.AddGear(Catalog().First(m => m.Id == "amp"));
        rig.AddGear(Catalog().First(m => m.Id == "cab"))!.Bypassed = true;
        rig.ToggleMasterBypassCommand.Execute(null);
        Assert.True(rig.IsMasterBypassed);

        rig.SelectedSeat = rig.Seats[1];

        // The lamp describes the board on screen, and seat 1's is not bypassed.
        Assert.False(rig.IsMasterBypassed);
        Assert.All(rig.Nodes, n => Assert.False(n.Bypassed));

        // …so the next press ARMS over seat 1 (bypassing all of it) instead of
        // "restoring" seat 0's pattern onto it.
        rig.ToggleMasterBypassCommand.Execute(null);
        Assert.All(chains.For(1).GetNodes(), n => Assert.True(n.Bypassed));
        // Seat 0's own board is untouched by any of it.
        Assert.All(chains.For(0).GetNodes(), n => Assert.True(n.Bypassed));
    }

    /// <summary>
    ///     The A/B compare slots are two snapshots of ONE board. Pointing the
    ///     surface at another seat and pressing SWITCH must never push the seat
    ///     the slots were filled from onto the seat now on screen — the same
    ///     wrong-guitar shape the browser apply and the tuner each shipped.
    /// </summary>
    [Fact]
    public void TheCompareSlots_DoNotCarryOneSeatsRigOntoAnother()
    {
        var chains = new FakeChains(2);
        var presets = new FakeDspTonePresetService();
        var rig = TestRigBuilders.Pedalboard(chains.For(0), presets, FakeLocalization.English(), chains: chains);
        rig.SetSeats(TwoSeats());

        // Seat 0 builds a board and starts a comparison from it.
        rig.AddGear(Catalog().First(m => m.Id == "amp"));
        rig.AddGear(Catalog().First(m => m.Id == "delay"));
        rig.SeedCompareCommand.Execute(null);

        // Seat 1 has its own, different board.
        rig.SelectedSeat = rig.Seats[1];
        rig.AddGear(Catalog().First(m => m.Id == "reverb"));

        rig.ToggleCompareCommand.Execute(null);
        rig.SelectCompareSlotCommand.Execute("A");

        // Nothing carrying seat 0's modules may have been applied to seat 1.
        foreach (var (player, preset) in presets.AppliedPresetsPerPlayer)
        {
            if (player != 1) continue;
            Assert.DoesNotContain("amp", preset.Rig.Modules.Select(m => m.ModuleTypeId));
            Assert.DoesNotContain("delay", preset.Rig.Modules.Select(m => m.ModuleTypeId));
        }

        // …and seat 1's chain still holds exactly what seat 1 built.
        Assert.Equal(["reverb"], Order(chains.For(1)));
    }

    // ------------------------------------------------- the NAM capture browser

    /// <summary>
    ///     "Load into rig" is an edit on this surface like any other, so it has to
    ///     land on the SELECTED seat.
    ///     <para>
    ///         The browser was handed the chain INSTANCE the pedalboard was
    ///         constructed with, and the seat picker works by repointing that field
    ///         — so the browser was permanently seat 0. Loading a capture while
    ///         player 2's board was on screen dropped a neural amp onto player 1's
    ///         rig and then rebuilt the board of the seat on screen, which shows
    ///         nothing at all: the wrong-guitar shape the preset browser (0.61) and
    ///         the compare strip (0.59) each shipped before it.
    ///     </para>
    ///     Asserted against the CHAINS, never against the browser's own state.
    /// </summary>
    [Fact]
    public void LoadingACaptureIntoTheRig_LandsOnTheSelectedSeat()
    {
        var chains = new FakeChains(2, NamCatalog());
        var library = new StubNamLibrary();
        var rig = TestRigBuilders.Pedalboard(
            chains.For(0), new FakeDspTonePresetService(), FakeLocalization.English(),
            tone3000: new StubTone3000Service(), tone3000Auth: new StubTone3000Auth(),
            namLibrary: library, chains: chains);
        rig.SetSeats(TwoSeats());

        var nam = rig.Browser.NamBrowser;
        Assert.NotNull(nam);
        nam!.ShowLibraryCommand.Execute(null);
        var row = Assert.Single(nam.LibraryRows);

        rig.SelectedSeat = rig.Seats[1];          // the board now shows player 2
        nam.LoadIntoRigCommand.Execute(row);

        Assert.Equal(["namamp"], Order(chains.For(1)));
        Assert.Empty(Order(chains.For(0)));       // player 1's board is untouched
        // …and the capture really reached the node the picker named.
        var loaded = chains.For(1).GetNodes()[0];
        Assert.Equal(
            Path.Combine(library.RootDirectory, "1-plexi.nam"),
            chains.For(1).GetNodeString(loaded.NodeId, "modelPath"));
    }

    private static IReadOnlyList<DspModuleType> NamCatalog() =>
        Catalog()
            .Append(new DspModuleType("namamp", "namamp",
                new[] { new DspParamInfo("inputDb", "inputDb", -20f, 20f, 0f) })
            {
                StringParams = [new DspStringParamInfo("modelPath", "Capture", "*.nam;*.aidax")]
            })
            .ToList();

    /// <summary>One capture in the local library — enough for a Library row.</summary>
    private sealed class StubNamLibrary : INamCaptureLibrary
    {
        public string RootDirectory => "X:\\nam";
        public event EventHandler? Changed { add { } remove { } }

        public IReadOnlyList<NamCaptureEntry> GetAll() =>
        [
            new(new NamCaptureManifest(1, 1, "Plexi", "capturist", "amp", "nam", "standard",
                    "CC-BY", "https://cc.org/by", "https://t3k/1", "1-plexi.nam", "sha", 1024,
                    DateTimeOffset.UnixEpoch),
                "X:\\nam\\1-plexi.nam")
        ];

        public bool IsModelDownloaded(long modelId) => modelId == 1;
        public bool IsToneDownloaded(long toneId) => toneId == 1;

        public Task<OnlineResult<NamCaptureEntry>> DownloadAsync(
            Tone3000Tone tone, Tone3000Model model, IProgress<double>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<NamCaptureEntry>.Fail(OnlineError.Network, "no"));

        public bool Delete(long modelId) => false;
        public int MigrateFileExtensions() => 0;
        public bool ImportBundled(string sourceFilePath, BundledNamCapture meta) => false;
    }

    private sealed class StubTone3000Service : ITone3000Service
    {
        public Task<OnlineResult<Tone3000SearchPage>> SearchAsync(
            Tone3000SearchQuery query, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<Tone3000SearchPage>.Fail(OnlineError.NotSignedIn, "no"));

        public Task<OnlineResult<Tone3000Tone>> GetToneAsync(long toneId, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<Tone3000Tone>.Fail(OnlineError.NotSignedIn, "no"));

        public Task<OnlineResult<IReadOnlyList<Tone3000Model>>> GetModelsAsync(
            long toneId, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<IReadOnlyList<Tone3000Model>>.Fail(OnlineError.NotSignedIn, "no"));

        public Task<OnlineResult<long>> DownloadModelAsync(
            Tone3000Model model, Stream destination, long maxBytes,
            IProgress<double>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<long>.Fail(OnlineError.NotSignedIn, "no"));
    }

    private sealed class StubTone3000Auth : ITone3000AuthService
    {
        public bool IsConfigured => true;
        public bool IsSignedIn => true;
        public string UserName => "tester";
        public event Action? AuthStateChanged { add { } remove { } }

        public Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default) =>
            Task.FromResult(OnlineResult<Tone3000User>.Fail(OnlineError.Network, "no"));

        public void SignOut() { }
        public void InvalidateSession(string reason) { }

        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>("token");
    }
}
