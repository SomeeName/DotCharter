using DotCharter.Services.Audio.Lyrics;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     <c>LyricAcousticModel.Load</c> acquires three native handles in order — a
///     <c>SessionOptions</c>, then an <c>InferenceSession</c> over it, then the
///     IO-name discovery — and released NONE of them on any failure path. The
///     options leaked on EVERY load (two models are built per auto-sync run) and
///     the whole 340 MB session leaked on either of the two metadata throws.
///     Model-load failure is a routine, user-triggerable path: a half-downloaded
///     graph, a partial model directory, an export whose IO names we cannot read.
///     <para>
///         <b>The real session cannot be constructed headlessly</b> — it needs the
///         340 MB int8 export on disk, and <c>OnnxSessionFactory</c> touches the
///         native runtime. So the two things the fix actually consists of are
///         factored into seams that CAN be driven here: the ownership rule
///         (<c>BuildOrRelease</c>) and the discovery that throws
///         (<c>ResolveIoNames</c>). The wiring between them is covered by the
///         source lint at the bottom.
///     </para>
/// </summary>
public sealed class LyricAcousticModelDisposalTests
{
    private sealed class Handle : IDisposable
    {
        public int Disposals;
        public void Dispose() => Disposals++;
    }

    [Fact]
    public void BuildOrRelease_HandsBackTheResultAndDisposesNothingWhenItSucceeds()
    {
        var options = new Handle();
        var session = new Handle();

        var built = LyricAcousticModel.BuildOrRelease(() => "model", options, session);

        Assert.Equal("model", built);
        Assert.Equal(0, options.Disposals);
        Assert.Equal(0, session.Disposals);
    }

    /// <summary>The whole point: a throw releases every handle it was handed, and
    /// the ORIGINAL exception is what the caller sees.</summary>
    [Fact]
    public void BuildOrRelease_DisposesEveryHandleWhenTheBuildThrows()
    {
        var options = new Handle();
        var session = new Handle();

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            LyricAcousticModel.BuildOrRelease<string>(
                () => throw new InvalidOperationException("the alignment model exposes no outputs."),
                options, session));

        Assert.Contains("no outputs", thrown.Message);
        Assert.Equal(1, options.Disposals);
        Assert.Equal(1, session.Disposals);
    }

    /// <summary>Reverse acquisition order — the session is built ON the options,
    /// so it goes first — and a handle that throws on the way out must not stop
    /// the others or mask the fault that started this.</summary>
    [Fact]
    public void BuildOrRelease_ReleasesInReverseOrderAndSurvivesAFailingDispose()
    {
        var order = new List<string>();
        var options = new RecordingHandle(order, "options");
        var bad = new ThrowingHandle(order, "session");

        var thrown = Assert.Throws<FileNotFoundException>(() =>
            LyricAcousticModel.BuildOrRelease<string>(
                () => throw new FileNotFoundException("corrupt graph"), options, bad));

        Assert.Equal("corrupt graph", thrown.Message);
        Assert.Equal(new[] { "session", "options" }, order);
    }

    [Fact]
    public void BuildOrRelease_ToleratesANullHandle()
    {
        var options = new Handle();

        Assert.Throws<InvalidOperationException>(() =>
            LyricAcousticModel.BuildOrRelease<string>(
                () => throw new InvalidOperationException("boom"), null, options));

        Assert.Equal(1, options.Disposals);
    }

    /// <summary>The two throws the ownership rule exists to cover, and the
    /// discovery itself: the published exports disagree on whether an
    /// attention-mask input is present, so the names are read, not assumed.</summary>
    [Fact]
    public void ResolveIoNames_FindsTheNamesOrThrowsTheTwoWayItCan()
    {
        var (input, mask, output) = LyricAcousticModel.ResolveIoNames(
            ["input_values", "attention_mask"], ["logits"]);
        Assert.Equal("input_values", input);
        Assert.Equal("attention_mask", mask);
        Assert.Equal("logits", output);

        // An export with no mask input is the other published shape.
        var (_, noMask, _) = LyricAcousticModel.ResolveIoNames(["input_values"], ["logits"]);
        Assert.Null(noMask);

        Assert.Throws<InvalidOperationException>(() =>
            LyricAcousticModel.ResolveIoNames([], ["logits"]));
        Assert.Throws<InvalidOperationException>(() =>
            LyricAcousticModel.ResolveIoNames(["input_values"], []));
    }

    /// <summary>
    ///     The seams above are only worth anything if <c>Load</c> and
    ///     <c>Dispose</c> actually use them — and neither can be executed without
    ///     the model file, so this reads the source. It is the same trade the
    ///     renderer guards make.
    /// </summary>
    [Fact]
    public void LoadAndDispose_OwnBothNativeHandles()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.Services", "Audio", "Lyrics", "LyricAcousticModel.cs"));

        var load = source.IndexOf("public static LyricAcousticModel Load(", StringComparison.Ordinal);
        Assert.True(load >= 0, "Load has moved — repoint this guard.");
        var body = source[load..];

        // The session build and the metadata discovery BOTH go through the
        // ownership helper; a bare `new InferenceSession(` is the pre-fix shape.
        Assert.Contains("BuildOrRelease(() => new InferenceSession(", body);
        Assert.Contains("ResolveIoNames(", body);

        var dispose = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(dispose >= 0);
        var disposeBody = source[dispose..];
        Assert.Contains("_session.Dispose();", disposeBody);
        Assert.Contains("_sessionOptions.Dispose();", disposeBody);
    }

    private sealed class RecordingHandle(List<string> order, string name) : IDisposable
    {
        public void Dispose() => order.Add(name);
    }

    private sealed class ThrowingHandle(List<string> order, string name) : IDisposable
    {
        public void Dispose()
        {
            order.Add(name);
            throw new InvalidOperationException(name + " could not be released");
        }
    }
}
