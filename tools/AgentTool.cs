#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property SdevEngLauncherRevision=5

using System.Diagnostics;

var invoked = AppContext.GetData("EntryPointFilePath") as string;
if (string.IsNullOrWhiteSpace(invoked))
    throw new InvalidOperationException("The .NET host did not provide the launcher file path.");

invoked = Path.GetFullPath(invoked);
if (!File.Exists(invoked))
    throw new FileNotFoundException("The sdeveng compatibility launcher is missing.", invoked);

var canonical = File.ResolveLinkTarget(invoked, returnFinalTarget: true)?.FullName
    ?? invoked;
var toolkit = Path.GetDirectoryName(Path.GetDirectoryName(canonical))
    ?? throw new InvalidOperationException("Unable to locate the sdeveng checkout.");
var project = Path.Combine(toolkit, "src", "SdevEng.Cli", "SdevEng.Cli.csproj");
if (!File.Exists(project)) throw new FileNotFoundException("The sdeveng CLI project is missing.", project);
var compiled = Path.Combine(toolkit, "src", "SdevEng.Cli", "bin", "Release", "net10.0", "SdevEng.Cli.dll");
var sourceFiles = Directory.EnumerateFiles(Path.Combine(toolkit, "src"), "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
    .Concat(Directory.EnumerateFiles(Path.Combine(toolkit, "src"), "*.csproj", SearchOption.AllDirectories))
    .Concat(new[] { Path.Combine(toolkit, "Directory.Build.props"), Path.Combine(toolkit, "Directory.Packages.props") })
    .Where(File.Exists);
var useCompiled = File.Exists(compiled) && sourceFiles.All(path => File.GetLastWriteTimeUtc(path) <= File.GetLastWriteTimeUtc(compiled));
var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
// Pass the launcher's checkout location to the canonical CLI.
// Preserve explicitly configured toolkit roots.
if (!start.Environment.ContainsKey("SDEVENG_ROOT")
    && !start.Environment.ContainsKey("CODEX_TOOLKIT_ROOT"))
{
    start.Environment["SDEVENG_ROOT"] = toolkit;
}
if (useCompiled) start.ArgumentList.Add(compiled);
else
{
    start.ArgumentList.Add("run");
    start.ArgumentList.Add("--no-launch-profile");
    start.ArgumentList.Add("--project");
    start.ArgumentList.Add(project);
    start.ArgumentList.Add("--");
}
foreach (var arg in args) start.ArgumentList.Add(arg);
using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the sdeveng CLI.");
await process.WaitForExitAsync();
return process.ExitCode;
