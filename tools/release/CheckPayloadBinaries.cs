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
// Exit codes: 0 = clean, 1 = a corrupt file (each one is named), 2 = usage.

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
if (report.IsClean)
{
    Console.WriteLine($"OK: {report.ImagesChecked} PE images and {report.FilesChecked - report.ImagesChecked} other files " +
                      "under the payload read as what they claim to be.");
    return 0;
}

Console.Error.WriteLine($"CORRUPT PAYLOAD: {report.Problems.Count} of {report.FilesChecked} files in {payloadDir}:");
foreach (var problem in report.Problems)
    Console.Error.WriteLine($"  {problem.Fault,-22} {problem.RelativePath}  ({problem.Detail})");
Console.Error.WriteLine("A zero-filled build output is what an interrupted earlier build leaves behind; " +
                       "New-Release.ps1 deletes bin/Release and obj/Release before it publishes for exactly that reason.");
return 1;
