using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Services.Audio.StemSeparation;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     A <c>.ckpt</c> / <c>.pt</c> / <c>.pth</c> is a pickle and can run code on
///     load. Importing one is off until the user accepts that risk for that URL; the
///     answer is stored per URL and pinned to the downloaded file's hash.
/// </summary>
public sealed class StemCheckpointConsentTests : IDisposable
{
    private const string CkptUrl = "https://example.invalid/models/bs_roformer.ckpt";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharter_PickleConsent_" + Guid.NewGuid().ToString("N"));
    private readonly FakeConfigurationService _cfg = new();
    private readonly AnsweringDialogs _dialogs = new();
    private readonly List<string> _imported = [];

    public StemCheckpointConsentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[test cleanup] {ex.Message}"); }
    }

    private StemModelManager Manager() => new(_cfg, logger: null, modelsDirectoryOverride: _dir);

    private async Task<StemModelsSettingsViewModel> ImportAsync(StemModelManager manager, string url, IDialogs? dialogs)
    {
        var vm = StemModelsRig.Create(manager, _cfg, dialogs: dialogs);
        vm.ImportCheckpoint = (u, _) =>
        {
            _imported.Add(u);
            return Task.FromException<StemModelDescriptor>(new InvalidOperationException("stub"));
        };
        vm.CustomModelUrl = url;
        await vm.ImportCustomModelCommand.ExecuteAsync(null);
        return vm;
    }

    [Theory]
    [InlineData(CkptUrl)]
    [InlineData("https://example.invalid/m.pt")]
    [InlineData("https://example.invalid/m.PTH?download=1")]
    public async Task A_declined_checkpoint_never_reaches_the_importer(string url)
    {
        _dialogs.Answer = false;
        using var vm = await ImportAsync(Manager(), url, _dialogs);

        Assert.Empty(_imported);
        var asked = Assert.Single(_dialogs.Asked);
        Assert.Contains("can run code on your PC", asked, StringComparison.Ordinal);
        Assert.False(Manager().PickleConsent.IsGranted(url));
        Assert.Contains("Not imported", vm.CustomModelImportStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_dialog_host_a_checkpoint_is_refused()
    {
        using var vm = await ImportAsync(Manager(), CkptUrl, dialogs: null);
        Assert.Empty(_imported);
        Assert.False(Manager().PickleConsent.IsGranted(CkptUrl));
    }

    [Fact]
    public async Task Confirming_imports_and_remembers_the_url()
    {
        using var vm = await ImportAsync(Manager(), CkptUrl, _dialogs);

        Assert.Equal([CkptUrl], _imported);
        Assert.True(Manager().PickleConsent.IsGranted(CkptUrl), "the consent did not persist.");

        // The same URL again: no second question.
        using var again = await ImportAsync(Manager(), CkptUrl, _dialogs);
        Assert.Single(_dialogs.Asked);
        Assert.Equal(2, _imported.Count);
    }

    [Fact]
    public async Task Safetensors_is_not_a_pickle_and_is_never_asked_about()
    {
        using var vm = await ImportAsync(Manager(), "https://example.invalid/m.safetensors", _dialogs);
        Assert.Empty(_dialogs.Asked);
        Assert.Single(_imported);
    }

    [Fact]
    public async Task The_manager_refuses_a_pickle_without_consent_before_downloading()
    {
        await Assert.ThrowsAsync<CheckpointConsentException>(() => Manager().ImportCheckpointFromUrlAsync(CkptUrl, ct: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Consent_is_pinned_to_the_first_file_and_withdrawn_when_it_changes()
    {
        var consent = Manager().PickleConsent;
        Assert.False(consent.AllowsFile(CkptUrl, "aa"));

        consent.Grant(CkptUrl);
        Assert.True(consent.AllowsFile(CkptUrl, "aa"));
        Assert.True(Manager().PickleConsent.AllowsFile(CkptUrl, "AA"));

        Assert.False(consent.AllowsFile(CkptUrl, "bb"));
        Assert.False(consent.IsGranted(CkptUrl), "a changed file kept its consent.");
    }

    [Theory]
    [InlineData("https://h/x.ckpt", true)]
    [InlineData("https://h/x.pt", true)]
    [InlineData("https://h/x.pth", true)]
    [InlineData("https://h/x.safetensors", false)]
    [InlineData("https://h/x.onnx", false)]
    [InlineData("https://h/x.ckpt.onnx", false)]
    public void Pickle_formats_are_the_ones_that_need_consent(string url, bool pickle) =>
        Assert.Equal(pickle, CheckpointPickleConsent.IsPickleFormat(url));

    private sealed class AnsweringDialogs : IDialogs
    {
        public bool Answer { get; set; } = true;

        public List<string> Asked { get; } = [];

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed { add { } remove { } }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult> => throw new NotSupportedException();

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false)
        {
            Asked.Add(title + " | " + message);
            return Task.FromResult(isConfirmation && Answer);
        }

        public bool CloseTop() => false;
    }
}
