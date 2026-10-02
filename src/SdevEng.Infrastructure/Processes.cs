using System.Diagnostics;
using System.Text;

namespace SdevEng;

public static class Processes
{
    public static bool OnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, name)) || OperatingSystem.IsWindows() && File.Exists(Path.Combine(p, name + ".exe")));
    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, string cwd)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args)
        {
            if (JevCredentials.Contains(arg)) throw new InvalidOperationException("Refusing to place JEV credentials in child-process arguments.");
            info.ArgumentList.Add(arg);
        }
        info.Environment.Remove(JevCredentials.EnvironmentVariable);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return info;
    }
    public static async Task<ProcessResult> Run(string exe, IEnumerable<string> args, string cwd, string? artifact = null, TimeSpan? timeout = null)
    {
        var info = StartInfo(exe, args, cwd);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        using var timer = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var writer = artifact is null ? null : new StreamWriter(artifact, false, new UTF8Encoding(false));
        var gate = new SemaphoreSlim(1); var buffer = new StringBuilder(); bool overflow = false;
        async Task Drain(StreamReader reader)
        {
            var chars = new char[4096]; int count;
            while ((count = await reader.ReadAsync(chars, timer.Token)) > 0)
            {
                await gate.WaitAsync(timer.Token);
                try
                {
                    if (writer is not null) await writer.WriteAsync(chars.AsMemory(0, count), timer.Token);
                    else if (buffer.Length + count <= 16 * 1024 * 1024) buffer.Append(chars, 0, count);
                    else overflow = true;
                }
                finally { gate.Release(); }
            }
        }
        var reads = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
        try
        {
            await process.WaitForExitAsync(timer.Token);
            // Build servers can inherit a redirected pipe after their parent exits.
            // Do not turn a completed command into a timeout while waiting for them.
            if (await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(2), timer.Token)) == reads)
                await reads;
            else
            {
                timer.Cancel();
                try { await reads; }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            return new(124, "Operation timed out; process tree terminated.");
        }
        if (overflow) throw new InvalidOperationException("Structured command output exceeded 16 MiB; narrow scope.");
        return new(process.ExitCode, buffer.ToString());
    }
}
