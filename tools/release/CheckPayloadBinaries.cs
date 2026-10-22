// .NET 10 file-based program. Refuses a staged release payload whose binaries are
// not binaries, using the REAL rules type (ReleasePayloadIntegrity from
// DotCharter.Services), so what the release checks and what the gate tests pin
// cannot drift apart.
//
//   dotnet run CheckPayloadBinaries.cs <payloadDir>
//
// Every .dll / .exe must start with "MZ", carry PE headers that parse and, if it
// is a managed assembly, metadata that reads; no file may be non-empty and all
// zeros. v0.59.0 (#137): an interrupted build left 260 zero-filled outputs, the
// next incremental build reused them, and the zip validated -- the packed
// DotCharter.exe died at launch with BadImageFormatException.
//
// It also refuses a native DLL / exe whose import table names a DLL that is neither
// shipped beside it nor a Windows system DLL (NativeImportAudit, DotCharter #176:
// v1.0.0's juce_sharp.dll imported VCRUNTIME140 / MSVCP140, which the zip did not carry,
// so a clean Windows had no audio).
//
// Exit codes: 0 = clean, 1 = a corrupt file or an unsatisfied import (each one is
// named), 2 = usage.

#:project ../../DotCharter.Services/DotCharter.Services.csproj

using DotCharter.Services.Update;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dotnet run CheckPayloadBinaries.cs <payloadDir>");
    return 2;
}

var payloadDir = Path.GetFullPath(args[0]);
if (!Directory.Exists(payloadDir))
{
    Console.Error.WriteLine($"CORRUPT PAYLOAD: no such directory: {payloadDir}");
    return 1;
}

var report = ReleasePayloadIntegrity.Scan(payloadDir);
if (!report.IsClean)
{
    Console.Error.WriteLine($"CORRUPT PAYLOAD: {report.Problems.Count} of {report.FilesChecked} files in {payloadDir}:");
    foreach (var problem in report.Problems)
        Console.Error.WriteLine($"  {problem.Fault,-22} {problem.RelativePath}  ({problem.Detail})");
    Console.Error.WriteLine("A zero-filled build output is what an interrupted earlier build leaves behind; " +
                           "New-Release.ps1 deletes bin/Release and obj/Release before it publishes for exactly that reason.");
    return 1;
}

var imports = NativeImportAudit.Scan(payloadDir);
if (!imports.IsClean)
{
    Console.Error.WriteLine($"UNSATISFIED NATIVE IMPORTS: {imports.Problems.Count} in {payloadDir}:");
    foreach (var problem in imports.Problems)
        Console.Error.WriteLine($"  {problem.RelativePath}  imports {problem.ImportedDll}  ({problem.Detail})");
    Console.Error.WriteLine("A machine without that DLL cannot load the image. Link the runtime statically " +
                           "(JuceSharp's JUCESHARP_STATIC_CRT) or ship the DLL beside the image (the VC++ runtime ONNX Runtime needs is New-Release.ps1's app-local copy, vcredist.json).");
    return 1;
}

Console.WriteLine($"OK: {report.ImagesChecked} PE images and {report.FilesChecked - report.ImagesChecked} other files " +
                  $"under the payload read as what they claim to be; {imports.ImagesChecked} native images import only " +
                  "Windows system DLLs or files shipped beside them.");
return 0;
