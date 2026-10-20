using DotCharter.Services.Infrastructure.Storage;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Infrastructure;

public sealed class ExtractedPayloadTests : IDisposable
{
    private static readonly string[] Required = ["model.onnx", "vocab.json"];

    private readonly string _staging = Directory.CreateTempSubdirectory("dotcharter-payload-").FullName;

    [Fact]
    public void FilesAtTheTop_AreTheRoot()
    {
        Touch(_staging, "model.onnx", "vocab.json");

        Assert.Equal(_staging, ExtractedPayload.FindRoot(_staging, Required));
    }

    [Fact]
    public void FilesInsideOneWrappingFolder_ThatFolderIsTheRoot()
    {
        var wrapped = Path.Combine(_staging, "model-v2");
        Touch(wrapped, "model.onnx", "vocab.json");
        Touch(Path.Combine(_staging, "docs"), "README.md");

        Assert.Equal(wrapped, ExtractedPayload.FindRoot(_staging, Required));
    }

    [Fact]
    public void AnArchiveMissingOneFile_HasNoRoot()
    {
        Touch(_staging, "model.onnx");
        Touch(Path.Combine(_staging, "nested"), "vocab.json");

        Assert.Null(ExtractedPayload.FindRoot(_staging, Required));
    }

    [Fact]
    public void FilesTwoFoldersDown_AreNotFound()
    {
        Touch(Path.Combine(_staging, "outer", "inner"), "model.onnx", "vocab.json");

        Assert.Null(ExtractedPayload.FindRoot(_staging, Required));
    }

    public void Dispose() => BestEffortDelete.Directory(_staging);

    private static void Touch(string directory, params string[] files)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(directory, file), "x");
        }
    }
}
