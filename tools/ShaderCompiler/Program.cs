// GLSL → SPIR-V batch compiler for the committed shader embedded resources.
// Usage: dotnet run --project tools/ShaderCompiler -- <shader-directory>
// Compiles every *.vert / *.frag into <name>.<ext>.spv next to the source,
// with Vulkan 1.0 target semantics and performance optimization — matching
// what VulkanPipelineManager.CreateShaderModule expects.

using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Shaderc;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: ShaderCompiler <shader-directory>");
    return 2;
}

var shaderDir = Path.GetFullPath(args[0]);
var sources = Directory.EnumerateFiles(shaderDir, "*.vert")
    .Concat(Directory.EnumerateFiles(shaderDir, "*.frag"))
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (sources.Length == 0)
{
    Console.Error.WriteLine($"No .vert/.frag sources found in {shaderDir}");
    return 2;
}

var api = Shaderc.GetApi();
var failures = 0;

unsafe
{
    var compiler = api.CompilerInitialize();
    var options = api.CompileOptionsInitialize();
    api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan10);
    api.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);

    foreach (var sourcePath in sources)
    {
        var fileName = Path.GetFileName(sourcePath);
        var kind = fileName.EndsWith(".vert", StringComparison.OrdinalIgnoreCase)
            ? ShaderKind.VertexShader
            : ShaderKind.FragmentShader;
        var source = File.ReadAllText(sourcePath);
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var entryBytes = "main"u8.ToArray();

        fixed (byte* pSource = sourceBytes)
        fixed (byte* pName = nameBytes)
        fixed (byte* pEntry = entryBytes)
        {
            var result = api.CompileIntoSpv(compiler, pSource, (nuint)sourceBytes.Length, kind,
                pName, pEntry, options);
            var status = api.ResultGetCompilationStatus(result);
            if (status != CompilationStatus.Success)
            {
                var message = Marshal.PtrToStringUTF8((nint)api.ResultGetErrorMessage(result));
                Console.Error.WriteLine($"FAIL {fileName}: {status}\n{message}");
                failures++;
            }
            else
            {
                var length = api.ResultGetLength(result);
                var spirv = new byte[length];
                Marshal.Copy((nint)api.ResultGetBytes(result), spirv, 0, (int)length);
                var outPath = sourcePath + ".spv";
                File.WriteAllBytes(outPath, spirv);
                Console.WriteLine($"OK   {fileName} -> {Path.GetFileName(outPath)} ({length} bytes)");
            }

            api.ResultRelease(result);
        }
    }

    api.CompileOptionsRelease(options);
    api.CompilerRelease(compiler);
}

return failures == 0 ? 0 : 1;
