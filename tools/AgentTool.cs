#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property SdevEngLauncherRevision=3

using System.Diagnostics;
using System.Runtime.CompilerServices;

static string SourcePath([CallerFilePath] string path = "") => path;
var invoked = SourcePath();
var canonical = File.ResolveLinkTarget(invoked, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(invoked);
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
