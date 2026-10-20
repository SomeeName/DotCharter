using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Online;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
/// Tests the in-app TONE3000 setup VM: the client id is persisted through
/// <see cref="DotCharter.Services.Infrastructure.Configuration.IConfigurationService"/>
/// (no more hand-editing appconfig.json), and sign-in refuses until a client id
/// is set. Uses <see cref="FakeLocalization"/> (reads the English resx) so a
/// missing key would surface.
/// </summary>
public sealed class Tone3000SettingsViewModelTests
{
    /// <summary>Auth double whose IsConfigured tracks the shared config's client id.</summary>
    private sealed class ConfigBackedAuth : ITone3000AuthService
    {
        private readonly FakeConfigurationService _config;
        public bool SignedIn;
        public string User = "capturist";
        public int SignInCalls;

        /// <summary>Set to hold the flow open — the stand-in for "the user is
        /// still in the browser and the loopback listener is bound".</summary>
        public TaskCompletionSource<bool>? Block;

        /// <summary>The token the last sign-in was handed. The VM used to pass
        /// none at all, so nothing could ever end a flow.</summary>
        public CancellationToken LastToken;

        public ConfigBackedAuth(FakeConfigurationService config) => _config = config;

        public bool IsConfigured => _config.LoadAppConfig().Tone3000ClientId.Length > 0;
        public bool IsSignedIn => SignedIn;
        public string UserName => User;
        public event Action? AuthStateChanged { add { } remove { } }

        public async Task<OnlineResult<Tone3000User>> SignInAsync(CancellationToken ct = default)
        {
            SignInCalls++;
            LastToken = ct;
            if (Block != null) await Block.Task;
            SignedIn = true;
            return OnlineResult<Tone3000User>.Success(new Tone3000User("id", User));
        }

        public void SignOut() => SignedIn = false;
        public void InvalidateSession(string reason) => SignedIn = false;
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(SignedIn ? "tok" : null);
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public void PostNormal(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());
        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }

    private static Tone3000SettingsViewModel NewVm(out FakeConfigurationService config, out ConfigBackedAuth auth)
    {
        config = new FakeConfigurationService();
        auth = new ConfigBackedAuth(config);
        return new Tone3000SettingsViewModel(auth, config, new InlineDispatcher(), FakeLocalization.English());
    }

    [Fact]
    public void Load_reads_clientId_from_config()
    {
        var vm = NewVm(out var config, out _);
        config.AppConfig.Tone3000ClientId = "abc123";

        vm.Load();

        Assert.Equal("abc123", vm.ClientId);
    }

    [Fact]
    public void Save_persists_trimmed_clientId_and_flips_configured()
    {
        var vm = NewVm(out var config, out _);
        Assert.False(vm.IsConfigured);

        vm.ClientId = "  paste-me  ";
        vm.SaveClientIdCommand.Execute(null);

        Assert.Equal("paste-me", config.LoadAppConfig().Tone3000ClientId);
        Assert.True(vm.IsConfigured);
        Assert.True(vm.HasStatus);
    }

    [Fact]
    public async Task SignIn_requires_a_clientId()
    {
        var vm = NewVm(out _, out var auth);

        await vm.SignInCommand.ExecuteAsync(null); // no client id yet
        Assert.Equal(0, auth.SignInCalls);
        Assert.True(vm.IsStatusError);

        vm.ClientId = "client";
        await vm.SignInCommand.ExecuteAsync(null); // Save runs first → now configured
        Assert.Equal(1, auth.SignInCalls);
        Assert.True(vm.IsSignedIn);
        Assert.False(vm.IsStatusError);
    }

    /// <summary>
    ///     A sign-in owns the loopback <c>HttpListener</c> on port 47821 for the
    ///     whole five-minute callback timeout. The Sign-in button was gated on
    ///     <c>IsConfigured</c> alone — <c>IsBusy</c> was bound by NOTHING — so a
    ///     second click started a second listener on the same prefix, which
    ///     cannot bind: an <c>HttpListenerException</c> reported to the user as a
    ///     failed sign-in while the first flow was still perfectly alive. The
    ///     guard is in the view model, not the markup.
    /// </summary>
    [Fact]
    public async Task SignIn_WhileOneIsAlreadyWaitingOnTheBrowser_StartsNoSecondFlow()
    {
        var vm = NewVm(out _, out var auth);
        vm.ClientId = "client";   // SignIn's own Save() persists it first
        auth.Block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = vm.SignInCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanSignIn);        // …and the button says so

        await vm.SignInCommand.ExecuteAsync(null);
        Assert.Equal(1, auth.SignInCalls); // the second click did nothing

        auth.Block.SetResult(true);
        await first;
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanSignIn);
    }

    /// <summary>Cancel is the only way to hand port 47821 back before the
    /// five-minute timeout; nothing offered one before.</summary>
    [Fact]
    public async Task CancelSignIn_CancelsTheTokenTheFlowIsWaitingOn()
    {
        var vm = NewVm(out _, out var auth);
        vm.ClientId = "client";   // SignIn's own Save() persists it first
        auth.Block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var flow = vm.SignInCommand.ExecuteAsync(null);
        Assert.False(auth.LastToken.IsCancellationRequested);

        vm.CancelSignInCommand.Execute(null);

        Assert.True(auth.LastToken.IsCancellationRequested);
        auth.Block.SetResult(true);
        await flow;
    }

    /// <summary>Signing out while a sign-in is pending must end it — otherwise it
    /// completes and signs the user straight back in, holding the port until it
    /// does.</summary>
    [Fact]
    public async Task SignOut_CancelsAPendingSignIn()
    {
        var vm = NewVm(out _, out var auth);
        vm.ClientId = "client";   // SignIn's own Save() persists it first
        auth.Block = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var flow = vm.SignInCommand.ExecuteAsync(null);
        vm.SignOutCommand.Execute(null);

        Assert.True(auth.LastToken.IsCancellationRequested);
        auth.Block.SetResult(true);
        await flow;
    }

    [Fact]
    public void RedirectUri_matches_the_registered_loopback()
    {
        var vm = NewVm(out _, out _);
        Assert.Equal(Tone3000OAuth.RedirectUri, vm.RedirectUri);
    }
}
