namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     #411: <c>JuceAudioPlaybackService.LoadAudioFromMemoryAsync</c> used to take
///     <c>bytes.ToArray()</c> before handing the blob to the engine — a SECOND
///     whole-song array (~50 MB for a five-minute 16-bit WAV, on the large object
///     heap) on top of the one <c>ProjectSessionService.LoadAudioFile</c> had just
///     read, for every library song opened. The native open copies the blob into
///     its own memory before it returns, so the managed copy bought nothing.
///     <para>
///         A SOURCE lint, like <see cref="StemSwapReleaseOrderTests" />: reaching the
///         method behaviourally means opening a real engine, and a process-wide
///         allocation counter is not a reliable witness in a parallel gate (other
///         classes allocate at the same time; see <c>AllocationAssertionLintTests</c>).
///         It finds its subject by CONTENT, not by path, and reads comment-stripped
///         code so prose cannot satisfy or trip it.
///     </para>
/// </summary>
public sealed class SongMemoryLoadFootprintTests
{
    private static readonly string[] CopyShapes = ["ToArray(", ".CopyTo(", "new byte[", "GC.AllocateUninitializedArray"];

    [Fact]
    public void LoadAudioFromMemoryAsync_HandsTheCallersBytesToTheEngine_WithoutAManagedCopy()
    {
        var (file, body) = MethodBody("public Task LoadAudioFromMemoryAsync(");

        Assert.Contains("OpenMemory(", body);
        foreach (var shape in CopyShapes)
        {
            Assert.False(body.Contains(shape, StringComparison.Ordinal),
                $"{file}: LoadAudioFromMemoryAsync contains '{shape}' — a managed copy of the whole song "
                + "before the native open, which copies it again itself. That is ~50 MB of large-object "
                + "heap per library song for nothing (#411). Hand bytes.Span to OpenMemory instead.");
        }
    }

    private static (string File, string Body) MethodBody(string signature)
    {
        var file = Directory
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Audio"),
                "JuceAudioPlaybackService*.cs")
            .FirstOrDefault(f => File.ReadAllText(f).Contains(signature, StringComparison.Ordinal));

        Assert.False(file is null,
            $"No JuceAudioPlaybackService partial declares '{signature}'. Repoint this lint at wherever "
            + "the in-memory song open now lives.");

        var text = SourceText.StripComments(File.ReadAllText(file!));
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{file}: '{signature}' survived the file scan but not the comment strip.");

        var next = text.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        var nextPrivate = text.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
        var end = new[] { next, nextPrivate, text.Length }.Where(i => i > start).Min();
        return (Path.GetFileName(file!), text[start..end]);
    }
}
